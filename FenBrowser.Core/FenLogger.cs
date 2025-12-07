using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    public static class FenLogger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        private static bool _initialized = false;
        
        // Configuration
        public static bool IsEnabled { get; set; } = true;
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public static void Initialize(string logFilePath)
        {
            try
            {
                lock (_lock)
                {
                    _logFilePath = logFilePath;
                    var dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    
                    // Create or overwrite log file with header
                    File.WriteAllText(_logFilePath, $"[FenLogger] Initialized at {DateTime.Now}\r\n");
                    _initialized = true;
                }
            }
            catch (Exception ex)
            {
                // Last resort console fallback
                Console.WriteLine($"[FenLogger] Failed to initialize: {ex.Message}");
            }
        }

        private static bool IsLevelEnabled(LogLevel level)
        {
            // LogLevel order in Core.Logging: Error=0, Warn=1, Info=2, Debug=3, Trace=4
            // We want to log if level <= MinimumLevel.
            // E.g. Min=Info(2). Error(0) logged. Debug(3) skipped?
            // Wait, LogLevel convention usually: Error < Info < Debug.
            // If Min=Info, we want Error & Info. So level <= Min?
            // Yes.
            return (int)level <= (int)MinimumLevel;
        }

        public static void Log(string message, LogCategory category = LogCategory.General, LogLevel level = LogLevel.Info)
        {
            if (!IsEnabled || !IsLevelEnabled(level) || !_initialized || string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logLine = $"[{timestamp}] [{level.ToString().ToUpper()}] [{category}] {message}\r\n";

                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, logLine);
                }
            }
            catch
            {
                // Silently fail to prevent app crash
            }
        }

        public static void Debug(string message, LogCategory category = LogCategory.General) => Log(message, category, LogLevel.Debug);
        public static void Info(string message, LogCategory category = LogCategory.General) => Log(message, category, LogLevel.Info);
        public static void Warn(string message, LogCategory category = LogCategory.General) => Log(message, category, LogLevel.Warn);
        public static void Error(string message, LogCategory category = LogCategory.General, Exception ex = null)
        {
            string fullMessage = message;
            if (ex != null)
            {
                fullMessage += $" | Exception: {ex.Message}\nStack: {ex.StackTrace}";
            }
            Log(fullMessage, category, LogLevel.Error);
        }
    }
}
