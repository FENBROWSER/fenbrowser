using FenBrowser.FenEngine.Typography;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// SVG text metrics are in user units, which a viewBox can scale by any factor, so
    /// unlike CSS line boxes they are not rounded to whole pixels. Rounding erased the
    /// hanging baseline of a 0.5-unit font (WPT dominant-baseline-hanging-small-font-size).
    /// </summary>
    public sealed class SvgTextMetricsTests
    {
        [Fact]
        public void SvgShapingKeepsFractionalMetricsAtSmallFontSizes()
        {
            GlyphRun run = SkiaFontService.ShapeWithTypeface("X", SKTypeface.Default, 0.5f);

            Assert.InRange(run.Metrics.Ascent, 0.01f, 1.0f);
            Assert.NotEqual(System.MathF.Round(run.Metrics.Ascent), run.Metrics.Ascent);
        }

        [Fact]
        public void CssMetricsStayRoundedToWholePixels()
        {
            using var font = new SKFont(SKTypeface.Default, 13.3f);

            var metrics = NormalizedFontMetrics.FromSkia(font.Metrics, 13.3f);

            Assert.Equal(System.MathF.Round(metrics.Ascent), metrics.Ascent);
            Assert.Equal(System.MathF.Round(metrics.Descent), metrics.Descent);
        }
    }
}
