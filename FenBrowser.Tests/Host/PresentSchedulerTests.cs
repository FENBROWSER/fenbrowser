using FenBrowser.Host;
using FenBrowser.Host.Widgets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class PresentSchedulerTests
    {
        [Fact]
        public void FirstWakeUpPresentsTheBootstrapFrame()
        {
            var scheduler = new PresentScheduler(() => { });

            Assert.True(scheduler.TryBeginPresent());
            Assert.False(scheduler.TryBeginPresent());
        }

        [Fact]
        public void IdleWakeUpDoesNotPresent()
        {
            var scheduler = new PresentScheduler(() => { }, initiallyRequested: false);

            Assert.False(scheduler.TryBeginPresent());
        }

        [Fact]
        public void RequestsWakeTheLoopOnceUntilConsumed()
        {
            int wakes = 0;
            var scheduler = new PresentScheduler(() => wakes++, initiallyRequested: false);

            scheduler.Request();
            scheduler.Request();
            scheduler.Request();
            Assert.Equal(1, wakes);

            Assert.True(scheduler.TryBeginPresent());
            Assert.False(scheduler.TryBeginPresent());

            scheduler.Request();
            Assert.Equal(2, wakes);
        }

        [Fact]
        public void RequestDuringAFrameSchedulesTheNextOne()
        {
            int wakes = 0;
            var scheduler = new PresentScheduler(() => wakes++, initiallyRequested: false);
            scheduler.Request();

            Assert.True(scheduler.TryBeginPresent());
            // A request raised while the frame is drawn must not be swallowed by it.
            scheduler.Request();

            Assert.Equal(2, wakes);
            Assert.True(scheduler.TryBeginPresent());
        }

        [Fact]
        public void PendingWorkPresentsWithoutARequest()
        {
            bool pending = true;
            var scheduler = new PresentScheduler(() => { }, initiallyRequested: false)
            {
                PendingWork = () => pending
            };

            Assert.True(scheduler.TryBeginPresent());
            pending = false;
            Assert.False(scheduler.TryBeginPresent());
        }
    }

    [Collection("Host UI Tests")]
    public class CompositorFrameVersionTests
    {
        [Fact]
        public void RepaintAdvancesVersionButCleanCompositeDoesNot()
        {
            var root = new FillWidget();
            root.Arrange(new SKRect(0, 0, 64, 32));
            var compositor = new Compositor(root) { DpiScale = 1f };
            using var bitmap = new SKBitmap(64, 32);
            using var canvas = new SKCanvas(bitmap);

            compositor.Composite(canvas, new SKSize(64, 32));
            var afterBootstrap = compositor.FrameVersion;
            Assert.True(afterBootstrap > 0);

            compositor.Composite(canvas, new SKSize(64, 32));
            Assert.Equal(afterBootstrap, compositor.FrameVersion);

            root.Invalidate(new SKRect(0, 0, 8, 8));
            compositor.Composite(canvas, new SKSize(64, 32));
            Assert.True(compositor.FrameVersion > afterBootstrap);
        }

        [Fact]
        public void LayerChangesAdvanceVersion()
        {
            var compositor = new Compositor(new FillWidget());
            var layer = new CompositorLayer("overlay", new SKRect(0, 0, 10, 10), _ => { });

            var before = compositor.FrameVersion;
            compositor.AddLayer(layer);
            var afterAdd = compositor.FrameVersion;
            compositor.RemoveLayer(layer);
            var afterRemove = compositor.FrameVersion;
            compositor.RemoveLayer(layer);

            Assert.True(afterAdd > before);
            Assert.True(afterRemove > afterAdd);
            Assert.Equal(afterRemove, compositor.FrameVersion);
        }

        private sealed class FillWidget : Widget
        {
            protected override SKSize OnMeasure(SKSize availableSpace) => availableSpace;

            protected override void OnArrange(SKRect finalRect)
            {
            }

            public override void Paint(SKCanvas canvas)
            {
                using var paint = new SKPaint { Color = SKColors.SteelBlue };
                canvas.DrawRect(Bounds, paint);
            }
        }
    }
}
