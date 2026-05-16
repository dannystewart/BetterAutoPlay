using System;
using System.IO;
using BepInEx.Logging;

namespace BetterAutoPlay
{
    internal static class DevLog
    {
        private const string MarkerFileName = "betterautoplay.devlog";
        private const string FullMarkerFileName = "betterautoplay.devlog.full";
        private const string LogFileName = "betterautoplay.dev.log";
        private static ManualLogSource s_log;
        private static string s_logFilePath;
        public static bool Enabled { get; private set; }
        public static bool FullEnabled { get; private set; }

        public static void Initialize(ManualLogSource log)
        {
            s_log = log;
            try
            {
                string asmPath = typeof(BetterAutoPlayPlugin).Assembly.Location;
                string dir = Path.GetDirectoryName(asmPath) ?? string.Empty;
                string markerPath = Path.Combine(dir, MarkerFileName);
                string fullMarkerPath = Path.Combine(dir, FullMarkerFileName);
                s_logFilePath = Path.Combine(dir, LogFileName);
                Enabled = File.Exists(markerPath);
                FullEnabled = Enabled && File.Exists(fullMarkerPath);
                if (Enabled)
                {
                    s_log.LogWarning("[DEVLOG] Enabled. Marker found: " + markerPath);
                    if (FullEnabled)
                        s_log.LogWarning("[DEVLOG] Full mode enabled. Marker found: " + fullMarkerPath);
                    AppendToFile("=== DEVLOG START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ===");
                }
            }
            catch (Exception ex)
            {
                Enabled = false;
                s_log?.LogWarning("[DEVLOG] Initialization failed: " + ex.Message);
            }
        }

        public static void Info(string message)
        {
            if (!Enabled || s_log == null)
                return;
            string line = "[DEVLOG] " + message;
            s_log.LogInfo(line);
            AppendToFile(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
        }

        private static void AppendToFile(string line)
        {
            if (string.IsNullOrEmpty(s_logFilePath))
                return;
            try
            {
                File.AppendAllText(s_logFilePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                s_log?.LogWarning("[DEVLOG] File write failed: " + ex.Message);
            }
        }
    }
}
