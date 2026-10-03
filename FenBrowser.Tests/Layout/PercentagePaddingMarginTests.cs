using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS 2.1 §8.3 / §8.4: percentage margins and padding refer to the width of the
    /// containing block - for the top and bottom sides too. Percentages used to fall
    /// back to 0, which collapsed every padding-top aspect-ratio box: YouTube's search
    /// thumbnails (`ytd-thumbnail:before { padding-top: 56.11% }`) laid out 0px tall,
    /// their IntersectionObserver never saw them, and no image loaded.
    /// </summary>
    public sealed class PercentagePaddingMarginTests
    {
        private static async Task<Func<string, BoxModel>> LayoutAsync(string css, string body, float width = 1000, float height = 800)
        {
            var html = "<!doctype html><html><head><style>body { margin: 0; } " + css + "</style></head><body>" + body + "</body></html>";
            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: width, viewportHeight: height);
            var computer = new LayoutEngineComputer(styles, width, height);
            computer.Measure(doc, new SKSize(width, height));
            computer.Arrange(doc, new SKRect(0, 0, width, height));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);
            return id => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)];
        }

        [Fact]
        public async Task PaddingTopPercentage_ResolvesAgainstContainingBlockWidth()
        {
            var box = await LayoutAsync(
                "#outer { width: 500px; } #inner { padding-top: 56.25%; }",
                "<div id='outer'><div id='inner'></div></div>");

            var inner = box("inner");
            Assert.Equal(281.25f, inner.ContentBox.Top - inner.PaddingBox.Top, 2);
            Assert.Equal(281.25f, inner.BorderBox.Height, 2);
            Assert.Equal(281.25f, box("outer").ContentBox.Height, 2);
        }

        [Fact]
        public async Task PaddingShorthandWithPercentages_ResolvesEverySide()
        {
            var box = await LayoutAsync(
                "#outer { width: 400px; } #inner { padding: 10% 5px 25%; }",
                "<div id='outer'><div id='inner'></div></div>");

            var inner = box("inner");
            Assert.Equal(40f, inner.ContentBox.Top - inner.PaddingBox.Top, 2);
            Assert.Equal(5f, inner.PaddingBox.Right - inner.ContentBox.Right, 2);
            Assert.Equal(100f, inner.PaddingBox.Bottom - inner.ContentBox.Bottom, 2);
            Assert.Equal(5f, inner.ContentBox.Left - inner.PaddingBox.Left, 2);
            Assert.Equal(390f, inner.ContentBox.Width, 2);
        }

        [Fact]
        public async Task GeneratedBeforeBoxPaddingPercentage_GivesItsElementHeight()
        {
            // YouTube's thumbnail shape: the ::before box sets the height, the link
            // inside is absolutely positioned over it.
            var box = await LayoutAsync(
                "#row { display: flex; width: 1250px; } " +
                "#thumb { display: block; position: relative; flex: 1; flex-basis: 0.000000001px; max-width: 500px; min-width: 240px; } " +
                "#thumb:before { display: block; content: ''; padding-top: 56.11%; } " +
                "#link { position: absolute; top: 0; right: 0; bottom: 0; left: 0; }",
                "<div id='row'><div id='thumb'><a id='link'></a></div><div>text</div></div>");

            Assert.Equal(500f, box("thumb").BorderBox.Width, 1);
            Assert.Equal(280.55f, box("thumb").BorderBox.Height, 1);
            Assert.Equal(280.55f, box("link").BorderBox.Height, 1);
        }

        [Fact]
        public async Task MarginPercentages_ResolveAgainstContainingBlockWidth()
        {
            var box = await LayoutAsync(
                "#outer { width: 600px; } #inner { margin: 0 10%; height: 10px; } #next { margin-top: 5%; height: 10px; }",
                "<div id='outer'><div id='inner'></div><div id='next'></div></div>");

            var inner = box("inner");
            Assert.Equal(60f, inner.BorderBox.Left - box("outer").ContentBox.Left, 2);
            Assert.Equal(480f, inner.ContentBox.Width, 2);
            Assert.Equal(inner.BorderBox.Bottom + 30f, box("next").BorderBox.Top, 2);
        }

        [Fact]
        public async Task LonghandPercentage_OverridesAnEarlierShorthandLength()
        {
            var box = await LayoutAsync(
                "#outer { width: 500px; } #inner { padding: 10px; padding-left: 10%; margin: 7px; margin-left: 4%; height: 10px; }",
                "<div id='outer'><div id='inner'></div></div>");

            var inner = box("inner");
            Assert.Equal(50f, inner.ContentBox.Left - inner.PaddingBox.Left, 2);
            Assert.Equal(10f, inner.ContentBox.Top - inner.PaddingBox.Top, 2);
            Assert.Equal(20f, inner.BorderBox.Left - box("outer").ContentBox.Left, 2);
        }

        [Fact]
        public async Task PercentagePaddingTop_StopsTheFirstChildMarginCollapsingThrough()
        {
            var box = await LayoutAsync(
                "#outer { width: 200px; } #parent { padding-top: 10%; } #child { margin-top: 30px; height: 10px; }",
                "<div id='outer'><div id='parent'><div id='child'></div></div></div>");

            // 20px of padding sits between the parent's top and the child's margin.
            Assert.Equal(box("parent").BorderBox.Top + 20f + 30f, box("child").BorderBox.Top, 2);
        }

        [Fact]
        public async Task AbsolutelyPositionedPercentagePadding_ResolvesAgainstItsContainingBlock()
        {
            var box = await LayoutAsync(
                "#cb { position: relative; width: 800px; height: 100px; } #abs { position: absolute; top: 0; left: 0; width: 100px; padding-top: 25%; }",
                "<div id='cb'><div id='abs'></div></div>");

            var abs = box("abs");
            Assert.Equal(200f, abs.ContentBox.Top - abs.PaddingBox.Top, 2);
            Assert.Equal(200f, abs.BorderBox.Height, 2);
        }

        [Fact]
        public async Task FlexContainerPercentagePadding_ResolvesAgainstItsContainingBlock()
        {
            var box = await LayoutAsync(
                "#outer { width: 300px; } #flex { display: flex; padding-left: 10%; } #item { width: 50px; height: 10px; }",
                "<div id='outer'><div id='flex'><div id='item'></div></div></div>");

            var flex = box("flex");
            Assert.Equal(30f, flex.ContentBox.Left - flex.PaddingBox.Left, 2);
            Assert.Equal(flex.BorderBox.Left + 30f, box("item").BorderBox.Left, 2);
        }
    }
}
