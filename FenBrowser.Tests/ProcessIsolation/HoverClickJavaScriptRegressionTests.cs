using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class HoverClickJavaScriptRegressionTests
{
    [Fact]
    public async Task RenderedPage_AppliesHoverClickAndJavaScriptDetection()
    {
        const int viewportWidth = 800;
        const int viewportHeight = 600;
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; font-family: Arial, sans-serif; }
    #hover-target {
      display: block;
      width: 160px;
      height: 48px;
      margin: 24px;
      background: rgb(10, 20, 30);
    }
    #hover-target:hover {
      background: rgb(1, 2, 3);
    }
    #probe-button {
      display: block;
      width: 140px;
      height: 40px;
      margin: 24px;
    }
  </style>
</head>
<body>
  <div id="js-state" data-js="disabled">disabled</div>
  <div id="click-state">idle</div>
  <div id="dbl-state">idle</div>
  <div id="context-state">idle</div>
  <div id="pointer-state">none</div>
  <div id="hover-target"></div>
  <button id="probe-button" type="button">Probe</button>
  <button id="dbl-button" type="button">Double</button>
  <button id="context-button" type="button">Context</button>
  <script>
    document.documentElement.setAttribute('data-js-engine', 'enabled');
    document.getElementById('js-state').setAttribute('data-js', 'enabled');
    document.getElementById('js-state').textContent = 'enabled';
    document.getElementById('probe-button').addEventListener('click', function () {
      document.getElementById('click-state').textContent = 'clicked';
    });
    document.getElementById('dbl-button').addEventListener('dblclick', function () {
      document.getElementById('dbl-state').textContent = 'double-clicked';
    });
    document.getElementById('context-button').addEventListener('contextmenu', function (event) {
      event.preventDefault();
      document.getElementById('context-state').textContent = 'context:' + event.defaultPrevented + ':' + event.button;
    });
    document.getElementById('hover-target').addEventListener('pointerdown', function (event) {
      document.getElementById('pointer-state').textContent = event.type + ':' + event.pointerType + ':' + event.button;
    });
  </script>
