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
            // Setup
            EventLoopCoordinator.ResetInstance();
            var coordinator = EventLoopCoordinator.Instance;
            bool reentrantDetected = false;

            // Scenario: Schedule a microtask that attempts to trigger another checkpoint
            // which would trigger recursion if not protected.
            coordinator.ScheduleMicrotask(() =>
            {
                try
                {
                    // DIRECT DEBUG: Check phase
                    var phase = EnginePhaseManager.CurrentPhase;
                    System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\test_debug.txt", $"[Test] Microtask running. Phase: {phase}\r\n");

                    // Directly calling Checkpoint to force the issue
                    coordinator.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException ex)
                {
                    System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\test_debug.txt", $"[Test] Catch InvalidOperation: {ex.Message}\r\n");
                    reentrantDetected = true;
                }
                catch (Exception ex)
                {
                     System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\test_debug.txt", $"[Test] Catch Exception: {ex.Message}\r\n");
                }
            });

            // Act
            // Trigger the first checkpoint safely from a "JS" phase
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
            try
            {
                // DEBUG: Print start phase
                System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\test_debug.txt", $"Test Start Phase: {EnginePhaseManager.CurrentPhase}\r\n");
                coordinator.PerformMicrotaskCheckpoint();
            }
            finally
            {
                EnginePhaseManager.TryEnterIdle();
            }

            // Assert
            Assert.True(reentrantDetected, $"Engine failed to detect re-entrant Microtask phase entry! Phase was likely not Microtasks.");
        }
    }
}
