using System.Reflection;
using FenBrowser.Core;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Typography;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class TypographyCachingTests
    {
        [Fact]
        public void SkiaTextMeasurer_CachesStableWidthAndLineHeightInputs()
        {
            var measurer = new SkiaTextMeasurer();

            var firstWidth = measurer.MeasureWidth("fenbrowser", "Arial", 16);
            var secondWidth = measurer.MeasureWidth("fenbrowser", "Arial", 16);
            var firstLineHeight = measurer.GetLineHeight("Arial", 16);
            var secondLineHeight = measurer.GetLineHeight("Arial", 16);

            Assert.Equal(firstWidth, secondWidth);
            Assert.Equal(firstLineHeight, secondLineHeight);
            Assert.Equal(1, GetConcurrentDictionaryCount(measurer, "_widthCache"));
            Assert.Equal(1, GetConcurrentDictionaryCount(measurer, "_lineHeightCache"));
        }

        [Fact]
        public void SkiaFontService_ReusesCachedMetricsWidthsAndGlyphRuns()
        {
            var fontService = new SkiaFontService();

            var firstMetrics = fontService.GetMetrics("Arial", 16);
            var secondMetrics = fontService.GetMetrics("Arial", 16);
            var firstWidth = fontService.MeasureTextWidth("fenbrowser", "Arial", 16);
            var secondWidth = fontService.MeasureTextWidth("fenbrowser", "Arial", 16);
            var firstRun = fontService.ShapeText("fenbrowser", "Arial", 16);
            var secondRun = fontService.ShapeText("fenbrowser", "Arial", 16);

            Assert.Equal(firstMetrics, secondMetrics);
            Assert.Equal(firstWidth, secondWidth);
            Assert.Same(firstRun, secondRun);
            Assert.Equal(1, GetConcurrentDictionaryCount(fontService, "_metricsCache"));
            Assert.Equal(1, GetConcurrentDictionaryCount(fontService, "_widthCache"));
            Assert.Equal(1, GetConcurrentDictionaryCount(fontService, "_glyphRunCache"));
        }

        [Fact]
        public void SkiaTextMeasurer_EvictsLeastRecentlyUsedEntries_WhenBudgetExceeded()
        {
            var config = new RenderPerformanceConfiguration
            {
                TextWidthCacheEntries = 2,
                TextWidthCacheBytes = 256,
                TextLineHeightCacheEntries = 2,
                TextLineHeightCacheBytes = 256
            };
            var measurer = new SkiaTextMeasurer(config);

            measurer.MeasureWidth("alpha", "Arial", 16);
            measurer.MeasureWidth("beta", "Arial", 16);
            measurer.MeasureWidth("gamma", "Arial", 16);

            var snapshot = measurer.GetCacheSnapshot();
            Assert.True(snapshot.WidthEntries <= 2);
            Assert.True(snapshot.EvictionCount > 0);
        }

        [Fact]
        public void SkiaFontService_EvictsLeastRecentlyUsedEntries_WhenBudgetExceeded()
        {
            var config = new RenderPerformanceConfiguration
            {
                FontWidthCacheEntries = 2,
                FontWidthCacheBytes = 256,
                FontGlyphRunCacheEntries = 2,
                FontGlyphRunCacheBytes = 512,
                FontMetricsCacheEntries = 2,
                FontMetricsCacheBytes = 256
            };
            var fontService = new SkiaFontService(config);

            fontService.MeasureTextWidth("alpha", "Arial", 16);
            fontService.MeasureTextWidth("beta", "Arial", 16);
            fontService.ShapeText("gamma", "Arial", 16);
            fontService.ShapeText("delta", "Arial", 16);
            fontService.ShapeText("epsilon", "Arial", 16);

            var snapshot = fontService.GetCacheSnapshot();
            Assert.True(snapshot.WidthEntries <= 2);
            Assert.True(snapshot.GlyphRunEntries <= 2);
            Assert.True(snapshot.EvictionCount > 0);
        }

        private static int GetConcurrentDictionaryCount(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var cache = field!.GetValue(target);
            Assert.NotNull(cache);

            var countProperty = cache!.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(countProperty);

            return (int)countProperty!.GetValue(cache)!;
        }
    }
}