</body>
</html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        ElementStateManager.Instance.SetHoveredElement(null);

        try
        {
            await host.Engine.RenderAsync(
                html,
                new Uri("https://fen.test/probe"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<Stream>(null),
                _ => { },
                viewportWidth: viewportWidth,
                viewportHeight: viewportHeight,
                forceJavascript: true);

            var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
            var jsState = FindById(root, "js-state");
            var clickState = FindById(root, "click-state");
            var dblState = FindById(root, "dbl-state");
            var contextState = FindById(root, "context-state");
            var pointerState = FindById(root, "pointer-state");
            var hoverTarget = FindById(root, "hover-target");
            var button = FindById(root, "probe-button");
            var dblButton = FindById(root, "dbl-button");
            var contextButton = FindById(root, "context-button");

            await WaitForAsync(
                () => string.Equals(root.GetAttribute("data-js-engine"), "enabled", StringComparison.Ordinal) &&
                      string.Equals(jsState.GetAttribute("data-js"), "enabled", StringComparison.Ordinal) &&
                      string.Equals(jsState.TextContent, "enabled", StringComparison.Ordinal),
                "JavaScript engine did not execute the detection script.");

            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            AssertHoverBackground(host.ComputedStyles, hoverTarget, 10, 20, 30, "before hover");

            Assert.True(renderer.LastLayout.TryGetElementRect(hoverTarget, out var hoverRect), "Missing hover target layout rect.");
            host.OnMouseMove(hoverRect.Left + hoverRect.Width / 2f, hoverRect.Top + hoverRect.Height / 2f);
            Assert.Same(hoverTarget, ElementStateManager.Instance.HoveredElement);

            await WaitForAsync(
                () => HasBackground(host.ComputedStyles, hoverTarget, 1, 2, 3),
                "Hover did not recascade #hover-target:hover background.");

            host.OnMouseDown(hoverRect.Left + hoverRect.Width / 2f, hoverRect.Top + hoverRect.Height / 2f, button: 0);
            await WaitForAsync(
                () => string.Equals(pointerState.TextContent, "pointerdown:mouse:0", StringComparison.Ordinal),
                "Mouse down did not synthesize the JavaScript pointerdown payload.");

            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            Assert.True(renderer.LastLayout.TryGetElementRect(button, out var buttonRect), "Missing button layout rect.");
            host.OnClick(buttonRect.Left + buttonRect.Width / 2f, buttonRect.Top + buttonRect.Height / 2f, button: 0);

            await WaitForAsync(
                () => string.Equals(clickState.TextContent, "clicked", StringComparison.Ordinal),
                "Button click did not dispatch the JavaScript click handler.");

            Assert.True(renderer.LastLayout.TryGetElementRect(dblButton, out var dblButtonRect), "Missing double-click button layout rect.");
            host.OnDoubleClick(dblButtonRect.Left + dblButtonRect.Width / 2f, dblButtonRect.Top + dblButtonRect.Height / 2f, button: 0);
            await WaitForAsync(
                () => string.Equals(dblState.TextContent, "double-clicked", StringComparison.Ordinal),
                "Button double-click did not dispatch the JavaScript dblclick handler.");

            Assert.True(renderer.LastLayout.TryGetElementRect(contextButton, out var contextButtonRect), "Missing context-menu button layout rect.");
            var contextDefaultAllowed = host.OnContextMenu(
                contextButtonRect.Left + contextButtonRect.Width / 2f,
                contextButtonRect.Top + contextButtonRect.Height / 2f,
                button: 2);
            await WaitForAsync(
                () => string.Equals(contextState.TextContent, "context:true:2", StringComparison.Ordinal),
                "Button right-click did not dispatch the JavaScript contextmenu handler with the mouse payload.");
            Assert.False(contextDefaultAllowed);
        }
        finally
        {
            ElementStateManager.Instance.SetHoveredElement(null);
        }
    }

    [Fact]
    public async Task RenderedPage_DeliversLatestRapidPointerMoveToJavaScript()
    {
        const int viewportWidth = 800;
        const int viewportHeight = 600;
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #move-target {
      width: 320px;
      height: 160px;
      margin: 40px;
      background: #f3e8ff;
    }
  </style>
</head>
<body>
  <div id="last-pointer">none</div>
  <div id="move-target"></div>
  <script>
    globalThis.__moveScriptRan = true;
    document.getElementById('move-target').addEventListener('pointermove', function (event) {
      document.getElementById('last-pointer').textContent = event.pointerType + ':' + Math.round(event.clientX) + ',' + Math.round(event.clientY);
    });
  </script>
</body>
</html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/pointer-move"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var pointerState = FindById(root, "last-pointer");
        var moveTarget = FindById(root, "move-target");

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(moveTarget, out var moveRect), "Missing pointer move target layout rect.");

        var firstX = moveRect.Left + 24;
        var firstY = moveRect.Top + 32;
        var latestX = moveRect.Left + 180;
        var latestY = moveRect.Top + 96;
        var hit = FenBrowser.FenEngine.Rendering.Interaction.HitTester.HitTest(
            renderer.CreateRenderContext(),
            latestX,
            latestY);
        Assert.Equal("move-target", hit?.GetAttribute("id"));
        await WaitForAsync(
            () => string.Equals(host.Engine.Evaluate("String(globalThis.__moveScriptRan)")?.ToString(), "true", StringComparison.Ordinal),
            $"Expected fixture script to execute. Telemetry js={host.Engine.LastRenderTelemetry?.JavaScriptExecuted}");
        Assert.Equal("function", host.Engine.Evaluate("String(typeof Math.round)")?.ToString());

        Assert.True(host.Engine.DispatchPointerEvent(
            moveTarget,
            "pointermove",
            new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
            {
                ClientX = latestX,
                ClientY = latestY,
                PointerType = "mouse",
                IsPrimary = true
            }));
        Assert.Equal($"mouse:{Math.Round(latestX)},{Math.Round(latestY)}", pointerState.TextContent);
        pointerState.TextContent = "none";

        host.OnMouseMove(firstX, firstY);
        host.OnMouseMove(latestX, latestY);

        await WaitForAsync(
            () => string.Equals(pointerState.TextContent, $"mouse:{Math.Round(latestX)},{Math.Round(latestY)}", StringComparison.Ordinal),
            $"Rapid pointer movement left JavaScript with stale coordinates. Actual: '{pointerState.TextContent}'.");
    }

    [Fact]
    public async Task RenderedPage_BoundsBlockingInputHandler()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #target {
      display: block;
      width: 180px;
      height: 56px;
      margin: 40px;
    }
  </style>
