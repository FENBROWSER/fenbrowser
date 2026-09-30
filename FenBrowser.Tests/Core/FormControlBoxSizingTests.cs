using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// github.com's login form: `width: 100%; padding: 12px; border: 1px;
/// box-sizing: border-box` controls in a 352px form whose text-align is center.
/// </summary>
public sealed class FormControlBoxSizingTests
{
    private const string Page = "<!doctype html><html><body style='margin:0'>" +
        "<div id='form' style='width:352px;text-align:center'>" +
        "<input id='field' type='text' value='udaie-a' style='width:100%;padding:12px;border:1px solid #888;box-sizing:border-box'>" +
        "<input id='submit' type='submit' value='Sign in' style='display:block;width:100%;padding:16px;border:1px solid #888;box-sizing:border-box'>" +
        "<textarea id='area' style='display:block;width:100%;padding:8px;border:1px solid #888;box-sizing:border-box'></textarea>" +
        "</div></body></html>";

    // CSS Box Sizing 3 §3: the 100% is the border box, so every control is 352px wide.
    // Form controls were sized as if it were the content box (378px, 386px) and
    // overflowed the form.
    [Fact]
    public async Task BorderBoxPercentageWidth_IncludesPaddingAndBorder_ForFormControls()
    {
        var (root, styles, renderer) = await RenderAsync();
        foreach (var id in new[] { "field", "submit", "area" })
        {
            Assert.True(renderer.LastLayout.TryGetElementRect(ById(root, id), out var rect), $"no box for #{id}");
            Assert.Equal(352f, rect.Width, 0.5f);
        }
    }

    private static Element ById(Element root, string id) =>
        root.Descendants().OfType<Element>().First(e => e.Id == id);

    private static async Task<(Element Root, Dictionary<Node, CssComputed> Styles, SkiaDomRenderer Renderer)> RenderAsync()
    {
        var baseUri = new Uri("https://form-sizing.test/");
        var doc = new HtmlParser(Page, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 800, viewportHeight: 600);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 800, 600), baseUri.AbsoluteUri, (_, _) => { });
        return (root, styles, renderer);
    }
}
