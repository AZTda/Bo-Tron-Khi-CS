using System;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class E5ccPidWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public E5ccPidWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadLocalSettings();
        }

        private void LoadLocalSettings()
        {
            // Populate dialog with default / simulation settings
            TxtP.Text = "10.0";
            TxtI.Text = "240";
            TxtD.Text = "40.0";

            TxtSpMin.Text = "0.0";
            TxtSpMax.Text = "300.0";
            TxtMvMin.Text = "0.0";
            TxtMvMax.Text = "100.0";
            TxtInputShift.Text = "0.0";

            TxtAlm1.Text = _config.temp_alarm_limit.ToString("F1");
            TxtAlm2.Text = "50.0";
            TxtCtrlPeriod.Text = "12";
        }

        private void OnReadClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected. Connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte slave = (byte)_config.e5cc_slave;
            int failCount = 0;

            // 1. Read PID parameters (0x2300 - 0x2305) — batch read 6 regs
            var pidResult = _handler.TryReadHoldingRegisters(slave, 0x2300, 6);
            if (pidResult.Success && pidResult.Data != null && pidResult.Data.Length >= 6)
            {
                TxtP.Text = (pidResult.Data[0] / 10.0f).ToString("F1");
                TxtI.Text = pidResult.Data[1].ToString();
                TxtD.Text = (pidResult.Data[2] / 10.0f).ToString("F1");
                TxtCtrlPeriod.Text = pidResult.Data[3].ToString();
                TxtMvMax.Text = (pidResult.Data[4] / 10.0f).ToString("F1");
                TxtMvMin.Text = (pidResult.Data[5] / 10.0f).ToString("F1");
            }
            else { failCount++; }

            // 2. Read SP limits (0x2400 - 0x2402) — batch read 3 regs
            var spResult = _handler.TryReadHoldingRegisters(slave, 0x2400, 3);
            if (spResult.Success && spResult.Data != null && spResult.Data.Length >= 3)
            {
                TxtInputShift.Text = ((short)spResult.Data[0] / 10.0f).ToString("F1");
                TxtSpMax.Text = ((short)spResult.Data[1] / 10.0f).ToString("F1");
                TxtSpMin.Text = ((short)spResult.Data[2] / 10.0f).ToString("F1");
            }
            else { failCount++; }

            // 3. Read Alarm thresholds (0x2200 - 0x2201) — batch read 2 regs
            var almResult = _handler.TryReadHoldingRegisters(slave, 0x2200, 2);
            if (almResult.Success && almResult.Data != null && almResult.Data.Length >= 2)
            {
                TxtAlm1.Text = ((short)almResult.Data[0] / 10.0f).ToString("F1");
                TxtAlm2.Text = ((short)almResult.Data[1] / 10.0f).ToString("F1");
            }
            else { failCount++; }

            if (failCount == 0)
            {
                MessageBox.Show("Read parameters from Omron E5CC successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Partially read E5CC parameters. {failCount}/3 register groups failed.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnWriteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                float p = ParseUtil.ParseFloat(TxtP.Text);
                ushort i = ParseUtil.ParseUshort(TxtI.Text);
                float d = ParseUtil.ParseFloat(TxtD.Text);
                ushort ctrlPeriod = ParseUtil.ParseUshort(TxtCtrlPeriod.Text);
                float mvMax = ParseUtil.ParseFloat(TxtMvMax.Text);
                float mvMin = ParseUtil.ParseFloat(TxtMvMin.Text);

                float spMin = ParseUtil.ParseFloat(TxtSpMin.Text);
                float spMax = ParseUtil.ParseFloat(TxtSpMax.Text);
                float offset = ParseUtil.ParseFloat(TxtInputShift.Text);

                float alm1 = ParseUtil.ParseFloat(TxtAlm1.Text);
                float alm2 = ParseUtil.ParseFloat(TxtAlm2.Text);

                // Update local SystemConfig
                _config.temp_alarm_limit = alm1;
                _config.Save();

                if (_handler.IsConnected)
                {
                    byte slave = (byte)_config.e5cc_slave;
                    int failCount = 0;

                    // Write PID + MV limits as batch (6 consecutive regs: P, I, D, CtrlPeriod, MvHi, MvLo)
                    ushort[] pidBatch = new ushort[] {
                        (ushort)(p * 10), i, (ushort)(d * 10),
                        ctrlPeriod, (ushort)(mvMax * 10), (ushort)(mvMin * 10)
                    };
                    var pidResult = _handler.TryWriteMultipleRegisters(slave, 0x2300, pidBatch);
                    if (!pidResult.Success) failCount++;

                    // Write SP limits as batch (3 consecutive regs: InputShift, SpHi, SpLo)
                    ushort[] spBatch = new ushort[] {
                        (ushort)(offset * 10), (ushort)(spMax * 10), (ushort)(spMin * 10)
                    };
                    var spResult = _handler.TryWriteMultipleRegisters(slave, 0x2400, spBatch);
                    if (!spResult.Success) failCount++;

                    // Write Alarm thresholds as batch (2 consecutive regs)
                    ushort[] almBatch = new ushort[] { (ushort)(alm1 * 10), (ushort)(alm2 * 10) };
                    var almResult = _handler.TryWriteMultipleRegisters(slave, 0x2200, almBatch);
                    if (!almResult.Success) failCount++;

                    if (failCount == 0)
                    {
                        MessageBox.Show("Saved settings locally and wrote successfully to Omron E5CC!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Saved locally, but {failCount}/3 register group write(s) failed.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    MessageBox.Show("Saved settings locally (simulation mode). Connect to write to controller.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save/write settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStartAtClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte slave = (byte)_config.e5cc_slave;
            // E5CC Operation Command: AT Execute = Command 02, Info 00 → 0x0200
            var result = _handler.TryWriteSingleRegister(slave, 0x0000, 0x0200); // execute AT
            if (result.Success)
            {
                MessageBox.Show("Auto-Tune execution command sent!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to start AT: {result.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStopAtClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte slave = (byte)_config.e5cc_slave;
            // E5CC Operation Command: AT Cancel = Command 02, Info 01 → 0x0201
            var result = _handler.TryWriteSingleRegister(slave, 0x0000, 0x0201); // cancel AT
            if (result.Success)
            {
                MessageBox.Show("Auto-Tune cancellation command sent!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to stop AT: {result.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
