using System;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Host;
using FenBrowser.Host.ProcessIsolation.Gpu;
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

        [Fact]
        public void CompositorThread_BurstRequestsAreCoalesced()
        {
            var root = new TestWidget(new SKColor(14, 165, 233, 255));
            root.Arrange(new SKRect(0, 0, 96, 40));
            root.Invalidate();

            var compositor = new Compositor(root) { DpiScale = 1f };
            using var compositorThread = new CompositorThread(compositor, maxFramesPerSecond: 5);

            compositorThread.UpdateViewport(96, 40, 1f);
            compositorThread.Start();

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > 0, TimeSpan.FromSeconds(2)),
                "Timed out waiting for first committed compositor frame.");

            var baseline = compositorThread.LastCommittedFrameSequence;
            for (var i = 0; i < 32; i++)
            {
                compositorThread.RequestFrame();
            }

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > baseline, TimeSpan.FromSeconds(2)),
                "Timed out waiting for coalesced compositor frame commit.");

            var snapshot = compositorThread.GetTelemetrySnapshot();
            Assert.True(snapshot.CoalescedFrameRequests > 0);
            Assert.InRange(snapshot.PendingFrameRequests, 0, 1);
        }

        [Fact]
        public void CompositorThread_HonorsFramePacingUnderRequestPressure()
        {
            var root = new TestWidget(new SKColor(168, 85, 247, 255));
            root.Arrange(new SKRect(0, 0, 128, 48));
            root.Invalidate();

            var compositor = new Compositor(root) { DpiScale = 1f };
            using var compositorThread = new CompositorThread(compositor, maxFramesPerSecond: 5);

            compositorThread.UpdateViewport(128, 48, 1f);
            compositorThread.Start();

            Assert.True(
                SpinWait.SpinUntil(() => compositorThread.LastCommittedFrameSequence > 0, TimeSpan.FromSeconds(2)),
                "Timed out waiting for first committed compositor frame.");

            var baseline = compositorThread.LastCommittedFrameSequence;
            var pressureWindow = Stopwatch.StartNew();
            while (pressureWindow.Elapsed < TimeSpan.FromMilliseconds(450))
            {
                compositorThread.RequestFrame();
                Thread.Sleep(1);
            }

            Thread.Sleep(350);

            var produced = compositorThread.LastCommittedFrameSequence - baseline;
            Assert.InRange(produced, 1, 4);

            var snapshot = compositorThread.GetTelemetrySnapshot();
            Assert.InRange(snapshot.TargetFrameIntervalMs, 190, 210);
        }

        [Fact]
        public void CompositorThread_SubmitsCompositorWorkItems()
        {
            var root = new TestWidget(new SKColor(251, 146, 60, 255));
            root.Arrange(new SKRect(0, 0, 144, 64));
            root.Invalidate();

            var submitter = new FakeCompositorWorkSubmitter();
            var compositor = new Compositor(root) { DpiScale = 1f };
            using var compositorThread = new CompositorThread(
                compositor,
                maxFramesPerSecond: 30,
                compositorWorkSubmitter: submitter);

            compositorThread.UpdateViewport(144, 64, 1f);
            compositorThread.Start();
            compositorThread.RequestFrame();

            Assert.True(
                SpinWait.SpinUntil(() => submitter.LastSubmittedFrameSequence > 0, TimeSpan.FromSeconds(2)),
                "Timed out waiting for compositor work submission.");

            var snapshot = compositorThread.GetTelemetrySnapshot();
            Assert.True(snapshot.GpuSubmissionAttemptCount > 0);
            Assert.True(snapshot.GpuSubmissionSuccessCount > 0);
            Assert.Equal(submitter.LastSubmittedFrameSequence, snapshot.LastSubmittedGpuFrameSequence);
            Assert.Equal(submitter.LastAcknowledgedFrameSequence, snapshot.LastAcknowledgedGpuFrameSequence);
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

        private sealed class FakeCompositorWorkSubmitter : ICompositorWorkSubmitter
        {
            private long _lastSubmitted;

            public bool TrySubmit(in GpuCompositorWorkItem workItem)
            {
                Interlocked.Exchange(ref _lastSubmitted, workItem.FrameSequence);
                return true;
            }

            public long LastSubmittedFrameSequence => Interlocked.Read(ref _lastSubmitted);

            public long LastAcknowledgedFrameSequence => LastSubmittedFrameSequence;
        }
    }
}
