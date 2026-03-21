using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Core.Engine; // Phase enum
using FenBrowser.FenEngine.Observers;
using FenBrowser.Core.Dom.V2;
using Xunit;
using System.Collections.Generic;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Tests for IntersectionObserver per Phase D spec Section 3.
    /// </summary>
    [Collection("Engine Tests")]
    public class IntersectionObserverTests
    {
        public IntersectionObserverTests()
        {
            // Clear coordinator state to ensure test isolation
            ObserverCoordinator.Instance.Clear();
        }

        [Fact]
        public void IntersectionObserver_Instance_ConstructsWithCallback()
        {
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) => FenValue.Undefined));
            var observer = new IntersectionObserverInstance(callback, 0);
            Assert.NotNull(observer);
        }

        [Fact]
        public void IntersectionObserver_Observe_AddsTarget()
        {
            // Arrange
            var callbackCallCount = 0;
            var receivedEntries = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCallCount++;
                if (args.Length > 0 && args[0].IsObject)
                {
                    var entries = args[0].AsObject();
                    var lenVal = entries.Get("length");
                    receivedEntries = (int)lenVal.AsNumber();
                }
                return FenValue.Undefined;
            }));

            var observer = new IntersectionObserverInstance(callback, 0);
            var element = new Element("div");
            var target = new FenObject { NativeObject = element };

            // Act
            observer.Observe(target);
            var layoutResult = new LayoutResult(
                new Dictionary<Element, ElementGeometry>
                {
                    { element, new ElementGeometry(10, 10, 100, 100) }
                },
                800,
                600,
                0,
                1000);
            observer.EvaluateWithLayoutResult(layoutResult, layoutResult.GetVisibleViewport(), jsObj =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element e) return e;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert
            Assert.Equal(1, callbackCallCount);
            Assert.Equal(1, receivedEntries);
        }

        [Fact]
        public void IntersectionObserver_Unobserve_RemovesTarget()
        {
            // Arrange
            var callbackCalls = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCalls++;
                return FenValue.Undefined;
            }));
            var observer = new IntersectionObserverInstance(callback, 0);
            var element = new Element("div");
            var target = new FenObject { NativeObject = element };

            // Act
            observer.Observe(target);
            observer.Unobserve(target);
            var layoutResult = new LayoutResult(
                new Dictionary<Element, ElementGeometry>
                {
                    { element, new ElementGeometry(10, 10, 100, 100) }
                },
                800,
                600,
                0,
                1000);
            observer.EvaluateWithLayoutResult(layoutResult, layoutResult.GetVisibleViewport(), jsObj =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element e) return e;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert
            Assert.Equal(0, callbackCalls);
        }

        [Fact]
        public void IntersectionObserver_Disconnect_ClearsAllTargets()
        {
            // Arrange
            var callbackCalls = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCalls++;
                return FenValue.Undefined;
            }));
            var observer = new IntersectionObserverInstance(callback, 0);
            var element1 = new Element("div");
            var element2 = new Element("span");
            var target1 = new FenObject { NativeObject = element1 };
            var target2 = new FenObject { NativeObject = element2 };

            // Act
            observer.Observe(target1);
            observer.Observe(target2);
            observer.Disconnect();
            var layoutResult = new LayoutResult(
                new Dictionary<Element, ElementGeometry>
                {
                    { element1, new ElementGeometry(10, 10, 100, 100) },
                    { element2, new ElementGeometry(20, 20, 100, 100) }
                },
                800,
                600,
                0,
                1000);
            observer.EvaluateWithLayoutResult(layoutResult, layoutResult.GetVisibleViewport(), jsObj =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element e) return e;
                return null;
            });
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert
            Assert.Equal(0, callbackCalls);
        }

        [Fact]
        public void IntersectionObserver_EvaluateWithLayoutResult_DetectsIntersection()
        {
            // This test verifies intersection detection by checking if callback is enqueued

            var callbackEnqueued = false;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackEnqueued = true;
                return FenValue.Undefined;
            }));

            var observer = new IntersectionObserverInstance(callback, 0);
            
            // Create a target with associated Element
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;

            observer.Observe(wrapper);

            // Create LayoutResult with element geometry inside viewport
            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 200, 150) } // Fully inside viewport
            };
            var layoutResult = new LayoutResult(elementRects, 800, 600, 0, 1000);
            var viewport = layoutResult.GetVisibleViewport();

            // Act - evaluate observer (should enqueue callback since element is intersecting)
            observer.EvaluateWithLayoutResult(layoutResult, viewport, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });

            // Execute pending callbacks
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - callback was called (element is in viewport, so intersection triggered)
            Assert.True(callbackEnqueued);
        }

        [Fact]
        public void IntersectionObserver_EvaluateWithLayoutResult_DetectsNonIntersection()
        {
            // Arrange
            var intersectingEntriesReceived = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                {
                    var entries = args[0].AsObject();
                    var lenVal = entries.Get("length");
                    var length = (int)lenVal.AsNumber();
                    for (int i = 0; i < length; i++)
                    {
                        var entryVal = entries.Get(i.ToString());
                        var entry = entryVal.AsObject();
                        if (entry != null)
                        {
                            var intersectVal = entry.Get("isIntersecting");
                            var isIntersecting = intersectVal.AsBoolean();
                            if (isIntersecting) intersectingEntriesReceived++;
                        }
                    }
                }
                return FenValue.Undefined;
            }));

            var observer = new IntersectionObserverInstance(callback, 0);
            
            // Create a target with associated Element
            var element = new Element("div");
            var wrapper = new FenObject();
            wrapper.NativeObject = element;

            observer.Observe(wrapper);

            // Create LayoutResult with element geometry BELOW viewport
            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 800, 200, 150) } // Below viewport (viewport is 0-600)
            };
            var layoutResult = new LayoutResult(elementRects, 800, 600, 0, 1000);
            var viewport = layoutResult.GetVisibleViewport();

            // Act - First evaluation (starts not intersecting)
            observer.EvaluateWithLayoutResult(layoutResult, viewport, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem)
                    return elem;
                return null;
            });

            // Execute pending callbacks
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            // Assert - no INTERSECTING entries (element is outside viewport)
            Assert.Equal(0, intersectingEntriesReceived);
        }

        [Fact]
        public void IntersectionObserver_DirtyFlag_SkipsUnchangedLayouts()
        {
            // Arrange
            var coordinator = ObserverCoordinator.Instance;
            coordinator.Clear();
            var callbackCalls = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCalls++;
                return FenValue.Undefined;
            }));
            var observer = new IntersectionObserverInstance(callback, 0);
            coordinator.RegisterIntersectionObserver(observer);
            var element = new Element("div");
            var wrapper = new FenObject { NativeObject = element };
            observer.Observe(wrapper);
            var elementRects = new Dictionary<Element, ElementGeometry>
            {
                { element, new ElementGeometry(100, 100, 200, 150) }
            };

            // Create two LayoutResults with same geometry and same LayoutId usage.
            var layoutResult1 = new LayoutResult(elementRects, 800, 600, 0, 1000);

            // Act - call twice with same scroll position
            coordinator.OnLayoutComplete(layoutResult1, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem) return elem;
                return null;
            });
            coordinator.ExecutePendingCallbacks(null);
            coordinator.OnLayoutComplete(layoutResult1, (jsObj) =>
            {
                if (jsObj is FenObject fenObj && fenObj.NativeObject is Element elem) return elem;
                return null;
            }); // Same result = skipped
            coordinator.ExecutePendingCallbacks(null);

            // Assert
            Assert.Equal(1, callbackCalls);
        }

        [Fact]
        public void IntersectionObserver_ThresholdArray_FiresWhenCrossingThresholdBoundaries()
        {
            var callbackCalls = 0;
            var lastRatio = 0d;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCalls++;
                var entries = args[0].AsObject();
                var firstEntry = entries.Get("0").AsObject();
                lastRatio = firstEntry.Get("intersectionRatio").ToNumber();
                return FenValue.Undefined;
            }));

            Assert.True(IntersectionObserverInstance.TryParseRootMargin("0px", out var rootMargin, out _));
            var observer = new IntersectionObserverInstance(callback, new[] { 0d, 0.5d, 1d }, "0px", rootMargin);
            var element = new Element("div");
            var wrapper = new FenObject { NativeObject = element };
            observer.Observe(wrapper);

            observer.EvaluateWithLayoutResult(
                new LayoutResult(
                    new Dictionary<Element, ElementGeometry> { { element, new ElementGeometry(0, 550, 100, 100) } },
                    800,
                    600,
                    0,
                    1000),
                new ElementGeometry(0, 0, 800, 600),
                jsObj => jsObj is FenObject fenObj && fenObj.NativeObject is Element resolved ? resolved : null);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            observer.EvaluateWithLayoutResult(
                new LayoutResult(
                    new Dictionary<Element, ElementGeometry> { { element, new ElementGeometry(0, 525, 100, 100) } },
                    800,
                    600,
                    0,
                    1000),
                new ElementGeometry(0, 0, 800, 600),
                jsObj => jsObj is FenObject fenObj && fenObj.NativeObject is Element resolved ? resolved : null);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            observer.EvaluateWithLayoutResult(
                new LayoutResult(
                    new Dictionary<Element, ElementGeometry> { { element, new ElementGeometry(0, 500, 100, 100) } },
                    800,
                    600,
                    0,
                    1000),
                new ElementGeometry(0, 0, 800, 600),
                jsObj => jsObj is FenObject fenObj && fenObj.NativeObject is Element resolved ? resolved : null);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.Equal(2, callbackCalls);
            Assert.Equal(1d, lastRatio);
        }

        [Fact]
        public void IntersectionObserver_RootMargin_ExpandsViewportForIntersection()
        {
            var intersectingEntries = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                var entries = args[0].AsObject();
                var firstEntry = entries.Get("0").AsObject();
                if (firstEntry.Get("isIntersecting").ToBoolean())
                {
                    intersectingEntries++;
                }

                return FenValue.Undefined;
            }));

            Assert.True(IntersectionObserverInstance.TryParseRootMargin("0px 0px 100px 0px", out var rootMargin, out _));
            var observer = new IntersectionObserverInstance(callback, new[] { 0d }, "0px 0px 100px 0px", rootMargin);
            var element = new Element("div");
            var wrapper = new FenObject { NativeObject = element };
            observer.Observe(wrapper);

            observer.EvaluateWithLayoutResult(
                new LayoutResult(
                    new Dictionary<Element, ElementGeometry> { { element, new ElementGeometry(0, 650, 100, 40) } },
                    800,
                    600,
                    0,
                    1000),
                new ElementGeometry(0, 0, 800, 600),
                jsObj => jsObj is FenObject fenObj && fenObj.NativeObject is Element resolved ? resolved : null);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.Equal(1, intersectingEntries);
        }

        [Fact]
        public void IntersectionObserver_TakeRecords_DrainsQueuedEntries()
        {
            var callbackCalls = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, thisVal) =>
            {
                callbackCalls++;
                return FenValue.Undefined;
            }));

            Assert.True(IntersectionObserverInstance.TryParseRootMargin("0px", out var rootMargin, out _));
            var observer = new IntersectionObserverInstance(callback, new[] { 0d }, "0px", rootMargin);
            var element = new Element("div");
            var wrapper = new FenObject { NativeObject = element };
            observer.Observe(wrapper);

            observer.EvaluateWithLayoutResult(
                new LayoutResult(
                    new Dictionary<Element, ElementGeometry> { { element, new ElementGeometry(10, 10, 100, 100) } },
                    800,
                    600,
                    0,
                    1000),
                new ElementGeometry(0, 0, 800, 600),
                jsObj => jsObj is FenObject fenObj && fenObj.NativeObject is Element resolved ? resolved : null);

            var records = observer.TakeRecords();
            Assert.Equal(1, records.Get("length").ToNumber());
            Assert.Equal(0, observer.TakeRecords().Get("length").ToNumber());

            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            Assert.Equal(0, callbackCalls);
        }
    }
}
