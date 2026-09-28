using System.Diagnostics;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgFilterWorkBudgetTests
    {
        private const string Ns = "xmlns='http://www.w3.org/2000/svg'";

        [Fact]
        public void StackedLargeMorphology_IsRefusedBeforeSkiaRunsIt()
        {
            // Thirty-two radius-256 morphologies over 2048x2048 held a render thread
            // for over two minutes before the work budget existed.
            string primitives = string.Concat(Enumerable.Repeat("<feMorphology operator='erode' radius='256'/>", 32));
            string svg = $"<svg {Ns} width='2048' height='2048'><filter id='f'>{primitives}</filter>" +
                         "<rect width='2048' height='2048' filter='url(#f)'/></svg>";

            var clock = Stopwatch.StartNew();
            using var result = new FenSvgRenderer().Render(svg);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Contains("SVG filter work", result.ErrorMessage);
            Assert.True(clock.ElapsedMilliseconds < 2000, $"refusal took {clock.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void ModestFilter_StaysInsideTheDefaultBudget()
        {
            string svg = $"<svg {Ns} width='512' height='512'><filter id='f'>" +
                         "<feGaussianBlur stdDeviation='4'/><feMorphology radius='2'/>" +
                         "<feSpecularLighting><fePointLight x='1' y='1' z='50'/></feSpecularLighting>" +
                         "</filter><rect width='512' height='512' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void Work_IsChargedInDeviceSpace()
        {
            // The same 64x64 user-space filter costs 16x more under a 4x viewBox
            // scale, because Skia runs it on 256x256 device pixels.
            var limits = SvgRenderLimits.Default;
            limits.MaxFilterWorkUnits = 64L * 64 * (2 * (4 + 4) + 1) * 4;
            string Svg(int size) =>
                $"<svg {Ns} width='{size}' height='{size}' viewBox='0 0 64 64'>" +
                "<filter id='f' x='0' y='0' width='1' height='1'><feMorphology radius='1'/></filter>" +
                "<rect width='64' height='64' filter='url(#f)'/></svg>";

            using var small = new FenSvgRenderer().Render(Svg(64), limits);
            using var large = new FenSvgRenderer().Render(Svg(256), limits);

            Assert.True(small.Success, small.ErrorMessage);
            Assert.False(large.Success);
            Assert.Contains("SVG filter work", large.ErrorMessage);
        }

        [Fact]
        public void Work_AccumulatesAcrossFilters()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxFilterWorkUnits = 100L * 100 * SvgRenderEngine.LightingWorkPerPixel * 3 / 2;
            const string lighting = "<feDiffuseLighting><fePointLight x='1' y='1' z='9'/></feDiffuseLighting>";
            string One = $"<svg {Ns} width='100' height='100'><filter id='f' x='0' y='0' width='1' height='1'>" +
                         lighting + "</filter><rect width='100' height='100' filter='url(#f)'/></svg>";
            string Two = $"<svg {Ns} width='100' height='100'><filter id='f' x='0' y='0' width='1' height='1'>" +
                         lighting + "</filter><rect width='100' height='100' filter='url(#f)'/>" +
                         "<rect width='100' height='100' filter='url(#f)'/></svg>";

            using var one = new FenSvgRenderer().Render(One, limits);
            using var two = new FenSvgRenderer().Render(Two, limits);

            Assert.True(one.Success, one.ErrorMessage);
            Assert.False(two.Success);
            Assert.Contains("SVG filter work", two.ErrorMessage);
        }

        [Fact]
        public void Limits_FillAndCapTheWorkBudget()
        {
            Assert.Equal(SvgRenderLimits.Default.MaxFilterWorkUnits,
                SvgRenderLimits.Normalize(new SvgRenderLimits()).MaxFilterWorkUnits);
            Assert.True(SvgRenderLimits.Strict.MaxFilterWorkUnits < SvgRenderLimits.Default.MaxFilterWorkUnits);

            var greedy = SvgRenderLimits.Default;
            greedy.MaxFilterWorkUnits = long.MaxValue;
            Assert.Equal(4L * 1024 * 1024 * 1024, SvgRenderLimits.Normalize(greedy).MaxFilterWorkUnits);
        }

        [Fact]
        public void Resources_ChargeAtomicallyAndNeverOverflow()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxFilterWorkUnits = 100;
            using var resources = new SvgRenderResources(limits);

            Assert.True(resources.TryChargeFilterWork(60));
            Assert.False(resources.TryChargeFilterWork(41));
            Assert.Equal(60, resources.FilterWorkUnits);
            Assert.False(resources.TryChargeFilterWork(long.MaxValue));
            Assert.False(resources.TryChargeFilterWork(-1));
            Assert.True(resources.TryChargeFilterWork(40));
        }
    }
}
