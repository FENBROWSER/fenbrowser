using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// CSS 2.1 §10.8.1: vertical-align:text-bottom puts a box's bottom at the bottom of its
/// parent's content area (baseline + font descent). github.com's login logo - a 48px
/// svg with style="vertical-align:text-bottom" in a 44px round clip - sat 5px below
/// the line top and lost its bottom to the clip.
/// </summary>
public sealed class VerticalAlignTextBottomTests
{
    [Fact]
    public async Task TallAtomicInline_StartsAtTheLineTop()
    {
        const string verticalAlign = "text-bottom";
        var html = "<!doctype html><html><body style='margin:0;font:14px/1.5 sans-serif'>" +
            "<div id='wrap' style='width:44px;height:44px;overflow:hidden'>" +
            "<span id='logo' style='display:inline-block;width:48px;height:48px;vertical-align:" + verticalAlign + "'></span>" +
            "</div></body></html>";
        var baseUri = new Uri("https://vertical-align-text-bottom.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 200, viewportHeight: 200);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 200, 200), baseUri.AbsoluteUri, (_, _) => { });

        var logo = root.Descendants().OfType<Element>().First(e => e.Id == "logo");
        var wrap = root.Descendants().OfType<Element>().First(e => e.Id == "wrap");
        Assert.True(renderer.LastLayout.TryGetElementRect(logo, out var logoRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(wrap, out var wrapRect));
        Assert.Equal(wrapRect.Y, logoRect.Y, 0.5f);
    }
}
