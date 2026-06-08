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
            RefreshPortsList();

            // Set current selection to active configuration port
            if (CbPorts.Items.Contains(_config.port))
            {
                CbPorts.SelectedItem = _config.port;
            }
            else
            {
                // If the configured port is not in the list, add it so we don't lose the selection
                if (!string.IsNullOrEmpty(_config.port))
                {
                    CbPorts.Items.Add(_config.port);
                    CbPorts.SelectedItem = _config.port;
                }
                else
                {
                    CbPorts.SelectedIndex = 0;
                }
            }
        }

        private void CbPorts_DropDownOpened(object sender, EventArgs e)
        {
            RefreshPortsList();
        }

        private void RefreshPortsList()
        {
            string currentSelected = CbPorts.SelectedItem?.ToString() ?? _config.port;

            CbPorts.Items.Clear();
            CbPorts.Items.Add("Virtual Sim");

            try
            {
                string[] ports = SerialPort.GetPortNames();
                foreach (string port in ports)
                {
                    if (!CbPorts.Items.Contains(port))
                    {
                        CbPorts.Items.Add(port);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning COM ports: {ex.Message}");
            }

            // Restore selection or select default
            if (!string.IsNullOrEmpty(currentSelected) && CbPorts.Items.Contains(currentSelected))
            {
                CbPorts.SelectedItem = currentSelected;
            }
            else
            {
                CbPorts.SelectedIndex = 0;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string selectedPort = CbPorts.SelectedItem?.ToString() ?? "Virtual Sim";
                _config.port = selectedPort;
                _config.simulation_mode = (selectedPort == "Virtual Sim");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save connection port: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
