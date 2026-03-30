using FenBrowser.FenEngine.Layout.Contexts;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class LayoutConstraintResolverTests
    {
        [Fact]
        public void ResolveWidth_UsesContainingBlockBeforeViewportWhenWidthIsUnbounded()
        {
            var state = new LayoutState(
                new SKSize(float.PositiveInfinity, 300f),
                640f,
                300f,
                1280f,
                720f);

            var resolution = LayoutConstraintResolver.ResolveWidth(state, "LayoutConstraintResolverTests.Unbounded");

            Assert.True(resolution.IsUnconstrained);
            Assert.Equal(640f, resolution.ResolvedAvailable);
            Assert.Equal("containing-block", resolution.Source);
        }

        [Fact]
        public void ResolveWidth_FallsBackToViewportWhenContainingBlockIsInvalid()
        {
            var state = new LayoutState(
                new SKSize(float.NaN, 300f),
                0f,
                300f,
                1024f,
                720f);

            var resolution = LayoutConstraintResolver.ResolveWidth(state, "LayoutConstraintResolverTests.ViewportFallback");

            Assert.True(resolution.IsUnconstrained);
            Assert.Equal(1024f, resolution.ResolvedAvailable);
            Assert.Equal("viewport", resolution.Source);
        }
    }
}
