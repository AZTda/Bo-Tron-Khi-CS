using System;
using System.IO;
using System.Text;

namespace Bo_Tron_Khi_CS
{
    public class Logger
    {
        private string _filePath;
        private DateTime _startTime;
        private readonly object _lock = new object();

        public bool IsLogging => !string.IsNullOrEmpty(_filePath);

        public void StartNewLog()
        {
            lock (_lock)
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string logDir = Path.Combine(exeDir, "logs");
                Directory.CreateDirectory(logDir);

                _startTime = DateTime.Now;
                string fileName = $"log_sensor_{_startTime:yyyyMMdd_HHmmss}.csv";
                _filePath = Path.Combine(logDir, fileName);

                // Write CSV header
                var sb = new StringBuilder();
                sb.Append("Time(s),Temp_SP,Temp_PV,MV_Percent,Relay1,Relay2,");
                sb.Append("MFC1_SP,MFC1_PV,MFC2_SP,MFC2_PV,MFC3_SP,MFC3_PV,");
                sb.Append("MFC4_SP,MFC4_PV,MFC5_SP,MFC5_PV,MFC6_SP,MFC6_PV,");
                sb.Append("Gas1_Conc,Gas2_Conc,Gas3_Conc");

                File.WriteAllText(_filePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }

        public void LogRow(PolledData data, double gas1, double gas2, double gas3)
        {
            lock (_lock)
            {
                if (!IsLogging) return;

                double seconds = (DateTime.Now - _startTime).TotalSeconds;
                var sb = new StringBuilder();
                sb.Append($"{seconds:F1},{data.E5ccSP:F1},{data.E5ccPV:F1},{data.E5ccMV:F1},{data.Relay1},{data.Relay2},");
                for (int i = 0; i < 6; i++)
                {
                    sb.Append($"{data.SccmSP[i]:F2},{data.SccmPV[i]:F2},");
                }
                sb.Append($"{gas1:F2},{gas2:F2},{gas3:F2}");

                try
                {
                    File.AppendAllText(_filePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing log: {ex.Message}");
                }
            }
        }

        public void StopLog()
        {
            lock (_lock)
            {
                _filePath = null;
            }
        }
    }
}
