using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class FlexShorthandValidationTests
{
    // CSS Flexbox 1 §7.1: grow and shrink are adjacent; `flex: 1 0% 1` matches nothing and
    // is dropped, so the item keeps its width (WPT flex-shorthand-flex-basis-middle).
    [Theory]
    [InlineData("flex:1 0% 1", 50f)]
    [InlineData("flex:1 auto 1", 50f)]
    [InlineData("flex:1 1 0", 166.67f)]
    [InlineData("flex:1 1 0%", 166.67f)]
    [InlineData("flex:1 1 4em", 166.67f)]
    [InlineData("flex:2 1e1px", 166.67f)]
    public async Task FlexShorthand_ThatDoesNotMatchTheGrammar_IsDropped(string flex, float expectedWidth)
    {
        var item = "<div style='width:50px;" + flex + "'></div>";
        var html = "<!doctype html><html><body style='margin:0'><div style='display:flex;width:500px;height:100px'>" +
            item.Replace("<div ", "<div id='a' ") + item + item + "</div></body></html>";
        var (root, renderer) = await RenderAsync(html);
        Assert.Equal(expectedWidth, Rect(root, renderer, "a").Width, 1f);
    }

    private static async Task<(Element Root, SkiaDomRenderer Renderer)> RenderAsync(string html)
    {
        var baseUri = new Uri("https://flex-shorthand.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1280, viewportHeight: 800);
        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(1280, 800);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 1280, 800), baseUri.AbsoluteUri, (_, _) => { });
        return (root, renderer);
    }

    private static FenBrowser.FenEngine.Layout.ElementGeometry Rect(Element root, SkiaDomRenderer renderer, string id)
    {
        var element = root.Descendants().OfType<Element>().First(e => e.Id == id);
        Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"no box for #{id}");
        return rect;
    }
}
