using System;
using System.Threading;

namespace FenBrowser.FenEngine.Adapters
{
    public enum SvgRendererBackend
    {
        FirstParty = 0
    }

    public static class SvgRendererBackendPolicy
    {
        public static bool IsAdmissible(SvgRendererBackend backend) =>
            backend == SvgRendererBackend.FirstParty;

        public static SvgRendererBackend Normalize(SvgRendererBackend backend) =>
            IsAdmissible(backend) ? backend : SvgRendererBackend.FirstParty;

        public static string Describe(SvgRendererBackend backend) =>
            IsAdmissible(backend)
                ? nameof(SvgRendererBackend.FirstParty)
                : "unsupported-" +
                  ((int)backend).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Process-wide SVG backend selection owned by FenEngine. The initial value
    /// comes from the cross-platform FEN_SVG_RENDERER environment variable and
    /// can be changed atomically before or during browser startup.
    /// <para>
    /// The first-party engine is the only backend. <c>legacy</c> and
    /// <c>svg-skia</c> stay accepted as deprecated aliases that resolve to
    /// first-party so existing deployments keep starting; any other unrecognized
    /// value resolves to first-party as well and is reported through the bounded
    /// configuration reason codes.
    /// </para>
    /// </summary>
    public static class SvgRendererConfiguration
    {
        public const string EnvironmentVariable = "FEN_SVG_RENDERER";
        public const string FontFallbackPathEnvironmentVariable = "FEN_SVG_FONT_PATH";
        public const string FirstPartyValue = "first-party";
        public const string FirstPartyAliasValue = "fen";
        public const string DeprecatedLegacyValue = "legacy";
        public const string DeprecatedLegacyAliasValue = "svg-skia";
        public const string DeprecatedAliasReasonCode = "svg-backend-deprecated-alias";
        public const string UnrecognizedValueReasonCode = "svg-backend-value-unrecognized";
        public const string UnknownDiagnosticReasonCode = "svg-backend-diagnostic-unknown";
        public const int MaxConfigurationDiagnosticChars = 200;
        private const SvgRendererBackend DefaultBackend = SvgRendererBackend.FirstParty;
        private static int _backend = (int)ReadEnvironmentOrDefault();
        private static string? _lastParseReasonCode;

        public static SvgRendererBackend Backend
        {
            get => (SvgRendererBackend)Volatile.Read(ref _backend);
            set
            {
                if (!SvgRendererBackendPolicy.IsAdmissible(value))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        SvgRendererBackendPolicy.Describe(value),
                        "Unknown SVG renderer backend.");
                }
                Volatile.Write(ref _backend, (int)SvgRendererBackendPolicy.Normalize(value));
            }
        }

        public static string? LastParseReasonCode => Volatile.Read(ref _lastParseReasonCode);

        public static bool TryParse(string? value, out SvgRendererBackend backend) =>
            TryParse(value, out backend, out _);

        public static bool TryParse(
            string? value,
            out SvgRendererBackend backend,
            out string? reasonCode)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                backend = DefaultBackend;
                reasonCode = null;
                RecordReasonCode(reasonCode);
                return true;
            }
            if (Matches(normalized, FirstPartyValue) ||
                Matches(normalized, FirstPartyAliasValue))
            {
                backend = SvgRendererBackend.FirstParty;
                reasonCode = null;
                RecordReasonCode(reasonCode);
                return true;
            }
            if (Matches(normalized, DeprecatedLegacyValue) ||
                Matches(normalized, DeprecatedLegacyAliasValue))
            {
                backend = SvgRendererBackend.FirstParty;
                reasonCode = DeprecatedAliasReasonCode;
                RecordReasonCode(reasonCode);
                return true;
            }

            backend = DefaultBackend;
            reasonCode = UnrecognizedValueReasonCode;
            RecordReasonCode(reasonCode);
            return false;
        }

        private static void RecordReasonCode(string? reasonCode) =>
            Volatile.Write(ref _lastParseReasonCode, reasonCode);

        public static string DescribeValue(string? value)
        {
            TryParse(value, out _, out string? reasonCode);
            return DescribeSelection(reasonCode);
        }

        public static string DescribeConfiguration() => DescribeSelection(LastParseReasonCode);

        public static string DescribeSelection(string? reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
            {
                return Bounded(SvgRendererBackendPolicy.Describe(DefaultBackend));
            }
            if (reasonCode == DeprecatedAliasReasonCode ||
                reasonCode == UnrecognizedValueReasonCode)
            {
                return Bounded(
                    SvgRendererBackendPolicy.Describe(DefaultBackend) + " (" + reasonCode + ")");
            }

            return Bounded(
                SvgRendererBackendPolicy.Describe(DefaultBackend) +
                " (" + UnknownDiagnosticReasonCode + ")");
        }

        private static string Bounded(string value)
        {
            if (value.Length <= MaxConfigurationDiagnosticChars)
            {
                return value;
            }

            return value.Substring(0, MaxConfigurationDiagnosticChars) + "...";
        }

        private static bool Matches(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        public static void ReloadFromEnvironment()
        {
            Backend = ReadEnvironmentOrDefault();
        }

        private static SvgRendererBackend ReadEnvironmentOrDefault()
        {
            return TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var backend)
                ? SvgRendererBackendPolicy.Normalize(backend)
                : DefaultBackend;
        }
    }

    /// <summary>
    /// Central renderer factory. The implementation is stateless and safe for
    /// concurrent calls, so one process-wide instance avoids allocation churn.
    /// </summary>
    public static class SvgRendererFactory
    {
        private static readonly ISvgRenderer FirstParty = new FenSvgRenderer();

        public static ISvgRenderer GetConfiguredRenderer() =>
            GetRenderer(SvgRendererConfiguration.Backend);

        public static ISvgRenderer GetRenderer(SvgRendererBackend backend)
        {
            if (!SvgRendererBackendPolicy.IsAdmissible(backend))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    SvgRendererBackendPolicy.Describe(backend),
                    "Unknown SVG renderer backend.");
            }

            return FirstParty;
        }
    }
}
