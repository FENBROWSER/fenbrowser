using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// position:fixed boxes are laid out in unscrolled document coordinates and the
/// paint tree counter-translates them by the viewport scroll so they stay put
/// while the canvas scrolls. github.com nests its fixed header inside a fixed
/// .header-wrapper; translating both dropped the header by the scroll offset,
/// so it slid down the page as the user scrolled.
/// </summary>
public sealed class FixedPositionScrollRepaintTests
{
    private const int Width = 400;
    private const int Height = 300;

    [Theory]
    [InlineData("<div id='bar'></div>", "fixed")]
    [InlineData("<div id='wrap'><header id='bar'></header></div>", "fixed")]
    [InlineData("<div id='bar'></div>", "sticky")]
    public async Task ScrolledFrame_KeepsFixedBarAtViewportTop(string barMarkup, string position)
    {
        var html = "<!doctype html><html><head><style>" +
            "html,body{margin:0;background:#fff}" +
            "#wrap{position:fixed;top:0;left:0;width:100%;height:2px}" +
            "#bar{position:" + position + ";top:0;left:0;width:100%;height:40px;background:rgb(255,0,0)}" +
            "#tall{height:3000px}" +
            "</style></head><body>" + barMarkup + "<div id='tall'></div></body></html>";
        var baseUri = new Uri("https://fixed-scroll.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        // The browser's cascade clears these once styles are computed; left set, every
        // frame looks like a style change and the paint tree is never reused.
        root.ClearDirty(InvalidationKind.Style);
        foreach (var node in root.Descendants())
        {
            node.ClearDirty(InvalidationKind.Style);
        }
        var renderer = new SkiaDomRenderer();

        using var first = RenderAt(renderer, root, styles, baseUri, scrollY: 0f,
            RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
        Assert.True(IsRed(first.GetPixel(Width / 2, 20)), "fixed bar missing at scroll 0");

        // Quiet frames until the renderer reuses its paint tree, so the scroll below is
        // the only thing that changed: nothing marks the DOM dirty for a viewport scroll.
        for (var i = 0; i < 8 && LastTelemetry?.PaintTreeRebuilt != false; i++)
        {
            RenderAt(renderer, root, styles, baseUri, scrollY: 0f, RenderFrameInvalidationReason.ProcessIsolation).Dispose();
        }
        Assert.False(LastTelemetry?.PaintTreeRebuilt, "a quiet frame still rebuilt the paint tree");

        // Same reason the renderer child sends for a broker frame request after a scroll.
        using var scrolled = RenderAt(renderer, root, styles, baseUri, scrollY: 200f,
            RenderFrameInvalidationReason.ProcessIsolation);
        Assert.True(IsRed(scrolled.GetPixel(Width / 2, 20)), "fixed bar left the viewport top after scrolling");
        Assert.False(IsRed(scrolled.GetPixel(Width / 2, 220)), "fixed bar dropped by the scroll offset");

        using var back = RenderAt(renderer, root, styles, baseUri, scrollY: 0f,
            RenderFrameInvalidationReason.ProcessIsolation);
        Assert.True(IsRed(back.GetPixel(Width / 2, 20)), "fixed bar missing after scrolling back to the top");
    }

    private SKBitmap RenderAt(
        SkiaDomRenderer renderer,
        Element root,
        Dictionary<Node, FenBrowser.Core.Css.CssComputed> styles,
        Uri baseUri,
        float scrollY,
        RenderFrameInvalidationReason reason)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        // Mirrors the renderer child: document-space viewport, canvas translated by -scrollY.
        renderer.ScrollManager.SetScrollBounds(null, Width, 3000, Width, Height);
        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);
        canvas.Save();
        canvas.Translate(0, -scrollY);
        LastTelemetry = renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, scrollY, Width, scrollY + Height),
            SeparateLayoutViewport = new SKSize(Width, Height),
            BaseUrl = baseUri.AbsoluteUri,
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "fixed-scroll-test",
            EmitVerificationReport = false
        }).Telemetry;
        canvas.Restore();
        canvas.Flush();
        return bitmap;
    }

    private RenderFrameTelemetry? LastTelemetry;

    private static bool IsRed(SKColor color) => color.Red > 200 && color.Green < 60 && color.Blue < 60;
}
