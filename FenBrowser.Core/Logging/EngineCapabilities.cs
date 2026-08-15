using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Feature support status for tracking engine capabilities.
    /// </summary>
    public enum FeatureStatus
    {
        Supported,
        Partial,
        Unsupported,
        Deprecated
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
    /// Diagnostics are deliberately bounded because feature names/values can originate
    /// in untrusted page content and this registry lives for the browser process.
    /// </summary>
    public static class EngineCapabilities
    {
        private const int MaxFeaturesPerCategory = 4096;
        private const int MaxFeatureKeyLength = 512;
        private const int MaxReasonLength = 1024;
        private const int MaxSuggestionLength = 1024;

        private static readonly ConcurrentDictionary<string, FeatureInfo> _htmlFeatures = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, FeatureInfo> _cssFeatures = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, FeatureInfo> _jsFeatures = new(StringComparer.Ordinal);

        private static int _logOnFirstEncounter = 1;
        private static int _logAllEncounters;

        #region Configuration

        public static void Configure(bool logOnFirstEncounter = true, bool logAllEncounters = false)
        {
            Volatile.Write(ref _logOnFirstEncounter, logOnFirstEncounter ? 1 : 0);
            Volatile.Write(ref _logAllEncounters, logAllEncounters ? 1 : 0);
        }

        #endregion

        #region HTML Features

        public static void LogUnsupportedHtml(string tagName, string reason = null, string suggestion = null)
        {
            LogFeature(_htmlFeatures, tagName?.ToUpperInvariant() ?? "UNKNOWN",
                FeatureStatus.Unsupported, reason, suggestion, LogCategory.HtmlParsing);
        }

        public static void LogPartialHtml(string tagName, string reason = null)
        {
            LogFeature(_htmlFeatures, tagName?.ToUpperInvariant() ?? "UNKNOWN",
                FeatureStatus.Partial, reason, null, LogCategory.HtmlParsing);
        }

        public static bool IsHtmlUnsupported(string tagName)
        {
            return _htmlFeatures.TryGetValue(NormalizeKey(tagName?.ToUpperInvariant() ?? string.Empty), out var info)
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region CSS Features

        public static void LogUnsupportedCss(string property, string value = null, string reason = null)
        {
            string key = string.IsNullOrEmpty(value) ? property : $"{property}: {value}";
            LogFeature(_cssFeatures, key?.ToLowerInvariant() ?? "unknown",
                FeatureStatus.Unsupported, reason, null, LogCategory.CssParsing);
        }

        public static void LogPartialCss(string property, string reason = null)
        {
            LogFeature(_cssFeatures, property?.ToLowerInvariant() ?? "unknown",
                FeatureStatus.Partial, reason, null, LogCategory.CssParsing);
        }

        public static bool IsCssUnsupported(string property)
        {
            return _cssFeatures.TryGetValue(NormalizeKey(property?.ToLowerInvariant() ?? string.Empty), out var info)
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region JavaScript Features

        public static void LogUnsupportedJs(string api, string method = null, string reason = null)
        {
            string key = string.IsNullOrEmpty(method) ? api : $"{api}.{method}";
            var normalizedKey = NormalizeKey(key);
            var normalizedReason = NormalizeDiagnosticText(reason, MaxReasonLength);
            LogFeature(
                _jsFeatures,
                normalizedKey,
                FeatureStatus.Unsupported,
                normalizedReason,
                null,
                LogCategory.JsExecution,
                new Dictionary<string, object>
                {
                    ["traceCategory"] = "WebIDL",
                    ["featureCategory"] = "JavaScript",
                    ["api"] = normalizedKey,
                    ["objectName"] = NormalizeDiagnosticText(api, MaxFeatureKeyLength),
                    ["propertyName"] = NormalizeDiagnosticText(method, MaxFeatureKeyLength),
                    ["featureStatus"] = FeatureStatus.Unsupported.ToString(),
                    ["reason"] = normalizedReason
                });
        }

        public static void LogPartialJs(string api, string reason = null)
        {
            LogFeature(_jsFeatures, api, FeatureStatus.Partial, reason, null, LogCategory.JsExecution);
        }

        public static bool IsJsUnsupported(string api)
        {
            return _jsFeatures.TryGetValue(NormalizeKey(api), out var info)
                && info.Status == FeatureStatus.Unsupported;
        }

        #endregion

        #region Reporting

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

        public static (int html, int css, int js) GetUnsupportedCounts()
        {
            return (
                _htmlFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported),
                _cssFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported),
                _jsFeatures.Values.Count(f => f.Status == FeatureStatus.Unsupported)
            );
        }

        public static IReadOnlyList<FeatureInfo> GetUnsupportedJsSnapshot()
        {
            return _jsFeatures.Values
                .Where(f => f.Status == FeatureStatus.Unsupported)
                .OrderByDescending(f => f.EncounterCount)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(CloneFeatureInfo)
                .ToList();
        }

        public static void Reset()
        {
            _htmlFeatures.Clear();
            _cssFeatures.Clear();
            _jsFeatures.Clear();
        }

        #endregion

        #region Internal

        private static void LogFeature(
            ConcurrentDictionary<string, FeatureInfo> dict,
            string key,
            FeatureStatus status,
            string reason,
            string suggestion,
            LogCategory category,
            IReadOnlyDictionary<string, object> fields = null)
        {
            key = NormalizeKey(key);
            reason = NormalizeDiagnosticText(reason, MaxReasonLength);
            suggestion = NormalizeDiagnosticText(suggestion, MaxSuggestionLength);

            bool isNewFeature = false;
            FeatureInfo info;

            while (true)
            {
                if (dict.TryGetValue(key, out var existing))
                {
                    // Replace instead of mutating a shared FeatureInfo instance. The old
                    // in-place EncounterCount++ raced across AddOrUpdate callbacks and
                    // could lose encounters or expose partially-updated diagnostics.
                    var updated = new FeatureInfo
                    {
                        Name = existing.Name,
                        Status = status,
                        Reason = string.IsNullOrEmpty(reason) ? existing.Reason : reason,
                        Suggestion = string.IsNullOrEmpty(suggestion) ? existing.Suggestion : suggestion,
                        EncounterCount = existing.EncounterCount == int.MaxValue
                            ? int.MaxValue
                            : existing.EncounterCount + 1,
                        LastEncountered = DateTime.UtcNow
                    };

                    if (dict.TryUpdate(key, updated, existing))
                    {
                        info = updated;
                        break;
                    }

                    continue;
                }

                // Do not allow page-controlled unique CSS values/API names to turn a
                // diagnostics registry into a process-lifetime memory sink.
                if (dict.Count >= MaxFeaturesPerCategory)
                {
                    return;
                }

                var created = new FeatureInfo
                {
                    Name = key,
                    Status = status,
                    Reason = reason,
                    Suggestion = suggestion,
                    EncounterCount = 1,
                    LastEncountered = DateTime.UtcNow
                };

                if (dict.TryAdd(key, created))
                {
                    info = created;
                    isNewFeature = true;
                    break;
                }
            }

            if ((Volatile.Read(ref _logOnFirstEncounter) != 0 && isNewFeature) ||
                Volatile.Read(ref _logAllEncounters) != 0)
            {
                string statusStr = status switch
                {
                    FeatureStatus.Unsupported => "UNSUPPORTED",
                    FeatureStatus.Partial => "PARTIAL",
                    _ => status.ToString().ToUpperInvariant()
                };

                string message = $"[{statusStr}] {info.Name}";
                if (!string.IsNullOrEmpty(info.Reason)) message += $" | {info.Reason}";
                if (!string.IsNullOrEmpty(info.Suggestion)) message += $" | Suggestion: {info.Suggestion}";

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
                    marker,
                    fields: fields);
            }
        }

        private static FeatureInfo CloneFeatureInfo(FeatureInfo f) => new()
        {
            Name = f.Name,
            Status = f.Status,
            Reason = f.Reason,
            Suggestion = f.Suggestion,
            EncounterCount = f.EncounterCount,
            LastEncountered = f.LastEncountered
        };

        private static string NormalizeKey(string value)
        {
            var normalized = NormalizeDiagnosticText(value, MaxFeatureKeyLength);
            return normalized.Length == 0 ? "unknown" : normalized;
        }

        private static string NormalizeDiagnosticText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var length = Math.Min(value.Length, maxLength);
            StringBuilder builder = null;
            for (var i = 0; i < length; i++)
            {
                var ch = value[i];
                var safe = char.IsControl(ch) ? ' ' : ch;
                if (builder == null && safe != ch)
                {
                    builder = new StringBuilder(length);
                    builder.Append(value, 0, i);
                }
                builder?.Append(safe);
            }

            var result = builder?.ToString() ?? value.Substring(0, length);
            return value.Length > maxLength ? result + "…" : result;
        }

        #endregion
    }
}
