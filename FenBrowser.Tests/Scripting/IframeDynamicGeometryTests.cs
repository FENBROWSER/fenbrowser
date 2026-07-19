using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class IframeDynamicGeometryTests
{
    [Fact]
    public async Task AttachedIframeResize_RecascadesAndRelayoutsFrameBox()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 600;
        const string html = """
            <!doctype html>
            <html><body style="margin:0">
              <iframe id="frame" style="display:block;width:300px;height:78px;border:0"></iframe>
            </body></html>
            """;
        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://parent.test/page"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var frame = root.Descendants().OfType<Element>()
            .Single(element => element.GetAttribute("id") == "frame");
        await WaitForAsync(
            () => host.Engine.Evaluate("String(document.getElementById('frame') !== null)")?.ToString() == "true");
        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(frame, out var initialRect));
        Assert.Equal(78f, initialRect.Height, 1f);

        await host.Engine.ExecuteScriptAsync("document.getElementById('frame').style.height='480px'");
        Assert.Contains("height:480px", frame.GetAttribute("style"));
        await host.Engine.FlushPendingLayoutAsync();
        Assert.True(host.ComputedStyles.TryGetValue(frame, out var resizedStyle));
        Assert.Equal(480d, resizedStyle.Height);
        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

        Assert.True(renderer.LastLayout.TryGetElementRect(frame, out var resizedRect));
        Assert.Equal(480f, resizedRect.Height, 1f);
    }

    [Fact]
    public async Task GeometryRead_FlushesPendingLayoutBeforeResolvingBox()
    {
        var baseUri = new Uri("https://parent.test/page");
        var document = new HtmlParser("<html><body><iframe id='frame'></iframe></body></html>", baseUri).Parse();
        var box = new BoxModel();
        var flushes = 0;
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FlushPendingLayout = () =>
            {
                flushes++;
                box.BorderBox = new SKRect(0, 0, 300, 480);
                box.PaddingBox = box.BorderBox;
                box.ContentBox = box.BorderBox;
            },
            LayoutBoxResolver = _ => box
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.Equal("480", engine.Evaluate("String(document.getElementById('frame').offsetHeight)")?.ToString());
        Assert.Equal(1, flushes);
    }

    [Fact]
    public async Task IframeResize_IsVisibleToSynchronousGeometryReads()
    {
        var baseUri = new Uri("https://parent.test/page");
        var document = new HtmlParser(
            "<html><body><iframe id='frame' style='width:300px;height:78px'></iframe></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate(
            "var frame=document.getElementById('frame');" +
            "frame.style.height='480px';" +
            "[frame.offsetHeight,frame.getBoundingClientRect().height].join('|')");

        Assert.Equal("480|480", result?.ToString());
    }

    [Fact]
    public async Task ResizeObserver_DeliversUpdatedInlineGeometry()
    {
        var baseUri = new Uri("https://parent.test/page");
        var document = new HtmlParser(
            "<html><body><iframe id='frame' style='width:300px;height:78px'></iframe></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        engine.Evaluate(
            "var frame=document.getElementById('frame');" +
            "var observer=new ResizeObserver(function(entries){globalThis.__resizeHeight=entries[0].contentRect.height;});" +
            "observer.observe(frame);frame.style.height='480px';");

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (engine.Evaluate("String(globalThis.__resizeHeight || 0)")?.ToString() == "480")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal("480", engine.Evaluate("String(globalThis.__resizeHeight)")?.ToString());
    }

    [Fact]
    public async Task ScreenMetrics_ReflectBrowsingViewport()
    {
        var baseUri = new Uri("https://parent.test/page");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            WindowWidth = 1280,
            WindowHeight = 800
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.Equal(
            "object|1280|800|1280|800|24|landscape-primary",
            engine.Evaluate(
                "[typeof screen,screen.width,screen.height,screen.availWidth,screen.availHeight," +
                "screen.colorDepth,screen.orientation.type].join('|')")?.ToString());
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (predicate())
                {
                    return;
                }
            }
            catch
            {
                // The detached script bootstrap has not installed document yet.
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the script document.");
    }

    private static void RenderFrame(
        SkiaDomRenderer renderer,
        Element root,
        System.Collections.Generic.Dictionary<Node, FenBrowser.Core.Css.CssComputed> styles,
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
            BaseUrl = "https://parent.test/page",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(IframeDynamicGeometryTests),
            EmitVerificationReport = false
        });
        canvas.Flush();
    }
}
