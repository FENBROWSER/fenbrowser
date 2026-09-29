using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// A paint-only invalidation rebuilds just the dirty subtree and splices it into
/// the retained paint tree. The splice must replace every node the subtree
/// produced last time; leaving any behind grows the tree by a copy of the
/// subtree per frame (github.com went 1,157 -> 26,625 paint nodes in 23 frames,
/// raster 360ms -> 8s, while its hero video repainted).
/// </summary>
public sealed class IncrementalPaintTreeGrowthTests
{
    private const int Width = 400;
    private const int Height = 300;

    [Theory]
    [InlineData("HTML")]
    [InlineData("BODY")]
    [InlineData("SECTION")]
    [InlineData("VIDEO")]
    public async Task PaintOnlyRepaint_DoesNotGrowThePaintTree(string dirtyTag)
    {
        var html = "<!doctype html><html><head><style>" +
            "html{background:#eef}body{margin:0;background:#fff}" +
            "section{position:relative;z-index:1;opacity:.9;height:120px;background:rgb(0,0,255)}" +
            ".glow{position:absolute;inset:0;filter:blur(8px);background:rgb(255,0,255)}" +
            "video{display:block;width:200px;height:100px;background:#000}" +
            "</style></head><body><section><div class='glow'></div><p>text</p></section>" +
            "<video></video><div style='position:fixed;top:0;height:10px;width:10px;background:red'></div>" +
            "<p>more</p></body></html>";
        var baseUri = new Uri("https://paint-growth.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        var renderer = new SkiaDomRenderer();
        var dirty = dirtyTag == "HTML" ? root : root.Descendants().OfType<Element>().First(e => e.TagName == dirtyTag);

        Render(renderer, root, styles, baseUri, RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
        var baseline = Render(renderer, root, styles, baseUri, RenderFrameInvalidationReason.ProcessIsolation);

        for (var i = 0; i < 4; i++)
        {
            dirty.MarkDirty(InvalidationKind.Paint);
            var telemetry = Render(renderer, root, styles, baseUri, RenderFrameInvalidationReason.ProcessIsolation);
            Assert.True(telemetry.PaintTreeRebuilt, "paint-dirty frame did not rebuild");
            Assert.Equal(baseline.PaintNodeCount, telemetry.PaintNodeCount);
        }
    }

    private static RenderFrameTelemetry Render(
        SkiaDomRenderer renderer,
        Element root,
        Dictionary<Node, FenBrowser.Core.Css.CssComputed> styles,
        Uri baseUri,
        RenderFrameInvalidationReason reason)
    {
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        var result = renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, Width, Height),
            BaseUrl = baseUri.AbsoluteUri,
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "paint-growth-test",
            EmitVerificationReport = false
        });
        return result.Telemetry;
    }
}
