using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public class CssLogicalProjectionTests
{
    [Fact]
    public async Task LogicalMarginAutoAndInsetLonghands_ProjectToTypedRuntime()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { direction: ltr; position: fixed; margin-inline: auto; padding-inline-start: 36px; inset-inline-end: 3vw; inset-block-start: 12px; }",
            viewportWidth: 1000,
            viewportHeight: 800);

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.True(style.MarginLeftAuto);
        Assert.True(style.MarginRightAuto);
        Assert.InRange(style.Padding.Left, 35.99d, 36.01d);
        Assert.InRange(style.Right ?? -1d, 29.99d, 30.01d);
        Assert.InRange(style.Top ?? -1d, 11.99d, 12.01d);
    }

    [Fact]
    public async Task EscapedTailwindLogicalUtilities_ProjectToTypedRuntime()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".mx-auto { margin-inline: auto; } .w-full { width: 100%; } .max-w-\\[1280px\\] { max-width: 1280px; } .max-w-\\[clamp\\(80px\\,10vw\\,160px\\)\\] { max-width: clamp(80px,10vw,160px); } .size-\\[clamp\\(80px\\,10vw\\,160px\\)\\] { width: clamp(80px,10vw,160px); height: clamp(80px,10vw,160px); } .end-\\[3vw\\] { inset-inline-end: 3vw; } .bottom-\\[3vw\\] { inset-block-end: 3vw; }",
            viewportWidth: 1000,
            viewportHeight: 800,
            className: "box mx-auto w-full max-w-[1280px] max-w-[clamp(80px,10vw,160px)] size-[clamp(80px,10vw,160px)] end-[3vw] bottom-[3vw]");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.True(style.MarginLeftAuto);
        Assert.True(style.MarginRightAuto);
        Assert.Equal("clamp(80px,10vw,160px)", style.WidthExpression);
        Assert.Equal("clamp(80px,10vw,160px)", style.HeightExpression);
        Assert.Equal("clamp(80px,10vw,160px)", style.MaxWidthExpression);
        Assert.InRange(style.Right ?? -1d, 29.99d, 30.01d);
        Assert.InRange(style.Bottom ?? -1d, 29.99d, 30.01d);
    }

    private static async Task<(System.Collections.Generic.Dictionary<Node, CssComputed> Computed, Element Box)> ComputeForSingleBoxAsync(
        string css,
        double viewportWidth,
        double viewportHeight,
        string className = "box")
    {
        var html = $@"
<!doctype html>
<html>
<head>
    <style>{css}</style>
</head>
<body>
    <div class='{className}'>Probe</div>
</body>
</html>";

        var parser = new HtmlParser(html, new Uri("https://test.local"));
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth, viewportHeight);
        var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));
        return (computed, box);
    }
}
