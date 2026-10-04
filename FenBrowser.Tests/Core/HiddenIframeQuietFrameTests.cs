using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Before laying out a frame the renderer looks for iframes that have no box yet - a
/// nested browsing context inserted since the last layout - and forces a full layout
/// while any remain. A display:none iframe never gets a box, so it was "unmaterialized"
/// forever: YouTube's watch page keeps two, and every frame was a full layout (about
/// 21 a second while the player animated), which every script geometry read then
/// waited behind.
/// </summary>
public sealed class HiddenIframeQuietFrameTests
{
    private const int Width = 400;
    private const int Height = 300;

    [Theory]
    [InlineData("<iframe style='display:none' src='about:blank'></iframe>")]
    [InlineData("<div style='display:none'><iframe src='about:blank'></iframe></div>")]
    public async Task QuietFrame_WithAHiddenIframe_DoesNotLayOutAgain(string iframeMarkup)
    {
        var html = "<!doctype html><html><head><style>html,body{margin:0}#box{height:40px;background:red}</style></head>" +
            "<body><div id='box'></div>" + iframeMarkup + "</body></html>";
        var baseUri = new Uri("https://hidden-iframe.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);
        root.ClearDirty(InvalidationKind.Style);
        foreach (var node in root.Descendants())
        {
            node.ClearDirty(InvalidationKind.Style);
        }

        var renderer = new SkiaDomRenderer();
        var first = Render(renderer, root, styles, baseUri, RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
        Assert.True(first.LayoutUpdated);

        root.ClearDirty(InvalidationKind.Layout);
        foreach (var node in root.Descendants())
        {
            node.ClearDirty(InvalidationKind.Layout);
        }

        var quiet = Render(renderer, root, styles, baseUri, RenderFrameInvalidationReason.Paint);
        Assert.False(quiet.LayoutUpdated, "a quiet frame laid the document out again because of a hidden iframe");
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
        return renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, Width, Height),
            SeparateLayoutViewport = new SKSize(Width, Height),
            BaseUrl = baseUri.AbsoluteUri,
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "hidden-iframe-test",
            EmitVerificationReport = false
        }).Telemetry;
    }
}
