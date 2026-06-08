using System;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class MfcSettingWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public MfcSettingWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (_config.mfc_max_sccm != null && _config.mfc_max_sccm.Count >= 6)
            {
                TxtMfc1Range.Text = _config.mfc_max_sccm[0].ToString("F0");
                TxtMfc2Range.Text = _config.mfc_max_sccm[1].ToString("F0");
                TxtMfc3Range.Text = _config.mfc_max_sccm[2].ToString("F0");
                TxtMfc4Range.Text = _config.mfc_max_sccm[3].ToString("F0");
                TxtMfc5Range.Text = _config.mfc_max_sccm[4].ToString("F0");
                TxtMfc6Range.Text = _config.mfc_max_sccm[5].ToString("F0");
            }
            TxtULimit.Text = _config.u_limit_percent.ToString("F0");
            TxtLLimit.Text = _config.l_limit_percent.ToString("F0");
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double[] ranges = new double[6];
                ranges[0] = ParseUtil.ParseDouble(TxtMfc1Range.Text);
                ranges[1] = ParseUtil.ParseDouble(TxtMfc2Range.Text);
                ranges[2] = ParseUtil.ParseDouble(TxtMfc3Range.Text);
                ranges[3] = ParseUtil.ParseDouble(TxtMfc4Range.Text);
                ranges[4] = ParseUtil.ParseDouble(TxtMfc5Range.Text);
                ranges[5] = ParseUtil.ParseDouble(TxtMfc6Range.Text);

                double uLimit = ParseUtil.ParseDouble(TxtULimit.Text);
                double lLimit = ParseUtil.ParseDouble(TxtLLimit.Text);

                // Save to local configuration
                for (int i = 0; i < 6; i++)
                {
                    _config.mfc_max_sccm[i] = ranges[i];
                }
                _config.u_limit_percent = uLimit;
                _config.l_limit_percent = lLimit;
                _config.Save();

                // Sync to device holding registers if connected
                if (_handler.IsConnected)
                {
                    byte ms = (byte)_config.mixing_slave;
                    int failCount = 0;

                    for (int ch = 0; ch < 6; ch++)
                    {
                        float maxSccm = (float)_config.mfc_max_sccm[ch];
                        ushort regAddr = (ushort)(224 + ch * 2);
                        ushort[] regs = ModbusHandler.FloatToRegs(maxSccm);
                        var result = _handler.TryWriteMultipleRegisters(ms, regAddr, regs);
                        if (!result.Success) failCount++;
                    }

                    // Write uLimit and lLimit
                    ushort[] uLimitRegs = ModbusHandler.FloatToRegs((float)uLimit);
                    var uResult = _handler.TryWriteMultipleRegisters(ms, 236, uLimitRegs);
                    if (!uResult.Success) failCount++;

                    ushort[] lLimitRegs = ModbusHandler.FloatToRegs((float)lLimit);
                    var lResult = _handler.TryWriteMultipleRegisters(ms, 238, lLimitRegs);
                    if (!lResult.Success) failCount++;

                    // Save MFC config trigger
                    var saveResult = _handler.TryWriteSingleRegister(ms, 240, 1);
                    if (!saveResult.Success) failCount++;

                    if (failCount > 0)
                    {
                        MessageBox.Show($"MFC flow ranges saved locally. Warning: {failCount} write(s) failed during device sync.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show("MFC flow ranges updated and synced to device.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show("MFC flow ranges saved locally (simulation mode).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Please enter valid numeric values.\nError: {ex.Message}", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
                double[] ranges = new double[6];
                ranges[0] = ParseUtil.ParseDouble(TxtMfc1Range.Text);
                ranges[1] = ParseUtil.ParseDouble(TxtMfc2Range.Text);
                ranges[2] = ParseUtil.ParseDouble(TxtMfc3Range.Text);
                ranges[3] = ParseUtil.ParseDouble(TxtMfc4Range.Text);
                ranges[4] = ParseUtil.ParseDouble(TxtMfc5Range.Text);
                ranges[5] = ParseUtil.ParseDouble(TxtMfc6Range.Text);

                double uLimit = ParseUtil.ParseDouble(TxtULimit.Text);
                double lLimit = ParseUtil.ParseDouble(TxtLLimit.Text);

                // Save to local configuration
                for (int i = 0; i < 6; i++)
                {
                    _config.mfc_max_sccm[i] = ranges[i];
                }
                _config.u_limit_percent = uLimit;
                _config.l_limit_percent = lLimit;
                _config.Save();

                byte ms = (byte)_config.mixing_slave;
                int failCount = 0;

                // Sync ranges sequentially
                for (int ch = 0; ch < 6; ch++)
                {
                    float maxSccm = (float)_config.mfc_max_sccm[ch];
                    ushort regAddr = (ushort)(224 + ch * 2);
                    ushort[] regs = ModbusHandler.FloatToRegs(maxSccm);
                    var result = _handler.TryWriteMultipleRegisters(ms, regAddr, regs);
                    if (!result.Success) failCount++;
                }

                // Write uLimit and lLimit
                ushort[] uLimitRegs = ModbusHandler.FloatToRegs((float)uLimit);
                var uResult = _handler.TryWriteMultipleRegisters(ms, 236, uLimitRegs);
                if (!uResult.Success) failCount++;

                ushort[] lLimitRegs = ModbusHandler.FloatToRegs((float)lLimit);
                var lResult = _handler.TryWriteMultipleRegisters(ms, 238, lLimitRegs);
                if (!lResult.Success) failCount++;

                // Save MFC config trigger
                var saveResult = _handler.TryWriteSingleRegister(ms, 240, 1);
                if (!saveResult.Success) failCount++;

                if (failCount == 0)
                {
                    MessageBox.Show("Successfully synced all MFC ranges and limit configurations to device/EEPROM.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Sync completed with errors. Warning: {failCount} write operation(s) failed during device sync.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sync failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
