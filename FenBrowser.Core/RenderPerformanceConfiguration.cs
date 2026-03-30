using System;

namespace FenBrowser.Core
{
    /// <summary>
    /// Shared render/perf budgets for hot-path caches and per-frame scheduling.
    /// These defaults are conservative and favor predictable bounded cost over peak-cache growth.
    /// </summary>
    public sealed class RenderPerformanceConfiguration
    {
        public static RenderPerformanceConfiguration Current { get; } = new RenderPerformanceConfiguration();

        public int FontMetricsCacheEntries { get; set; } = 512;
        public long FontMetricsCacheBytes { get; set; } = 128 * 1024;

        public int FontWidthCacheEntries { get; set; } = 4096;
        public long FontWidthCacheBytes { get; set; } = 768 * 1024;

        public int FontGlyphRunCacheEntries { get; set; } = 2048;
        public long FontGlyphRunCacheBytes { get; set; } = 2 * 1024 * 1024;

        public int TextWidthCacheEntries { get; set; } = 4096;
        public long TextWidthCacheBytes { get; set; } = 512 * 1024;

        public int TextLineHeightCacheEntries { get; set; } = 512;
        public long TextLineHeightCacheBytes { get; set; } = 64 * 1024;

        public double DefaultReservedRenderBudgetMs { get; set; } = 8.0;
        public double BusyFrameReservedRenderBudgetMs { get; set; } = 10.0;

        public int MaxTasksPerFrame { get; set; } = 16;
        public int MaxBackgroundTasksPerBusyFrame { get; set; } = 1;
        public int MaxNonInteractiveTasksPerBusyFrame { get; set; } = 4;

        public RenderPerformanceConfiguration Clone()
        {
            return new RenderPerformanceConfiguration
            {
                FontMetricsCacheEntries = FontMetricsCacheEntries,
                FontMetricsCacheBytes = FontMetricsCacheBytes,
                FontWidthCacheEntries = FontWidthCacheEntries,
                FontWidthCacheBytes = FontWidthCacheBytes,
                FontGlyphRunCacheEntries = FontGlyphRunCacheEntries,
                FontGlyphRunCacheBytes = FontGlyphRunCacheBytes,
                TextWidthCacheEntries = TextWidthCacheEntries,
                TextWidthCacheBytes = TextWidthCacheBytes,
                TextLineHeightCacheEntries = TextLineHeightCacheEntries,
                TextLineHeightCacheBytes = TextLineHeightCacheBytes,
                DefaultReservedRenderBudgetMs = DefaultReservedRenderBudgetMs,
                BusyFrameReservedRenderBudgetMs = BusyFrameReservedRenderBudgetMs,
                MaxTasksPerFrame = MaxTasksPerFrame,
                MaxBackgroundTasksPerBusyFrame = MaxBackgroundTasksPerBusyFrame,
                MaxNonInteractiveTasksPerBusyFrame = MaxNonInteractiveTasksPerBusyFrame
            };
        }

        public void Normalize()
        {
            FontMetricsCacheEntries = Math.Max(1, FontMetricsCacheEntries);
            FontWidthCacheEntries = Math.Max(1, FontWidthCacheEntries);
            FontGlyphRunCacheEntries = Math.Max(1, FontGlyphRunCacheEntries);
            TextWidthCacheEntries = Math.Max(1, TextWidthCacheEntries);
            TextLineHeightCacheEntries = Math.Max(1, TextLineHeightCacheEntries);

            FontMetricsCacheBytes = Math.Max(1024, FontMetricsCacheBytes);
            FontWidthCacheBytes = Math.Max(1024, FontWidthCacheBytes);
            FontGlyphRunCacheBytes = Math.Max(1024, FontGlyphRunCacheBytes);
            TextWidthCacheBytes = Math.Max(1024, TextWidthCacheBytes);
            TextLineHeightCacheBytes = Math.Max(1024, TextLineHeightCacheBytes);

            DefaultReservedRenderBudgetMs = NormalizeBudget(DefaultReservedRenderBudgetMs, 8.0);
            BusyFrameReservedRenderBudgetMs = NormalizeBudget(BusyFrameReservedRenderBudgetMs, 10.0);
            MaxTasksPerFrame = Math.Max(1, MaxTasksPerFrame);
            MaxBackgroundTasksPerBusyFrame = Math.Max(0, MaxBackgroundTasksPerBusyFrame);
            MaxNonInteractiveTasksPerBusyFrame = Math.Max(0, MaxNonInteractiveTasksPerBusyFrame);
        }

        private static double NormalizeBudget(double value, double fallback)
        {
            return double.IsFinite(value) && value > 0
                ? value
                : fallback;
        }
    }
}
