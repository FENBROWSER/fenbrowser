using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Security;
using SkiaSharp;
using Xunit;
using FenEventTarget = FenBrowser.FenEngine.DOM.EventTarget;
using FenExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;
using FenInputEvent = FenBrowser.FenEngine.Interaction.InputEvent;
using FenInputEventType = FenBrowser.FenEngine.Interaction.InputEventType;

namespace FenBrowser.Tests.Interaction
{
    public class MouseInputContractTests
    {
        [Fact]
        public void ProcessEvent_ClickDispatchesToHitTargetWithMousePayload()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var target = new Element("button");

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = target,
                        Bounds = new SKRect(0, 0, 120, 40),
                        Color = SKColors.LightGray
                    }
                }
            };

            string eventType = null;
            double clientX = -1;
            double button = -1;

            var handler = FenValue.FromFunction(new FenFunction("clickHandler", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                eventType = evt?.Type;
                clientX = evt?.Get("clientX").ToNumber() ?? -1;
                button = evt?.Get("button").ToNumber() ?? -1;
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(target, "click", handler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.Click, X = 27, Y = 13, Button = 2, Buttons = 2 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.Equal("click", eventType);
                Assert.Equal(27d, clientX);
                Assert.Equal(2d, button);
            }
            finally
            {
                FenEventTarget.Registry.Remove(target, "click", handler, false);
            }
        }

        [Fact]
        public void ProcessEvent_MouseMoveTargetsFrontMostHitElement()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));

            var back = new Element("div");
            var front = new Element("div");
            int backCount = 0;
            int frontCount = 0;

            var backHandler = FenValue.FromFunction(new FenFunction("backMove", (args, thisVal) =>
            {
                backCount++;
                return FenValue.Undefined;
            }));

            var frontHandler = FenValue.FromFunction(new FenFunction("frontMove", (args, thisVal) =>
            {
                frontCount++;
                return FenValue.Undefined;
            }));

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = back,
                        Bounds = new SKRect(0, 0, 80, 80),
                        Color = SKColors.Red
                    },
                    new BackgroundPaintNode
                    {
                        SourceNode = front,
                        Bounds = new SKRect(0, 0, 80, 80),
                        Color = SKColors.Blue
                    }
                }
            };

            FenEventTarget.Registry.Add(back, "mousemove", backHandler, false);
            FenEventTarget.Registry.Add(front, "mousemove", frontHandler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.MouseMove, X = 10, Y = 10, Buttons = 1 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.Equal(0, backCount);
                Assert.Equal(1, frontCount);
            }
            finally
            {
                FenEventTarget.Registry.Remove(back, "mousemove", backHandler, false);
                FenEventTarget.Registry.Remove(front, "mousemove", frontHandler, false);
            }
        }
    }
}
