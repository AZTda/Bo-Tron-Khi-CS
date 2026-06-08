using System;
using System.Windows;
using System.Windows.Controls;

namespace Bo_Tron_Khi_CS
{
    public partial class MfcConfigWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public MfcConfigWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadLocalSettings();
        }

        private void LoadLocalSettings()
        {
            // Populate fields from SystemConfig lists (assumes length >= 6)
            for (int ch = 1; ch <= 6; ch++)
            {
                int idx = ch - 1;
                GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);
                
                // Note: local config holds min_sccm implicitly as 0.0, or we read from local configs
                minS.Text = "0.0"; 
                maxS.Text = _config.mfc_max_sccm[idx].ToString("F1");
                minV.Text = (_config.mfc_min_v[idx] / 1000.0f).ToString("F3");
                maxV.Text = (_config.mfc_max_v[idx] / 1000.0f).ToString("F3");
            }
        }

        private void GetFields(int ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV)
        {
            minS = FindName($"TxtMinS_{ch}") as TextBox;
            maxS = FindName($"TxtMaxS_{ch}") as TextBox;
            minV = FindName($"TxtMinV_{ch}") as TextBox;
            maxV = FindName($"TxtMaxV_{ch}") as TextBox;
        }

        private void OnReadClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected. Connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Read MFC ranges (sccm) from address 224 (12 registers)
            var rangeResult = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 224, 12);
            if (!rangeResult.Success || rangeResult.Data == null || rangeResult.Data.Length != 12)
            {
                MessageBox.Show($"Failed to read MFC ranges: {rangeResult.ErrorMessage}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Read Calibration min/max (mV) from address 274 (12 registers)
            var caliResult = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 274, 12);
            if (!caliResult.Success || caliResult.Data == null || caliResult.Data.Length != 12)
            {
                MessageBox.Show($"Failed to read calibration values: {caliResult.ErrorMessage}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            for (int ch = 1; ch <= 6; ch++)
            {
                int idx = ch - 1;
                float maxSccm = ModbusHandler.RegsToFloat(rangeResult.Data[idx * 2], rangeResult.Data[idx * 2 + 1]);
                
                // min/max values are in millivolts (mV) stored as uint16
                ushort mvMin = caliResult.Data[idx * 2];
                ushort mvMax = caliResult.Data[idx * 2 + 1];
                float minVolt = mvMin / 1000.0f;
                float maxVolt = mvMax / 1000.0f;

                GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);
                minS.Text = "0.0"; // min sccm is implicitly 0
                maxS.Text = maxSccm.ToString("F1");
                minV.Text = minVolt.ToString("F3");
                maxV.Text = maxVolt.ToString("F3");
            }

            string retryInfo = (rangeResult.RetryCount + caliResult.RetryCount) > 0 ? $" (retried {rangeResult.RetryCount + caliResult.RetryCount}x)" : "";
            MessageBox.Show($"Read settings from device successfully!{retryInfo}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnWriteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int failCount = 0;
                byte slave = (byte)_config.mixing_slave;

                for (int ch = 1; ch <= 6; ch++)
                {
                    int idx = ch - 1;
                    GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);

                    float maxSccm = ParseUtil.ParseFloat(maxS.Text);
                    float minVolt = ParseUtil.ParseFloat(minV.Text);
                    float maxVolt = ParseUtil.ParseFloat(maxV.Text);

                    // Update local SystemConfig
                    _config.mfc_max_sccm[idx] = maxSccm;
                    _config.mfc_min_v[idx] = (int)(minVolt * 1000);
                    _config.mfc_max_v[idx] = (int)(maxVolt * 1000);

                    if (_handler.IsConnected)
                    {
                        // Write MFC max range (sccm) at 224 + idx * 2 (float)
                        ushort[] rangeRegs = ModbusHandler.FloatToRegs(maxSccm);
                        var rResult = _handler.TryWriteMultipleRegisters(slave, (ushort)(224 + idx * 2), rangeRegs);
                        if (!rResult.Success) failCount++;

                        // Write Calibration Min/Max (mV) at 274 + idx * 2 (uint16 each)
                        ushort mvMin = (ushort)(minVolt * 1000);
                        ushort mvMax = (ushort)(maxVolt * 1000);

                        var minRes = _handler.TryWriteSingleRegister(slave, (ushort)(274 + idx * 2), mvMin);
                        if (!minRes.Success) failCount++;

                        var maxRes = _handler.TryWriteSingleRegister(slave, (ushort)(274 + idx * 2 + 1), mvMax);
                        if (!maxRes.Success) failCount++;
                    }
                }

                _config.Save();

                if (_handler.IsConnected)
                {
                    // Trigger MFC Config Save (register 240)
                    var saveResult = _handler.TryWriteSingleRegister(slave, 240, 1);
                    if (!saveResult.Success) failCount++;

                    if (failCount == 0)
                    {
                        MessageBox.Show("Saved settings locally and wrote successfully to Mixing Board!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Saved locally, but {failCount} write operation(s) failed during device sync.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    MessageBox.Show("Saved settings locally (simulation mode). Connect to Modbus to write to device.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save/write settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

