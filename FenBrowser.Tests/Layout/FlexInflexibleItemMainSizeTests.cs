using System;
using System.Linq;
using System.Threading.Tasks;
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
    /// CSS Flexbox 1 §9.7: an item that neither grows nor shrinks is frozen at its
    /// hypothetical main size (its basis clamped by min/max), not its probed content size.
    /// </summary>
    public class FlexInflexibleItemMainSizeTests
    {
        private const float ViewportWidth = 1400f;
        private const float ViewportHeight = 900f;

        private static async Task<(Document doc, LayoutEngineComputer computer)> LayoutAsync(string body)
        {
            string html = "<!doctype html><html><head><style>" +
                          "body{margin:0;font-size:16px;font-family:sans-serif}" +
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

        private const string SearchBox =
            "<div id='sb' style='flex:1;display:flex;margin-left:40px;height:40px'>" +
            "<div style='width:100%'><div style='display:flex;width:100%;height:100%'>" +
            "<div style='display:flex;flex:1;margin-left:32px;padding:0 4px 0 16px'>" +
            "<form style='display:flex;flex-direction:column;flex:1'><input id='q' style='width:100%;border:none;padding:1px 0'></form>" +
            "</div><button style='width:64px'>s</button></div></div></div>";

        [Fact]
        public async Task PercentWidthInput_DoesNotResolveAgainstTheViewportInsideAFlexItem()
        {
            // CSS Sizing 3 §5.2.1: a percentage width that depends on the size being
            // computed is cyclic, and the input contributes its intrinsic width instead.
            // #center's basis is a definite 732px, so the row is 100 + 732 + 225 wide.
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;width:1368px;padding:0 16px;justify-content:space-between'>" +
                "<div id='start' style='display:flex;width:100px'></div>" +
                "<div id='center' style='flex:0 1 732px;min-width:0;display:flex;align-items:center'>" + SearchBox + "</div>" +
                "<div id='end' style='display:flex;min-width:225px;justify-content:flex-end'></div>" +
                "</div>");

            var center = Box(doc, computer, "center");
            Assert.InRange(center.BorderBox.Width, 731.5f, 732.5f);
            var input = Box(doc, computer, "q");
            Assert.True(input.BorderBox.Right <= center.BorderBox.Right + 0.5f,
                $"input right {input.BorderBox.Right} past #center right {center.BorderBox.Right}");
            var end = Box(doc, computer, "end");
            Assert.True(end.BorderBox.Right <= 1384.5f, $"#end right {end.BorderBox.Right}");
        }

        [Theory]
        [InlineData("xx")]
        [InlineData("<div style='width:1500px'></div>")]
        [InlineData("<input style='width:100%'>")]
        [InlineData("<div style='flex:1;display:flex'><input style='width:100%'></div>")]
        [InlineData("<div style='width:100%'><input style='width:100%'></div>")]
        [InlineData("<div style='flex:1;display:flex'><div style='width:100%'><input style='width:100%'></div></div>")]
        [InlineData("<div style='flex:1;display:flex'><form style='display:flex;flex-direction:column;flex:1'><input style='width:100%'></form></div>")]
        public async Task InflexibleItemWithDefiniteBasis_IsItsBasis_WhateverItsContent(string inner)
        {
            // CSS Flexbox 1 §9.7: with flex-grow:0 and room to spare the item is frozen at
            // its hypothetical main size, the 732px basis (min-width:0 lets wider content
            // overflow), not at its content width.
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;width:1368px;padding:0 16px;justify-content:space-between'>" +
                "<div id='start' style='display:flex;width:100px'></div>" +
                "<div id='center' style='flex:0 1 732px;min-width:0;display:flex;align-items:center'>" + inner + "</div>" +
                "<div id='end' style='display:flex;min-width:225px;justify-content:flex-end'></div>" +
                "</div>");
            Assert.Equal(732f, Box(doc, computer, "center").BorderBox.Width);
        }

        [Fact]
        public async Task InflexibleItem_IsClampedByMaxWidth_AndHeldAtItsAutomaticMinimum()
        {
            // §9.2 step 3E: the hypothetical main size clamps the basis by min/max-width;
            // with min-width:auto the minimum is the content's min-content width (§4.5).
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;width:1000px'>" +
                "<div id='capped' style='flex:0 0 500px;max-width:300px'>a</div>" +
                "<div id='floored' style='flex:0 0 10px'><div style='width:120px;height:10px'></div></div>" +
                "</div>");

            Assert.InRange(Box(doc, computer, "capped").BorderBox.Width, 299.5f, 300.5f);
            Assert.InRange(Box(doc, computer, "floored").BorderBox.Width, 119.5f, 120.5f);
        }

        [Fact]
        public async Task ColumnItemWithDefiniteBasis_IsAtLeastItsBasisTall()
        {
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;flex-direction:column;height:600px'>" +
                "<div id='tall' style='flex:0 0 200px'>a</div>" +
                "</div>");

            Assert.InRange(Box(doc, computer, "tall").BorderBox.Height, 199.5f, 200.5f);
        }

        [Fact]
        public async Task InflexibleItem_IsClampedByAnIntrinsicMaxWidth()
        {
            // WPT css-flexbox/flex-item-max-width-min-content: max-width:min-content bounds
            // the hypothetical main size below the 200px basis.
            var (doc, computer) = await LayoutAsync(
                "<div style='display:flex;height:100px;width:200px'>" +
                "<div id='item' style='max-width:min-content;flex-basis:200px'><div style='width:100px;height:10px'></div></div>" +
                "</div>");

            Assert.InRange(Box(doc, computer, "item").BorderBox.Width, 99.5f, 100.5f);
        }
    }
}
