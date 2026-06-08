using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using MiniExcelLibs;

namespace Bo_Tron_Khi_CS
{
    public partial class AutoTableWindow : Window
    {
        private readonly MainWindow _main;
        private readonly SystemConfig _config;
        private bool _isRecalculating = false;

        public AutoTableWindow(MainWindow main)
        {
            _main = main;
            _config = main._config;
            _isRecalculating = true; // Prevent text changed events during component initialization

            InitializeComponent();

            LoadCurrentSettings();
            _isRecalculating = false;

            DgridRecipe.ItemsSource = _main._recipeSteps;
        }

        private void LoadCurrentSettings()
        {
            TxtStableTime.Text = _config.stable_time.ToString();
            TxtTotalFlow.Text = _config.total_flow.ToString("F1");
            TxtGasOn.Text = _config.gas_on_time.ToString();
            TxtCo1.Text = _config.co1.ToString("F1");
            TxtCo2.Text = _config.co2.ToString("F1");
            TxtCo3.Text = _config.co3.ToString("F1");
            TxtNumPoint.Text = _main._recipeSteps.Count.ToString();

            RecalcRanges();
        }

        private void OnConfigTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isRecalculating || _config == null || LblRangesInfo == null) return;
            RecalcRanges();
        }

        private void OnNumPointTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isRecalculating) return;

            if (ParseUtil.TryParseInt(TxtNumPoint.Text, out int n) && n >= 0)
            {
                var steps = _main._recipeSteps;
                if (steps.Count == n) return;

                _isRecalculating = true;
                try
                {
                    if (steps.Count < n)
                    {
                        while (steps.Count < n)
                        {
                            steps.Add(new RecipeStep
                            {
                                Index = steps.Count + 1,
                                Temp = 100.0,
                                Gas1Ppm = 1.0,
                                Gas2Ppm = 1.0,
                                Gas3Ppm = 1.0,
                                ExposureTime = 10,
                                RecoveryTime = 10
                            });
                        }
                    }
                    else if (steps.Count > n)
                    {
                        while (steps.Count > n)
                        {
                            steps.RemoveAt(steps.Count - 1);
                        }
                    }

                    DgridRecipe.Items.Refresh();
                    _main.RefreshRecipeGrid();
                }
                finally
                {
                    _isRecalculating = false;
                }
            }
        }

        private void RecalcRanges()
        {
            if (_isRecalculating || _config == null || LblRangesInfo == null) return;
            _isRecalculating = true;
            try
            {
                if (!ParseUtil.TryParseDouble(TxtTotalFlow.Text, out double tot) || tot <= 0)
                {
                    _isRecalculating = false;
                    return;
                }
                if (!ParseUtil.TryParseDouble(TxtCo1.Text, out double co1))
                {
                    _isRecalculating = false;
                    return;
                }
                if (!ParseUtil.TryParseDouble(TxtCo2.Text, out double co2))
                {
                    _isRecalculating = false;
                    return;
                }
                if (!ParseUtil.TryParseDouble(TxtCo3.Text, out double co3))
                {
                    _isRecalculating = false;
                    return;
                }

                double maxTot = Math.Min(_config.mfc_max_sccm[0], _config.mfc_max_sccm[1]);
                if (tot > maxTot)
                {
                    tot = maxTot;
                    TxtTotalFlow.Text = tot.ToString("F1");
                }

                // Update configuration values dynamically
                _config.total_flow = tot;
                _config.co1 = co1;
                _config.co2 = co2;
                _config.co3 = co3;
                _config.Save();
                _main.SyncConfigToUI();

                double uLimit = _config.u_limit_percent / 100.0;
                double lLimit = _config.l_limit_percent / 100.0;

                double maxMfc3 = _config.mfc_max_sccm[2];
                double maxMfc4 = _config.mfc_max_sccm[3];
                double maxMfc5 = _config.mfc_max_sccm[4];
                double maxMfc6 = _config.mfc_max_sccm[5];

                double minQ1 = lLimit * maxMfc3;
                double maxQ1 = Math.Min(uLimit * maxMfc4, tot);
                double minG1 = minQ1 / tot * co1;
                double maxG1 = maxQ1 / tot * co1;

                double minQ2 = lLimit * maxMfc5;
                double maxQ2 = Math.Min(uLimit * maxMfc5, tot);
                double minG2 = minQ2 / tot * co2;
                double maxG2 = maxQ2 / tot * co2;

                double minQ3 = lLimit * maxMfc6;
                double maxQ3 = Math.Min(uLimit * maxMfc6, tot);
                double minG3 = minQ3 / tot * co3;
                double maxG3 = maxQ3 / tot * co3;

                LblRangesInfo.Text = $"Recommended ppm range: Gas 1: {minG1:F1} - {maxG1:F1} ppm | Gas 2: {minG2:F1} - {maxG2:F1} ppm | Gas 3: {minG3:F1} - {maxG3:F1} ppm";
            }
            catch
            {
                if (LblRangesInfo != null)
                {
                    LblRangesInfo.Text = "Recommended ppm range: calculation error";
                }
            }
            finally
            {
                _isRecalculating = false;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ParseUtil.TryParseInt(TxtStableTime.Text, out int st)) _config.stable_time = st;
                if (ParseUtil.TryParseDouble(TxtTotalFlow.Text, out double tf)) _config.total_flow = tf;
                if (ParseUtil.TryParseInt(TxtGasOn.Text, out int go)) _config.gas_on_time = go;
                if (ParseUtil.TryParseDouble(TxtCo1.Text, out double c1)) _config.co1 = c1;
                if (ParseUtil.TryParseDouble(TxtCo2.Text, out double c2)) _config.co2 = c2;
                if (ParseUtil.TryParseDouble(TxtCo3.Text, out double c3)) _config.co3 = c3;

                _config.recipe_steps = _main._recipeSteps;
                _config.Save();

                // Sync inputs back to main window UI textboxes
                _main.SyncConfigToUI();

                MessageBox.Show("Configuration saved to JSON successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save config: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnImportExcelClick(object sender, RoutedEventArgs e)
        {
            var openDlg = new OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                Title = "Select Recipe Step File"
            };

            if (openDlg.ShowDialog() == true)
            {
                try
                {
                    _main._recipeSteps.Clear();
                    var rows = MiniExcel.Query(openDlg.FileName).ToList();
                    for (int i = 1; i < rows.Count; i++)
                    {
                        var row = rows[i] as IDictionary<string, object>;
                        if (row == null) continue;

                        var keys = row.Keys.ToList();
                        var step = new RecipeStep
                        {
                            Index = i,
                            Temp = Convert.ToDouble(row[keys[1]]),
                            Gas1Ppm = Convert.ToDouble(row[keys[2]]),
                            Gas2Ppm = Convert.ToDouble(row[keys[3]]),
                            Gas3Ppm = Convert.ToDouble(row[keys[4]]),
                            ExposureTime = Convert.ToInt32(row[keys[5]]),
                            RecoveryTime = Convert.ToInt32(row[keys[6]])
                        };
                        _main._recipeSteps.Add(step);
                    }

                    DgridRecipe.Items.Refresh();
                    TxtNumPoint.Text = _main._recipeSteps.Count.ToString();
                    _main.RefreshRecipeGrid();
                    MessageBox.Show($"Loaded {_main._recipeSteps.Count} steps successfully!", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to read recipe file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnAddStepClick(object sender, RoutedEventArgs e)
        {
            int idx = _main._recipeSteps.Count + 1;
            var step = new RecipeStep
            {
                Index = idx,
                Temp = 25.0,
                Gas1Ppm = 10.0,
                Gas2Ppm = 10.0,
                Gas3Ppm = 10.0,
                ExposureTime = 120,
                RecoveryTime = 120
            };
            _main._recipeSteps.Add(step);
            DgridRecipe.Items.Refresh();
            TxtNumPoint.Text = _main._recipeSteps.Count.ToString();
            _main.RefreshRecipeGrid();
        }

        private void OnDeleteStepClick(object sender, RoutedEventArgs e)
        {
            if (DgridRecipe.SelectedItem is RecipeStep selected)
            {
                _main._recipeSteps.Remove(selected);
                for (int i = 0; i < _main._recipeSteps.Count; i++)
                {
                    _main._recipeSteps[i].Index = i + 1;
                }
                DgridRecipe.Items.Refresh();
                TxtNumPoint.Text = _main._recipeSteps.Count.ToString();
                _main.RefreshRecipeGrid();
            }
        }

        private void OnSyncClick(object sender, RoutedEventArgs e)
        {
            if (!_main._handler.IsConnected)
            {
                MessageBox.Show("Device not connected. Please connect Modbus before syncing.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 1. Save current UI values to config first
                if (ParseUtil.TryParseInt(TxtStableTime.Text, out int st)) _config.stable_time = st;
                if (ParseUtil.TryParseDouble(TxtTotalFlow.Text, out double tf)) _config.total_flow = tf;
                if (ParseUtil.TryParseInt(TxtGasOn.Text, out int go)) _config.gas_on_time = go;
                if (ParseUtil.TryParseDouble(TxtCo1.Text, out double c1)) _config.co1 = c1;
                if (ParseUtil.TryParseDouble(TxtCo2.Text, out double c2)) _config.co2 = c2;
                if (ParseUtil.TryParseDouble(TxtCo3.Text, out double c3)) _config.co3 = c3;

                _config.recipe_steps = _main._recipeSteps;
                _config.Save();
                _main.SyncConfigToUI();

                byte ms = (byte)_config.mixing_slave;
                int failCount = 0;

                // 2. Sync concentration configuration parameters
                ushort[] stableRegs = ModbusHandler.FloatToRegs((float)_config.stable_time);
                var resStable = _main._handler.TryWriteMultipleRegisters(ms, 242, stableRegs);
                if (!resStable.Success) failCount++;

                ushort[] totalFlowRegs = ModbusHandler.FloatToRegs((float)_config.total_flow);
                var resTotal = _main._handler.TryWriteMultipleRegisters(ms, 244, totalFlowRegs);
                if (!resTotal.Success) failCount++;

                ushort[] gasOnRegs = ModbusHandler.FloatToRegs((float)_config.gas_on_time);
                var resGasOn = _main._handler.TryWriteMultipleRegisters(ms, 246, gasOnRegs);
                if (!resGasOn.Success) failCount++;

                ushort[] co1Regs = ModbusHandler.FloatToRegs((float)_config.co1);
                var resCo1 = _main._handler.TryWriteMultipleRegisters(ms, 248, co1Regs);
                if (!resCo1.Success) failCount++;

                ushort[] co2Regs = ModbusHandler.FloatToRegs((float)_config.co2);
                var resCo2 = _main._handler.TryWriteMultipleRegisters(ms, 250, co2Regs);
                if (!resCo2.Success) failCount++;

                ushort[] co3Regs = ModbusHandler.FloatToRegs((float)_config.co3);
                var resCo3 = _main._handler.TryWriteMultipleRegisters(ms, 252, co3Regs);
                if (!resCo3.Success) failCount++;

                // Trigger Save Concentration Config to EEPROM (register 254)
                var saveConcResult = _main._handler.TryWriteSingleRegister(ms, 254, 1);
                if (!saveConcResult.Success) failCount++;

                // 3. Sync Recipe Table Steps to address 50 in chunks of 5 steps
                int totalSteps = _main._recipeSteps.Count;
                // If totalSteps > 200, clamp it to 200 (TABLE_SIZE limit)
                if (totalSteps > 200) totalSteps = 200;

                for (int pos = 0; pos < totalSteps; pos += 5)
                {
                    // 62 registers: totalPoint (1), pos (1), 5 items * 12 regs (60)
                    ushort[] chunkRegs = new ushort[62];
                    chunkRegs[0] = (ushort)totalSteps;
                    chunkRegs[1] = (ushort)pos;

                    for (int j = 0; j < 5; j++)
                    {
                        int stepIdx = pos + j;
                        int regBase = 2 + j * 12;

                        if (stepIdx < totalSteps)
                        {
                            var step = _main._recipeSteps[stepIdx];

                            // tempSet (int32 -> 2 regs)
                            int tempVal = (int)Math.Round(step.Temp);
                            chunkRegs[regBase] = (ushort)(tempVal & 0xFFFF);
                            chunkRegs[regBase + 1] = (ushort)((tempVal >> 16) & 0xFFFF);

                            // gas1 (float -> 2 regs)
                            ushort[] g1 = ModbusHandler.FloatToRegs((float)step.Gas1Ppm);
                            chunkRegs[regBase + 2] = g1[0];
                            chunkRegs[regBase + 3] = g1[1];

                            // gas2 (float -> 2 regs)
                            ushort[] g2 = ModbusHandler.FloatToRegs((float)step.Gas2Ppm);
                            chunkRegs[regBase + 4] = g2[0];
                            chunkRegs[regBase + 5] = g2[1];

                            // gas3 (float -> 2 regs)
                            ushort[] g3 = ModbusHandler.FloatToRegs((float)step.Gas3Ppm);
                            chunkRegs[regBase + 6] = g3[0];
                            chunkRegs[regBase + 7] = g3[1];

                            // respTime (int32 -> 2 regs)
                            int respVal = (int)step.ExposureTime;
                            chunkRegs[regBase + 8] = (ushort)(respVal & 0xFFFF);
                            chunkRegs[regBase + 9] = (ushort)((respVal >> 16) & 0xFFFF);

                            // recTime (int32 -> 2 regs)
                            int recVal = (int)step.RecoveryTime;
                            chunkRegs[regBase + 10] = (ushort)(recVal & 0xFFFF);
                            chunkRegs[regBase + 11] = (ushort)((recVal >> 16) & 0xFFFF);
                        }
                        else
                        {
                            // Pad remaining items with zeros
                            for (int r = 0; r < 12; r++)
                            {
                                chunkRegs[regBase + r] = 0;
                            }
                        }
                    }

                    var resTable = _main._handler.TryWriteMultipleRegisters(ms, 50, chunkRegs);
                    if (!resTable.Success) failCount++;
                }

                // Trigger Save Auto Table to EEPROM (register 269)
                var saveTableResult = _main._handler.TryWriteSingleRegister(ms, 269, 1);
                if (!saveTableResult.Success) failCount++;

                if (failCount == 0)
                {
                    MessageBox.Show("Recipe configuration and step table synced to HMI successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Sync completed with errors. Warning: {failCount} write operation(s) failed during recipe sync.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Recipe sync failed: {ex.Message}", "Sync Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStartAutoClick(object sender, RoutedEventArgs e)
        {
            _main.StartAutoRecipe();
            Close();
        }

        private void OnStopAutoClick(object sender, RoutedEventArgs e)
        {
            _main.StopAutoRecipe();
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
