using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BaseFrameReusePolicyTests
    {
        [Fact]
        public void CanReuseBaseFrame_RequiresBaseFrame()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: false,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 0);

            Assert.False(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_RejectsViewportChange()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(900, 600),
                previousScrollY: 0,
                currentScrollY: 0);

            Assert.False(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_RejectsScrollJump()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 40);

            Assert.False(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_AllowsStableViewportAndScroll()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 120,
                currentScrollY: 120.2f);

            Assert.True(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_RejectsNavigationInvalidation()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 0,
                invalidationReasons: RenderFrameInvalidationReason.Navigation);

            Assert.False(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_RejectsExceededReuseStreak()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 0,
                consecutiveReuseCount: 120,
                maxConsecutiveReuseCount: 120,
                baseFrameAgeMs: 250,
                maxBaseFrameAgeMs: 2000);

            Assert.False(canReuse);
        }

        [Fact]
        public void CanReuseBaseFrame_RejectsStaleBaseFrameAge()
        {
            var canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(800, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 0,
                consecutiveReuseCount: 0,
                maxConsecutiveReuseCount: 120,
                baseFrameAgeMs: 2501,
                maxBaseFrameAgeMs: 2000);

            Assert.False(canReuse);
        }
    }
}
