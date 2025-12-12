using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    public static class FenLogger
    {
        // Wrapper for LogManager to maintain existing API
        
        public static bool IsEnabled 
        { 
            get => true; // Managed by LogManager
            set => LogManager.Initialize(value, LogCategory.All, LogLevel.Info); // Basic forward
        }

        public static void Initialize(string logFilePath)
        {
            // LogManager handles its own path, but we can log the attempt
            LogManager.Log(LogCategory.General, LogLevel.Info, $"Legacy FenLogger.Initialize called with: {logFilePath}");
        }

        public static void Log(string message, LogCategory category = LogCategory.General, LogLevel level = LogLevel.Info)
        {
            LogManager.Log(category, level, message);
        }

        public static void Debug(string message, LogCategory category = LogCategory.General) 
            => LogManager.Log(category, LogLevel.Debug, message);
            
        public static void Info(string message, LogCategory category = LogCategory.General) 
            => LogManager.Log(category, LogLevel.Info, message);
            
        public static void Warn(string message, LogCategory category = LogCategory.General) 
            => LogManager.Log(category, LogLevel.Warn, message);
            
        public static void Error(string message, LogCategory category = LogCategory.General, Exception ex = null) 
            => LogManager.LogError(category, message, ex);
    }
}
