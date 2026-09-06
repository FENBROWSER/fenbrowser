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
        public void UseFilter_CompositesAndFiltersTheInstantiatedSubtree()
        {
            const string svg =
                "<svg width='60' height='30'><defs><g id='box'><rect width='10' height='10' fill='red'/></g>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='60' height='30'>" +
                "<feOffset dx='20'/></filter></defs><use href='#box' x='5' y='5' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(8, 8).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(28, 8));
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
        public void Image_LocalFragmentRendersAsFilterGraphSource()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='20'><defs>" +
                "<g fill='lime'><path id='tile' d='M0 0h10v20H0z'/></g>" +
                "<filter id='f' x='0' y='0' width='1' height='1'>" +
                "<feImage href='#tile'/></filter></defs>" +
                "<rect width='20' height='20' fill='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 10).Alpha);
        }

        [Fact]
        public void Image_LocalFragmentsFeedNamedCompositeInputs()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'><defs>" +
                "<path id='blue' d='M0 0h20v20H0z' fill='blue'/>" +
                "<path id='red' d='M0 0h10v20H0z' fill='red'/>" +
                "<filter id='f' x='0' y='0' width='1' height='1'>" +
                "<feImage xlink:href='#blue' result='blueImage'/>" +
                "<feImage xlink:href='#red' result='redImage'/>" +
                "<feComposite in='redImage' in2='blueImage' operator='over'/>" +
                "</filter></defs><rect width='20' height='20' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 10));
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
        public void ArithmeticComposite_PreservesSvgInputCoefficientOrdering()
        {
            const string prefix =
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<feFlood flood-color='red' result='first'/>" +
                "<feFlood flood-color='blue' result='second'/>" +
                "<feComposite in='first' in2='second' operator='arithmetic' ";
            const string suffix =
                "/></filter></defs><rect width='20' height='20' filter='url(#f)'/></svg>";

            using var first = new FenSvgRenderer().Render(prefix + "k2='1'" + suffix);
            using var second = new FenSvgRenderer().Render(prefix + "k3='1'" + suffix);
            using var defaults = new FenSvgRenderer().Render(prefix + suffix);

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.True(defaults.Success, defaults.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.False(second.RequiresFallback, string.Join("; ", second.Warnings));
            Assert.False(defaults.RequiresFallback, string.Join("; ", defaults.Warnings));
            Assert.Equal(SKColors.Red, first.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Blue, second.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, defaults.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Theory]
        [InlineData("turbulence")]
        [InlineData("fractalNoise")]
        public void Turbulence_ProducesDeterministicBoundedNoise(string type)
        {
            string svg =
                "<svg width='32' height='32'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='32' height='32'><feTurbulence type='" + type +
                "' baseFrequency='.08 .12' numOctaves='3' seed='7'/></filter></defs>" +
                "<rect width='32' height='32' filter='url(#f)'/></svg>";

            using var first = new FenSvgRenderer().Render(svg);
            using var second = new FenSvgRenderer().Render(svg);

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.False(second.RequiresFallback, string.Join("; ", second.Warnings));
            Assert.Equal(first.Bitmap.GetPixel(7, 9), second.Bitmap.GetPixel(7, 9));
            Assert.NotEqual(first.Bitmap.GetPixel(7, 9), first.Bitmap.GetPixel(23, 21));
            Assert.True(first.Bitmap.GetPixel(7, 9).Alpha > 0);
        }

        [Fact]
        public void Turbulence_FractionalSeedsFollowSvgIntegerConversion()
        {
            const string prefix =
                "<svg width='24' height='24'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='24' height='24'><feTurbulence baseFrequency='.08' seed='";
            const string suffix =
                "'/></filter></defs><rect width='24' height='24' filter='url(#f)'/></svg>";

            using var baseline = new FenSvgRenderer().Render(prefix + "0" + suffix);
            Assert.True(baseline.Success, baseline.ErrorMessage);
            foreach (string seed in new[] { "-.8", "-.5", "-.2", ".2", ".5", "1.5" })
            {
                using var candidate = new FenSvgRenderer().Render(prefix + seed + suffix);
                Assert.True(candidate.Success, candidate.ErrorMessage);
                Assert.False(candidate.RequiresFallback, string.Join("; ", candidate.Warnings));
                for (int y = 0; y < 24; y++)
                    for (int x = 0; x < 24; x++)
                        Assert.Equal(baseline.Bitmap.GetPixel(x, y), candidate.Bitmap.GetPixel(x, y));
            }
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

        [Fact]
        public void ConvolveMatrix_UsesDivisorAndNormalizedBias()
        {
            using var biased = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f' color-interpolation-filters='sRGB'>" +
                "<feConvolveMatrix order='1' kernelMatrix='0' divisor='1' bias='.5' preserveAlpha='true'/>" +
                "</filter></defs><rect width='20' height='20' fill='black' filter='url(#f)'/></svg>");
            using var divided = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f' color-interpolation-filters='sRGB'>" +
                "<feConvolveMatrix order='1' kernelMatrix='2' divisor='2' preserveAlpha='true'/>" +
                "</filter></defs><rect width='20' height='20' fill='rgb(64,32,16)' filter='url(#f)'/></svg>");

            Assert.True(biased.Success, biased.ErrorMessage);
            Assert.True(divided.Success, divided.ErrorMessage);
            Assert.False(biased.RequiresFallback, string.Join("; ", biased.Warnings));
            Assert.False(divided.RequiresFallback, string.Join("; ", divided.Warnings));
            SKColor biasedCenter = biased.Bitmap.GetPixel(10, 10);
            Assert.InRange(biasedCenter.Red, (byte)127, (byte)129);
            Assert.InRange(biasedCenter.Green, (byte)127, (byte)129);
            Assert.InRange(biasedCenter.Blue, (byte)127, (byte)129);
            SKColor dividedCenter = divided.Bitmap.GetPixel(10, 10);
            Assert.InRange(dividedCenter.Red, (byte)63, (byte)65);
            Assert.InRange(dividedCenter.Green, (byte)31, (byte)33);
            Assert.InRange(dividedCenter.Blue, (byte)15, (byte)17);
        }

        [Fact]
        public void ConvolveMatrix_EdgeModesControlOutsideSamples()
        {
            const string prefix =
                "<svg width='20' height='20'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='20' height='20'><feConvolveMatrix order='3 1' " +
                "kernelMatrix='1 1 1' divisor='3' edgeMode='";
            const string suffix =
                "'/></filter></defs><rect width='20' height='20' fill='red' filter='url(#f)'/></svg>";

            using var none = new FenSvgRenderer().Render(prefix + "none" + suffix);
            using var duplicate = new FenSvgRenderer().Render(prefix + "duplicate" + suffix);

            Assert.True(none.Success, none.ErrorMessage);
            Assert.True(duplicate.Success, duplicate.ErrorMessage);
            Assert.False(none.RequiresFallback, string.Join("; ", none.Warnings));
            Assert.False(duplicate.RequiresFallback, string.Join("; ", duplicate.Warnings));
            Assert.InRange(none.Bitmap.GetPixel(0, 10).Alpha, (byte)169, (byte)171);
            Assert.Equal(255, duplicate.Bitmap.GetPixel(0, 10).Alpha);
        }

        [Theory]
        [InlineData("<feGaussianBlur stdDeviation='999'/>")]
        [InlineData("<feComponentTransfer><feFuncR type='unknown'/></feComponentTransfer>")]
        [InlineData("<feDisplacementMap xChannelSelector='Q'/>")]
        [InlineData("<feConvolveMatrix order='3' kernelMatrix='1 2'/>")]
        [InlineData("<feConvolveMatrix order='0' kernelMatrix='1'/>")]
        [InlineData("<feConvolveMatrix order='26' kernelMatrix='1'/>")]
        [InlineData("<feConvolveMatrix order='1' kernelMatrix='1' divisor='0'/>")]
        [InlineData("<feConvolveMatrix order='1' kernelMatrix='1' targetX='1'/>")]
        [InlineData("<feConvolveMatrix order='1' kernelMatrix='1' edgeMode='mirror'/>")]
        [InlineData("<feConvolveMatrix order='1' kernelMatrix='1' kernelUnitLength='2'/>")]
        [InlineData("<feImage href='image.png'/>")]
        [InlineData("<feComposite operator='arithmetic' k1='invalid'/>")]
        [InlineData("<feComposite operator='arithmetic' k4='32768'/>")]
        [InlineData("<feTurbulence baseFrequency='-.1'/>")]
        [InlineData("<feTurbulence numOctaves='17'/>")]
        [InlineData("<feTurbulence stitchTiles='stitch'/>")]
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
