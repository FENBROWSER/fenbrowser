using System;
using System.IO;
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
            string debugPath = Path.Combine(AppContext.BaseDirectory, "test_debug.txt");

            coordinator.ScheduleMicrotask(() =>
            {
                try
                {
                    var phase = EnginePhaseManager.CurrentPhase;
                    File.AppendAllText(debugPath, $"[Test] Microtask running. Phase: {phase}\r\n");
                    coordinator.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException ex)
                {
                    File.AppendAllText(debugPath, $"[Test] Catch InvalidOperation: {ex.Message}\r\n");
                    reentrantDetected = true;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(debugPath, $"[Test] Catch Exception: {ex.Message}\r\n");
                }
            });

            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
            try
            {
                File.AppendAllText(debugPath, $"Test Start Phase: {EnginePhaseManager.CurrentPhase}\r\n");
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
