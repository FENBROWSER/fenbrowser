using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// CSS Grid 2 §11.8: auto tracks absorb free space only when the axis' content
/// distribution is normal or stretch. Primer's .Button-content is exactly this grid;
/// github.com's "Continue with Google" icon sat at the button edge with the label
/// centred in a stretched middle track.
/// </summary>
public sealed class GridAutoTrackStretchTests
{
    [Fact]
    public async Task PlaceContentCenter_CentresContentSizedTracks()
    {
        var (grid, icon, label) = await LayOutAsync("place-content:center");
        float leftGap = icon.X - grid.X;
        float rightGap = grid.X + grid.Width - (label.X + label.Width);
        Assert.True(leftGap > 40f, $"icon at {leftGap}px from the start edge");
        Assert.Equal(leftGap, rightGap, 1.5f);
        Assert.Equal(icon.X + icon.Width + 8f, label.X, 1.5f);
    }

    // github.com's markup and Primer's rules: the grid is a flex:1 item of a full-width
    // inline-flex button. Without §11.6 (Maximize Tracks) the minmax(0,auto) label track
    // stayed 0 wide, the label overflowed it and the centred content sat off to the right.
    [Fact]
    public async Task PrimerButton_CentresIconAndLabelTogether()
    {
        const string css =
            ".Button{display:inline-flex;justify-content:space-between;align-items:center;gap:4px;height:32px;padding:0 12px;font-size:14px;" +
            "border:1px solid;position:relative;min-width:max-content;text-align:center;flex-direction:row}" +
            ".Button--fullWidth{width:100%}" +
            ".Button-content{flex:1 0 auto;grid-template-columns:min-content minmax(0,auto) min-content;" +
            "grid-template-areas:\"leadingVisual text trailingVisual\";place-content:center;align-items:center;display:grid}" +
            ".Button-content>:not(:last-child){margin-right:8px}" +
            ".Button-visual{pointer-events:none;display:flex}" +
            ".Button-leadingVisual{grid-area:leadingVisual}" +
            ".Button-label{line-height:20px;white-space:nowrap;grid-area:text}";
        var html = "<!doctype html><html><head><style>" + css + "</style></head><body style='margin:0'><div style='width:352px'>" +
            "<button id='button' type='submit' class='Button Button--fullWidth'>  <span class='Button-content'>\n      " +
            "<span id='icon' class='Button-visual Button-leadingVisual'>\n        <i style='display:block;width:16px;height:16px'></i>\n      </span>\n    " +
            "<span id='label' class='Button-label'>Continue with Google</span>\n  " +
            "</span>\n</button></div></body></html>";
        var (root, renderer) = await RenderAsync(html, viewportWidth: 800);
        var button = Rect(root, renderer, "button");
        var icon = Rect(root, renderer, "icon");
        var label = Rect(root, renderer, "label");
        Assert.True(label.Width > 100f, $"label track is {label.Width}px wide");
        float leftGap = icon.X - button.X;
        float rightGap = button.X + button.Width - (label.X + label.Width);
        Assert.Equal(leftGap, rightGap, 2f);
        Assert.Equal(icon.X + icon.Width + 8f, label.X, 1.5f);
    }

    [Fact]
    public async Task NormalDistribution_StillStretchesTheAutoTrack()
    {
        var (grid, icon, label) = await LayOutAsync("");
        Assert.Equal(grid.X, icon.X, 0.5f);
        Assert.Equal(grid.X + grid.Width, label.X + label.Width, 1.5f);
    }

    private static async Task<(ElementGeometry Grid, ElementGeometry Icon, ElementGeometry Label)> LayOutAsync(string distribution)
    {
        var html = "<!doctype html><html><body style='margin:0'>" +
            "<div id='grid' style='display:grid;width:300px;grid-template-columns:min-content minmax(0,auto) min-content;" +
            "grid-template-areas:\"leadingVisual text trailingVisual\";align-items:center;" + distribution + "'>" +
            "<span id='icon' style='grid-area:leadingVisual;display:flex;width:16px;height:16px;margin-right:8px'></span>" +
            "<span id='label' style='grid-area:text;white-space:nowrap'>Continue with Google</span>" +
            "</div></body></html>";
        var (root, renderer) = await RenderAsync(html);
        return (Rect(root, renderer, "grid"), Rect(root, renderer, "icon"), Rect(root, renderer, "label"));
    }

    private static ElementGeometry Rect(Element root, SkiaDomRenderer renderer, string id)
    {
        var element = root.Descendants().OfType<Element>().First(e => e.Id == id);
        Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"no box for #{id}");
        return rect;
    }

    private static async Task<(Element Root, SkiaDomRenderer Renderer)> RenderAsync(string html, int viewportWidth = 400)
    {
        var baseUri = new Uri("https://grid-auto-stretch.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: viewportWidth, viewportHeight: 200);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(viewportWidth, 200);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, viewportWidth, 200), baseUri.AbsoluteUri, (_, _) => { });
        return (root, renderer);
    }
}
