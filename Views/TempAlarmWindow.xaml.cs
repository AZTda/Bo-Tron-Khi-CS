using System;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class TempAlarmWindow : Window
    {
        private readonly SystemConfig _config;

        public TempAlarmWindow(SystemConfig config)
        {
            InitializeComponent();
            _config = config;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            TxtLimit.Text = _config.temp_alarm_limit.ToString("F1");
            ChkBeep.IsChecked = _config.temp_alarm_enabled;
            ChkStop.IsChecked = _config.temp_auto_stop;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ParseUtil.TryParseDouble(TxtLimit.Text, out double limit))
                {
                    _config.temp_alarm_limit = limit;
                }
                else
                {
                    MessageBox.Show("Please enter a valid alarm threshold number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _config.temp_alarm_enabled = ChkBeep.IsChecked == true;
                _config.temp_auto_stop = ChkStop.IsChecked == true;
                _config.Save();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
