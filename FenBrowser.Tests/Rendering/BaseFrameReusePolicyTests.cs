using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BaseFrameReusePolicyTests
    {
        [Fact]
        public void DamageThenPreservedFrame_KeepsUpdatedPixels()
        {
            var viewport = new SKSize(32, 32);
            using var initialSurface = SKSurface.Create(new SKImageInfo(32, 32));
            initialSurface.Canvas.Clear(SKColors.White);
            using var initialSeed = initialSurface.Snapshot();
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0, 0, 32, 32));
            canvas.DrawImage(initialSeed, 0, 0, SKSamplingOptions.Default);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(new SKRect(4, 4, 20, 20), paint);
            using var updatedFrame = recorder.EndRecording();

            var updatedSeed = BrowserIntegration.CreateNextFrameSeed(
                updatedFrame, viewport,
                new RenderFrameResult
                {
                    RasterMode = RenderFrameRasterMode.Damage,
                    RetainedBackingStoreUpdated = true
                }, initialSeed, out _);
            try
            {
                var preservedSeed = BrowserIntegration.CreateNextFrameSeed(
                    updatedFrame, viewport,
                    new RenderFrameResult { RasterMode = RenderFrameRasterMode.PreservedBaseFrame },
                    updatedSeed, out _);
                Assert.Same(updatedSeed, preservedSeed);
                Assert.NotNull(preservedSeed);
                Assert.NotEqual(System.IntPtr.Zero, preservedSeed.Handle);
                using var pixels = SKBitmap.FromImage(preservedSeed);
                Assert.Equal(SKColors.Blue, pixels.GetPixel(10, 10));
                Assert.Equal(SKColors.White, pixels.GetPixel(25, 25));
            }
            finally
            {
                if (!ReferenceEquals(initialSeed, updatedSeed)) updatedSeed?.Dispose();
            }
        }

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

        [Fact]
        public void ReuseStreak_IncrementsOnlyWhenTheExistingSeedIsRetained()
        {
            Assert.Equal(
                8,
                BrowserIntegration.NextConsecutiveBaseFrameReuseCount(
                    canReuseBaseFrame: true,
                    retainedExistingSeed: true,
                    hasCurrentSeed: true,
                    currentCount: 7));

            Assert.Equal(
                0,
                BrowserIntegration.NextConsecutiveBaseFrameReuseCount(
                    canReuseBaseFrame: true,
                    retainedExistingSeed: false,
                    hasCurrentSeed: true,
                    currentCount: 7));
        }
    }
}
