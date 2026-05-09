using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Observers;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class ResizeObserverSpecComplianceTests : IDisposable
    {
        public ResizeObserverSpecComplianceTests()
        {
            EngineContext.Reset();
            ObserverCoordinator.Instance.Clear();
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
        }

        public void Dispose()
        {
            ObserverCoordinator.Instance.Clear();
            EngineContext.Reset();
        }

        [Fact]
        public void ResizeObserverEntry_ContainsContentRectAndBoxSizes()
        {
            FenObject observedEntry = null;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, _) =>
            {
                var entries = args[0].AsObject();
                observedEntry = entries.Get("0").AsObject() as FenObject;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var target = Wrap(element);
            observer.Observe(target);

            var layout = CreateLayout(element, 120, 60);
            observer.EvaluateWithLayoutResult(layout, ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.NotNull(observedEntry);

            var contentRect = observedEntry.Get("contentRect").AsObject();
            Assert.Equal(120d, contentRect.Get("width").ToNumber());
            Assert.Equal(60d, contentRect.Get("height").ToNumber());

            var contentBoxSize = observedEntry.Get("contentBoxSize").AsObject();
            Assert.Equal(1d, contentBoxSize.Get("length").ToNumber());
            Assert.Equal(120d, contentBoxSize.Get("0").AsObject().Get("inlineSize").ToNumber());
            Assert.Equal(60d, contentBoxSize.Get("0").AsObject().Get("blockSize").ToNumber());

            var borderBoxSize = observedEntry.Get("borderBoxSize").AsObject();
            Assert.Equal(1d, borderBoxSize.Get("length").ToNumber());
            Assert.Equal(120d, borderBoxSize.Get("0").AsObject().Get("inlineSize").ToNumber());
            Assert.Equal(60d, borderBoxSize.Get("0").AsObject().Get("blockSize").ToNumber());
        }

        [Fact]
        public void Unobserve_StopsFurtherCallbacksForTarget()
        {
            var callbackCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, _) =>
            {
                callbackCount++;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var target = Wrap(element);
            observer.Observe(target);

            observer.EvaluateWithLayoutResult(CreateLayout(element, 100, 50), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            Assert.Equal(1, callbackCount);

            observer.Unobserve(target);
            observer.EvaluateWithLayoutResult(CreateLayout(element, 180, 90), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.Equal(1, callbackCount);
        }

        [Fact]
        public void SizeThreshold_IgnoresJitterUpToHalfPixel()
        {
            var callbackCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, _) =>
            {
                callbackCount++;
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var target = Wrap(element);
            observer.Observe(target);

            observer.EvaluateWithLayoutResult(CreateLayout(element, 200, 120), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            Assert.Equal(1, callbackCount);

            observer.EvaluateWithLayoutResult(CreateLayout(element, 200.4f, 120.4f), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            Assert.Equal(1, callbackCount);

            observer.EvaluateWithLayoutResult(CreateLayout(element, 201f, 121f), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);
            Assert.Equal(2, callbackCount);
        }

        [Fact]
        public void ObserveSameTargetTwice_DoesNotDuplicateEntry()
        {
            var entryCount = 0;
            var callback = FenValue.FromFunction(new FenFunction("callback", (args, _) =>
            {
                entryCount = (int)args[0].AsObject().Get("length").ToNumber();
                return FenValue.Undefined;
            }));

            var observer = new ResizeObserverInstance(callback);
            var element = new Element("div");
            var target = Wrap(element);

            observer.Observe(target);
            observer.Observe(target);

            observer.EvaluateWithLayoutResult(CreateLayout(element, 150, 75), ResolveElement);
            ObserverCoordinator.Instance.ExecutePendingCallbacks(null);

            Assert.Equal(1, entryCount);
        }

        private static FenObject Wrap(Element element)
        {
            var wrapper = new FenObject();
            wrapper.NativeObject = element;
            return wrapper;
        }

        private static LayoutResult CreateLayout(Element element, float width, float height)
        {
            return new LayoutResult(
                new Dictionary<Element, ElementGeometry>
                {
                    { element, new ElementGeometry(0, 0, width, height) }
                },
                800,
                600,
                0,
                1000);
        }

        private static Element ResolveElement(IObject value)
        {
            if (value is FenObject obj && obj.NativeObject is Element element)
                return element;
            return null;
        }
    }
}
