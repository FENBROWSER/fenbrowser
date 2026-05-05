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
    public class ScriptDomUpdateContractTests
    {
        [Fact]
        public void ProcessEvent_Click_HandlerCanMutateTargetAttributes()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var button = new Element("button");

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = button,
                        Bounds = new SKRect(0, 0, 80, 30),
                        Color = SKColors.LightGray
                    }
                }
            };

            var handler = FenValue.FromFunction(new FenFunction("mutateAttribute", (args, thisVal) =>
            {
                button.SetAttribute("data-clicked", "true");
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(button, "click", handler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.Click, X = 5, Y = 5, Button = 0 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.Equal("true", button.GetAttribute("data-clicked"));
            }
            finally
            {
                FenEventTarget.Registry.Remove(button, "click", handler, false);
            }
        }

        [Fact]
        public void ProcessEvent_Click_HandlerCanAppendDomNodes()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));

            var container = new Element("div");
            var button = new Element("button");
            container.AppendChild(button);

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = button,
                        Bounds = new SKRect(0, 0, 80, 30),
                        Color = SKColors.LightGray
                    }
                }
            };

            var handler = FenValue.FromFunction(new FenFunction("appendNode", (args, thisVal) =>
            {
                var appended = new Element("span");
                appended.SetAttribute("id", "added-by-handler");
                container.AppendChild(appended);
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(button, "click", handler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.Click, X = 5, Y = 5, Button = 0 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.NotNull(container.QuerySelector("#added-by-handler"));
            }
            finally
            {
                FenEventTarget.Registry.Remove(button, "click", handler, false);
            }
        }
    }
}
