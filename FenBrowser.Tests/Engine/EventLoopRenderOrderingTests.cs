using System.Collections.Generic;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
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
        public EventLoopRenderOrderingTests()
        {
            EventLoopCoordinator.ResetInstance();
            EnginePhaseManager.EnterPhase(EnginePhase.Idle);
        }

        [Fact]
        public void RenderingUpdate_RunsObserversBeforeAnimationFramesBeforePaint()
        {
            var loop = EventLoopCoordinator.Instance;
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
            var loop = EventLoopCoordinator.Instance;
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
        public void AnimationFrameCallback_RunsInAnimationPhase()
        {
            var loop = EventLoopCoordinator.Instance;
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
            var loop = EventLoopCoordinator.Instance;
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

        [Fact]
        public void QueueMicrotaskGlobal_ExposedToScript()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var log = [];
                queueMicrotask(function() { log.push('mt'); });
                log.push('sync');
            ");

            // Microtasks fire as part of script completion via EventLoop.
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            var log = runtime.GetGlobal("log");
            // log is a JS array; verify entries via runtime API.
            var asObj = log.AsObject();
            Assert.Equal("sync", asObj.Get("0").ToString2());
            Assert.Equal("mt", asObj.Get("1").ToString2());
        }
    }
}
