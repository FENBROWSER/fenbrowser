using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Production compatibility renderer: first-party for its declared static
    /// subset, legacy only when bounded capability detection reports that omitting
    /// an unsupported feature can change output. Security failures never fallback.
    /// </summary>
    public sealed class HybridSvgRenderer : ISvgRenderer
    {
        private readonly ISvgRenderer _firstParty;
        private readonly ISvgRenderer _legacy;

        public HybridSvgRenderer(ISvgRenderer firstParty, ISvgRenderer legacy)
        {
            _firstParty = firstParty ?? throw new ArgumentNullException(nameof(firstParty));
            _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        }

        public SvgRenderResult Render(string svgContent) => Render(svgContent, SvgRenderLimits.Default);

        public SvgRenderResult Render(string svgContent, SvgRenderLimits limits)
        {
            var primary = _firstParty.Render(svgContent, limits);
            if (!primary.Success || !primary.RequiresFallback || primary.HadResourceRejection)
            {
                return primary;
            }

            var fallback = _legacy.Render(svgContent, limits);
            if (!fallback.Success)
            {
                primary.Warnings = MergeWarnings(
                    primary.Warnings,
                    $"compatibility fallback failed: {fallback.ErrorMessage}");
                fallback.Dispose();
                return primary;
            }

            fallback.Warnings = MergeWarnings(primary.Warnings, "legacy compatibility fallback used");
            fallback.FallbackReasonCodes = primary.FallbackReasonCodes;
            fallback.ResourceRejectionReasonCodes = primary.ResourceRejectionReasonCodes;
            fallback.UsedLegacyFallback = true;
            primary.Dispose();

            EngineLogCompat.Debug(
                $"[HybridSvgRenderer] compatibility fallback warnings={fallback.Warnings.Count}",
                LogCategory.Rendering);
            return fallback;
        }

        private static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> source, string message)
        {
            var merged = new List<string>(Math.Min(SvgParseReportWarningLimit, source.Count + 1));
            for (int i = 0; i < source.Count && merged.Count < SvgParseReportWarningLimit; i++)
            {
                if (!merged.Contains(source[i]))
                {
                    merged.Add(source[i]);
                }
            }
            if (merged.Count < SvgParseReportWarningLimit && !merged.Contains(message))
            {
                merged.Add(message);
            }
            return merged;
        }

        private const int SvgParseReportWarningLimit = 32;
    }
}
