using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// The pieces of github.com's marketing header that rendered wrong, each reduced
    /// to the CSS it turns on. Every case is checked against Chrome's used geometry.
    /// </summary>
    public class FlexItemIntrinsicSizingTests
    {
        private const float ViewportWidth = 1280f;
        private const float ViewportHeight = 800f;

        private static async Task<(Document doc, LayoutEngineComputer computer)> LayoutAsync(string body)
        {
            string html = "<!doctype html><html><head><style>" +
                          "body{margin:0;font-size:16px;line-height:1.5;font-family:sans-serif}" +
                          ".row{display:flex;align-items:center;width:600px}" +
                          "</style></head><body>" + body + "</body></html>";
            var doc = new HtmlParser(html, new Uri("https://example.test/")).Parse();
            var styles = await CssLoader.ComputeAsync(doc.DocumentElement, new Uri("https://example.test/"), null, viewportWidth: ViewportWidth, viewportHeight: ViewportHeight);
            var computer = new LayoutEngineComputer(styles, ViewportWidth, ViewportHeight);
            computer.Measure(doc, new SKSize(ViewportWidth, ViewportHeight));
            computer.Arrange(doc, new SKRect(0, 0, ViewportWidth, ViewportHeight));
            return (doc, computer);
        }

        private static BoxModel Box(Document doc, LayoutEngineComputer computer, string id)
        {
            var element = doc.Descendants().OfType<Element>().First(e => e.Id == id);
            var box = computer.GetBox(element);
            Assert.NotNull(box);
            return box;
        }

        [Fact]
        public async Task FlexContainerHoldingOnlyAnSvg_IsTheSvgsHeight_NotALineBox()
        {
            // CSS Flexbox §4: the svg is a flex item (blockified), not inline content
            // on a line, so there is no line-height strut around it. Chrome: 16px.
            var (doc, computer) = await LayoutAsync(
                "<div class='row'><span id='c' style='display:flex'><svg width='16' height='16'></svg></span></div>");

            var box = Box(doc, computer, "c");
            Assert.InRange(box.BorderBox.Height, 15.5f, 16.5f);
            Assert.InRange(box.BorderBox.Width, 15.5f, 16.5f);
        }

        [Fact]
        public async Task BlockLevelSvgWithPathChildren_IsItsAttributeSize()
        {
            // An <svg>'s <path> children are SVG content, not CSS boxes: the element
            // is replaced and sized from width/height whatever its display. github's
            // octicons are `display:block` svgs holding a path. Chrome: 16x16.
            var (doc, computer) = await LayoutAsync(
                "<div class='row'><span id='c' style='display:flex'>" +
                "<svg id='s' style='display:block' width='16' height='16' viewBox='0 0 16 16'><path d='M1 1h14v14H1z'></path></svg>" +
                "</span></div>");

            var svg = Box(doc, computer, "s");
            Assert.InRange(svg.BorderBox.Width, 15.5f, 16.5f);
            Assert.InRange(svg.BorderBox.Height, 15.5f, 16.5f);
            var box = Box(doc, computer, "c");
            Assert.InRange(box.BorderBox.Width, 15.5f, 16.5f);
        }

        [Theory]
        [InlineData("inline-flex")]
        [InlineData("grid")]
        [InlineData("block")]
        public async Task PercentWidthChildOfAutoWidthFlexItem_BehavesAsAutoForTheItemsContentSize(string display)
        {
            // CSS Sizing 3 §5.2.1: a percentage resolved against an indefinite size
            // behaves as auto for the intrinsic contribution. The flex item's
            // flex-basis:content is therefore its child's max-content width — the
            // width of "Search" plus 24px padding and 2px border — not the container
            // width. Chrome: 76.7px for the item and its child alike.
            var (doc, computer) = await LayoutAsync(
                "<div class='row'><div id='item' style='width:auto'>" +
                "<a id='child' style='display:" + display + ";width:100%;box-sizing:border-box;padding:0 12px;border:1px solid red'>" +
                "<span style='display:flex;width:100%'><span style='width:100%'>Search</span></span></a>" +
                "</div><div style='flex:0 0 auto'>tail</div></div>");

            var item = Box(doc, computer, "item");
            var child = Box(doc, computer, "child");
            Assert.True(item.BorderBox.Width < 150f, $"flex item took {item.BorderBox.Width}px of the 600px row.");
            Assert.True(child.BorderBox.Width < 150f, $"percent-width child took {child.BorderBox.Width}px.");
            Assert.InRange(Math.Abs(child.BorderBox.Width - item.BorderBox.Width), 0f, 1f);
            Assert.InRange(child.BorderBox.Height, 24f, 28f);
        }

        [Fact]
        public async Task InlineFlexOnALine_ContributesItsFirstItemBaseline_NotItsBottomEdge()
        {
            // CSS Flexbox §8.5: an inline-flex box's baseline is its first item's
            // baseline. With the 14px label centred in a 32px min-height box the
            // line box fits the box exactly. Chrome: 32px for the block.
            var (doc, computer) = await LayoutAsync(
                "<div id='line' style='width:600px'><a style='display:inline-flex;border:1px solid red;align-items:center;min-height:32px;box-sizing:border-box'>" +
                "<span style='display:flex'><span style='font-size:14px'>Sign in</span></span></a></div>");

            var line = Box(doc, computer, "line");
            Assert.InRange(line.BorderBox.Height, 31.5f, 32.5f);
        }
    }
}
