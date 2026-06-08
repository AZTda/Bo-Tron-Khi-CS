using System;
using System.Windows;
using System.Windows.Controls;

namespace Bo_Tron_Khi_CS
{
    public partial class MfcCalibWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public MfcCalibWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadSettings();
        }

        private void LoadSettings()
        {
            for (int ch = 1; ch <= 6; ch++)
            {
                int idx = ch - 1;
                GetFields(ch, out TextBox txtMin, out TextBox txtMax, out TextBox txtFac);
                
                txtMin.Text = _config.mfc_min_v[idx].ToString("F0");
                txtMax.Text = _config.mfc_max_v[idx].ToString("F0");
                txtFac.Text = _config.mfc_factor[idx].ToString("G");
            }
        }

        private void GetFields(int ch, out TextBox minV, out TextBox maxV, out TextBox factor)
        {
            minV = FindName($"TxtMinV_{ch}") as TextBox;
            maxV = FindName($"TxtMaxV_{ch}") as TextBox;
            factor = FindName($"TxtFactor_{ch}") as TextBox;
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            for (int ch = 1; ch <= 6; ch++)
            {
                GetFields(ch, out TextBox txtMin, out TextBox txtMax, out TextBox txtFac);
                txtMin.Text = "0";
                txtMax.Text = "5000";
                txtFac.Text = "1";
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double[] minV = new double[6];
                double[] maxV = new double[6];
                double[] factors = new double[6];

                for (int ch = 1; ch <= 6; ch++)
                {
                    GetFields(ch, out TextBox txtMin, out TextBox txtMax, out TextBox txtFac);
                    minV[ch - 1] = ParseUtil.ParseDouble(txtMin.Text);
                    maxV[ch - 1] = ParseUtil.ParseDouble(txtMax.Text);
                    factors[ch - 1] = ParseUtil.ParseDouble(txtFac.Text);
                }

                // Save to local configuration
                for (int i = 0; i < 6; i++)
                {
                    _config.mfc_min_v[i] = minV[i];
                    _config.mfc_max_v[i] = maxV[i];
                    _config.mfc_factor[i] = factors[i];
                }
                _config.Save();

                // Sync to device holding registers if connected
                if (_handler.IsConnected)
                {
                    byte ms = (byte)_config.mixing_slave;
                    
                    // Sync all 6 channels to address 45 (ADR_HOLDING_MFC_CALI_CONFIG)
                    ushort[] caliRegs = new ushort[24];
                    for (int ch = 0; ch < 6; ch++)
                    {
                        short minVoltVal = (short)_config.mfc_min_v[ch];
                        short maxVoltVal = (short)_config.mfc_max_v[ch];
                        double realFactor = _config.mfc_factor[ch];
                        
                        if (realFactor < 0.1) realFactor = 0.1;
                        if (realFactor > 10.0) realFactor = 10.0;
                        
                        int factorVal = (int)Math.Round(1000.0 / realFactor);
                        
                        int baseIdx = ch * 4;
                        caliRegs[baseIdx] = (ushort)minVoltVal;
                        caliRegs[baseIdx + 1] = (ushort)maxVoltVal;
                        caliRegs[baseIdx + 2] = (ushort)(factorVal & 0xFFFF);
                        caliRegs[baseIdx + 3] = (ushort)((factorVal >> 16) & 0xFFFF);
                    }

                    var result = _handler.TryWriteMultipleRegisters(ms, 45, caliRegs);
                    if (!result.Success)
                    {
                        MessageBox.Show($"MFC calibration saved locally. Warning: failed to sync to device: {result.ErrorMessage}", "Sync Failure", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show("MFC voltage calibration & factor configuration saved.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show("MFC voltage calibration saved locally (simulation mode).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Please enter valid numeric values.\nError: {ex.Message}", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSyncHmiClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Device not connected. Please connect Modbus before syncing.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // First, save current inputs to configuration memory
                double[] minV = new double[6];
                double[] maxV = new double[6];
                double[] factors = new double[6];

                for (int ch = 1; ch <= 6; ch++)
                {
                    GetFields(ch, out TextBox txtMin, out TextBox txtMax, out TextBox txtFac);
                    minV[ch - 1] = ParseUtil.ParseDouble(txtMin.Text);
                    maxV[ch - 1] = ParseUtil.ParseDouble(txtMax.Text);
                    factors[ch - 1] = ParseUtil.ParseDouble(txtFac.Text);
                }

                for (int i = 0; i < 6; i++)
                {
                    _config.mfc_min_v[i] = minV[i];
                    _config.mfc_max_v[i] = maxV[i];
                    _config.mfc_factor[i] = factors[i];
                }
                _config.Save();

                // Now write the 24 registers starting at address 45 to the board
                byte ms = (byte)_config.mixing_slave;
                ushort[] caliRegs = new ushort[24];
                for (int ch = 0; ch < 6; ch++)
                {
                    short minVoltVal = (short)_config.mfc_min_v[ch];
                    short maxVoltVal = (short)_config.mfc_max_v[ch];
                    double realFactor = _config.mfc_factor[ch];
                    
                    if (realFactor < 0.1) realFactor = 0.1;
                    if (realFactor > 10.0) realFactor = 10.0;
                    
                    int factorVal = (int)Math.Round(1000.0 / realFactor);
                    
                    int baseIdx = ch * 4;
                    caliRegs[baseIdx] = (ushort)minVoltVal;
                    caliRegs[baseIdx + 1] = (ushort)maxVoltVal;
                    caliRegs[baseIdx + 2] = (ushort)(factorVal & 0xFFFF);
                    caliRegs[baseIdx + 3] = (ushort)((factorVal >> 16) & 0xFFFF);
                }

                var result = _handler.TryWriteMultipleRegisters(ms, 45, caliRegs);
                if (result.Success)
                {
                    string retryInfo = result.RetryCount > 0 ? $" (retried {result.RetryCount}x)" : "";
                    MessageBox.Show($"Successfully synced all calibration limits and factors to HMI/board.{retryInfo}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Sync failed: {result.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sync failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
