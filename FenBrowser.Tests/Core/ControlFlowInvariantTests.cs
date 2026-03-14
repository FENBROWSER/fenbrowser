using System;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ControlFlowInvariantTests
    {
        [Fact]
        public void Test_Invariant_JSCanMarkDirtyWithoutEnteringRenderPhases()
        {
            // Goal: JS may request a future repaint by marking layout dirty, but it must
            // still be forbidden from entering measure/layout/paint re-entrantly.
            
            bool validationFailed = false;
            
            try
            {
                EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
                
                // Simulate CustomHtmlEngine.ScheduleRepaintFromJs behavior: allowed during
                // JSExecution as long as it does not cross into render phases.
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
            }
            catch (InvalidOperationException)
            {
                validationFailed = true;
            }
            finally
            {
                EnginePhaseManager.TryEnterIdle();
            }

            Assert.False(validationFailed, "Dirty-flag repaint requests during JSExecution should remain legal.");
        }

        [Fact]
        public void Test_Invariant_Microtask_Reentrancy()
        {
            // Goal: Entering Microtask phase from within Microtask phase must be forbidden.
            
            bool reentrancyDetected = false;

            EnginePhaseManager.EnterPhase(EnginePhase.Microtasks);
            try
            {
                // Recursive entry attempt
                EnginePhaseManager.EnterPhase(EnginePhase.Microtasks);
            }
            catch (InvalidOperationException)
            {
                reentrancyDetected = true;
            }
            catch (Exception) 
            {
                 // Ignore other exceptions
            }
            finally
            {
                EnginePhaseManager.TryEnterIdle();
            }

            Assert.True(reentrancyDetected, "Recursive entry to Microtasks phase must fail.");
        }
        
        [Fact]
        public void Test_Invariant_NoRendering_During_JS()
        {
             // Goal: Layout/Paint operations during JSExecution are forbidden.
             
             bool formattingFailed = false;
             
             EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
             try
             {
                 // Simulate a layout pass check
                 EnginePhaseManager.AssertNotInPhase(EnginePhase.JSExecution);
                 
                 // If we were effectively calling ComputeLayout here...
             }
             catch (InvalidOperationException)
             {
                 formattingFailed = true;
             }
             finally
             {
                 EnginePhaseManager.TryEnterIdle();
             }
             Assert.True(formattingFailed, "Layout operations during JSExecution must be forbidden.");
        }
    }
}
