using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The tile rasterizer keeps tiles across frames: all of them while only the viewport
/// scrolls, and the undamaged ones when the paint tree changes. Every frame a long-lived
/// renderer produces must match what a fresh renderer paints for the same state, pixel
/// for pixel; a stale tile shows up as a difference.
/// </summary>
public sealed class RetainedTileReuseTests
{
    private const int Width = 600;
    private const int Height = 400;

    private const string Page =
        "<!doctype html><html><head><style>" +
        "html,body{margin:0;background:#fff}" +
        ".row{height:90px;margin:10px;background:rgb(200,220,240)}" +
        "#spin{width:120px;height:60px;margin:20px;transform:rotate(20deg) translate(30px,10px);background:rgb(0,0,200)}" +
        "#head{position:fixed;top:0;left:0;width:100%;height:30px;background:rgb(200,0,0)}" +
        "</style></head><body><div id='head'></div>" +
        "<div class='row'></div><div id='spin'></div><div class='row'></div><div class='row'></div>" +
        "<div class='row'></div><div class='row'></div><div class='row'></div><div class='row'></div>" +
        "<div class='row'></div><div class='row'></div><div class='row'></div><div class='row'></div>" +
        "</body></html>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScrollWithUnchangedPage_ReusesTilesAndMatchesAFreshRender(bool fixedHeader)
    {
        // A fixed header moves in document space on every scroll, so on a page this
        // small it damages most tiles; without one the tree is reused and so are tiles.
        var page = await LoadAsync(fixedHeader ? Page : Page.Replace("<div id='head'></div>", string.Empty));
        var renderer = new SkiaDomRenderer();
        Frame(renderer, page, 0f, RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom).Dispose();
        Settle(renderer, page, 0f);

        foreach (var scrollY in new[] { 60f, 140f, 300f, 140f, 0f })
        {
            using var retained = Frame(renderer, page, scrollY, RenderFrameInvalidationReason.ProcessIsolation);
            var stats = renderer.LastRetainedTileRasterization;
            if (!fixedHeader)
            {
                Assert.True(stats.ReusedTileCount > 0, $"no tile reused at scrollY={scrollY}");
            }
            AssertMatchesFresh(retained, page, scrollY);
        }
    }

    [Fact]
    public async Task RepaintInsideATransform_RedrawsItsTilesAndMatchesAFreshRender()
    {
        var page = await LoadAsync();
        var renderer = new SkiaDomRenderer();
        Frame(renderer, page, 0f, RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom).Dispose();
        Settle(renderer, page, 0f);

        var spin = page.Root.Descendants().OfType<Element>().First(e => e.Id == "spin");
        page.Styles[spin].BackgroundColor = new SKColor(0, 160, 0);
        spin.MarkDirty(InvalidationKind.Paint);

        using var retained = Frame(renderer, page, 0f, RenderFrameInvalidationReason.ProcessIsolation);
        Assert.True(renderer.LastRetainedTileRasterization.ReusedTileCount > 0, "every tile was redrawn");
        AssertMatchesFresh(retained, page, 0f);
    }

    [Fact]
    public void CanvasBackgroundChange_RedrawsEveryTile()
    {
        // Tiles are cleared to the canvas background, which no paint node carries, so a
        // tree diff never damages it: a page whose background arrived with its stylesheet
        // kept white tiles below its content (css/filter-effects/feconvolve-region-001).
        var tree = new ImmutablePaintTree(new List<PaintNodeBase>
        {
            new BackgroundPaintNode { Bounds = new SKRect(0, 0, 40, 20), Color = new SKColor(0, 0, 200) }
        });
        var viewport = new SKRect(0, 0, Width, Height);
        using var rasterizer = new RetainedTileRasterizer();
        var skia = new SkiaRenderer();

        using var first = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(first))
        {
            rasterizer.Rasterize(canvas, skia, tree, viewport, SKColors.White, null, null, false, null);
        }

        using var second = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(second))
        {
            var stats = rasterizer.Rasterize(canvas, skia, tree, viewport, new SKColor(144, 238, 144), null, null, false, null);
            Assert.Equal(stats.VisibleTileCount, stats.RasterizedTileCount);
        }

        Assert.Equal(new SKColor(144, 238, 144), second.GetPixel(Width - 1, Height - 1));
        Assert.Equal(new SKColor(144, 238, 144), second.GetPixel(300, 300));
    }

    private sealed record LoadedPage(Element Root, Dictionary<Node, FenBrowser.Core.Css.CssComputed> Styles, Uri BaseUri);

    private static async Task<LoadedPage> LoadAsync(string html = Page)
    {
        var baseUri = new Uri("https://tile-reuse.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        root.ClearDirty(InvalidationKind.Style);
        foreach (var node in root.Descendants())
        {
            node.ClearDirty(InvalidationKind.Style);
        }

        return new LoadedPage(root, styles, baseUri);
    }

    private static void Settle(SkiaDomRenderer renderer, LoadedPage page, float scrollY)
    {
        for (var i = 0; i < 8; i++)
        {
            Frame(renderer, page, scrollY, RenderFrameInvalidationReason.ProcessIsolation).Dispose();
            if (renderer.LastFrameTelemetry?.PaintTreeRebuilt == false) return;
        }
    }

    private static void AssertMatchesFresh(SKBitmap retained, LoadedPage page, float scrollY)
    {
        using var fresh = Frame(new SkiaDomRenderer(), page, scrollY,
            RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
        int differing = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (retained.GetPixel(x, y) != fresh.GetPixel(x, y)) differing++;
            }
        }

        Assert.True(differing == 0,
            $"{differing} pixels differ from a fresh render at scrollY={scrollY} " +
            $"(at 300,300 retained {retained.GetPixel(300, 300)} fresh {fresh.GetPixel(300, 300)})");
    }

    private static SKBitmap Frame(SkiaDomRenderer renderer, LoadedPage page, float scrollY, RenderFrameInvalidationReason reason)
    {
        var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        renderer.ScrollManager.SetScrollBounds(null, Width, 2000, Width, Height);
        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);
        canvas.Save();
        canvas.Translate(0, -scrollY);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = page.Root,
            Canvas = canvas,
            Styles = page.Styles,
            Viewport = new SKRect(0, scrollY, Width, scrollY + Height),
            SeparateLayoutViewport = new SKSize(Width, Height),
            BaseUrl = page.BaseUri.AbsoluteUri,
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "tile-reuse-test",
            EmitVerificationReport = false
        });
        canvas.Restore();
        canvas.Flush();
        return bitmap;
    }
}
