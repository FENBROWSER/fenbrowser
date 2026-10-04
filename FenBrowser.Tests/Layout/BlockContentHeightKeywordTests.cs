using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Sizing 3 §3.1: in a block container's block axis the content-based keywords
    /// (min-content, max-content, fit-content) behave as its automatic height, so as a
    /// min-height or max-height they mean "the content height".
    /// </summary>
    public class BlockContentHeightKeywordTests
    {
        [Fact]
        public async Task MaxHeightMinContent_ClampsAColumnItemToItsContent()
        {
            // WPT css/css-flexbox/flex-item-max-height-min-content.html.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;flex-direction:column;width:100px;height:200px'>" +
                "<div id='item' style='max-height:min-content;flex-basis:200px'><div style='height:100px'></div></div>" +
                "</div>");

            Assert.Equal(100f, probe.Rect("item").Height, 1);
        }

        [Fact]
        public async Task MinHeightFitContent_KeepsAFixedHeightBoxAsTallAsItsContent()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='box' style='height:10px;min-height:fit-content;box-sizing:border-box;padding:5px'><div style='height:60px'></div></div>");

            Assert.Equal(70f, probe.Rect("box").Height, 1);
        }
    }
}
