using System;
using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.Core.Logging
{
    // Compatibility facade preserved for existing call sites.
    // All writes route through the new EngineLog runtime via FenLogger/EngineLogCompat.
    public static class StructuredLogger
    {
        private static readonly Dictionary<string, bool> ModuleEnabled = new(StringComparer.OrdinalIgnoreCase);
        private static bool _globalEnabled = true;

        public static void Initialize(string basePath = null)
        {
            // No-op initialization for compatibility.
            _globalEnabled = true;
            EngineLogCompat.IsEnabled = true;
        }

        public static bool GlobalEnabled
        {
            get => _globalEnabled;
            set
            {
                _globalEnabled = value;
                EngineLogCompat.IsEnabled = value;
            }
        }

        public static void EnableModule(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return;
            ModuleEnabled[moduleName] = true;
        }

        public static void DisableModule(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return;
            ModuleEnabled[moduleName] = false;
        }

        public static bool IsModuleEnabled(string moduleName)
        {
            if (!_globalEnabled)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return true;
            }

            return !ModuleEnabled.TryGetValue(moduleName, out var enabled) || enabled;
        }

        public static void SetModuleFile(string moduleName, string fileName)
        {
            // No-op: per-module file fan-out was removed with legacy logger backend.
        }

        public static void Debug(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            EngineLogCompat.Debug($"[{module}] {message}", GetCategory(module));
        }

        public static void Info(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            EngineLogCompat.Info($"[{module}] {message}", GetCategory(module));
        }

        public static void Warn(string module, string message)
        {
            if (!IsModuleEnabled(module)) return;
            EngineLogCompat.Warn($"[{module}] {message}", GetCategory(module));
        }

        public static void Error(string module, string message, Exception ex = null)
        {
            EngineLogCompat.Error($"[{module}] {message}", GetCategory(module), ex);
        }

        public static void Perf(string module, string operation, long milliseconds)
        {
            if (!IsModuleEnabled(module)) return;
            EngineLogCompat.LogMetric($"[{module}] {operation}", milliseconds, "ms", GetCategory(module));
        }

        public static ModuleLogger ForModule(string moduleName)
        {
            return new ModuleLogger(moduleName);
        }

        public static List<string> GetEnabledModules()
        {
            var result = new List<string>();
            foreach (var pair in ModuleEnabled)
            {
                if (pair.Value)
                {
                    result.Add(pair.Key);
                }
            }

            return result;
        }

        public static void ToggleModule(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return;
            ModuleEnabled[moduleName] = !IsModuleEnabled(moduleName);
        }

        public static string DumpRawSource(string url, string htmlContent)
            => EngineLogCompat.DumpRawSource(url, htmlContent);

        public static string DumpEngineSource(string url, string htmlContent)
            => EngineLogCompat.DumpEngineSource(url, htmlContent);

        public static string DumpRenderedText(string url, string textContent)
            => EngineLogCompat.DumpRenderedText(url, textContent);

        public static void LogComputedStyle(
            string selector,
            string tag,
            string display,
            double? fontSize,
            double? width,
            double? height,
            string padding,
            string margin,
            string boxInfo)
        {
            if (!IsModuleEnabled("CSS")) return;
            Debug(
                "CSS",
                $"[CSS-DIAG] {selector} <{tag}> Display={display ?? "null"} " +
                $"FontSize={fontSize?.ToString("F1") ?? "null"} " +
                $"W={width?.ToString("F0") ?? "auto"} H={height?.ToString("F0") ?? "auto"} " +
                $"Padding={padding} Margin={margin} Box={boxInfo}");
        }

        public static void LogBoxModel(
            string elementId,
            string tag,
            float contentW,
            float contentH,
            float paddingTop,
            float paddingRight,
            float paddingBottom,
            float paddingLeft,
            float marginTop,
            float marginRight,
            float marginBottom,
            float marginLeft)
        {
            if (!IsModuleEnabled("Layout")) return;
            Debug(
                "Layout",
                $"[BOX] {elementId}#{tag} Content={contentW:F0}x{contentH:F0} " +
                $"Padding=[{paddingTop:F0},{paddingRight:F0},{paddingBottom:F0},{paddingLeft:F0}] " +
                $"Margin=[{marginTop:F0},{marginRight:F0},{marginBottom:F0},{marginLeft:F0}]");
        }

        private static LogCategory GetCategory(string module)
        {
            return module?.ToUpperInvariant() switch
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
    }

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
