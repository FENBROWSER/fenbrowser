using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private static string _basePath = GetDefaultBasePath();
        
        /// <summary>
        /// Initialize the structured logger with optional base path.
        /// </summary>
        public static void Initialize(string basePath = null)
        {
            _basePath = !string.IsNullOrWhiteSpace(basePath)
                ? basePath
                : GetDefaultBasePath();
            
            try
            {
                if (!Directory.Exists(_basePath))
                {
                    Directory.CreateDirectory(_basePath);
                }
            }
            catch { }
            
            // Enable default modules and set files
            EnableModule("Layout");
            SetModuleFile("Layout", "layout.log");
            
            EnableModule("CSS");
            SetModuleFile("CSS", "css.log");
            
            EnableModule("Rendering");
            SetModuleFile("Rendering", "rendering.log");
            
            EnableModule("JavaScript");
            SetModuleFile("JavaScript", "javascript.log");
            
            EnableModule("Network");
            SetModuleFile("Network", "network.log");
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
        
        /// <summary>
        /// Dump raw HTML source fetched from network (CURL-like).
        /// </summary>
        public static string DumpRawSource(string url, string htmlContent)
        {
            if (!_globalEnabled) return null;
            
            try
            {
                var fileName = $"raw_source_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                var filePath = Path.Combine(_basePath, fileName);
                File.WriteAllText(filePath, $"<!-- URL: {url} -->\n<!-- Type: Network Fetch (Raw) -->\n{htmlContent ?? string.Empty}");
                Info("Network", $"Raw network source dumped to: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Error("Network", "Failed to dump raw source", ex);
                return null;
            }
        }

        /// <summary>
        /// Dump the engine's processed DOM source (Normalized HTML).
        /// </summary>
        public static string DumpEngineSource(string url, string htmlContent)
        {
            if (!_globalEnabled) return null;

            try
            {
                var fileName = $"engine_source_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                var filePath = Path.Combine(_basePath, fileName);
                File.WriteAllText(filePath, $"<!-- URL: {url} -->\n<!-- Type: Fen Engine Processed DOM -->\n{htmlContent ?? string.Empty}");
                Info("Rendering", $"Engine source dumped to: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Error("Rendering", "Failed to dump engine source", ex);
                return null;
            }
        }

        /// <summary>
        /// Dump the final rendered text content to a file. Returns the absolute path.
        /// </summary>
        public static string DumpRenderedText(string url, string textContent)
        {
            if (!_globalEnabled) return null;

            try
            {
                var fileName = $"rendered_text_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var filePath = Path.Combine(_basePath, fileName);
                string normalizedText = NormalizeRenderedText(textContent);
                File.WriteAllText(
                    filePath,
                    $"URL: {url}\nDumped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n----------------------------------\n{normalizedText}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Info("Rendering", $"Rendered text dumped to: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Error("Rendering", "Failed to dump rendered text", ex);
                return null;
            }
        }

        private static string NormalizeRenderedText(string textContent)
        {
            if (string.IsNullOrEmpty(textContent))
            {
                return string.Empty;
            }

            return textContent
                .Replace("\u00E2\u0153\u201C", "\u2713", StringComparison.Ordinal)
                .Replace("\u00E2\u0153\u201D", "\u2714", StringComparison.Ordinal)
                .Replace("\u00E2\u0153\u02DC", "\u2718", StringComparison.Ordinal);
        }
        
        /// <summary>
        /// Dump computed styles for an element to JSON format.
        /// </summary>
        public static void LogComputedStyle(string selector, string tag, string display, 
            double? fontSize, double? width, double? height, 
            string padding, string margin, string boxInfo)
        {
            if (!IsModuleEnabled("CSS")) return;
            
            var log = $"[CSS-DIAG] {selector} <{tag}> Display={display ?? "null"} " +
                      $"FontSize={fontSize?.ToString("F1") ?? "null"} " +
                      $"W={width?.ToString("F0") ?? "auto"} H={height?.ToString("F0") ?? "auto"} " +
                      $"Padding={padding} Margin={margin} Box={boxInfo}";
            
            Debug("CSS", log);
        }
        
        /// <summary>
        /// Log element box model calculation.
        /// </summary>
        public static void LogBoxModel(string elementId, string tag, 
            float contentW, float contentH, 
            float paddingTop, float paddingRight, float paddingBottom, float paddingLeft,
            float marginTop, float marginRight, float marginBottom, float marginLeft)
        {
            if (!IsModuleEnabled("Layout")) return;
            
            var log = $"[BOX] {elementId}#{tag} Content={contentW:F0}x{contentH:F0} " +
                      $"Padding=[{paddingTop:F0},{paddingRight:F0},{paddingBottom:F0},{paddingLeft:F0}] " +
                      $"Margin=[{marginTop:F0},{marginRight:F0},{marginBottom:F0},{marginLeft:F0}]";
            
            Debug("Layout", log);
        }

        private static string GetDefaultBasePath()
        {
            try
            {
                var diagnosticsLogs = DiagnosticPaths.GetLogsDirectory();
                if (!string.IsNullOrWhiteSpace(diagnosticsLogs))
                {
                    return diagnosticsLogs;
                }
            }
            catch
            {
                // Fall back to AppContext below.
            }

            return Path.Combine(AppContext.BaseDirectory, "logs");
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
