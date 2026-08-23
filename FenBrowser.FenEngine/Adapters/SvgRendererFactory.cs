using System;
using System.Threading;

namespace FenBrowser.FenEngine.Adapters
{
    public enum SvgRendererBackend
    {
        LegacySvgSkia = 0,
        FirstParty = 1,
        FirstPartyWithLegacyFallback = 2
    }

    /// <summary>
    /// Process-wide SVG backend selection owned by FenEngine. The initial value
    /// comes from the cross-platform FEN_SVG_RENDERER environment variable and
    /// can be changed atomically before or during browser startup.
    /// </summary>
    public static class SvgRendererConfiguration
    {
        public const string EnvironmentVariable = "FEN_SVG_RENDERER";
        private static int _backend = (int)ReadEnvironmentOrDefault();

        public static SvgRendererBackend Backend
        {
            get => (SvgRendererBackend)Volatile.Read(ref _backend);
            set
            {
                if (!Enum.IsDefined(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown SVG renderer backend.");
                }
                Volatile.Write(ref _backend, (int)value);
            }
        }

        public static bool TryParse(string value, out SvgRendererBackend backend)
        {
            if (string.Equals(value, "first-party", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "fen", StringComparison.OrdinalIgnoreCase))
            {
                backend = SvgRendererBackend.FirstParty;
                return true;
            }
            if (string.Equals(value, "hybrid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            {
                backend = SvgRendererBackend.FirstPartyWithLegacyFallback;
                return true;
            }
            if (string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "svg-skia", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(value))
            {
                backend = SvgRendererBackend.LegacySvgSkia;
                return true;
            }

            backend = SvgRendererBackend.LegacySvgSkia;
            return false;
        }

        public static void ReloadFromEnvironment()
        {
            Backend = ReadEnvironmentOrDefault();
        }

        private static SvgRendererBackend ReadEnvironmentOrDefault()
        {
            return TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var backend)
                ? backend
                : SvgRendererBackend.LegacySvgSkia;
        }
    }

    /// <summary>
    /// Central renderer factory. Implementations are stateless and safe for
    /// concurrent calls, so one process-wide instance avoids allocation churn.
    /// </summary>
    public static class SvgRendererFactory
    {
        private static readonly ISvgRenderer Legacy = new SvgSkiaRenderer();
        private static readonly ISvgRenderer FirstParty = new FenSvgRenderer();
        private static readonly ISvgRenderer Hybrid = new HybridSvgRenderer(FirstParty, Legacy);

        public static ISvgRenderer GetConfiguredRenderer() =>
            GetRenderer(SvgRendererConfiguration.Backend);

        public static ISvgRenderer GetRenderer(SvgRendererBackend backend) => backend switch
        {
            SvgRendererBackend.LegacySvgSkia => Legacy,
            SvgRendererBackend.FirstParty => FirstParty,
            SvgRendererBackend.FirstPartyWithLegacyFallback => Hybrid,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown SVG renderer backend.")
        };
    }
}
