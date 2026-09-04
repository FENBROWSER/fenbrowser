using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgVectorEffectTests
    {
        [Fact]
        public void NonScalingStroke_PreservesDeviceWidthUnderUniformScale()
        {
            const string svg =
                "<svg width='100' height='100'><rect x='2' y='2' width='6' height='6' " +
                "transform='scale(10)' fill='none' stroke='red' stroke-width='4' " +
                "vector-effect='non-scaling-stroke'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 50));
            Assert.Equal(0, result.Bitmap.GetPixel(30, 50).Alpha);
        }

        [Fact]
        public void OrdinaryStroke_StillScalesWithTheShapeTransform()
        {
            const string svg =
                "<svg width='100' height='100'><rect x='2' y='2' width='6' height='6' " +
                "transform='scale(10)' fill='none' stroke='red' stroke-width='4'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(30, 50));
        }

        [Fact]
        public void CssNonScalingStroke_UsesTheSameBoundedDeviceSpacePath()
        {
            const string svg =
                "<svg width='100' height='100'><style>.edge{vector-effect:non-scaling-stroke}</style>" +
                "<rect class='edge' x='2' y='2' width='6' height='6' transform='scale(10)' " +
                "fill='none' stroke='blue' stroke-width='4'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 50));
            Assert.Equal(0, result.Bitmap.GetPixel(30, 50).Alpha);
        }

        [Fact]
        public void NonScalingPaintServerStroke_RemainsExplicitFallback()
        {
            const string svg =
                "<svg width='40' height='40'><defs><linearGradient id='g'>" +
                "<stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs>" +
                "<rect x='5' y='5' width='20' height='20' fill='none' stroke='url(#g)' " +
                "stroke-width='4' vector-effect='non-scaling-stroke' transform='scale(1.2)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("paint-server stroke"));
        }

        [Fact]
        public void NonScalingStrokeWidthMarker_UsesDeviceSpaceAndDefinitionStyle()
        {
            const string svg =
                "<svg width='120' height='120'><defs><marker id='m' refY='5' markerWidth='10' markerHeight='10'>" +
                "<rect width='10' height='10' fill='green'/></marker></defs>" +
                "<line x2='10' y1='5' y2='5' vector-effect='non-scaling-stroke' stroke='red' " +
                "stroke-width='10' marker-start='url(#m)' transform='scale(10)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(50, 50));
            Assert.Equal(0, result.Bitmap.GetPixel(110, 50).Alpha);
        }
    }
}
