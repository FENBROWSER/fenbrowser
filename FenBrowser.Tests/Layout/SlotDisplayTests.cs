using System;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // HTML §15.3.1 makes the HTML slot element display: contents; a slot in another
    // namespace is an ordinary element (css/css-shadow/slot-non-html-display-value).
    public class SlotDisplayTests
    {
        [Fact]
        public async Task OnlyHtmlSlotsDefaultToDisplayContents()
        {
            var uri = new Uri("https://slots.test/");
            var document = new HtmlParser("<!doctype html><html><body><slot id=h></slot><slot id=b style='display: block'></slot></body></html>", uri).Parse();
            var svgSlot = document.CreateElementNS("http://www.w3.org/2000/svg", "slot");
            document.Body.AppendChild(svgSlot);
            var styles = await CssLoader.ComputeAsync(document.DocumentElement, uri, null);

            Assert.Equal("contents", styles[document.GetElementById("h")].Display);
            Assert.Equal("block", styles[document.GetElementById("b")].Display);
            Assert.NotEqual("contents", styles[svgSlot].Display);
        }
    }
}
