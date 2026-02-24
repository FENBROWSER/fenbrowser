using System;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class PaintCompositingStabilityControllerTests
    {
        private sealed class TestClock
        {
            public long Ticks { get; private set; }
            public long Now() => Ticks;
            public void AdvanceMs(int ms) => Ticks += TimeSpan.FromMilliseconds(ms).Ticks;
        }

        [Fact]
        public void InvalidationBurst_EntersForcedRebuildMode()
        {
            var clock = new TestClock();
            var controller = new PaintCompositingStabilityController(
                burstThreshold: 3,
                forcedRebuildFrames: 2,
                burstWindow: TimeSpan.FromMilliseconds(100),
                tickProvider: clock.Now);

            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);
            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);
            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);

            Assert.True(controller.ShouldForcePaintRebuild);
            Assert.True(controller.ForceRebuildFramesRemaining > 0);
        }

        [Fact]
        public void ForcedRebuildMode_DecaysAfterBoundedRebuildFrames()
        {
            var clock = new TestClock();
            var controller = new PaintCompositingStabilityController(
                burstThreshold: 2,
                forcedRebuildFrames: 2,
                burstWindow: TimeSpan.FromMilliseconds(100),
                tickProvider: clock.Now);

            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);
            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: false);
            Assert.True(controller.ShouldForcePaintRebuild);

            controller.ObserveFrame(hasPaintInvalidationSignal: false, rebuiltPaintTree: true);
            controller.ObserveFrame(hasPaintInvalidationSignal: false, rebuiltPaintTree: true);

            Assert.False(controller.ShouldForcePaintRebuild);
            Assert.Equal(0, controller.ForceRebuildFramesRemaining);
        }

        [Fact]
        public void WindowTrim_PreventsSparseInvalidationsFromTriggeringStressMode()
        {
            var clock = new TestClock();
            var controller = new PaintCompositingStabilityController(
                burstThreshold: 3,
                forcedRebuildFrames: 3,
                burstWindow: TimeSpan.FromMilliseconds(50),
                tickProvider: clock.Now);

            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);
            clock.AdvanceMs(200);
            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);
            clock.AdvanceMs(200);
            controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: true);

            Assert.False(controller.ShouldForcePaintRebuild);
            Assert.True(controller.RecentInvalidationCount <= 1);
        }
    }
}
