using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Structured logger with per-module sinks and runtime toggle.
    /// Wraps LogManager with simpler API and module-specific controls.
    /// </summary>
    public static class StructuredLogger
    {
        // Per-module enable flags (all true by default)
        private static readonly Dictionary<string, bool> _moduleEnabled = new Dictionary<string, bool>();
        
        // Per-module log files (optional separate files)
        private static readonly Dictionary<string, string> _moduleFiles = new Dictionary<string, string>();
        
        // Master toggle
        private static bool _globalEnabled = true;
        
        // Log file base path
        private static string _basePath = @"C:\Users\udayk\Videos\FENBROWSER\logs";
        
        /// <summary>
        /// Initialize the structured logger with optional base path.
        /// </summary>
        public static void Initialize(string basePath = null)
        {
            if (!string.IsNullOrEmpty(basePath))
            {
                _basePath = basePath;
            }
            
            try
            {
                if (!Directory.Exists(_basePath))
                {
                    Directory.CreateDirectory(_basePath);
                }
            }
            catch { }
            
            // Enable default modules
            EnableModule("Layout");
            EnableModule("CSS");
            EnableModule("Rendering");
            EnableModule("JavaScript");
            EnableModule("Network");
        }
        
        /// <summary>
        /// Global toggle - turns all logging on/off instantly.
        /// </summary>
        public static bool GlobalEnabled
        {
            get => _globalEnabled;
            set
            {
                _globalEnabled = value;
                LogManager.Initialize(value, LogCategory.All, value ? LogLevel.Debug : LogLevel.Error);
            }
        }
        
        /// <summary>
        /// Enable a specific module's logging.
        /// </summary>
        public static void EnableModule(string moduleName)
        {
            _moduleEnabled[moduleName] = true;
        }
        
        /// <summary>
        /// Disable a specific module's logging.
        /// </summary>
        public static void DisableModule(string moduleName)
        {
            _moduleEnabled[moduleName] = false;
        }
        
        /// <summary>
        /// Check if a module is enabled.
        /// </summary>
        public static bool IsModuleEnabled(string moduleName)
        {
            if (!_globalEnabled) return false;
            return _moduleEnabled.TryGetValue(moduleName, out var enabled) ? enabled : true;
        }
        
        /// <summary>
        /// Set up a separate log file for a module.
        /// </summary>
        public static void SetModuleFile(string moduleName, string fileName)
        {
            _moduleFiles[moduleName] = Path.Combine(_basePath, fileName);
        }
        
        /// <summary>
        /// Log a debug message for a module.
        /// </summary>
        public static void Debug(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            
            var fullMessage = $"[{module}] {message}";
            LogManager.Log(GetCategory(module), LogLevel.Debug, fullMessage);
            WriteToModuleFile(module, "DEBUG", message);
        }
        
        /// <summary>
        /// Log an info message for a module.
        /// </summary>
        public static void Info(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            
            var fullMessage = $"[{module}] {message}";
            LogManager.Log(GetCategory(module), LogLevel.Info, fullMessage);
            WriteToModuleFile(module, "INFO", message);
        }
        
        /// <summary>
        /// Log a warning for a module.
        /// </summary>
        public static void Warn(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            
            var fullMessage = $"[{module}] {message}";
            LogManager.Log(GetCategory(module), LogLevel.Warn, fullMessage);
            WriteToModuleFile(module, "WARN", message);
        }
        
        /// <summary>
        /// Log an error for a module.
        /// </summary>
        public static void Error(string module, string message, Exception ex = null)
        {
            // Errors always log
            var fullMessage = $"[{module}] {message}";
            if (ex != null) fullMessage += $"\n{ex}";
            LogManager.LogError(GetCategory(module), fullMessage, ex);
            WriteToModuleFile(module, "ERROR", message + (ex != null ? $"\n{ex}" : ""));
        }
        
        /// <summary>
        /// Log performance timing for a module.
        /// </summary>
        public static void Perf(string module, string operation, long milliseconds)
        {
            if (!IsModuleEnabled(module)) return;
            
            LogManager.LogPerf(GetCategory(module), $"[{module}] {operation}", milliseconds);
            WriteToModuleFile(module, "PERF", $"{operation}: {milliseconds}ms");
        }
        
        /// <summary>
        /// Create a scoped logger for a specific module.
        /// </summary>
        public static ModuleLogger ForModule(string moduleName)
        {
            return new ModuleLogger(moduleName);
        }
        
        /// <summary>
        /// Get all enabled modules.
        /// </summary>
        public static List<string> GetEnabledModules()
        {
            var result = new List<string>();
            foreach (var kvp in _moduleEnabled)
            {
                if (kvp.Value) result.Add(kvp.Key);
            }
            return result;
        }
        
        /// <summary>
        /// Toggle a module on/off.
        /// </summary>
        public static void ToggleModule(string moduleName)
        {
            if (_moduleEnabled.TryGetValue(moduleName, out var enabled))
            {
                _moduleEnabled[moduleName] = !enabled;
            }
            else
            {
                _moduleEnabled[moduleName] = false;
            }
        }
        
        private static LogCategory GetCategory(string module)
        {
            return module.ToUpperInvariant() switch
            {
                "LAYOUT" => LogCategory.Layout,
                "CSS" => LogCategory.CSS,
                "RENDERING" => LogCategory.Rendering,
                "JAVASCRIPT" => LogCategory.JavaScript,
                "NETWORK" => LogCategory.Network,
                "HTML" => LogCategory.HtmlParsing,
                _ => LogCategory.General
            };
        }
        
        private static void WriteToModuleFile(string module, string level, string message)
        {
            if (!_moduleFiles.TryGetValue(module, out var filePath)) return;
            
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            catch { }
        }
    }
    
    /// <summary>
    /// Scoped logger for a specific module.
    /// </summary>
    public class ModuleLogger
    {
        private readonly string _module;
        
        public ModuleLogger(string module)
        {
            _module = module;
        }
        
        public void Debug(string message) => StructuredLogger.Debug(_module, message);
        public void Info(string message) => StructuredLogger.Info(_module, message);
        public void Warn(string message) => StructuredLogger.Warn(_module, message);
        public void Error(string message, Exception ex = null) => StructuredLogger.Error(_module, message, ex);
        public void Perf(string operation, long ms) => StructuredLogger.Perf(_module, operation, ms);
        
        public bool IsEnabled => StructuredLogger.IsModuleEnabled(_module);
    }
}
