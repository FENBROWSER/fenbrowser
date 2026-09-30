using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// An inline &lt;svg&gt; receives the page's computed presentation properties. font-size
/// went across as the declared text (calc(), clamp(), rem, a var() chain), which the SVG
/// renderer cannot evaluate, so it fell back and painted nothing: github.com's login
/// logo sits under `font-size: var(--text-body-size-medium)`.
/// </summary>
public sealed class InlineSvgComputedFontSizeTests
{
    [Theory]
    [InlineData("calc(10px + 4px)")]
    [InlineData("clamp(12px, 2vw, 18px)")]
    [InlineData("0.875rem")]
    public async Task InlineSvg_UnderAComputedFontSize_StillPaints(string fontSize)
    {
        var html = "<!doctype html><html><body style='margin:0;background:#fff'>" +
            "<div style='font-size:" + fontSize + "'>" +
            "<svg width='40' height='40' viewBox='0 0 40 40'><rect width='40' height='40' fill='rgb(255,0,0)'/></svg>" +
            "</div></body></html>";
        var baseUri = new Uri("https://inline-svg-font-size.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 200, viewportHeight: 100);

        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        new SkiaDomRenderer().Render(root, canvas, styles, new SKRect(0, 0, 200, 100), baseUri.AbsoluteUri, (_, _) => { });

        var pixel = bitmap.GetPixel(20, 20);
        Assert.True(pixel.Red > 200 && pixel.Green < 60 && pixel.Blue < 60, $"svg not painted: {pixel}");
    }
}
