using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// CSS Color 4 §15.1: opacity applies to the element as a whole, once. Any
/// opacity below 1 also makes the element a stacking context, and the stacking
/// context already groups the element's own paint with its descendants; wrapping
/// the element's own nodes in a second group squared the opacity (.5 painted as .25).
/// </summary>
public sealed class OpacityAppliedOnceTests
{
    private const int Width = 200;
    private const int Height = 100;

    [Theory]
    [InlineData("opacity:.5", 128)]
    [InlineData("opacity:.5;position:relative;z-index:1", 128)]
    [InlineData("opacity:.25", 191)]
    public async Task OpacityGroup_BlendsOnceOverTheBackdrop(string style, int expectedGreen)
    {
        var html = "<!doctype html><html><head><style>" +
            "html,body{margin:0;background:#fff}" +
            "#box{width:100px;height:50px;background:rgb(255,0,0);" + style + "}" +
            "</style></head><body><div id='box'></div></body></html>";
        var baseUri = new Uri("https://opacity-once.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: Width, viewportHeight: Height);

        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        new SkiaDomRenderer().RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, Width, Height),
            BaseUrl = baseUri.AbsoluteUri,
            InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom,
            RequestedBy = "opacity-once-test",
            EmitVerificationReport = false
        });
        canvas.Flush();

        var pixel = bitmap.GetPixel(50, 25);
        Assert.Equal(255, pixel.Red);
        Assert.InRange(pixel.Green, expectedGreen - 3, expectedGreen + 3);
        Assert.InRange(pixel.Blue, expectedGreen - 3, expectedGreen + 3);
    }
}
