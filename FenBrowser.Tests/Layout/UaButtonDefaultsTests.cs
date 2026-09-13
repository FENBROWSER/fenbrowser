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
    /// The user-agent defaults for a bare &lt;button&gt;, checked against what
    /// Chromium's html.css gives it: 1px 6px padding inside a 2px border,
    /// border-box sizing, and the 13.333px control font with line-height:normal
    /// rather than the page's inherited line-height.
    /// </summary>
    public class UaButtonDefaultsTests
    {
        private static async Task<(Document doc, LayoutEngineComputer computer)> LayoutAsync(string body)
        {
            string html = "<!doctype html><html><head><style>" +
                          "body{margin:0;font-size:16px;line-height:1.5;font-family:sans-serif}" +
                          "</style></head><body>" + body + "</body></html>";
            var doc = new HtmlParser(html, new Uri("https://example.test/")).Parse();
            var styles = await CssLoader.ComputeAsync(doc.DocumentElement, new Uri("https://example.test/"), null, viewportWidth: 1280, viewportHeight: 800);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
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
        public async Task BareButton_IsAControlHeight_NotAPageLineBox()
        {
            // Chrome: 21px tall — ~15px of 13.333px text plus 2px padding and 4px border.
            var (doc, computer) = await LayoutAsync("<div><button id='b'>B</button></div>");

            var button = Box(doc, computer, "b");
            Assert.InRange(button.BorderBox.Height, 19f, 24f);
            Assert.InRange(button.BorderBox.Width - button.ContentBox.Width, 15.9f, 16.1f);
            Assert.InRange(button.BorderBox.Height - button.ContentBox.Height, 5.9f, 6.1f);
        }

        [Fact]
        public async Task FullWidthButton_FillsItsContainer_PaddingIncluded()
        {
            // box-sizing:border-box on the control: width:100% is the container
            // width, with the 6px+6px padding and 2px+2px border inside it.
            var (doc, computer) = await LayoutAsync(
                "<div style='width:200px'><button id='b' style='width:100%'>B</button></div>");

            var button = Box(doc, computer, "b");
            Assert.InRange(button.BorderBox.Width, 199.5f, 200.5f);
        }
    }
}
