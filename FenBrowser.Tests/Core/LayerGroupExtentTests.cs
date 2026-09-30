using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Opacity groups and filtered stacking contexts are culled and their Skia layers
/// sized by the paint extent of the whole subtree, not the element's own box. A
/// descendant that overflows the group must still paint, and a group scrolled back
/// into view must paint again.
/// </summary>
public sealed class LayerGroupExtentTests
{
    private const int Width = 600;
    private const int Height = 400;

    [Theory]
    [InlineData("opacity:.5")]
    [InlineData("filter:blur(2px)")]
    public async Task OverflowingDescendantOfLayerGroup_StillPaints(string groupStyle)
    {
        var html = "<!doctype html><html><head><style>" +
            "html,body{margin:0;background:#fff}" +
            "#group{position:relative;width:50px;height:50px;" + groupStyle + "}" +
            "#far{position:absolute;left:250px;top:250px;width:60px;height:60px;background:rgb(0,0,255)}" +
            "</style></head><body><div id='group'><div id='far'></div></div></body></html>";
        using var bitmap = await RenderAsync(html, scrollY: 0f);

        var far = bitmap.GetPixel(280, 280);
        Assert.True(far.Blue > 100 && far.Red < 200, $"overflowing child of the group was not painted: {far}");
    }

    [Fact]
    public async Task LayerGroupScrolledBackIntoView_Paints()
    {
        var html = "<!doctype html><html><head><style>" +
            "html,body{margin:0;background:#fff}" +
            "#spacer{height:2000px}" +
            "#group{width:200px;height:100px;opacity:.5;background:rgb(255,0,0)}" +
            "</style></head><body><div id='group'></div><div id='spacer'></div></body></html>";
        var baseUri = new Uri("https://layer-extent.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        var renderer = new SkiaDomRenderer();

        using (var offscreen = Render(renderer, root, styles, baseUri, 1500f,
                   RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom))
        {
            var p = offscreen.GetPixel(50, 50);
            Assert.True(p.Red > 240 && p.Green > 240, $"group painted while scrolled away: {p}");
        }

        using var back = Render(renderer, root, styles, baseUri, 0f, RenderFrameInvalidationReason.ProcessIsolation);
        var q = back.GetPixel(50, 50);
        Assert.True(q.Red > 200 && q.Green < 230, $"group missing after scrolling back: {q}");
    }

    private static async Task<SKBitmap> RenderAsync(string html, float scrollY)
    {
        var baseUri = new Uri("https://layer-extent.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        return Render(new SkiaDomRenderer(), root, styles, baseUri, scrollY,
            RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
    }

    private static SKBitmap Render(
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
        renderer.ScrollManager.SetScrollBounds(null, Width, 2400, Width, Height);
        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);
        canvas.Save();
        canvas.Translate(0, -scrollY);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, scrollY, Width, scrollY + Height),
            SeparateLayoutViewport = new SKSize(Width, Height),
            BaseUrl = baseUri.AbsoluteUri,
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "layer-extent-test",
            EmitVerificationReport = false
        });
        canvas.Restore();
        canvas.Flush();
        return bitmap;
    }
}
