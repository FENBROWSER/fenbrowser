using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class EventInvariantTests
    {
        [Fact]
        public void Test_Invariant_Microtasks_NonReentrant()
        {
            EventLoopCoordinator.ResetInstance();
            var coordinator = EventLoopCoordinator.Instance;
            bool reentrantDetected = false;

            coordinator.ScheduleMicrotask(() =>
            {
                try
                {
                    coordinator.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException)
                {
                    reentrantDetected = true;
                }
            });

            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
            try
            {
                coordinator.PerformMicrotaskCheckpoint();
            }
            finally
            {
                EnginePhaseManager.TryEnterIdle();
            }

            Assert.True(reentrantDetected, "Engine failed to detect re-entrant Microtask phase entry! Phase was likely not Microtasks.");
        }
    }
}
