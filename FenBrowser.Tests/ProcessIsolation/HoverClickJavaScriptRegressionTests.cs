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
  <div id="hover-target"></div>
  <button id="probe-button" type="button">Probe</button>
  <script>
    document.documentElement.setAttribute('data-js-engine', 'enabled');
    document.getElementById('js-state').setAttribute('data-js', 'enabled');
    document.getElementById('js-state').textContent = 'enabled';
    document.getElementById('probe-button').addEventListener('click', function () {
      document.getElementById('click-state').textContent = 'clicked';
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
            var hoverTarget = FindById(root, "hover-target");
            var button = FindById(root, "probe-button");

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

            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            Assert.True(renderer.LastLayout.TryGetElementRect(button, out var buttonRect), "Missing button layout rect.");
            host.OnClick(buttonRect.Left + buttonRect.Width / 2f, buttonRect.Top + buttonRect.Height / 2f, button: 0);

            await WaitForAsync(
                () => string.Equals(clickState.TextContent, "clicked", StringComparison.Ordinal),
                "Button click did not dispatch the JavaScript click handler.");
        }
        finally
        {
            ElementStateManager.Instance.SetHoveredElement(null);
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
