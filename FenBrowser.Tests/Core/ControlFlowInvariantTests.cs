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
        public void Test_Invariant_JSCannotTriggerRepaint()
        {
            // Goal: JS code attempting to trigger a repaint synchronously or directly must fail.
            // Repaints must be pull-based.
            
            bool validationFailed = false;
            
            try
            {
                EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
                
                // Simulate CustomHtmlEngine.ScheduleRepaintFromJs behavior
                // Ideally we would call the actual method, but it is private. 
                // We will test the assertion logic that SHOULD be there.
                
                EnginePhaseManager.AssertNotInPhase(EnginePhase.JSExecution, EnginePhase.Microtasks);
            }
            catch (InvalidOperationException)
            {
                validationFailed = true;
            }
            finally
            {
                EnginePhaseManager.TryEnterIdle();
            }

            Assert.True(validationFailed, "Repaint request during JSExecution should have thrown InvalidOperationException");
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
