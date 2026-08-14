using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: HTML Living Standard §8.1.4.3 "Update the rendering" — observers run before
    // animation frame callbacks, animation frame callbacks run before paint. Microtask
    // checkpoints drain between each step.
    [Collection("Engine Tests")]
    public class EventLoopRenderOrderingTests
    {
        [Fact]
        public void RenderingUpdate_RunsObserversBeforeAnimationFramesBeforePaint()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            var log = new List<string>();

            loop.SetObserverCallback(() => log.Add("observers"));
            loop.SetRenderCallback(() => log.Add("paint"));
            loop.ScheduleAnimationFrame(() => log.Add("raf"));
            loop.NotifyLayoutDirty();

            loop.ProcessRenderingUpdate();

            Assert.Equal(new[] { "observers", "raf", "paint" }, log);
        }

        [Fact]
        public void RenderingUpdate_DrainsMicrotasksBetweenSteps()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            var log = new List<string>();

            loop.SetObserverCallback(() =>
            {
                log.Add("observers");
                loop.ScheduleMicrotask(() => log.Add("microtask-after-observers"));
            });
            loop.ScheduleAnimationFrame(() =>
            {
                log.Add("raf");
                loop.ScheduleMicrotask(() => log.Add("microtask-after-raf"));
            });
            loop.SetRenderCallback(() => log.Add("paint"));
            loop.NotifyLayoutDirty();

            loop.ProcessRenderingUpdate();

            // Per spec: each observer/rAF step is followed by a microtask checkpoint
            // before the next step runs.
            var observersIdx = log.IndexOf("observers");
            var microObsIdx = log.IndexOf("microtask-after-observers");
            var rafIdx = log.IndexOf("raf");
            var microRafIdx = log.IndexOf("microtask-after-raf");
            var paintIdx = log.IndexOf("paint");

            Assert.True(observersIdx < microObsIdx, "microtask after observers must drain before rAF");
            Assert.True(microObsIdx < rafIdx);
            Assert.True(rafIdx < microRafIdx, "microtask after rAF must drain before paint");
            Assert.True(microRafIdx < paintIdx);
        }

        [Fact]
        public void RenderingUpdate_CoalescesDirtySignalsWithinOneFrame()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            var paintCount = 0;
            loop.SetRenderCallback(() => paintCount++);

            loop.NotifyLayoutDirty();
            loop.ProcessRenderingUpdate();

            // A second timer/DOM mutation in the same frame must wait for the
            // next rendering opportunity instead of forcing another paint.
            loop.NotifyLayoutDirty();
            loop.ProcessRenderingUpdate();

            Assert.Equal(1, paintCount);
        }

        [Fact]
        public void RenderingUpdate_PreservesMutationRaisedByRenderCallback()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            var paintCount = 0;
            loop.SetRenderCallback(() =>
            {
                paintCount++;
                if (paintCount == 1)
                {
                    loop.NotifyLayoutDirty();
                }
            });

            loop.NotifyLayoutDirty();
            loop.ProcessRenderingUpdate();

            Thread.Sleep(20);
            loop.ProcessRenderingUpdate();

            Assert.Equal(2, paintCount);
        }

        [Fact]
        public void AnimationFrameCallback_RunsInAnimationPhase()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            EnginePhase observedPhase = EnginePhase.Idle;

            loop.ScheduleAnimationFrame(() =>
            {
                observedPhase = EngineContext.Current.CurrentPhase;
            });
            loop.NotifyLayoutDirty();
            loop.ProcessRenderingUpdate();

            Assert.Equal(EnginePhase.Animation, observedPhase);
        }

        [Fact]
        public void MicrotaskQueued_FromAnimationFrame_DrainsImmediately()
        {
            var loop = EventLoopCoordinator.CreateIsolated();
            var log = new List<string>();

            loop.ScheduleAnimationFrame(() =>
            {
                log.Add("raf1");
                loop.ScheduleMicrotask(() => log.Add("mt"));
            });
            loop.ScheduleAnimationFrame(() => log.Add("raf2"));
            loop.NotifyLayoutDirty();

            loop.ProcessRenderingUpdate();

            // Both rAFs are dequeued in a single batch (per spec) but microtask
            // checkpoint runs between them.
            Assert.Equal(new[] { "raf1", "mt", "raf2" }, log);
        }

    }
}
