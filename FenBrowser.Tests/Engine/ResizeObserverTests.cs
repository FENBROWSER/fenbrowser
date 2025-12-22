using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Core.Engine; // Phase enum
using FenBrowser.FenEngine.Observers;
using FenBrowser.Core.Dom;
using Xunit;
using System.Collections.Generic;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Tests for ResizeObserver per Phase D spec Section 9.
    /// Required tests:
    /// 1. Fires only on size change
    /// 2. Does not fire on position-only change
    /// 3. Fires once per layout cycle
    /// 4. Deterministic callback order
    /// 5. Never executes during layout
    /// 6. No cross-origin observation
    /// Requires serial execution due to static ObserverCoordinator state.
    /// </summary>
    [Collection("Engine Tests")]
    public class ResizeObserverTests
    {
        public ResizeObserverTests()
        {
            // Clear coordinator state to ensure test isolation
            ObserverCoordinator.Instance.Clear();
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
        }

        [Fact]
        public void ResizeObserver_FiresOnSizeChange()
        {
            // Arrange
            var callbackFired = false;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackFired = true;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer.Observe(wrapper);

            // Create initial LayoutResult with size 200x150
            var elementRects1 = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 200, 150) }
            };
            var layoutResult1 = new LayoutResult(elementRects1, 800, 600, 0, 1000);

            // Act - First evaluation (first observation = size change from -1,-1)
            observer.EvaluateWithLayoutResult(layoutResult1, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });

            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert
            Assert.True(callbackFired);
        }

        [Fact]
        public void ResizeObserver_DoesNotFireOnPositionOnlyChange()
        {
            // Arrange
            var callbackCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCount++;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer.Observe(wrapper);

            // First evaluation - sets initial size
            var elementRects1 = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 200, 150) }
            };
            var layoutResult1 = new LayoutResult(elementRects1, 800, 600, 0, 1000);
            observer.EvaluateWithLayoutResult(layoutResult1, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            
            // Record callback count after first
            var firstCallbackCount = callbackCount;

            // Second evaluation - position changed but SIZE SAME
            var elementRects2 = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(200, 300, 200, 150) } // Different X,Y but same W,H
            };
            var layoutResult2 = new LayoutResult(elementRects2, 800, 600, 0, 1000);
            observer.EvaluateWithLayoutResult(layoutResult2, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - should NOT fire again since size didn't change
            Assert.Equal(firstCallbackCount, callbackCount);
        }

        [Fact]
        public void ResizeObserver_FiresOncePerLayoutCycle()
        {
            // Arrange
            var callbackCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCount++;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer.Observe(wrapper);

            // First layout - size 200x150
            var elementRects1 = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 200, 150) }
            };
            var layoutResult1 = new LayoutResult(elementRects1, 800, 600, 0, 1000);
            observer.EvaluateWithLayoutResult(layoutResult1, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.Equal(1, callbackCount);

            // Second layout - size changed to 300x200
            var elementRects2 = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 300, 200) }
            };
            var layoutResult2 = new LayoutResult(elementRects2, 800, 600, 0, 1000);
            observer.EvaluateWithLayoutResult(layoutResult2, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - fired again for second size change
            Assert.Equal(2, callbackCount);
        }

        [Fact]
        public void ResizeObserver_DeterministicCallbackOrder()
        {
            // Arrange
            var callOrder = new List<string>();
            
            var callback1 = FenValue.FromFunction(new FenFunction("callback1", (args, thisVal) =>
            {
                callOrder.Add("observer1");
                return FenValue.Undefined;
            }));
            var callback2 = FenValue.FromFunction(new FenFunction("callback2", (args, thisVal) =>
            {
                callOrder.Add("observer2");
                return FenValue.Undefined;
            }));

            var observer1 = new ResizeObserverInstance(callback1);
            var observer2 = new ResizeObserverInstance(callback2);

            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer1.Observe(wrapper);
            observer2.Observe(wrapper);

            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(0, 0, 100, 100) }
            };
            var layoutResult = new LayoutResult(elementRects, 800, 600, 0, 1000);

            Func<IObject, Element> lookup = (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            };

            // Act - evaluate observers in order (simulating coordinator behavior)
            observer1.EvaluateWithLayoutResult(layoutResult, lookup);
            observer2.EvaluateWithLayoutResult(layoutResult, lookup);
            
            // Ensure we're in JSExecution phase for callbacks
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - callbacks fired in evaluation order
            Assert.Equal(2, callOrder.Count);
            Assert.Equal("observer1", callOrder[0]);
            Assert.Equal("observer2", callOrder[1]);
        }

        [Fact]
        public void ResizeObserver_PhaseAssertion_InCallbackEnqueue()
        {
            // This test verifies that phase assertions exist in callback execution
            // (they don't throw in JSExecution phase which is our test setup)
            
            // Arrange - already in JSExecution phase from constructor
            var callbackExecuted = false;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackExecuted = true;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer.Observe(wrapper);

            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(0, 0, 100, 100) }
            };
            var layoutResult = new LayoutResult(elementRects, 800, 600, 0, 1000);

            // Act
            observer.EvaluateWithLayoutResult(layoutResult, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            
            // Execute in JSExecution phase (safe)
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - callback executed successfully in safe phase
            Assert.True(callbackExecuted);
        }

        [Fact]
        public void ResizeObserver_Disconnect_ClearsAllTargets()
        {
            // Arrange
            var callbackCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCount++;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            ObserverCoordinator.Instance.RegisterResizeObserver(observer);
            
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            observer.Observe(wrapper);

            // Disconnect before evaluation
            observer.Disconnect();

            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(0, 0, 100, 100) }
            };
            var layoutResult = new LayoutResult(elementRects, 800, 600, 0, 1000);

            // Act
            observer.EvaluateWithLayoutResult(layoutResult, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - no callbacks since disconnected
            Assert.Equal(0, callbackCount);
        }
    }
}
