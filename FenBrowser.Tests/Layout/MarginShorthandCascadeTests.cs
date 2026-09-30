using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// CSS Cascade 5 §6.4: a shorthand sets its longhands, and whichever declaration wins the
/// cascade for a longhand decides that side. accounts.google.com's card is `margin: 0 auto`
/// overridden at 1240px by `margin-left: 200px; margin-right: 200px`; the shorthand's
/// auto survived and the card stretched across the whole viewport.
/// </summary>
public sealed class MarginShorthandCascadeTests
{
    [Theory]
    // A later longhand overrides the shorthand's auto (rule order, then declaration order).
    [InlineData(".a,.b{margin:0 auto;width:100%} .a{margin-left:200px;margin-right:200px;width:auto}", 200f, 880f)]
    [InlineData(".a{margin:0 auto;width:100%} @media (min-width:1240px) and (orientation:landscape){.a{margin-left:200px;margin-right:200px;width:auto}}", 200f, 880f)]
    [InlineData(".a{margin:0 auto;margin-left:200px;width:300px}", 200f, 300f)]
    // A later shorthand still wins over an earlier longhand.
    [InlineData(".a{margin-left:200px;width:auto} .a{margin:0 auto;width:300px}", 490f, 300f)]
    [InlineData(".a{margin-left:200px;margin:0 auto;width:300px}", 490f, 300f)]
    public async Task LaterDeclaration_DecidesEachMarginSide(string css, float expectedX, float expectedWidth)
    {
        var html = "<!doctype html><html><head><style>" + css + "</style></head><body style='margin:0'>" +
            "<div style='display:flex;flex-direction:column'><div id='x' class='a'>x</div></div></body></html>";
        var baseUri = new Uri("https://margin-cascade.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1280, viewportHeight: 800);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(1280, 800);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 1280, 800), baseUri.AbsoluteUri, (_, _) => { });

        var x = root.Descendants().OfType<Element>().First(e => e.Id == "x");
        Assert.True(renderer.LastLayout.TryGetElementRect(x, out var rect));
        Assert.Equal(expectedX, rect.X, 0.5f);
        Assert.Equal(expectedWidth, rect.Width, 0.5f);
    }
}
