using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
using FenEngineEventTarget = FenBrowser.FenEngine.DOM.EventTarget;
using FenBrowser.FenEngine.Security;
using System;
using System.Threading;
using Xunit;
using FenRuntimeCore = FenBrowser.FenEngine.Core.FenRuntime;

namespace FenBrowser.Tests.DOM
{
    /// <summary>
    /// Tests for DOM Level 3 Events and Input Hardening (Phase E-5)
    /// Covers bubbling, propagation control, and specific event types.
    /// </summary>
    [Collection("Engine Tests")]
    public class InputEventTests
    {
        private readonly IExecutionContext _context;

        public InputEventTests()
        {
            var perm = new PermissionManager(JsPermissions.StandardWeb);
            _context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            
            // Clean/Reset registry for tests
            // Note: Registry is static, so tests MUST clean up their listeners
        }

        [Fact]
        public void EventBubbling_TraversesUpToRoot()
        {
            // Tree: div -> span -> button
            var div = new Element("div");
            var span = new Element("span");
            var button = new Element("button");

            div.AppendChild(span);
            span.AppendChild(button);

            var eventPath = new List<Element>();
            
            // Add listeners
            var handlerFunc = new FenFunction("handler", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                if (evt?.CurrentTarget is Element el)
                {
                    eventPath.Add(el);
                }
                return FenValue.Undefined;
            });
            handlerFunc.IsAsync = false; // Native sync
            var handler = FenValue.FromFunction(handlerFunc);

            FenEngineEventTarget.Registry.Add(div, "click", handler, false);
            FenEngineEventTarget.Registry.Add(span, "click", handler, false);
            FenEngineEventTarget.Registry.Add(button, "click", handler, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            FenEngineEventTarget.DispatchEvent(button, evt, _context);

            // Assert bubbling order: button -> span -> div
            var pathStr = string.Join("->", eventPath.ConvertAll(e => e.TagName));
            Assert.Equal("BUTTON->SPAN->DIV", pathStr);

            // Cleanup
            FenEngineEventTarget.Registry.Remove(div, "click", handler, false);
            FenEngineEventTarget.Registry.Remove(span, "click", handler, false);
            FenEngineEventTarget.Registry.Remove(button, "click", handler, false);
        }

