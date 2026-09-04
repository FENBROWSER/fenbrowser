using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgPatternTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void UserSpacePattern_RepeatsItsPictureTile()
        {
            using var result = _renderer.Render(
                "<svg width='40' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='10' height='10'><rect width='5' height='10' fill='red'/></pattern>" +
                "<rect width='40' height='20' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(2, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(7, 5).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(12, 5));
        }

        [Fact]
        public void UserSpacePattern_PaintsStrokeGeometry()
        {
            using var result = _renderer.Render(
                "<svg width='30' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='4' height='4'><rect width='4' height='4' fill='blue'/></pattern>" +
                "<rect x='3' y='3' width='24' height='14' fill='none' " +
                "stroke='url(#p)' stroke-width='4'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 3));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 10).Alpha);
        }

        [Fact]
        public void ObjectBoundingBoxContent_ResolvesAgainstPaintedGeometry()
        {
            using var result = _renderer.Render(
                "<svg width='30' height='20'><pattern id='p' width='1' height='1' " +
                "patternContentUnits='objectBoundingBox'><rect width='1' height='1' fill='lime'/>" +
                "</pattern><rect x='5' y='5' width='20' height='10' fill-opacity='.5' " +
                "fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(15, 10);
            Assert.True(center.Green > 200 && center.Alpha is >= 120 and <= 136, center.ToString());
        }

        [Fact]
        public void PatternTemplate_InheritsGeometryAndContent()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs>" +
                "<pattern id='base' patternUnits='userSpaceOnUse' width='5' height='5'>" +
                "<rect width='5' height='5' fill='green'/></pattern>" +
                "<pattern id='derived' href='#base'/></defs>" +
                "<rect width='20' height='10' fill='url(#derived)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(17, 7));
        }

        [Fact]
        public void PatternContent_InheritsCurrentColorThroughItsDefinitionTree()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs><pattern id='p' " +
                "patternUnits='userSpaceOnUse' width='10' height='10' " +
                "color='red' fill='currentColor'><circle color='lime' fill='inherit' " +
                "cx='5' cy='5' r='5'/></pattern></defs>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void PatternViewBox_MapsContentIntoEachTile()
        {
            using var result = _renderer.Render(
                "<svg width='40' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='20' height='20' viewBox='10 0 20 20' preserveAspectRatio='none'>" +
                "<rect x='10' width='10' height='20' fill='red'/>" +
                "<rect x='20' width='10' height='20' fill='blue'/></pattern>" +
                "<rect width='40' height='20' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 10));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(25, 10));
        }

        [Fact]
        public void NonInvertiblePatternTransform_UsesPaintFallback()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' width='1' height='1' " +
                "patternTransform='scale(0)'><rect width='1' height='1' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p) green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 5));
        }

        [Fact]
        public void ZeroAreaPatternViewBox_PaintsNothingWithoutCompatibilityFallback()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='20' height='10' viewBox='0 0 0 10'>" +
                "<rect width='20' height='10' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(10, 5).Alpha);
        }

        [Fact]
        public void RecursivePatternPaint_TerminatesAndSkipsNestedPaint()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><pattern id='p' width='1' height='1'>" +
                "<rect width='1' height='1' fill='url(#p)'/></pattern>" +
                "<rect width='10' height='10' fill='url(#p)'/></svg>",
                new SvgRenderLimits { MaxRenderTimeMs = 2000 });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains(result.Warnings, warning => warning.Contains("pattern paint-server cycle"));
        }

        [Fact]
        public void PatternContentOutsideRenderTree_DoesNotLeakIntoTemplate()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><pattern id='p' href='#invalid' width='1' height='1'/>" +
                "<rect width='10' height='10' fill='url(#p) green'/>" +
                "<text><pattern id='invalid'><rect width='10' height='10' fill='red'/>" +
                "</pattern></text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void OversizedMappedPatternTile_RemainsExplicitFallback()
        {
            using var result = _renderer.Render(
                "<svg width='100' height='100'><pattern id='p' width='100' height='100'>" +
                "<rect width='100' height='100' fill='red'/></pattern>" +
                "<rect width='100' height='100' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("pattern tile exceeds"));
        }
    }
}
