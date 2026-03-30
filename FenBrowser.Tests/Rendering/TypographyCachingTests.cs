using System.Reflection;
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
