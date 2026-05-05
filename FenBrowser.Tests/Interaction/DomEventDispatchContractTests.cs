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
    public class DomEventDispatchContractTests
    {
        [Fact]
        public void ProcessEvent_Click_BubblesFromTargetToAncestor()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));

            var parent = new Element("div");
            var child = new Element("button");
            parent.AppendChild(child);

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = child,
                        Bounds = new SKRect(0, 0, 80, 30),
                        Color = SKColors.LightBlue
                    }
                }
            };

            var path = new List<string>();

            var childHandler = FenValue.FromFunction(new FenFunction("childClick", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                path.Add((evt?.CurrentTarget as Element)?.TagName ?? string.Empty);
                return FenValue.Undefined;
            }));

            var parentHandler = FenValue.FromFunction(new FenFunction("parentClick", (args, thisVal) =>
            {
                var evt = args[0].AsObject() as DomEvent;
                path.Add((evt?.CurrentTarget as Element)?.TagName ?? string.Empty);
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(child, "click", childHandler, false);
            FenEventTarget.Registry.Add(parent, "click", parentHandler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.Click, X = 5, Y = 5, Button = 0 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.Equal("BUTTON->DIV", string.Join("->", path));
            }
            finally
            {
                FenEventTarget.Registry.Remove(child, "click", childHandler, false);
                FenEventTarget.Registry.Remove(parent, "click", parentHandler, false);
            }
        }

        [Fact]
        public void ProcessEvent_Click_StopPropagationPreventsAncestorDispatch()
        {
            var manager = new InputManager();
            var context = new FenExecutionContext(new PermissionManager(JsPermissions.StandardWeb));

            var parent = new Element("div");
            var child = new Element("button");
            parent.AppendChild(child);

            var renderContext = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = child,
                        Bounds = new SKRect(0, 0, 80, 30),
                        Color = SKColors.LightBlue
                    }
                }
            };

            var childCalled = 0;
            var parentCalled = 0;

            var childHandler = FenValue.FromFunction(new FenFunction("childStop", (args, thisVal) =>
            {
                childCalled++;
                var evt = args[0].AsObject() as DomEvent;
                evt?.StopPropagation();
                return FenValue.Undefined;
            }));

            var parentHandler = FenValue.FromFunction(new FenFunction("parentShouldNotRun", (args, thisVal) =>
            {
                parentCalled++;
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(child, "click", childHandler, false);
            FenEventTarget.Registry.Add(parent, "click", parentHandler, false);
            try
            {
                var dispatched = manager.ProcessEvent(
                    new FenInputEvent { Type = FenInputEventType.Click, X = 5, Y = 5, Button = 0 },
                    renderContext,
                    context);

                Assert.True(dispatched);
                Assert.Equal(1, childCalled);
                Assert.Equal(0, parentCalled);
            }
            finally
            {
                FenEventTarget.Registry.Remove(child, "click", childHandler, false);
                FenEventTarget.Registry.Remove(parent, "click", parentHandler, false);
            }
        }
    }
}
