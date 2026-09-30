using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// CSS Flexbox 1 §4.1: an absolutely-positioned child of a flex container with auto
/// insets sits where it would as the container's sole flex item. github.com's login
/// "or" divider is a centred flex row whose rule is an abs child with no top.
/// </summary>
public sealed class FlexAbsoluteChildStaticPositionTests
{
    [Fact]
    public async Task AutoTop_FollowsAlignItemsCenter()
    {
        var rect = await LayOutRuleAsync("align-items:center;justify-content:center", "left:0;right:0;height:2px");
        Assert.Equal(19f, rect.Top, 0.5f);
        Assert.Equal(200f, rect.Width, 0.5f);
    }

    [Fact]
    public async Task AutoInsets_FollowJustifyContentAndAlignItemsEnd()
    {
        var rect = await LayOutRuleAsync("align-items:flex-end;justify-content:flex-end", "width:20px;height:10px");
        Assert.Equal(30f, rect.Top, 0.5f);
        Assert.Equal(180f, rect.Left, 0.5f);
    }

    // Box Alignment 3 §4.4: safe/unsafe keep the alignment unless (safe) the box overflows.
    [Fact]
    public async Task OverflowPositionKeywords_AlignLikeTheirValue()
    {
        var rect = await LayOutRuleAsync("align-items:safe end;justify-content:unsafe center", "width:20px;height:10px");
        Assert.Equal(30f, rect.Top, 0.5f);
        Assert.Equal(90f, rect.Left, 0.5f);

        var overflowing = await LayOutRuleAsync("align-items:safe end", "width:20px;height:60px");
        Assert.Equal(0f, overflowing.Top, 0.5f);
    }

    [Fact]
    public async Task DefaultAlignment_KeepsTheFlowOrigin()
    {
        var rect = await LayOutRuleAsync("", "width:20px;height:10px");
        Assert.Equal(0f, rect.Top, 0.5f);
        Assert.Equal(0f, rect.Left, 0.5f);
    }

    private static async Task<SKRect> LayOutRuleAsync(string containerStyle, string ruleStyle)
    {
        var html = "<!doctype html><html><body style='margin:0'>" +
            "<div style='display:flex;position:relative;width:200px;height:40px;" + containerStyle + "'>" +
            "<span>or</span><div id='rule' style='position:absolute;" + ruleStyle + "'></div>" +
            "</div></body></html>";
        var baseUri = new Uri("https://flex-static-position.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 400, viewportHeight: 200);

        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(400, 200);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(root, canvas, styles, new SKRect(0, 0, 400, 200), baseUri.AbsoluteUri, (_, _) => { });

        var rule = root.Descendants().OfType<Element>().First(e => e.Id == "rule");
        Assert.True(renderer.LastLayout.TryGetElementRect(rule, out var rect), "no box for #rule");
        return new SKRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
    }
}
