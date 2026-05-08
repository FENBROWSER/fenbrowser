using System;
using System.Threading;
using FenBrowser.Host;
using FenBrowser.Host.Widgets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    [Collection("Host UI Tests")]
    public class CompositorThreadTests
    {
        [Fact]
        public void CompositorThread_ComposesAndPublishesLatestFrame()
        {
            var root = new TestWidget(new SKColor(220, 38, 38, 255));
            root.Arrange(new SKRect(0, 0, 120, 48));
            root.Invalidate();

            var compositor = new Compositor(root) { DpiScale = 1f };
            using var compositorThread = new CompositorThread(compositor);

            compositorThread.UpdateViewport(120, 48, 1f);
            compositorThread.Start();
            compositorThread.RequestFrame();

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > 0, TimeSpan.FromSeconds(2)),
                "Timed out waiting for first committed compositor frame.");

            using var bitmap = new SKBitmap(120, 48);
            using var canvas = new SKCanvas(bitmap);

            Assert.True(compositorThread.TryDrawLatest(canvas, new SKSize(120, 48)));
        }

        [Fact]
        public void CompositorThread_RequestsProduceNewCommittedFrames()
        {
            var root = new TestWidget(new SKColor(22, 163, 74, 255));
            root.Arrange(new SKRect(0, 0, 96, 40));
            root.Invalidate();

            var compositor = new Compositor(root) { DpiScale = 1f };
            using var compositorThread = new CompositorThread(compositor);

            compositorThread.UpdateViewport(96, 40, 1f);
            compositorThread.Start();
            compositorThread.RequestFrame();

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > 0, TimeSpan.FromSeconds(2)),
                "Timed out waiting for first committed compositor frame.");

            var firstSequence = compositorThread.LastCommittedFrameSequence;
            root.Invalidate(new SKRect(0, 0, 24, 24));
            compositorThread.RequestFrame();

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > firstSequence, TimeSpan.FromSeconds(2)),
                "Timed out waiting for second committed compositor frame.");
        }

        private sealed class TestWidget : Widget
        {
            private readonly SKColor _fill;

            public TestWidget(SKColor fill)
            {
                _fill = fill;
            }

            protected override SKSize OnMeasure(SKSize availableSpace)
            {
                return availableSpace;
            }

            protected override void OnArrange(SKRect finalRect)
            {
            }

            public override void Paint(SKCanvas canvas)
            {
                using var paint = new SKPaint
                {
                    Color = _fill,
                    Style = SKPaintStyle.Fill
                };

                canvas.DrawRect(Bounds, paint);
            }
        }
    }
}
