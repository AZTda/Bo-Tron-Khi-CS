using System;
using System.IO.Ports;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class ModbusConnWindow : Window
    {
        private readonly SystemConfig _config;

        public ModbusConnWindow(SystemConfig config)
        {
            InitializeComponent();
            _config = config;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // Scan COM ports
            CbPorts.Items.Clear();
            CbPorts.Items.Add("Virtual Sim");
            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                CbPorts.Items.Add(port);
            }

            bool isTcp = _config.port.StartsWith("TCP:");

            // If it is serial and the configured port is not in the list, add it
            if (!isTcp && !string.IsNullOrEmpty(_config.port) && !CbPorts.Items.Contains(_config.port))
            {
                CbPorts.Items.Add(_config.port);
            }

            // Set selections
            if (isTcp)
            {
                CbPorts.SelectedIndex = 0; // default serial combo selection to sim since TCP is checked
                var parts = _config.port.Split(':');
                string ip = "127.0.0.1";
                string portStr = "502";
                if (parts.Length >= 2) ip = parts[1];
                if (parts.Length >= 3) portStr = parts[2];
                TxtTcpIp.Text = ip;
                TxtTcpPort.Text = portStr;
            }
            else
            {
                TxtTcpIp.Text = "127.0.0.1";
                TxtTcpPort.Text = "502";
                
                if (CbPorts.Items.Contains(_config.port))
                {
                    CbPorts.SelectedItem = _config.port;
                }
                else
                {
                    CbPorts.SelectedIndex = 0;
                }
            }

            // Set baudrate
            string baudStr = _config.baudrate.ToString();
            for (int i = 0; i < CbBaud.Items.Count; i++)
            {
                if ((CbBaud.Items[i] as FrameworkElement).Tag?.ToString() == baudStr || 
                    (CbBaud.Items[i] as System.Windows.Controls.ComboBoxItem)?.Content.ToString() == baudStr)
                {
                    CbBaud.SelectedIndex = i;
                    break;
                }
            }

            // Set parity
            if (_config.parity == "O") CbParity.SelectedIndex = 1;
            else if (_config.parity == "N") CbParity.SelectedIndex = 2;
            else CbParity.SelectedIndex = 0;

            TxtTimeout.Text = _config.timeout.ToString("F2");
            TxtMixingSlave.Text = _config.mixing_slave.ToString();
            TxtE5ccSlave.Text = _config.e5cc_slave.ToString();
            ChkUseTcp.IsChecked = isTcp;

            UpdateEnabledStates();
        }

        private void OnUseTcpChanged(object sender, RoutedEventArgs e)
        {
            UpdateEnabledStates();
        }

        private void UpdateEnabledStates()
        {
            if (CbPorts == null || CbBaud == null || CbParity == null || TxtTcpIp == null || TxtTcpPort == null) return;
            
            bool isTcp = ChkUseTcp.IsChecked == true;
            CbPorts.IsEnabled = !isTcp;
            CbBaud.IsEnabled = !isTcp;
            CbParity.IsEnabled = !isTcp;
            TxtTcpIp.IsEnabled = isTcp;
            TxtTcpPort.IsEnabled = isTcp;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChkUseTcp.IsChecked == true)
                {
                    string ip = TxtTcpIp.Text.Trim();
                    string portStr = TxtTcpPort.Text.Trim();
                    if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";
                    if (string.IsNullOrEmpty(portStr)) portStr = "502";
                    _config.port = $"TCP:{ip}:{portStr}";
                }
                else
                {
                    _config.port = CbPorts.SelectedItem?.ToString() ?? "Virtual Sim";
                }
                
                string baudText = (CbBaud.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString();
                if (ParseUtil.TryParseInt(baudText, out int baud))
                {
                    _config.baudrate = baud;
                }

                string parityText = (CbParity.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString();
                if (parityText.Contains("Odd")) _config.parity = "O";
                else if (parityText.Contains("None")) _config.parity = "N";
                else _config.parity = "E";

                if (ParseUtil.TryParseDouble(TxtTimeout.Text, out double timeout))
                {
                    _config.timeout = timeout;
                }

                if (ParseUtil.TryParseInt(TxtMixingSlave.Text, out int mixSlave))
                {
                    _config.mixing_slave = mixSlave;
                }

                if (ParseUtil.TryParseInt(TxtE5ccSlave.Text, out int e5ccSlave))
                {
                    _config.e5cc_slave = e5ccSlave;
                }

                _config.simulation_mode = (_config.port == "Virtual Sim");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid input values: {ex.Message}", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
