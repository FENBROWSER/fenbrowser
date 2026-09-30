using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// CSS Flexbox 1 §4.3: a flex item with z-index other than auto stacks by it even while
/// position is static. github.com's login "or" divider puts its label (z-index:1, page
/// background) over an absolutely positioned rule; the rule struck through the label.
/// </summary>
public sealed class FlexItemZIndexStackingTests
{
    [Theory]
    [InlineData("flex")]
    [InlineData("grid")]
    public async Task StaticItemWithZIndex_PaintsAbovePositionedSibling(string display)
    {
        var html = "<!doctype html><html><body style='margin:0;background:#fff'>" +
            "<div style='display:" + display + ";position:relative;width:200px;height:40px;align-items:center;justify-content:center;place-items:center'>" +
            "<span style='z-index:1;width:40px;height:20px;background:rgb(0,0,255)'></span>" +
            "<div style='position:absolute;left:0;right:0;top:19px;height:2px;background:rgb(255,0,0)'></div>" +
            "</div></body></html>";
        var baseUri = new Uri("https://flex-item-z-index.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 200, viewportHeight: 100);

        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        new SkiaDomRenderer().Render(root, canvas, styles, new SKRect(0, 0, 200, 100), baseUri.AbsoluteUri, (_, _) => { });

        var outside = bitmap.GetPixel(10, 20);
        Assert.True(outside.Red > 200 && outside.Blue < 60, $"rule not painted: {outside}");
        var label = bitmap.GetPixel(100, 20);
        Assert.True(label.Blue > 200 && label.Red < 60, $"rule painted over the z-index:1 item: {label}");
    }
}
