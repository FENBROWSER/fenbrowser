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
        var baseUri = new Uri("https://grid-auto-stretch.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 400, viewportHeight: 200);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(400, 200);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 400, 200), baseUri.AbsoluteUri, (_, _) => { });

        ElementGeometry Rect(string id)
        {
            var element = root.Descendants().OfType<Element>().First(e => e.Id == id);
            Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"no box for #{id}");
            return rect;
        }

        return (Rect("grid"), Rect("icon"), Rect("label"));
    }
}
