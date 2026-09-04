using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgFilterTests
    {
        [Fact]
        public void GaussianBlur_RendersThroughFirstPartyFilterLayer()
        {
            const string svg =
                "<svg width='80' height='50'><defs><filter id='b'>" +
                "<feGaussianBlur stdDeviation='3'/></filter></defs>" +
                "<rect x='25' y='15' width='20' height='20' fill='red' filter='url(#b)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(23, 25).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(35, 25).Red > 100);
        }

        [Fact]
        public void SequentialBlurAndOffset_UsePreviousPrimitiveOutput()
        {
            const string svg =
                "<svg width='100' height='50'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='100' height='50'>" +
                "<feGaussianBlur stdDeviation='1'/><feOffset dx='30' dy='0'/>" +
                "</filter></defs><rect x='5' y='15' width='15' height='15' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(42, 22).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(12, 22).Alpha);
        }

        [Fact]
        public void ColorMatrix_AppliesNormalizedSvgOffsets()
        {
            const string svg =
                "<svg width='30' height='30'><defs><filter id='f'><feColorMatrix values='" +
                "0 0 0 0 1  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0'/></filter></defs>" +
                "<rect x='5' y='5' width='20' height='20' fill='blue' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            SKColor pixel = result.Bitmap.GetPixel(15, 15);
            Assert.True(pixel.Red > 240 && pixel.Green < 10 && pixel.Blue < 10);
        }

        [Fact]
        public void GroupFilter_CompositesChildrenAsOneSourceGraphic()
        {
            const string svg =
                "<svg width='80' height='40'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='80' height='40'><feOffset dx='20'/></filter></defs>" +
                "<g filter='url(#f)'><rect x='2' y='4' width='10' height='10'/>" +
                "<rect x='2' y='22' width='10' height='10'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(25, 8).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(25, 26).Alpha > 0);
        }

        [Fact]
        public void Flood_ReplacesSourceInsideItsPrimitiveRegion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30' primitiveUnits='userSpaceOnUse'>" +
                "<feFlood x='5' y='5' width='10' height='10' flood-color='lime'/></filter></defs>" +
                "<rect width='30' height='30' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(2, 2).Alpha);
        }

        [Fact]
        public void NamedFilterResult_CanFeedALaterPrimitive()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='40' height='20'><feOffset dx='10' result='shifted'/>" +
                "<feOffset in='shifted' dx='5'/></filter></defs>" +
                "<rect width='5' height='10' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(2, 5).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(17, 5));
        }

        [Fact]
        public void SourceAlpha_ProducesBlackWithSourceOpacity()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'><feOffset in='SourceAlpha'/></filter></defs>" +
                "<rect x='4' y='4' width='12' height='12' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FloodAndBlend_UseNamedGraphInputs()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<feFlood flood-color='lime' result='flood'/>" +
                "<feBlend in='SourceGraphic' in2='flood' mode='multiply'/></filter></defs>" +
                "<rect width='20' height='20' fill='blue' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.True(center.Red < 5 && center.Green < 5 && center.Blue < 5, center.ToString());
        }

        [Fact]
        public void CompositeAndMerge_BuildABranchedFilterGraph()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='20'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='20'><feOffset dx='10' result='shifted'/>" +
                "<feFlood flood-color='lime' result='flood'/>" +
                "<feComposite in='flood' in2='shifted' operator='in' result='colored'/>" +
                "<feMerge><feMergeNode in='SourceGraphic'/><feMergeNode in='colored'/></feMerge>" +
                "</filter></defs><rect width='8' height='8' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(3, 3));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(13, 3));
        }

        [Fact]
        public void ObjectBoundingBoxPrimitiveUnits_ScaleOffsetByTargetBounds()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='150' height='90'><defs><filter id='f' primitiveUnits='objectBoundingBox'>" +
                "<feOffset dx='.1' dy='.2'/></filter></defs>" +
                "<rect x='10' y='10' width='100' height='50' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(12, 12).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(22, 22));
        }

        [Fact]
        public void GroupObjectBounds_EnableObjectBoundingBoxFilterRegions()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='50' height='30'><defs><filter id='f'><feFlood flood-color='lime'/></filter></defs>" +
                "<g filter='url(#f)'><rect x='10' y='5' width='20' height='10' fill='red'/></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(15, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(2, 2).Alpha);
        }

        [Fact]
        public void ColorMatrixSaturateZero_ProducesGrayscale()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<feColorMatrix type='saturate' values='0'/></filter></defs>" +
                "<rect width='20' height='20' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.InRange(Math.Abs(center.Red - center.Green), 0, 2);
            Assert.InRange(Math.Abs(center.Green - center.Blue), 0, 2);
            Assert.Equal((byte)255, center.Alpha);
        }

        [Fact]
        public void ColorMatrixHueRotate_UsesDegrees()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<feColorMatrix type='hueRotate' values='90'/></filter></defs>" +
                "<rect width='20' height='20' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.True(center.Green > center.Red && center.Green > center.Blue, center.ToString());
            Assert.Equal((byte)255, center.Alpha);
        }

        [Fact]
        public void ComponentTransfer_UsesLastFunctionAndIdentityForMissingChannels()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f' color-interpolation-filters='sRGB'><feComponentTransfer>" +
                "<feFuncR type='linear' slope='0' intercept='1'/>" +
                "<feFuncR type='linear' slope='0' intercept='0'/>" +
                "</feComponentTransfer></filter></defs>" +
                "<rect width='20' height='20' fill='#804020' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.Equal((byte)0, center.Red);
            Assert.Equal((byte)64, center.Green);
            Assert.Equal((byte)32, center.Blue);
            Assert.Equal((byte)255, center.Alpha);
        }

        [Fact]
        public void ComponentTransfer_ImplementsTableDiscreteLinearAndGammaFunctions()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f' color-interpolation-filters='sRGB'><feComponentTransfer>" +
                "<feFuncR type='table' tableValues='1 0'/>" +
                "<feFuncG type='discrete' tableValues='0 1'/>" +
                "<feFuncB type='gamma' amplitude='1' exponent='2' offset='0'/>" +
                "</feComponentTransfer></filter></defs>" +
                "<rect width='20' height='20' fill='#404080' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.InRange(center.Red, (byte)190, (byte)192);
            Assert.Equal((byte)0, center.Green);
            Assert.InRange(center.Blue, (byte)63, (byte)65);
            Assert.Equal((byte)255, center.Alpha);

            using var linear = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f' color-interpolation-filters='sRGB'><feComponentTransfer>" +
                "<feFuncB type='linear' slope='.5' intercept='.25'/>" +
                "</feComponentTransfer></filter></defs>" +
                "<rect width='20' height='20' fill='#000080' filter='url(#f)'/></svg>");
            Assert.True(linear.Success, linear.ErrorMessage);
            Assert.False(linear.RequiresFallback, string.Join("; ", linear.Warnings));
            Assert.InRange(linear.Bitmap.GetPixel(10, 10).Blue, (byte)127, (byte)129);
        }

        [Fact]
        public void ComponentTransfer_DefaultsToLinearRgbChannels()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'><feComponentTransfer>" +
                "<feFuncR type='linear' slope='0' intercept='.5'/>" +
                "</feComponentTransfer></filter></defs>" +
                "<rect width='20' height='20' fill='black' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.InRange(result.Bitmap.GetPixel(10, 10).Red, (byte)187, (byte)189);
        }

        [Fact]
        public void DisplacementMap_UsesNamedMapAndSelectedChannels()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='60' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='60' height='30'><feFlood flood-color='#ff0080' result='map'/>" +
                "<feDisplacementMap in='SourceGraphic' in2='map' scale='20' " +
                "xChannelSelector='R' yChannelSelector='B'/></filter></defs>" +
                "<rect x='20' y='8' width='10' height='10' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(12, 12));
            Assert.Equal(0, result.Bitmap.GetPixel(25, 12).Alpha);
        }

        [Fact]
        public void DisplacementMap_AcceptsSourceGraphicAsDisplacementInput()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f'>" +
                "<feDisplacementMap in='SourceGraphic' in2='SourceGraphic' scale='0'/>" +
                "</filter></defs><rect x='5' y='5' width='15' height='15' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FilterRegion_ClipsFinalGraphOutput()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='60' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset dx='20'/></filter></defs>" +
                "<rect x='10' y='5' width='20' height='10' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(35, 10).Alpha);
        }

        [Fact]
        public void ObjectBoundingBoxFilterRegion_ClipsFinalGraphOutput()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='480' height='100'><defs><filter id='f' filterUnits='objectBoundingBox' " +
                "x='-.3' y='0' width='1.3' height='1'><feOffset dx='80'/></filter></defs>" +
                "<rect x='60' y='10' width='360' height='40' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(200, 20));
            Assert.Equal(0, result.Bitmap.GetPixel(450, 20).Alpha);
        }

        [Theory]
        [InlineData("<feGaussianBlur stdDeviation='999'/>")]
        [InlineData("<feTurbulence/>")]
        [InlineData("<feComponentTransfer><feFuncR type='unknown'/></feComponentTransfer>")]
        [InlineData("<feDisplacementMap xChannelSelector='Q'/>")]
        public void UnsupportedOrUnboundedPrimitive_RemainsExplicitFallback(string primitive)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='30' height='30'><defs><filter id='f'>{primitive}</filter></defs>" +
                "<rect width='20' height='20' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ConcurrentFilterRenders_DoNotShareNativeFilterOwnership()
        {
            const string svg =
                "<svg width='30' height='30'><defs><filter id='f'><feGaussianBlur stdDeviation='2'/></filter></defs>" +
                "<circle cx='15' cy='15' r='8' filter='url(#f)'/></svg>";

            Parallel.For(0, 32, _ =>
            {
                using var result = new FenSvgRenderer().Render(svg);
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback);
            });
        }
    }
}
