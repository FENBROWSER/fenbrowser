using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Feature support status for tracking engine capabilities.
    /// </summary>
    public enum FeatureStatus
    {
        Supported,       // Fully implemented
        Partial,         // Partially implemented
        Unsupported,     // Not implemented
        Deprecated       // Was supported, now removed
    }

    /// <summary>
    /// Represents a feature with its support status and details.
    /// </summary>
    public class FeatureInfo
    {
        public string Name { get; set; }
        public FeatureStatus Status { get; set; }
        public string Reason { get; set; }
        public string Suggestion { get; set; }
        public int EncounterCount { get; set; }
        public DateTime LastEncountered { get; set; }
    }

    /// <summary>
    /// Central registry for tracking HTML, CSS, and JavaScript feature support.
    /// Follows FenBrowser motto: modularity, security, privacy, reliability.
    /// </summary>
    public static class EngineCapabilities
    {
        // Thread-safe dictionaries for concurrent access
        private static readonly ConcurrentDictionary<string, FeatureInfo> _htmlFeatures = new();
        private static readonly ConcurrentDictionary<string, FeatureInfo> _cssFeatures = new();
        private static readonly ConcurrentDictionary<string, FeatureInfo> _jsFeatures = new();
        
        // Logging control
        private static bool _logOnFirstEncounter = true;
        private static bool _logAllEncounters = false;

        #region Configuration

        /// <summary>
        /// Configure logging behavior for feature encounters.
        /// </summary>
        public static void Configure(bool logOnFirstEncounter = true, bool logAllEncounters = false)
        {
            _logOnFirstEncounter = logOnFirstEncounter;
            _logAllEncounters = logAllEncounters;
        }

        #endregion

        #region HTML Features

        /// <summary>
        /// Log an unsupported HTML element.
        /// </summary>
        public static void LogUnsupportedHtml(string tagName, string reason = null, string suggestion = null)
        {
            LogFeature(_htmlFeatures, tagName?.ToUpperInvariant() ?? "UNKNOWN", 
                FeatureStatus.Unsupported, reason, suggestion, LogCategory.HtmlParsing);
        }

        /// <summary>
        /// Log a partially supported HTML element.
        /// </summary>
        public static void LogPartialHtml(string tagName, string reason = null)
        {
            LogFeature(_htmlFeatures, tagName?.ToUpperInvariant() ?? "UNKNOWN", 
                FeatureStatus.Partial, reason, null, LogCategory.HtmlParsing);
        }

        /// <summary>
        /// Check if an HTML element is tracked as unsupported.
        /// </summary>
        public static bool IsHtmlUnsupported(string tagName)
        {
            return _htmlFeatures.TryGetValue(tagName?.ToUpperInvariant() ?? "", out var info) 
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region CSS Features

        /// <summary>
        /// Log an unsupported CSS property.
        /// </summary>
        public static void LogUnsupportedCss(string property, string value = null, string reason = null)
        {
            string key = string.IsNullOrEmpty(value) ? property : $"{property}: {value}";
            LogFeature(_cssFeatures, key?.ToLowerInvariant() ?? "unknown", 
                FeatureStatus.Unsupported, reason, null, LogCategory.CssParsing);
        }

        /// <summary>
        /// Log a partially supported CSS property.
        /// </summary>
        public static void LogPartialCss(string property, string reason = null)
        {
            LogFeature(_cssFeatures, property?.ToLowerInvariant() ?? "unknown", 
                FeatureStatus.Partial, reason, null, LogCategory.CssParsing);
        }

        /// <summary>
        /// Check if a CSS property is tracked as unsupported.
        /// </summary>
        public static bool IsCssUnsupported(string property)
        {
            return _cssFeatures.TryGetValue(property?.ToLowerInvariant() ?? "", out var info) 
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region JavaScript Features

        /// <summary>
        /// Log an unsupported JavaScript API or method.
        /// </summary>
        public static void LogUnsupportedJs(string api, string method = null, string reason = null)
        {
            string key = string.IsNullOrEmpty(method) ? api : $"{api}.{method}";
            LogFeature(_jsFeatures, key, FeatureStatus.Unsupported, reason, null, LogCategory.JsExecution);
        }

        /// <summary>
        /// Log a partially supported JavaScript API.
        /// </summary>
        public static void LogPartialJs(string api, string reason = null)
        {
            LogFeature(_jsFeatures, api, FeatureStatus.Partial, reason, null, LogCategory.JsExecution);
        }

        /// <summary>
        /// Check if a JS API is tracked as unsupported.
        /// </summary>
        public static bool IsJsUnsupported(string api)
        {
            return _jsFeatures.TryGetValue(api ?? "", out var info) 
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region Reporting

        /// <summary>
        /// Get a summary of all unsupported features for debugging.
        /// </summary>
        public static string GetFailureSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== FenBrowser Engine Capability Report ===");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine();

            AppendCategoryReport(sb, "HTML Elements", _htmlFeatures);
            AppendCategoryReport(sb, "CSS Properties", _cssFeatures);
            AppendCategoryReport(sb, "JavaScript APIs", _jsFeatures);

            return sb.ToString();
        }

        private static void AppendCategoryReport(StringBuilder sb, string categoryName, 
            ConcurrentDictionary<string, FeatureInfo> features)
        {
            var unsupported = features.Values.Where(f => f.Status == FeatureStatus.Unsupported)
                .OrderByDescending(f => f.EncounterCount).ToList();
            var partial = features.Values.Where(f => f.Status == FeatureStatus.Partial)
                .OrderByDescending(f => f.EncounterCount).ToList();

            sb.AppendLine($"--- {categoryName} ---");
            sb.AppendLine($"Unsupported: {unsupported.Count}, Partial: {partial.Count}");
            
            if (unsupported.Count > 0)
            {
                sb.AppendLine("Top Unsupported:");
                foreach (var f in unsupported.Take(10))
                {
                    sb.AppendLine($"  [{f.EncounterCount}x] {f.Name}" + 
                        (string.IsNullOrEmpty(f.Reason) ? "" : $" - {f.Reason}"));
                }
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Get counts of unsupported features by category.
        /// </summary>
        public static (int html, int css, int js) GetUnsupportedCounts()
        {
            return (
                _htmlFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported),
                _cssFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported),
                _jsFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported)
            );
        }

        /// <summary>
        /// Clear all tracked features. Useful for testing or resetting between pages.
        /// </summary>
        public static void Reset()
        {
            _htmlFeatures.Clear();
            _cssFeatures.Clear();
            _jsFeatures.Clear();
        }

        #endregion

        #region Internal

        private static void LogFeature(ConcurrentDictionary<string, FeatureInfo> dict, string key, 
            FeatureStatus status, string reason, string suggestion, LogCategory category)
        {
            bool isNewFeature = false;
            
            var info = dict.AddOrUpdate(key,
                // Add new
                _ =>
                {
                    isNewFeature = true;
                    return new FeatureInfo
                    {
                        Name = key,
                        Status = status,
                        Reason = reason,
                        Suggestion = suggestion,
                        EncounterCount = 1,
                        LastEncountered = DateTime.Now
                    };
                },
                // Update existing
                (_, existing) =>
                {
                    existing.EncounterCount++;
                    existing.LastEncountered = DateTime.Now;
                    if (!string.IsNullOrEmpty(reason)) existing.Reason = reason;
                    return existing;
                });

            // Log based on configuration
            if ((_logOnFirstEncounter && isNewFeature) || _logAllEncounters)
            {
                string statusStr = status switch
                {
                    FeatureStatus.Unsupported => "UNSUPPORTED",
                    FeatureStatus.Partial => "PARTIAL",
                    _ => status.ToString().ToUpper()
                };

                string message = $"[{statusStr}] {key}";
                if (!string.IsNullOrEmpty(reason)) message += $" | {reason}";
                if (!string.IsNullOrEmpty(suggestion)) message += $" | Suggestion: {suggestion}";

                var marker = status switch
                {
                    FeatureStatus.Unsupported => LogMarker.Unimplemented,
                    FeatureStatus.Partial => LogMarker.Partial,
                    _ => LogMarker.None
                };

                EngineLog.Write(
                    EngineLogCompatibility.FromLegacyCategory(category),
                    LogSeverity.Warn,
                    message,
                    marker);
            }
        }

        #endregion
    }
}

