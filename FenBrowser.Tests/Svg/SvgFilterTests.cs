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
        public void FilterHref_InheritsTemplatePrimitivesAndRegions()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs>" +
                "<filter id='base' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>" +
                "<feFlood flood-color='lime'/></filter>" +
                "<filter id='derived' href='#base'/></defs>" +
                "<rect width='20' height='20' filter='url(#derived)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FloodCurrentColor_UsesReferencedElementColor()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<feFlood flood-color='currentColor'/></filter></defs>" +
                "<rect width='20' height='20' color='blue' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FloodProperties_DoNotInheritFromFilteredTarget()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>" +
                "<feFlood/></filter></defs>" +
                "<rect width='20' height='20' fill='white' flood-color='blue' " +
                "flood-opacity='.25' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FloodProperties_InheritThroughFilterDomChain()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20' " +
                "flood-color='lime' flood-opacity='.5'><feFlood/></filter></defs>" +
                "<rect width='20' height='20' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor pixel = result.Bitmap.GetPixel(10, 10);
            Assert.Equal((byte)0, pixel.Red);
            Assert.Equal((byte)255, pixel.Green);
            Assert.Equal((byte)0, pixel.Blue);
            Assert.InRange(pixel.Alpha, (byte)120, (byte)136);
        }

        [Fact]
        public void LightingColor_DoesNotInheritFromFilteredTarget()
        {
            const string filter =
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>" +
                "<feDiffuseLighting surfaceScale='2'><feDistantLight elevation='90'/>" +
                "</feDiffuseLighting></filter>";
            const string prefix = "<svg width='20' height='20'><defs>" + filter + "</defs>" +
                "<rect width='20' height='20' fill='white' ";
            const string suffix = "filter='url(#f)'/></svg>";

            using var baseline = new FenSvgRenderer().Render(prefix + suffix);
            using var candidate = new FenSvgRenderer().Render(
                prefix + "lighting-color='red' " + suffix);

            Assert.True(baseline.Success, baseline.ErrorMessage);
            Assert.True(candidate.Success, candidate.ErrorMessage);
            Assert.False(baseline.RequiresFallback, string.Join("; ", baseline.Warnings));
            Assert.False(candidate.RequiresFallback, string.Join("; ", candidate.Warnings));
            for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(baseline.Bitmap.GetPixel(x, y), candidate.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void FilterRegion_IsRejectedBeforeOversizedPictureAdmission()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'><defs>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>" +
                "<feImage href='#source'/></filter><path id='source' d='M0 0H10V10H0Z'/>" +
                "</defs><rect width='10' height='10' filter='url(#f)'/></svg>",
                new SvgRenderLimits { MaxRasterWidth = 10, MaxRasterHeight = 10 });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("raster admission", StringComparison.Ordinal));
        }

        [Fact]
        public void NestedOpacity_IsIncludedInEffectLayerAdmission()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'><feOffset dx='1'/></filter></defs>" +
                "<g filter='url(#f)'><g opacity='.5'><rect width='10' height='10' fill='red'/></g></g></svg>",
                new SvgRenderLimits { MaxActiveLayers = 1 });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("layer budget", StringComparison.Ordinal));
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

        [Theory]
        [InlineData("feDiffuseLighting", "diffuseConstant='1'")]
        [InlineData("feSpecularLighting", "specularConstant='1' specularExponent='4'")]
        public void DistantLighting_UsesAlphaSurfaceAndLightingColor(
            string primitive,
            string parameters)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<" + primitive + " " + parameters + " surfaceScale='2' lighting-color='red'>" +
                "<feDistantLight azimuth='0' elevation='90'/></" + primitive + ">" +
                "</filter></defs><rect width='20' height='20' fill='white' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(10, 10);
            Assert.True(center.Red > 200, center.ToString());
            Assert.True(center.Green < 5 && center.Blue < 5, center.ToString());
            Assert.True(center.Alpha > 0, center.ToString());
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
        [InlineData("<feMorphology operator='grow' radius='1'/>")]
        [InlineData("<feMorphology operator='' radius='1'/>")]
        public void UnsupportedOrUnboundedPrimitive_FailsClosedAsExplicitUnsupported(string primitive)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='30' height='30'><defs><filter id='f'>{primitive}</filter></defs>" +
                "<rect width='20' height='20' filter='url(#f)'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback || result.HadResourceRejection);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void MorphologyOperators_ApplyErodeAndDilateWithoutFallback()
        {
            const string erode =
                "<svg width='30' height='30'><defs><filter id='f'>" +
                "<feMorphology operator='erode' radius='2'/></filter></defs>" +
                "<rect x='5' y='5' width='20' height='20' fill='red' filter='url(#f)'/></svg>";
            const string dilate =
                "<svg width='30' height='30'><defs><filter id='f'>" +
                "<feMorphology operator='dilate' radius='2'/></filter></defs>" +
                "<rect x='5' y='5' width='20' height='20' fill='red' filter='url(#f)'/></svg>";

            using var eroded = new FenSvgRenderer().Render(erode);
            using var dilated = new FenSvgRenderer().Render(dilate);

            Assert.True(eroded.Success, eroded.ErrorMessage);
            Assert.True(dilated.Success, dilated.ErrorMessage);
            Assert.False(eroded.RequiresFallback, string.Join("; ", eroded.Warnings));
            Assert.False(dilated.RequiresFallback, string.Join("; ", dilated.Warnings));
            Assert.Equal(0, eroded.Bitmap.GetPixel(3, 15).Alpha);
            Assert.True(eroded.Bitmap.GetPixel(15, 15).Red > 200);
            Assert.True(dilated.Bitmap.GetPixel(3, 15).Red > 200);
        }

        [Fact]
        public void FilterTemplateChain_ResolvesPrimitivesThroughEveryHref()
        {
            const string svg =
                "<svg width='40' height='40'><defs>" +
                "<filter id='f4' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset dx='2'/></filter>" +
                "<filter id='f3' href='#f4'/>" +
                "<filter id='f2' href='#f3'/>" +
                "<filter id='f1' href='#f2'/>" +
                "</defs><rect x='5' y='5' width='30' height='30' fill='red' filter='url(#f1)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(7, 20));
            Assert.Equal(0, result.Bitmap.GetPixel(4, 20).Alpha);
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

        [Fact]
        public void EmptyFilterTemplate_ReplacesTheSourceWithATransparentResult()
        {
            const string svg =
                "<svg width='20' height='20'><defs><filter id='f'/></defs>" +
                "<rect width='20' height='20' fill='red' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(0, CountOpaquePixels(result));
        }

        [Fact]
        public void EmptyFilterTemplateWithTitleDescMetadata_AlsoReplacesTheSource()
        {
            const string svg =
                "<svg width='20' height='20'><defs><filter id='f'>" +
                "<title>t</title><desc>d</desc><metadata>m</metadata></filter></defs>" +
                "<rect width='20' height='20' fill='red' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(0, CountOpaquePixels(result));
        }

        [Fact]
        public void UnresolvedLocalFilterReference_RendersTheSourceUnfiltered()
        {
            const string svg =
                "<svg width='20' height='20'>" +
                "<rect width='20' height='20' fill='red' filter='url(#notthere)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(20 * 20, CountOpaquePixels(result));
        }

        [Fact]
        public void FilterReferenceToANonFilterElement_RendersTheSourceUnfiltered()
        {
            const string svg =
                "<svg width='20' height='20'><rect id='r' width='1' height='1'/>" +
                "<rect width='20' height='20' fill='red' filter='url(#r)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(20 * 20, CountOpaquePixels(result));
        }

        [Fact]
        public void DiffuseAndSpecularLighting_RenderThroughFirstPartyFilterLayer()
        {
            const string diffuse =
                "<svg width='30' height='30'><defs><filter id='f'>" +
                "<feDiffuseLighting><fePointLight x='15' y='15' z='20'/></feDiffuseLighting>" +
                "</filter></defs><rect width='20' height='20' filter='url(#f)'/></svg>";
            const string specular =
                "<svg width='30' height='30'><defs><filter id='f'>" +
                "<feSpecularLighting specularExponent='12'>" +
                "<feDistantLight azimuth='45' elevation='60'/></feSpecularLighting>" +
                "</filter></defs><rect width='20' height='20' filter='url(#f)'/></svg>";

            using var diffuseResult = new FenSvgRenderer().Render(diffuse);
            Assert.True(diffuseResult.Success, diffuseResult.ErrorMessage);
            Assert.False(diffuseResult.RequiresFallback);

            using var specularResult = new FenSvgRenderer().Render(specular);
            Assert.True(specularResult.Success, specularResult.ErrorMessage);
            Assert.False(specularResult.RequiresFallback);
        }

        private static int CountOpaquePixels(SvgRenderResult result)
        {
            SKBitmap bitmap = result.Bitmap;
            Assert.NotNull(bitmap);
            int opaque = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0) opaque++;
                }
            }
            return opaque;
        }

        private static SKColor Unpremultiply(SKColor premultiplied)
        {
            if (premultiplied.Alpha == 0) return SKColors.Transparent;
            return new SKColor(
                (byte)Math.Min(255, premultiplied.Red * 255 / premultiplied.Alpha),
                (byte)Math.Min(255, premultiplied.Green * 255 / premultiplied.Alpha),
                (byte)Math.Min(255, premultiplied.Blue * 255 / premultiplied.Alpha),
                premultiplied.Alpha);
        }

        [Fact]
        public void FillPaint_PaintsTheTargetFillAcrossTheWholeFilterRegion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='40' height='40'><feOffset in='FillPaint'/></filter></defs>" +
                "<rect x='15' y='15' width='10' height='10' fill='blue' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));

            // Conceptually infinite extent: the fill paint is not clipped to the
            // target geometry, so every pixel of the filter region is blue.
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(38, 38));
        }

        [Fact]
        public void StrokePaint_PaintsTheTargetStrokeAcrossTheWholeFilterRegion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='40' height='40'><feOffset in='StrokePaint'/></filter></defs>" +
                "<rect x='15' y='15' width='10' height='10' stroke='lime' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(38, 38));
        }

        [Fact]
        public void FillPaint_AppliesTheTargetsOwnFillOpacity()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset in='FillPaint'/></filter></defs>" +
                "<rect width='30' height='30' fill='red' fill-opacity='.5' " +
                "filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor sampled = Unpremultiply(result.Bitmap.GetPixel(15, 15));
            Assert.Equal(255, sampled.Red);
            Assert.Equal(0, sampled.Green);
            Assert.Equal(0, sampled.Blue);
            Assert.InRange((int)sampled.Alpha, 126, 128);
        }

        [Fact]
        public void FillPaint_ResolvesCurrentColorFromTheTargetChain()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30' color='lime'><defs><filter id='f' " +
                "filterUnits='userSpaceOnUse' x='0' y='0' width='30' height='30'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<rect width='30' height='30' fill='currentColor' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(15, 15));
        }

        [Fact]
        public void FillPaint_ResolvesAnObjectBoundingBoxGradientOnTheTargetBounds()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='60' height='40'><defs>" +
                "<linearGradient id='g'><stop offset='0' stop-color='red'/>" +
                "<stop offset='1' stop-color='blue'/></linearGradient>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='60' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<rect x='20' y='10' width='20' height='20' fill='url(#g)' " +
                "filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));

            // The gradient's unit square maps onto the target's 20x20 object
            // bounding box, so the filter region outside that box holds the pad
            // colour of whichever end it is nearest.
            Assert.True(result.Bitmap.GetPixel(22, 20).Red > 200, "centre-left stays red");
            Assert.True(result.Bitmap.GetPixel(38, 20).Blue > 200, "centre-right turns blue");
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(58, 38));
        }

        [Fact]
        public void FillPaint_ResolvesAUserSpaceGradientInTargetUserSpace()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<linearGradient id='g' gradientUnits='userSpaceOnUse' " +
                "x1='0' y1='0' x2='40' y2='0'>" +
                "<stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/>" +
                "</linearGradient>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<rect x='10' y='10' width='20' height='20' fill='url(#g)' " +
                "filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(1, 20).Red > 200, "left of the ramp is red");
            Assert.True(result.Bitmap.GetPixel(38, 20).Blue > 200, "right of the ramp is blue");
        }

        [Fact]
        public void FillPaint_RespectsAnExplicitPrimitiveSubregion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='40' height='40' primitiveUnits='userSpaceOnUse'>" +
                "<feOffset in='FillPaint' x='10' y='10' width='10' height='10'/></filter></defs>" +
                "<rect x='15' y='15' width='10' height='10' fill='blue' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 15));
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(30, 30).Alpha);
        }

        [Fact]
        public void FillPaint_NoneIsATransparentImage()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset in='FillPaint'/></filter></defs>" +
                "<rect width='30' height='30' fill='none' stroke='red' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, CountOpaquePixels(result));
        }

        [Fact]
        public void MergeNode_AcceptsFillPaintAsAStandardInput()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feMerge><feMergeNode in='SourceGraphic'/><feMergeNode in='FillPaint'/>" +
                "</feMerge></filter></defs>" +
                "<g fill='lime'><rect x='5' y='5' width='10' height='10' filter='url(#f)'/></g>" +
                "</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(35, 35));
        }

        [Theory]
        [InlineData("StrokePaint", "k1='0' k2='1' k3='-1' k4='0'",
            "fill='red' stroke='red'", 0, 0, 0, 0)]
        [InlineData("FillPaint", "k1='0' k2='2' k3='-1.5' k4='0'",
            "fill='#0F0' stroke='none'", 0, 255, 0, 128)]
        [InlineData("StrokePaint", "k1='1' k2='-.5' k3='.2' k4='-.1'",
            "fill='rgb(43,17,12)' stroke='rgb(32,42,37)'", 0, 0, 0, 153)]
        [InlineData("StrokePaint", "k1='0' k2='10' k3='20' k4='0'",
            "fill='rgb(0,127,0)' stroke='rgb(0,0,127)'", 0, 255, 255, 255)]
        public void ArithmeticComposite_OverPaintInputsMatchesTheWptReferenceColours(
            string secondInput,
            string coefficients,
            string targetPaint,
            int red,
            int green,
            int blue,
            int alpha)
        {
            // filters-composite-03-f-manual.svg states each expected result as an
            // opaque reference stroke drawn over white, so the filtered result is
            // the premultiplied value that composites onto #7FFF7F / #666 /
            // #00FFFF. Only the infinite extent of the paint inputs makes the
            // whole region carry that result, and the rect's own stroke width is
            // deliberately not the document's default to pin that the keyword
            // exposes the stroke paint rather than the stroke geometry.
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs>" +
                "<filter id='f' x='0' y='0' width='1' height='1'>" +
                "<feComposite operator='arithmetic' in='FillPaint' in2='" + secondInput + "' " +
                coefficients + "/></filter></defs>" +
                "<rect x='5' y='5' width='20' height='20' stroke-width='2' " +
                targetPaint + " filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor sampled = Unpremultiply(result.Bitmap.GetPixel(15, 15));
            if (alpha == 0)
            {
                Assert.Equal(0, sampled.Alpha);
                return;
            }
            Assert.Equal((byte)red, sampled.Red);
            Assert.Equal((byte)green, sampled.Green);
            Assert.Equal((byte)blue, sampled.Blue);
            Assert.InRange((int)sampled.Alpha, alpha - 1, alpha + 1);
        }

        [Theory]
        [InlineData("BackgroundImage")]
        [InlineData("BackgroundAlpha")]
        public void BackdropFilterInputs_FailClosedWithAnAccurateReason(string keyword)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset in='" + keyword + "'/></filter></defs>" +
                "<g enable-background='new'><rect width='30' height='30' fill='red' " +
                "filter='url(#f)'/></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains(
                    "filter input '" + keyword + "' requires compatibility fallback: " +
                    "the renderer paints each frame in a single pass and captures no document backdrop",
                    StringComparison.Ordinal));
            Assert.Contains("filter-effects", result.FallbackReasonCodes);
        }

        [Fact]
        public void FillPaint_WithAnUnresolvablePaintServer_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset in='FillPaint'/></filter></defs>" +
                "<rect width='30' height='30' fill='url(#missing)' filter='url(#f)'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains(
                    "'FillPaint' input paint server is unresolvable", StringComparison.Ordinal));
            Assert.Contains("paint-server", result.FallbackReasonCodes);
        }

        [Fact]
        public void StrokePaint_WithAZeroStrokeWidth_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='30' height='30'><defs><filter id='f' filterUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><feOffset in='StrokePaint'/></filter></defs>" +
                "<rect width='30' height='30' stroke='red' stroke-width='0' " +
                "filter='url(#f)'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains(
                    "'StrokePaint' input needs a resolvable stroke geometry",
                    StringComparison.Ordinal));
            Assert.Contains("unsupported-feature", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("FillPaint", "fill='blue'")]
        [InlineData("StrokePaint", "stroke='blue' stroke-width='2'")]
        public void PaintInput_OnAUseTargetThatPaintsNothing_ResolvesTheInstantiationPaint(
            string keyword,
            string instancePaint)
        {
            // A 'use' styles its whole referenced subtree, so a target that declares
            // no paint of its own is painted with the instantiation's. The keyword
            // names that paint: reading the target back off its document-tree
            // ancestry yields the default black fill / absent stroke instead, which
            // is a filter input the element never painted with, unrecoverable once
            // composited. The walk already has the correct style, so the filter build
            // takes it rather than replaying a chain that is not the one in force.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<rect id='r' x='15' y='15' width='10' height='10' filter='url(#f)'/>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='" + keyword + "'/></filter></defs>" +
                "<use href='#r' " + instancePaint + "/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(38, 38));
        }

        [Fact]
        public void PaintInput_OnAUseTargetWithItsOwnPaint_KeepsTheTargetDeclaration()
        {
            // The threaded style is the one the walk paints the target with, not the
            // instantiation's raw style: a declaration on the target itself is
            // resolved on top of the instance and still wins.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<rect id='r' x='15' y='15' width='10' height='10' fill='red' filter='url(#f)'/>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<use href='#r' fill='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void PaintInput_OnAContainerUseInstanceTarget_ResolvesTheInstantiationPaint()
        {
            // A container paints nothing of its own, so its own filter region was
            // the one case a browser rendered and this renderer used to refuse.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<g id='g' filter='url(#f)'><rect width='40' height='40'/></g>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<use href='#g' fill='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void PaintInput_OnASwitchUseInstanceTarget_ResolvesTheInstantiationPaint()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<switch id='sw' filter='url(#f)'><rect width='40' height='40'/></switch>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<use href='#sw' fill='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void PaintInput_OnAUseInstanceReachingAUse_ResolvesTheInstantiationPaint()
        {
            // The instantiated 'use' is itself the filter target, so the element the
            // keyword names is styled by the outer instance.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<rect id='r' width='40' height='40'/>" +
                "<use id='inner' href='#r' filter='url(#f)'/>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<use href='#inner' fill='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void PaintInput_ONestedSvgUseInstanceTarget_ResolvesTheInstantiationPaint()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<svg id='n' width='40' height='40' filter='url(#f)'>" +
                "<rect width='40' height='40'/></svg>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></defs>" +
                "<use href='#n' fill='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void FillPaint_ReachedThroughAPattern_StillResolvesTheContentStyle()
        {
            // The pattern path resolves its content style with the same resolver the
            // filter input uses and hands that very style to the content walk, so
            // the two agree by construction. Only 'use' instantiation styles a
            // target from outside its own ancestry.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40'><defs>" +
                "<pattern id='p' patternUnits='userSpaceOnUse' width='40' height='40'>" +
                "<rect width='40' height='40' fill='blue' filter='url(#f)'/>" +
                "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset in='FillPaint'/></filter></pattern></defs>" +
                "<rect width='40' height='40' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(40 * 40, CountOpaquePixels(result));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 20));
        }

        [Fact]
        public void FilterPrimitiveBudget_StaysAtThirtyTwoPrimitives()
        {
            using var admitted = new FenSvgRenderer().Render(
                FilterWithPrimitives(32));
            using var exceeded = new FenSvgRenderer().Render(
                FilterWithPrimitives(33));

            Assert.True(admitted.Success, admitted.ErrorMessage);
            Assert.False(admitted.RequiresFallback, string.Join("; ", admitted.Warnings));
            AssertFailsClosed(exceeded);
            Assert.Contains(exceeded.Warnings,
                warning => warning.Contains("primitive budget", StringComparison.Ordinal));
        }

        private static string FilterWithPrimitives(int count)
        {
            var svg = new System.Text.StringBuilder(
                "<svg width='20' height='20'><defs><filter id='f' " +
                "filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>");
            for (int i = 0; i < count; i++) svg.Append("<feOffset dx='0' dy='0'/>");
            svg.Append("</filter></defs><rect width='20' height='20' fill='red' filter='url(#f)'/></svg>");
            return svg.ToString();
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
