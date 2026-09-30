using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// accounts.google.com's sign-in card: a border-box column flex card (min-height:384px)
/// holding a wrapping row whose two columns are `flex-basis:50%; flex-grow:1; max-width:50%`
/// and whose button row is `width:100%` on a line of its own.
/// </summary>
public sealed class FlexWrapLineResolutionTests
{
    // CSS Flexbox 1 §7.3.1: a percentage basis is a share of the container, not pixels.
    [Theory]
    [InlineData("flex-basis:50%", "flex-basis:50%", "width:100%", 404f, 404f, 808f, true)]
    [InlineData("flex-basis:50%;flex-grow:1;max-width:50%", "flex-basis:50%;flex-grow:1;max-width:50%", "width:10px", 404f, 404f, 10f, true)]
    // §9.3: lines break on hypothetical sizes (404+404+34 > 808), then flex per line.
    [InlineData("flex:1 1 50%", "flex:1 1 50%", "", 404f, 404f, -1f, true)]
    [InlineData("flex:1 1 25%", "flex:1 1 25%", "flex:1 1 25%", 269.33f, 269.33f, 269.33f, false)]
    public async Task PercentageBasis_ResolvesAgainstTheContainer_AndLinesBreakOnIt(
        string a, string b, string c, float aWidth, float bWidth, float cWidth, bool cWraps)
    {
        var html = "<!doctype html><html><body style='margin:0'><div style='display:flex;flex-direction:column;width:808px'>" +
            "<main style='display:flex;flex-direction:row;flex-wrap:wrap;flex-grow:1'>" +
            "<div id='a' style='" + a + "'>left</div><div id='b' style='" + b + "'>right side</div><div id='c' style='" + c + "'>row</div>" +
            "</main></div></body></html>";
        var (root, renderer) = await RenderAsync(html);

        var ra = Rect(root, renderer, "a");
        var rb = Rect(root, renderer, "b");
        var rc = Rect(root, renderer, "c");
        Assert.Equal(aWidth, ra.Width, 1f);
        Assert.Equal(bWidth, rb.Width, 1f);
        if (cWidth >= 0) Assert.Equal(cWidth, rc.Width, 1f);
        Assert.Equal(ra.Y, rb.Y, 0.5f);
        Assert.Equal(cWraps, rc.Y > ra.Y + 1f);
    }

    // Stretching a row item to its line sets its cross size only; its resolved main size
    // (here a max-width:50% column) must survive the relayout.
    [Fact]
    public async Task StretchedItem_KeepsItsResolvedMainSize()
    {
        var html = "<!doctype html><html><body style='margin:0'>" +
            "<main style='display:flex;flex-wrap:wrap;width:808px'>" +
            "<div id='a' style='box-sizing:border-box;flex-basis:50%;flex-grow:1;max-width:50%;padding-right:24px;margin-top:-72px'>Sign in</div>" +
            "<div id='b' style='box-sizing:border-box;flex-basis:50%;flex-grow:1;max-width:50%;height:200px'>form</div>" +
            "</main></body></html>";
        var (root, renderer) = await RenderAsync(html);

        Assert.Equal(404f, Rect(root, renderer, "a").Width, 1f);
        Assert.Equal(404f, Rect(root, renderer, "b").X, 1f);
    }

    // A content-sized column container grows past a border-box min-height to fit its
    // items; the min-height names the border box, so its content minimum is 384 - 144.
    [Theory]
    [InlineData(300f, 444f)]
    [InlineData(100f, 384f)]
    public async Task BorderBoxMinHeight_IsAMinimum_ForAContentSizedColumn(float contentHeight, float expectedCardHeight)
    {
        var html = "<!doctype html><html><body style='margin:0'>" +
            "<div id='card' style='display:flex;flex-direction:column;box-sizing:border-box;min-height:384px;padding:108px 36px 36px;width:880px'>" +
            "<div id='body' style='display:flex;flex-direction:column;flex-grow:1'><div style='height:" + contentHeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px'></div></div>" +
            "</div></body></html>";
        var (root, renderer) = await RenderAsync(html);

        var card = Rect(root, renderer, "card");
        var body = Rect(root, renderer, "body");
        Assert.Equal(expectedCardHeight, card.Height, 1f);
        Assert.True(body.Y + body.Height <= card.Y + card.Height - 36f + 0.5f, $"body ends at {body.Y + body.Height}, card content ends at {card.Y + card.Height - 36f}");
    }

    private static ElementGeometry Rect(Element root, SkiaDomRenderer renderer, string id)
    {
        var element = root.Descendants().OfType<Element>().First(e => e.Id == id);
        Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"no box for #{id}");
        return rect;
    }

    private static async Task<(Element Root, SkiaDomRenderer Renderer)> RenderAsync(string html)
    {
        var baseUri = new Uri("https://flex-wrap-lines.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1280, viewportHeight: 800);
        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(1280, 800);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 1280, 800), baseUri.AbsoluteUri, (_, _) => { });
        return (root, renderer);
    }
}
