using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Security;
using Xunit;

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

            EventTarget.Registry.Add(div, "click", handler, false);
            EventTarget.Registry.Add(span, "click", handler, false);
            EventTarget.Registry.Add(button, "click", handler, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            EventTarget.DispatchEvent(button, evt, _context);

            // Assert bubbling order: button -> span -> div
            var pathStr = string.Join("->", eventPath.ConvertAll(e => e.TagName));
            Assert.Equal("BUTTON->SPAN->DIV", pathStr);

            // Cleanup
            EventTarget.Registry.Remove(div, "click", handler, false);
            EventTarget.Registry.Remove(span, "click", handler, false);
            EventTarget.Registry.Remove(button, "click", handler, false);
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

            EventTarget.Registry.Add(child, "click", childHandler, false);
            EventTarget.Registry.Add(parent, "click", parentHandler, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            EventTarget.DispatchEvent(child, evt, _context);

            Assert.True(childHandled);
            Assert.False(parentHandled);
            Assert.True(evt.PropagationStopped);

            EventTarget.Registry.Remove(child, "click", childHandler, false);
            EventTarget.Registry.Remove(parent, "click", parentHandler, false);
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

            EventTarget.Registry.Add(target, "click", handler1, false);
            EventTarget.Registry.Add(target, "click", handler2, false);

            var evt = new DomEvent("click", bubbles: true, cancelable: true);
            EventTarget.DispatchEvent(target, evt, _context);

            Assert.True(handler1Called);
            Assert.False(handler2Called);
            Assert.True(evt.ImmediatePropagationStopped);

            EventTarget.Registry.Remove(target, "click", handler1, false);
            EventTarget.Registry.Remove(target, "click", handler2, false);
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
    }
}
