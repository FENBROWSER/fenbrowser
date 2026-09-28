using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgFilterWorkBudgetTests
    {
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