        [Fact]
        public void StopPropagation_HaltsBubbling()
        {
            var parent = new Element("div");
            var child = new Element("button");
            parent.AppendChild(child);

            bool childHandled = false;
            bool parentHandled = false;

            var childHandler = FenValue.FromFunction(new FenFunction("childHandler", (args, thisVal) =>
            {
                childHandled = true;
                ((DomEvent)args[0].AsObject()).StopPropagation();
                return FenValue.Undefined;
            }));

            var parentHandler = FenValue.FromFunction(new FenFunction("parentHandler", (args, thisVal) =>
            {
                parentHandled = true;
                return FenValue.Undefined;
            }));

            FenEngineEventTarget.Registry.Add(child, "click", childHandler, false);
            FenEngineEventTarget.Registry.Add(parent, "click", parentHandler, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            FenEngineEventTarget.DispatchEvent(child, evt, _context);

            Assert.True(childHandled);
            Assert.False(parentHandled);
            Assert.True(evt.PropagationStopped);

            FenEngineEventTarget.Registry.Remove(child, "click", childHandler, false);
            FenEngineEventTarget.Registry.Remove(parent, "click", parentHandler, false);
        }

        [Fact]
        public void StopImmediatePropagation_PreventsSameElementListeners()
        {
            var target = new Element("button");
            bool handler1Called = false;
            bool handler2Called = false;

            var handler1 = FenValue.FromFunction(new FenFunction("h1", (args, thisVal) =>
            {
                handler1Called = true;
                ((DomEvent)args[0].AsObject()).StopImmediatePropagation();
                return FenValue.Undefined;
            }));

            var handler2 = FenValue.FromFunction(new FenFunction("h2", (args, thisVal) =>
            {
                handler2Called = true;
                return FenValue.Undefined;
            }));

            FenEngineEventTarget.Registry.Add(target, "click", handler1, false);
            FenEngineEventTarget.Registry.Add(target, "click", handler2, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            FenEngineEventTarget.DispatchEvent(target, evt, _context);

            Assert.True(handler1Called);
            Assert.False(handler2Called);
            Assert.True(evt.ImmediatePropagationStopped);

            FenEngineEventTarget.Registry.Remove(target, "click", handler1, false);
            FenEngineEventTarget.Registry.Remove(target, "click", handler2, false);
        }

        [Fact]
        public void TouchEvent_Properties_AreCorrect()
        {
            var target = new Element("div");
            var touch = new Touch(1, target, 10, 20, 30, 40, 50, 60);
            var touches = new TouchList(new List<Touch> { touch });

            var touchEvent = new TouchEvent("touchstart", touches, touches, touches);

            Assert.Equal("touchstart", touchEvent.Type);
            Assert.Equal(1, touchEvent.Touches.Length);
            Assert.Equal(10, touchEvent.Touches.Item(0).ClientX);
        }

        [Fact]
        public void PreventDefault_WorksIfCancelable()
        {
            var evt = new DomEvent("scroll", bubbles: true, cancelable: true);
            evt.PreventDefault();
            Assert.True(evt.DefaultPrevented);

            var nonCancelable = new DomEvent("scroll", bubbles: true, cancelable: false);
            nonCancelable.PreventDefault();
            Assert.False(nonCancelable.DefaultPrevented);
        }

        [Fact]
        public void DispatchEvent_PreservesEventTarget()
        {
            var target = new Element("button");
            Element observedTarget = null;

            var handler = FenValue.FromFunction(new FenFunction("targetObserver", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                observedTarget = evt?.Target;
                return FenValue.Undefined;
            }));

            FenEngineEventTarget.Registry.Add(target, "click", handler, false);

            var evtDispatch = new DomEvent("click", bubbles: true, cancelable: true);
            FenEngineEventTarget.DispatchEvent(target, evtDispatch, _context);

            Assert.Same(target, evtDispatch.Target);
            Assert.Same(target, observedTarget);

            FenEngineEventTarget.Registry.Remove(target, "click", handler, false);
        }

        [Fact]
        public void DispatchEvent_ResetsExpiredExecutionBudget()
        {
            var target = new Element("button");
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(
                new PermissionManager(JsPermissions.StandardWeb),
                new TinyResourceLimits());
            var handlerCalled = false;

            var handler = FenValue.FromFunction(new FenFunction("budgetReset", (args, thisVal) =>
            {
                context.CheckExecutionTimeLimit();
                handlerCalled = true;
                return FenValue.Undefined;
            }));

            FenEngineEventTarget.Registry.Add(target, "click", handler, false);
            try
            {
                Thread.Sleep(30);

                var evtDispatch = new DomEvent("click", bubbles: true, cancelable: true);
                FenEngineEventTarget.DispatchEvent(target, evtDispatch, context);

                Assert.True(handlerCalled);
            }
            finally
            {
                FenEngineEventTarget.Registry.Remove(target, "click", handler, false);
            }
        }

        [Fact]
        public void DispatchEvent_OnHandlerTimeout_DoesNotEscape()
        {
            var target = new Element("button");
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(
                new PermissionManager(JsPermissions.StandardWeb),
                new TinyResourceLimits());
            var wrappedTarget = DomWrapperFactory.Wrap(target, context).AsObject();

            wrappedTarget.Set("onmousemove", FenValue.FromFunction(new FenFunction("onmousemove", (args, thisVal) =>
            {
                Thread.Sleep(30);
                context.CheckExecutionTimeLimit();
                return FenValue.Undefined;
            })), context);

            var evtDispatch = new DomEvent("mousemove", bubbles: true, cancelable: false);
            var ex = Record.Exception(() => FenEngineEventTarget.DispatchEvent(target, evtDispatch, context));

            Assert.Null(ex);
        }

        [Fact]
        public void DisabledControls_DispatchEvent_And_Click_RuntimeSemantics()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            var button = document.CreateElement("button");
            button.SetAttribute("disabled", string.Empty);
            document.Body!.AppendChild(button);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var __dispatchPass = false;
                var __clickPass = true;
                var __ctorType = '';
                var __ctorName = '';
                var __err = '';
                try {
                    const elem = document.getElementsByTagName('button').item(0);
                    __ctorType = typeof elem.constructor;
                    __ctorName = elem.constructor && elem.constructor.name ? elem.constructor.name : '';
                    elem.addEventListener('click', () => { __dispatchPass = true; }, { once: true });
                    elem.dispatchEvent(new Event('click'));
                    elem.onclick = () => { __clickPass = false; };
                    elem.click();
                } catch (e) {
                    __err = String(e);
                }
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__err").ToString());
            Assert.True(runtime.GetGlobal("__dispatchPass").ToBoolean());
            Assert.True(runtime.GetGlobal("__clickPass").ToBoolean());
            Assert.Equal("function", runtime.GetGlobal("__ctorType").ToString());
            Assert.False(string.IsNullOrEmpty(runtime.GetGlobal("__ctorName").ToString()));
        }

