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
                    int failCount = 0;

                    for (int ch = 0; ch < 6; ch++)
                    {
                        float minVolt = (float)(_config.mfc_min_v[ch] / 1000.0);
                        float maxVolt = (float)(_config.mfc_max_v[ch] / 1000.0);

                        // Write min_v and max_v as a single batch (4 regs) instead of 2 separate calls
                        ushort[] minVoltRegs = ModbusHandler.FloatToRegs(minVolt);
                        ushort[] maxVoltRegs = ModbusHandler.FloatToRegs(maxVolt);
                        ushort[] batch = new ushort[] { minVoltRegs[0], minVoltRegs[1], maxVoltRegs[0], maxVoltRegs[1] };

                        var result = _handler.TryWriteMultipleRegisters(ms, (ushort)(ch * 8 + 4), batch);
                        if (!result.Success) failCount++;
                    }

                    if (failCount > 0)
                    {
                        MessageBox.Show($"MFC calibration saved locally. Warning: {failCount} channel(s) failed to sync to device.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
