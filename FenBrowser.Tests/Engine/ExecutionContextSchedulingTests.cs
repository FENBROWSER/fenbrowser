using System;
using System.Threading;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class ExecutionContextSchedulingTests
    {
        public ExecutionContextSchedulingTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void ScheduleMicrotask_DefaultScheduler_RunsOnlyAtCheckpoint()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            var ran = false;
            var phaseSeen = EnginePhase.Idle;

            context.ScheduleMicrotask(() =>
            {
                ran = true;
                phaseSeen = EngineContext.Current.CurrentPhase;
            });

            Assert.False(ran);
            Assert.Equal(1, EventLoopCoordinator.Instance.MicrotaskCount);

            EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
            try
            {
                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            }
            finally
            {
                EngineContext.Current.EndPhase();
            }

            Assert.True(ran);
            Assert.Equal(EnginePhase.Microtasks, phaseSeen);
            Assert.Equal(0, EventLoopCoordinator.Instance.MicrotaskCount);
        }

        [Fact]
        public void ScheduleCallback_DefaultScheduler_EnqueuesTimerTaskBeforeExecution()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            using var taskQueued = new ManualResetEventSlim(false);
            var ran = false;
            var phaseSeen = EnginePhase.Idle;
            void HandleWorkEnqueued() => taskQueued.Set();

            EventLoopCoordinator.Instance.OnWorkEnqueued += HandleWorkEnqueued;
            try
            {
                context.ScheduleCallback(() =>
                {
                    ran = true;
                    phaseSeen = EngineContext.Current.CurrentPhase;
                }, 0);

                Assert.False(ran);
                Assert.True(taskQueued.Wait(TimeSpan.FromSeconds(2)), "Timer callback was not enqueued onto the event loop.");
                Assert.True(EventLoopCoordinator.Instance.TaskCount > 0);

                EventLoopCoordinator.Instance.ProcessNextTask();

                Assert.True(ran);
                Assert.Equal(EnginePhase.JSExecution, phaseSeen);
                Assert.Equal(0, EventLoopCoordinator.Instance.TaskCount);
            }
            finally
            {
                EventLoopCoordinator.Instance.OnWorkEnqueued -= HandleWorkEnqueued;
            }
        }
    }
}