        [Fact]
        public void DispatchEvent_WindowOnError_Receives_ActualThrownErrorObject()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            var button = document.CreateElement("button");
            document.Body!.AppendChild(button);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var elem = document.getElementsByTagName('button').item(0);
                window.onerror = function(message, source, lineno, colno, error) {
                    window.__onerrorSeen = true;
                    window.__onerrorType = typeof error;
                    window.__onerrorName = error && error.name;
                    window.__onerrorMessage = error && error.message;
                    window.__onerrorMessageArg = message;
                };

                elem.addEventListener('click', function() {
                    throw new TypeError('boom');
                });

                elem.dispatchEvent(new Event('click'));
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.True(window.Get("__onerrorSeen").ToBoolean());
            Assert.Equal("object", window.Get("__onerrorType").ToString());
            Assert.Equal("TypeError", window.Get("__onerrorName").ToString());
            Assert.Equal("boom", window.Get("__onerrorMessage").ToString());
            Assert.Equal("boom", window.Get("__onerrorMessageArg").ToString());
        }

        [Fact]
        public void TestDriverClick_Delegates_To_ElementClick_RuntimeSemantics()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            var button = document.CreateElement("button");
            document.Body!.AppendChild(button);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var elem = document.getElementsByTagName('button').item(0);
                var clicks = 0;
                elem.onclick = function() { clicks++; };
                test_driver.click(elem);
                window.__testDriverClicks = clicks;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal(1, (int)window.Get("__testDriverClicks").ToNumber());
        }

        [Fact]
        public void ShadowRootHosted_Checkable_Click_Dispatches_Input_And_Change()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            var host = document.CreateElement("div");
            document.Body!.AppendChild(host);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var host = document.getElementsByTagName('div').item(0);
                var root = host.attachShadow({ mode: 'open' });
                var input = document.createElement('input');
                input.type = 'checkbox';
                var inputCount = 0;
                var changeCount = 0;
                input.addEventListener('input', function() { inputCount++; });
                input.addEventListener('change', function() { changeCount++; });
                root.appendChild(input);
                window.__shadowConnectedBeforeClick = input.isConnected;
                input.click();
                window.__shadowInputCount = inputCount;
                window.__shadowChangeCount = changeCount;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.True(window.Get("__shadowConnectedBeforeClick").ToBoolean());
            Assert.Equal(1, (int)window.Get("__shadowInputCount").ToNumber());
            Assert.Equal(1, (int)window.Get("__shadowChangeCount").ToNumber());
        }

        [Fact]
        public void AddEventListener_WindowSignal_RemovesListenerAfterAbort()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var controller = new AbortController();
                var calls = 0;
                function handler() { calls++; }

                window.addEventListener('probe', handler, { signal: controller.signal });
                window.dispatchEvent(new Event('probe'));
                controller.abort('done');
                window.dispatchEvent(new Event('probe'));

                window.__signalCalls = calls;
                window.__signalAborted = controller.signal.aborted;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal(1, (int)window.Get("__signalCalls").ToNumber());
            Assert.True(window.Get("__signalAborted").ToBoolean());
        }

        [Fact]
        public void AddEventListener_DuplicateSignalRegistration_DoesNotRemoveOriginalListener()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            var button = document.CreateElement("button");
            document.Body!.AppendChild(button);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var controller = new AbortController();
                var elem = document.getElementsByTagName('button').item(0);
                var calls = 0;
                function handler() { calls++; }

                elem.addEventListener('click', handler);
                elem.addEventListener('click', handler, { signal: controller.signal });
                controller.abort('done');
                elem.dispatchEvent(new Event('click'));

                window.__duplicateSignalCalls = calls;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal(1, (int)window.Get("__duplicateSignalCalls").ToNumber());
        }

        private sealed class TinyResourceLimits : IResourceLimits
        {
            public int MaxCallStackDepth => 100;
            public TimeSpan MaxExecutionTime => TimeSpan.FromMilliseconds(10);
            public long MaxInstructionCount => 100_000_000;
            public long MaxTotalMemory => 50 * 1024 * 1024;
            public int MaxStringLength => 1_000_000;
            public int MaxArrayLength => 100_000;
            public int MaxObjectProperties => 10_000;
            public int MaxPropertyChainDepth => 100;

            public bool CheckCallStack(int currentDepth) => currentDepth < MaxCallStackDepth;
            public bool CheckExecutionTime(TimeSpan elapsed) => elapsed < MaxExecutionTime;
            public bool CheckMemory(long bytes) => bytes < MaxTotalMemory;
            public bool CheckString(int length) => length < MaxStringLength;
            public bool CheckArray(int length) => length < MaxArrayLength;
            public bool CheckObjectProperties(int count) => count < MaxObjectProperties;
        }
    }
}