</head>
<body>
  <button id="target" type="button">Block</button>
  <script>
    document.getElementById('target').addEventListener('mousedown', function () {
      while (true) {}
    });
  </script>
</body>
</html>
""";

        var previousTimeout = Environment.GetEnvironmentVariable("FEN_FENJS_INPUT_EVENT_TIMEOUT_MS");
        Environment.SetEnvironmentVariable("FEN_FENJS_INPUT_EVENT_TIMEOUT_MS", "100");
        try
        {
            using var host = new BrowserHost();
            var renderer = new SkiaDomRenderer();
            host.EnableJavaScript = true;
            host.SetActiveRenderer(renderer);
            host.Engine.SetExternalRenderer(renderer);

            await host.Engine.RenderAsync(
                html,
                new Uri("https://fen.test/blocking-input"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<Stream>(null),
                _ => { },
                viewportWidth: viewportWidth,
                viewportHeight: viewportHeight,
                forceJavascript: true);

            var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
            var target = FindById(root, "target");
            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            Assert.True(renderer.LastLayout.TryGetElementRect(target, out var targetRect), "Missing target layout rect.");

            var dispatch = Task.Run(() => host.OnMouseDown(
                targetRect.Left + targetRect.Width / 2f,
                targetRect.Top + targetRect.Height / 2f,
                button: 0));

            var completed = await Task.WhenAny(dispatch, Task.Delay(3000));

            Assert.Same(dispatch, completed);
            await dispatch;
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_FENJS_INPUT_EVENT_TIMEOUT_MS", previousTimeout);
        }
    }

    private static void RenderFrame(
        SkiaDomRenderer renderer,
        Element root,
        System.Collections.Generic.Dictionary<Node, CssComputed> styles,
        int viewportWidth,
        int viewportHeight)
    {
        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://fen.test/probe",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = "HoverClickJavaScriptRegressionTests",
            EmitVerificationReport = false
        });
        canvas.Flush();
    }

    private static Element FindById(Element root, string id)
    {
        return root.Descendants()
            .OfType<Element>()
            .First(e => string.Equals(e.GetAttribute("id"), id, StringComparison.Ordinal));
    }

    private static void AssertHoverBackground(
        System.Collections.Generic.Dictionary<Node, CssComputed> styles,
        Element element,
        byte red,
        byte green,
        byte blue,
        string phase)
    {
        Assert.True(
            HasBackground(styles, element, red, green, blue),
            $"Expected {phase} background rgb({red}, {green}, {blue}).");
    }

    private static bool HasBackground(
        System.Collections.Generic.Dictionary<Node, CssComputed> styles,
        Element element,
        byte red,
        byte green,
        byte blue)
    {
        return styles.TryGetValue(element, out var style) &&
               style?.BackgroundColor is SKColor color &&
               color.Red == red &&
               color.Green == green &&
               color.Blue == blue;
    }

    private static async Task WaitForAsync(Func<bool> predicate, string failureMessage)
    {
        for (var i = 0; i < 100; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), failureMessage);
    }
}
