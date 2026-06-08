using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Bo_Tron_Khi_CS
{
    public class RecipeStep
    {
        public int Index { get; set; }
        public double Temp { get; set; }
        public double Gas1Ppm { get; set; }
        public double Gas2Ppm { get; set; }
        public double Gas3Ppm { get; set; }
        public int ExposureTime { get; set; }
        public int RecoveryTime { get; set; }
    }

    public class SystemConfig
    {
        public string port { get; set; } = "Virtual Sim";
        public int baudrate { get; set; } = 19200;
        public string parity { get; set; } = "E";
        public double timeout { get; set; } = 0.5;
        public double poll_interval { get; set; } = 0.5;
        public int mixing_slave { get; set; } = 2;
        public int e5cc_slave { get; set; } = 1;
        public bool simulation_mode { get; set; } = true;
        public double total_flow { get; set; } = 400.0;
        public int stable_time { get; set; } = 10;
        public int gas_on_time { get; set; } = 5;
        public double co1 { get; set; } = 1000.0;
        public double co2 { get; set; } = 1000.0;
        public double co3 { get; set; } = 1000.0;
        public double temp_sp { get; set; } = 25.0;
        public double temp_alarm_limit { get; set; } = 50.0;
        public bool temp_alarm_enabled { get; set; } = false;
        public bool temp_auto_stop { get; set; } = false;
        public List<RecipeStep> recipe_steps { get; set; } = new List<RecipeStep>();
        public string carrier_gas { get; set; } = "Air/N2";
        public List<string> gas_names { get; set; } = new List<string> { "CH4", "CO", "NH3", "NO2" };
        public List<string> gas_colors { get; set; } = new List<string> { "#EF4444", "#3B82F6", "#10B981", "#F59E0B" };
        public List<double> bottle_conc { get; set; } = new List<double> { 5000.0, 500.0, 500.0, 100.0 };
        public List<double> mfc_max_sccm { get; set; } = new List<double> { 500.0, 500.0, 50.0, 200.0, 100.0, 100.0 };
        public List<double> mfc_min_v { get; set; } = new List<double> { 0, 0, 0, 0, 0, 0 };
        public List<double> mfc_max_v { get; set; } = new List<double> { 5000, 5000, 5000, 5000, 5000, 5000 };
        public List<double> mfc_factor { get; set; } = new List<double> { 1, 1, 1, 1, 1, 1 };
        public int baseline_time { get; set; } = 60;
        public int exposure_time { get; set; } = 300;
        public int recovery_time { get; set; } = 120;
        public int cycles { get; set; } = 3;
        public List<double> conc_steps { get; set; } = new List<double> { 10.0, 25.0, 50.0, 100.0 };
        public string seq_type { get; set; } = "step";
        public double u_limit_percent { get; set; } = 98.0;
        public double l_limit_percent { get; set; } = 2.0;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gas_mixer_config.json");

        public static SystemConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<SystemConfig>(json) ?? new SystemConfig();
                    if (config.recipe_steps == null || config.recipe_steps.Count == 0)
                    {
                        config.recipe_steps = new List<RecipeStep>
                        {
                            new RecipeStep { Index = 1, Temp = 100.0, Gas1Ppm = 1.0, Gas2Ppm = 1.0, Gas3Ppm = 1.0, ExposureTime = 10, RecoveryTime = 10 },
                            new RecipeStep { Index = 2, Temp = 100.0, Gas1Ppm = 2.0, Gas2Ppm = 2.0, Gas3Ppm = 2.0, ExposureTime = 10, RecoveryTime = 10 },
                            new RecipeStep { Index = 3, Temp = 100.0, Gas1Ppm = 2.9, Gas2Ppm = 3.0, Gas3Ppm = 3.0, ExposureTime = 10, RecoveryTime = 10 }
                        };
                        config.Save(); // Save default steps to file
                    }
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }
            
            var defConfig = new SystemConfig();
            defConfig.recipe_steps = new List<RecipeStep>
            {
                new RecipeStep { Index = 1, Temp = 100.0, Gas1Ppm = 1.0, Gas2Ppm = 1.0, Gas3Ppm = 1.0, ExposureTime = 10, RecoveryTime = 10 },
                new RecipeStep { Index = 2, Temp = 100.0, Gas1Ppm = 2.0, Gas2Ppm = 2.0, Gas3Ppm = 2.0, ExposureTime = 10, RecoveryTime = 10 },
                new RecipeStep { Index = 3, Temp = 100.0, Gas1Ppm = 2.9, Gas2Ppm = 3.0, Gas3Ppm = 3.0, ExposureTime = 10, RecoveryTime = 10 }
            };
            return defConfig;
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }

    public static class ParseUtil
    {
        public static double ParseDouble(string text, double defaultVal = 0.0)
        {
            if (string.IsNullOrEmpty(text)) return defaultVal;
            text = text.Replace(',', '.').Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return defaultVal;
        }

        public static float ParseFloat(string text, float defaultVal = 0.0f)
        {
            if (string.IsNullOrEmpty(text)) return defaultVal;
            text = text.Replace(',', '.').Trim();
            if (float.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return defaultVal;
        }

        public static int ParseInt(string text, int defaultVal = 0)
        {
            if (string.IsNullOrEmpty(text)) return defaultVal;
            if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int val))
            {
                return val;
            }
            return defaultVal;
        }

        public static ushort ParseUshort(string text, ushort defaultVal = 0)
        {
            if (string.IsNullOrEmpty(text)) return defaultVal;
            if (ushort.TryParse(text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out ushort val))
            {
                return val;
            }
            return defaultVal;
        }
        
        public static bool TryParseDouble(string text, out double result)
        {
            result = 0.0;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Replace(',', '.').Trim();
            return double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        public static bool TryParseInt(string text, out int result)
        {
            result = 0;
            if (string.IsNullOrEmpty(text)) return false;
            return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
    }
}
