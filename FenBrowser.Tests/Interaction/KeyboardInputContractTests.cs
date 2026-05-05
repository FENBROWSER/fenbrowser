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
    public class KeyboardInputContractTests
    {
        [Fact]
        public void ProcessEvent_KeyDownTargetsFocusedElement_AndIncludesKey()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var input = new Element("input");

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = input,
                        Bounds = new SKRect(0, 0, 100, 30),
                        Color = SKColors.White
                    }
                }
            };

            int keydownCount = 0;
            string observedKey = null;

            var handler = FenValue.FromFunction(new FenFunction("keydownHandler", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                keydownCount++;
                observedKey = evt?.Get("key").ToString();
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(input, "keydown", handler, false);
            try
            {
                _ = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.MouseDown, X = 5, Y = 5, Button = 0 },
                    renderContext,
                    context);

                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.KeyDown, Key = "K" },
                    null,
                    context);

                Assert.True(dispatched);
                Assert.Equal(1, keydownCount);
                Assert.Equal("K", observedKey);
            }
            finally
            {
                FenEventTarget.Registry.Remove(input, "keydown", handler, false);
            }
        }

        [Fact]
        public void ProcessEvent_KeyDownWithoutFocusedElement_ReturnsFalse()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));

            var dispatched = manager.ProcessEvent(
                new FenInputEvent { Type = FenInputEventType.KeyDown, Key = "X" },
                null,
                context);

            Assert.False(dispatched);
        }
    }
}
