using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public class LayoutStyleResolverHotPathTests
    {
        [Theory]
        [InlineData("STATIC", "static")]
        [InlineData("relative", "relative")]
        [InlineData(" Absolute ", "absolute")]
        [InlineData("fixed", "fixed")]
        [InlineData("Sticky", "sticky")]
        public void GetEffectivePosition_ReturnsCanonicalPositionKeyword(string value, string expected)
        {
            var style = new CssComputed { Position = value };

            Assert.Equal(expected, LayoutStyleResolver.GetEffectivePosition(style));
        }

        [Fact]
        public void GetEffectivePosition_UsesComputedMapFallbackWithoutNormalizingUnrelatedProperties()
        {
            var style = new CssComputed();
            style.Map["position"] = "fixed";
            style.Map["width"] = "25px";

            string position = LayoutStyleResolver.GetEffectivePosition(style);

            Assert.Equal("fixed", position);
            Assert.Null(style.Width);
        }

        [Fact]
        public void GetEffectivePosition_CommonCanonicalValue_DoesNotAllocate()
        {
            var style = new CssComputed { Position = "absolute" };
            Assert.Equal("absolute", LayoutStyleResolver.GetEffectivePosition(style));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1_000; index++)
            {
                LayoutStyleResolver.GetEffectivePosition(style);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }
}
