using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>What the SVG engine reports through <see cref="LogSubsystem.Svg"/>.</summary>
    public enum SvgDiagnosticsMode
    {
        /// <summary>Nothing is emitted and nothing is computed for diagnostics.</summary>
        Off = 0,

        /// <summary>One rate-limited Warn event per rejected render.</summary>
        Failures = 1,

        /// <summary>Failures plus one Info event per admitted render.</summary>
        Verbose = 2
    }

    /// <summary>
    /// Structured diagnostics for every render that crosses the
    /// <see cref="ISvgRenderer"/> seam, selected by the cross-platform
    /// <c>FEN_SVG_DIAGNOSTICS</c> flag (<c>off</c> | <c>failures</c> | <c>verbose</c>,
    /// default <c>off</c>). The flag decides what is emitted; the engine log
    /// configuration still decides where it goes (ring buffer, NDJSON, trace).
    ///
    /// PRIVACY: events never carry SVG source, URLs or resolved resource bytes.
    /// A document is identified by its length and a truncated SHA-256, and every
    /// text field is already bounded by the renderer before it reaches here.
    /// Recording never throws into the render path.
    /// </summary>
    public static class SvgDiagnostics
    {
        public const string EnvironmentVariable = "FEN_SVG_DIAGNOSTICS";
        public const string OffValue = "off";
        public const string FailuresValue = "failures";
        public const string VerboseValue = "verbose";
        public const string UnrecognizedValueReasonCode = "svg-diagnostics-value-unrecognized";
        public const string UnspecifiedSource = "unspecified";
        internal const int MaxSourceLabelChars = 32;
        internal const int SourceHashHexChars = 16;
        internal static readonly TimeSpan RejectionRateWindow = TimeSpan.FromSeconds(10);

        private static int _mode = (int)ReadEnvironmentOrDefault(out _lastParseReasonCode);
        private static int _levelAppliedForMode = -1;
        private static string? _lastParseReasonCode;
        private static int _unrecognizedValueReported;

        public static SvgDiagnosticsMode Mode
        {
            get => (SvgDiagnosticsMode)Volatile.Read(ref _mode);
            set
            {
                if (value is < SvgDiagnosticsMode.Off or > SvgDiagnosticsMode.Verbose)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown SVG diagnostics mode.");
                }
                Volatile.Write(ref _mode, (int)value);
            }
        }

        /// <summary>Reason code from the last flag parse, or null when it was recognized.</summary>
        public static string? LastParseReasonCode => Volatile.Read(ref _lastParseReasonCode);

        public static bool TryParse(string? value, out SvgDiagnosticsMode mode)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (normalized.Length == 0 || Matches(normalized, OffValue))
            {
                mode = SvgDiagnosticsMode.Off;
                return true;
            }
            if (Matches(normalized, FailuresValue))
            {
                mode = SvgDiagnosticsMode.Failures;
                return true;
            }
            if (Matches(normalized, VerboseValue))
            {
                mode = SvgDiagnosticsMode.Verbose;
                return true;
            }

            // Fail quiet: an unknown value must never turn diagnostics on.
            mode = SvgDiagnosticsMode.Off;
            return false;
        }

        /// <summary>
        /// Records one completed render. Called by the renderer after the result
        /// is final (fail-closed enforcement and diagnostic bounding applied).
        /// </summary>
        internal static void RecordRender(SvgRenderRequest? request, SvgRenderResult? result, TimeSpan elapsed)
        {
            var mode = Mode;
            ReportUnrecognizedFlagOnce();
            if (mode == SvgDiagnosticsMode.Off || result == null)
            {
                return;
            }

            try
            {
                bool admitted = SvgRenderResult.IsAdmissible(result);
                if (admitted && mode != SvgDiagnosticsMode.Verbose)
                {
                    return;
                }

                EnsureLevel(mode);
                string source = SanitizeSource(request?.DiagnosticSource);
                var fields = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["source"] = source,
                    ["sourceChars"] = request?.Content?.Length ?? 0,
                    ["sourceSha256"] = HashPrefix(request?.Content),
                    ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 3)
                };

                if (admitted)
                {
                    fields["width"] = result.Width;
                    fields["height"] = result.Height;
                    fields["rasterWidth"] = result.Bitmap?.Width ?? 0;
                    fields["rasterHeight"] = result.Bitmap?.Height ?? 0;
                    fields["downscaled"] = result.IsDownscaled;
                    fields["warnings"] = result.Warnings?.Count ?? 0;
                    EngineLog.Write(
                        LogSubsystem.Svg, LogSeverity.Info, "SVG render admitted",
                        LogMarker.None, default, fields);
                    return;
                }

                string firstCode = FirstCode(result);
                fields["fallbackReasonCodes"] = Join(result.FallbackReasonCodes);
                fields["resourceRejectionReasonCodes"] = Join(result.ResourceRejectionReasonCodes);
                fields["requiresFallback"] = result.RequiresFallback;
                fields["resourceRejected"] = result.HadResourceRejection;
                fields["error"] = result.ErrorMessage ?? string.Empty;
                EngineLog.WriteRateLimited(
                    "svg.reject:" + source + ":" + firstCode,
                    RejectionRateWindow,
                    LogSubsystem.Svg, LogSeverity.Warn, "SVG render rejected",
                    LogMarker.Fallback, default, fields);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Diagnostics are best-effort; a logging failure must never fail a render.
            }
        }

        /// <summary>
        /// Records that the inline SVG bridge withheld referenced custom properties
        /// that failed validation or bounds (a security-relevant, rate-limited event).
        /// </summary>
        internal static void RecordInlineContextWithheld(int withheld)
        {
            var mode = Mode;
            if (mode == SvgDiagnosticsMode.Off || withheld <= 0)
            {
                return;
            }

            try
            {
                EnsureLevel(mode);
                EngineLog.WriteRateLimited(
                    "svg.inline-context-withheld",
                    RejectionRateWindow,
                    LogSubsystem.Svg, LogSeverity.Warn,
                    "Inline SVG custom properties withheld",
                    LogMarker.Fallback, default,
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["withheld"] = withheld });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }

        /// <summary>Test isolation: re-reads nothing, only resets one-shot state.</summary>
        internal static void ResetForTesting(SvgDiagnosticsMode mode)
        {
            Mode = mode;
            Volatile.Write(ref _levelAppliedForMode, -1);
            Volatile.Write(ref _unrecognizedValueReported, 0);
        }

        internal static string SanitizeSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return UnspecifiedSource;
            }

            var builder = new StringBuilder(Math.Min(source.Length, MaxSourceLabelChars));
            foreach (char ch in source)
            {
                if (builder.Length == MaxSourceLabelChars) break;
                builder.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
            }
            return builder.ToString();
        }

        internal static string HashPrefix(string? content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(hash, 0, SourceHashHexChars / 2).ToLowerInvariant();
        }

        private static void EnsureLevel(SvgDiagnosticsMode mode)
        {
            if (Interlocked.Exchange(ref _levelAppliedForMode, (int)mode) == (int)mode)
            {
                return;
            }

            EngineLog.EnsureSubsystemEnabled(
                LogSubsystem.Svg,
                mode == SvgDiagnosticsMode.Verbose ? LogSeverity.Info : LogSeverity.Warn);
        }

        private static void ReportUnrecognizedFlagOnce()
        {
            if (LastParseReasonCode == null ||
                Interlocked.Exchange(ref _unrecognizedValueReported, 1) != 0)
            {
                return;
            }

            try
            {
                EngineLog.Write(
                    LogSubsystem.Svg, LogSeverity.Warn,
                    "FEN_SVG_DIAGNOSTICS value not recognized; SVG diagnostics stay off",
                    LogMarker.Fallback, default,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["reasonCode"] = UnrecognizedValueReasonCode,
                        ["accepted"] = OffValue + "|" + FailuresValue + "|" + VerboseValue
                    });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }

        private static SvgDiagnosticsMode ReadEnvironmentOrDefault(out string? reasonCode)
        {
            bool recognized = TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var mode);
            reasonCode = recognized ? null : UnrecognizedValueReasonCode;
            return mode;
        }

        private static string FirstCode(SvgRenderResult result)
        {
            if (result.FallbackReasonCodes is { Count: > 0 } fallback) return SanitizeSource(fallback[0]);
            if (result.ResourceRejectionReasonCodes is { Count: > 0 } rejected) return SanitizeSource(rejected[0]);
            return "failure";
        }

        private static string Join(IReadOnlyList<string>? values) =>
            values == null || values.Count == 0 ? string.Empty : string.Join(",", values);

        private static bool Matches(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
