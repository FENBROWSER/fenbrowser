using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class ClickActivationDispatchTests
{
    [Fact]
    public async Task HandleElementClick_DispatchesJavaScriptClickExactlyOnce()
    {
        using var host = await RenderAsync(
            """
            <!doctype html>
            <html><body>
              <button id="target" type="button">Target</button>
              <script>
                globalThis.__clickCount = 0;
                document.getElementById('target').addEventListener('click', function () {
                  globalThis.__clickCount++;
                });
              </script>
            </body></html>
            """);
        await WaitForScriptAsync(host);
        var target = FindById(host, "target");

        await host.HandleElementClick(target);

        Assert.Equal("1", host.Engine.Evaluate("String(globalThis.__clickCount)")?.ToString());
    }

    [Fact]
    public async Task HandleElementClick_PreventDefaultBlocksCheckboxActivation()
    {
        using var host = await RenderAsync(
            """
            <!doctype html>
            <html><body>
              <input id="target" type="checkbox">
              <script>
                globalThis.__clickCount = 0;
                document.getElementById('target').addEventListener('click', function (event) {
                  globalThis.__clickCount++;
                  event.preventDefault();
                });
              </script>
            </body></html>
            """);
        await WaitForScriptAsync(host);
        var target = FindById(host, "target");

        await host.HandleElementClick(target);

        Assert.Equal("1", host.Engine.Evaluate("String(globalThis.__clickCount)")?.ToString());
        Assert.False(ElementStateManager.Instance.IsChecked(target));
    }

    [Fact]
    public async Task DispatchClickAndActivate_DispatchesClickAndDefaultActivationExactlyOnce()
    {
        using var host = await RenderAsync(
            """
            <!doctype html>
            <html><body>
              <input id="target" type="checkbox" style="width: 24px; height: 24px">
              <script>
                globalThis.__clickCount = 0;
                document.getElementById('target').addEventListener('click', function () {
                  globalThis.__clickCount++;
                });
              </script>
            </body></html>
            """);
        await WaitForScriptAsync(host);
        var target = FindById(host, "target");
        var renderer = new SkiaDomRenderer();
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        using var bitmap = new SkiaSharp.SKBitmap(640, 360);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = host.Engine.GetActiveDom(),
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SkiaSharp.SKRect(0, 0, 640, 360),
            BaseUrl = "https://fen.test/click-activation",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(DispatchClickAndActivate_DispatchesClickAndDefaultActivationExactlyOnce),
            EmitVerificationReport = false
        });
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var rect));

        await host.DispatchClickAndActivate(rect.Left + (rect.Width / 2f), rect.Top + (rect.Height / 2f), button: 0);

        Assert.Equal("1", host.Engine.Evaluate("String(globalThis.__clickCount)")?.ToString());
        Assert.True(ElementStateManager.Instance.IsChecked(target));
    }

    [Fact]
    public async Task OnMouseWheel_DispatchesDeltasAndHonorsPreventDefault()
    {
        using var host = await RenderAsync(
            """
            <!doctype html>
            <html><body>
              <div id="target" style="width: 100px; height: 100px">Target</div>
              <script>
                globalThis.__clickCount = 0;
                globalThis.__wheel = '';
                document.getElementById('target').addEventListener('wheel', function (event) {
                  globalThis.__wheel = event.deltaX + ',' + event.deltaY;
                  event.preventDefault();
                });
              </script>
            </body></html>
            """);
        await WaitForScriptAsync(host);
        var target = FindById(host, "target");
        var renderer = new SkiaDomRenderer();
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        using var bitmap = new SkiaSharp.SKBitmap(640, 360);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = host.Engine.GetActiveDom(),
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SkiaSharp.SKRect(0, 0, 640, 360),
            BaseUrl = "https://fen.test/wheel-input",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(OnMouseWheel_DispatchesDeltasAndHonorsPreventDefault),
            EmitVerificationReport = false
        });
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var rect));

        var defaultAllowed = host.OnMouseWheel(
            rect.Left + (rect.Width / 2f),
            rect.Top + (rect.Height / 2f),
            deltaX: 3,
            deltaY: -7);

        Assert.False(defaultAllowed);
        Assert.Equal("3,-7", host.Engine.Evaluate("globalThis.__wheel")?.ToString());
    }

    [Fact]
    public async Task OverflowElement_WheelAndBothScrollbarTracksUpdateOwnedScrollState()
    {
        using var host = await RenderAsync(
            """
            <!doctype html>
            <html><body>
              <div id="scroller" style="width:180px;height:100px;overflow:auto">
                <div id="surface" style="width:420px;height:300px">surface</div>
              </div>
            </body></html>
            """);
        var scroller = FindById(host, "scroller");
        var surface = FindById(host, "surface");
        var renderer = new SkiaDomRenderer();
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        using var bitmap = new SkiaSharp.SKBitmap(640, 360);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = host.Engine.GetActiveDom(),
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SkiaSharp.SKRect(0, 0, 640, 360),
            BaseUrl = "https://fen.test/overflow-input",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(OverflowElement_WheelAndBothScrollbarTracksUpdateOwnedScrollState),
            EmitVerificationReport = false
        });
        Assert.True(renderer.LastLayout.TryGetElementRect(scroller, out var rect));
        Assert.True(renderer.LastLayout.TryGetElementRect(surface, out var surfaceRect));
        Assert.True(renderer.ScrollManager.HasVerticalScrollbar(scroller));
        Assert.True(renderer.ScrollManager.HasHorizontalScrollbar(scroller));

        var wheelAllowed = host.OnMouseWheel(
            surfaceRect.Left + 20f,
            surfaceRect.Top + 20f,
            deltaX: -1,
            deltaY: -1);

        Assert.False(wheelAllowed);
        var afterWheel = renderer.ScrollManager.GetScrollOffset(scroller);
        Assert.True(afterWheel.x > 0f);
        Assert.True(afterWheel.y > 0f);

        renderer.ScrollManager.SetScrollPosition(scroller, 0f, 0f);
        host.OnMouseDown(rect.Right - 2f, rect.Top + rect.Height * 0.7f, button: 0);
        host.OnMouseUp(rect.Right - 2f, rect.Top + rect.Height * 0.7f, button: 0);
        Assert.True(renderer.ScrollManager.GetScrollOffset(scroller).y > 0f);

        renderer.ScrollManager.SetScrollPosition(scroller, 0f, 0f);
        host.OnMouseDown(rect.Left + rect.Width * 0.7f, rect.Bottom - 2f, button: 0);
        host.OnMouseUp(rect.Left + rect.Width * 0.7f, rect.Bottom - 2f, button: 0);
        Assert.True(renderer.ScrollManager.GetScrollOffset(scroller).x > 0f);
    }

    [Fact]
    public async Task GetComputedStyle_ExposesRawMapPropertiesAsCamelCaseMembers()
    {
        using var host = await RenderAsync(
            """
            <!doctype html><html><body>
              <div id="target" style="border-radius:9px;box-shadow:1px 2px 3px #000"></div>
            </body></html>
            """);

        Assert.Equal(
            "9px|1px 2px 3px #000|9px|1px 2px 3px #000",
            host.Engine.Evaluate(
                "var s=getComputedStyle(document.getElementById('target'));" +
                "[s.borderRadius,s.boxShadow,s.getPropertyValue('border-radius'),s.getPropertyValue('box-shadow')].join('|')")?.ToString());
    }

    [Fact]
    public async Task PerformPointerActions_DispatchesTrustedSequenceThroughInputPipeline()
    {
        using var host = await RenderAsync(
            """
            <!doctype html><html><body>
              <input id="target" type="checkbox" style="width:24px;height:24px">
              <script>
                globalThis.__clickCount=0;
                globalThis.__events=[];
                var target=document.getElementById('target');
                ['pointerdown','mousedown','pointerup','mouseup','click'].forEach(
                  n=>target.addEventListener(n,()=>globalThis.__events.push(n)));
              </script>
            </body></html>
            """);
        await WaitForScriptAsync(host);
        var target = FindById(host, "target");
        var renderer = new SkiaDomRenderer();
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);
        using var bitmap = new SkiaSharp.SKBitmap(640, 360);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = host.Engine.GetActiveDom(),
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SkiaSharp.SKRect(0, 0, 640, 360),
            BaseUrl = "https://fen.test/pointer-actions",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(PerformPointerActions_DispatchesTrustedSequenceThroughInputPipeline),
            EmitVerificationReport = false
        });
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var rect));

        var chain = new ActionChain { Type = "pointer", Id = "mouse" };
        chain.Actions.Add(new InputAction
        {
            Type = "pointerMove",
            Origin = "viewport",
            X = (int)(rect.Left + rect.Width / 2f),
            Y = (int)(rect.Top + rect.Height / 2f)
        });
        chain.Actions.Add(new InputAction { Type = "pointerDown", Button = 0 });
        chain.Actions.Add(new InputAction { Type = "pointerUp", Button = 0 });

        await host.PerformActionsAsync(new List<ActionChain> { chain });

        Assert.Equal(
            "pointerdown,mousedown,pointerup,mouseup,click",
            host.Engine.Evaluate("globalThis.__events.join(',')")?.ToString());
        Assert.True(ElementStateManager.Instance.IsChecked(target));
    }

    private static async Task<BrowserHost> RenderAsync(string html)
    {
        var host = new BrowserHost();
        host.EnableJavaScript = true;
        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/click-activation"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: 640,
            viewportHeight: 360,
            forceJavascript: true);
        return host;
    }

    private static Element FindById(BrowserHost host, string id)
    {
        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        return Assert.IsType<Element>(root.OwnerDocument?.GetElementById(id));
    }

    private static async Task WaitForScriptAsync(BrowserHost host)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (string.Equals(
                    host.Engine.Evaluate("typeof globalThis.__clickCount")?.ToString(),
                    "number",
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Click fixture script did not execute.");
    }
}
