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

        [Fact]
        public async Task ShrinkToFitBoxHoldingAnInlineRun_IsAsWideAsTheRun_NotItsWidestChild()
        {
            // CSS Sizing 3 §4.1: max-content lays inline content out with no soft
            // wraps, so a shrink-to-fit box holding three nowrap inline-block
            // buttons is as wide as the three together. github.com's segmented
            // control measured one button wide and stacked the rest.
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;flex-direction:column;align-items:center;width:1000px'>" +
                "<div id='pill' style='display:flex;white-space:nowrap;border:1px solid red'>" +
                "<div id='run' style='width:100%;padding:8px'>" +
                "<button style='min-width:130px;height:40px'>Code</button>" +
                "<button style='min-width:130px;height:40px'>Plan</button>" +
                "<button style='min-width:130px;height:40px'>Secure</button>" +
                "</div></div></div>");

            var pill = Box(doc, computer, "pill");
            var run = Box(doc, computer, "run");
            Assert.InRange(pill.BorderBox.Width, 3 * 130 + 16 + 2 - 1f, 3 * 130 + 16 + 2 + 12f);
            Assert.InRange(run.BorderBox.Height, 54f, 62f);
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

        // github.com's header search trigger: a percent-width box with padding whose
        // only visible content is a 16px icon (the label is display:none above 1012px).
        // CSS Sizing 3 §5.2.1 makes the percentage auto for the contribution, so the
        // shrink-to-fit parent is 16px + 24px padding. A content-box child then resolves
        // 100% to those 40px and overflows by its padding (64px); the border-box button
        // does not. Each row is Chrome's used width for the parent and the child.
        [Theory]
        [InlineData("flex", "<div id='c' style='width:100%;padding:0 12px'><span style='display:inline-block;width:16px;height:16px'></span></div>", 40f, 64f)]
        [InlineData("flex", "<div id='c' style='display:grid;width:100%;padding:0 12px'><span style='display:block;width:16px;height:16px'></span></div>", 40f, 64f)]
        [InlineData("flex", "<div id='c' style='display:grid;grid-template-columns:minmax(0,1fr);width:100%;padding:0 12px'><span style='display:block;width:16px;height:16px'></span></div>", 40f, 64f)]
        [InlineData("flex", "<div id='c' style='display:grid;grid-template-columns:minmax(0,1fr);width:100%;padding:0 12px'><span style='display:flex;width:100%'><span style='display:block;width:16px;height:16px'></span></span></div>", 40f, 64f)]
        [InlineData("flex", "<button id='c' style='display:grid;width:100%;padding:0 12px;border:2px solid'><span style='display:block;width:16px;height:16px'></span></button>", 44f, 44f)]
        // The live header's shape: the icon sits in a `display:flex` label that stretches
        // to the percentage spans around it, so the label's width is no measure of content.
        [InlineData("flex", "<button id='c' style='display:grid;grid-template-columns:minmax(0,1fr);width:100%;box-sizing:border-box;padding:1px 12px;border:1px solid'><span style='display:flex;width:100%'><span style='width:100%'><span style='display:flex;align-items:center'><svg width='16' height='16' style='display:block;flex-shrink:0'></svg></span></span></span></button>", 42f, 42f)]
        [InlineData("float", "<div id='c' style='display:grid;width:100%;padding:0 12px'><span style='display:block;width:16px;height:16px'></span></div>", 40f, 64f)]
        public async Task PaddedPercentWidthChild_ContributesItsContentPlusPaddingOnce(string parent, string child, float expectedParent, float expectedChild)
        {
            string wrapper = parent == "float"
                ? "<div style='float:left'><div id='slot'>" + child + "</div></div>"
                : "<div style='display:flex;width:750px'><div id='slot'>" + child + "</div><div>x</div></div>";
            var (doc, computer) = await LayoutAsync(wrapper);

            var slot = Box(doc, computer, "slot");
            var inner = Box(doc, computer, "c");
            Assert.InRange(slot.BorderBox.Width, expectedParent - 0.5f, expectedParent + 0.5f);
            Assert.InRange(inner.BorderBox.Width, expectedChild - 0.5f, expectedChild + 0.5f);
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

        private const string FourBlocksInARow =
            "<div style='display:flex'>" +
            "<span style='display:block;width:100px;height:10px'></span><span style='display:block;width:100px;height:10px'></span>" +
            "<span style='display:block;width:100px;height:10px'></span><span style='display:block;width:100px;height:10px'></span></div>";

        // github.com's header: a nav whose links form a nowrap flex row next to a
        // width:100% CTA container. CSS Flexbox §4.5 stops the nav at its min-content
        // width (the whole row) and §9.7 hands the rest of the overflow to the CTA; an
        // item that opts out (overflow:hidden, min-width:0) shrinks in proportion.
        // Each row is Chrome's used x/width for the two items.
        [Theory]
        [InlineData("", FourBlocksInARow, "margin-left:16px", 400f, 184f)]
        [InlineData("overflow:hidden", FourBlocksInARow, "margin-left:16px", 233.6f, 350.4f)]
        [InlineData("min-width:0", FourBlocksInARow, "margin-left:16px", 233.6f, 350.4f)]
        public async Task ShrinkingRow_StopsAnItemAtItsAutomaticMinimum_AndGivesTheRestToItsSiblings(
            string firstStyle, string firstContent, string secondStyle, float expectedFirst, float expectedSecond)
        {
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;width:600px'>" +
                "<div id='first' style='" + firstStyle + "'>" + firstContent + "</div>" +
                "<div id='second' style='width:100%;height:10px;" + secondStyle + "'></div></div>");

            var first = Box(doc, computer, "first");
            var second = Box(doc, computer, "second");
            Assert.InRange(first.BorderBox.Width, expectedFirst - 0.5f, expectedFirst + 0.5f);
            Assert.InRange(second.BorderBox.Width, expectedSecond - 0.5f, expectedSecond + 0.5f);
        }

        [Fact]
        public async Task GrowingItem_InARowWithNoFreeSpace_IsItsFlexBaseSize()
        {
            // github.com's logo row: an empty `flex:1` menu-toggle slot beside the logo,
            // held by the outer row at exactly its minimum (48px). With zero free space
            // the slot's size is its 0px flex basis; it kept its 48px probe width and
            // pushed the logo onto the nav. Chrome: slot 0px, logo at the row's start.
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;width:600px'>" +
                "<div id='top' style='display:flex;justify-content:space-between'>" +
                "<div id='slot' style='flex:1'></div>" +
                "<a id='logo' style='display:inline-flex;margin-right:16px'><svg width='32' height='32'></svg></a></div>" +
                "<div style='width:100%'>menu</div></div>");

            var top = Box(doc, computer, "top");
            var slot = Box(doc, computer, "slot");
            var logo = Box(doc, computer, "logo");
            Assert.InRange(top.BorderBox.Width, 47.5f, 48.5f);
            Assert.True(slot.BorderBox.Width <= 1.5f, $"slot kept {slot.BorderBox.Width}px");
            Assert.InRange(logo.BorderBox.Left - top.BorderBox.Left, 0f, 1.5f);
        }
    }
}
