using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS 2.1 §10.8.1: line-height: 0 is a real value. The strut and the text's inline
    /// boxes collapse, so a line holding only a 24px atomic box is 24px tall.
    /// </summary>
    public class LineHeightZeroStrutTests
    {
        [Theory]
        [InlineData("0", "14px")]
        [InlineData("0px", "14px")]
        [InlineData("0", "28px")]
        public async Task AtomicInlineInAZeroLineHeightBlock_SetsTheLineHeight(string lineHeight, string fontSize)
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                $"<div id='line' style='line-height:{lineHeight};font-size:{fontSize}'>" +
                "<span id='icon' style='display:inline-flex;width:24px;height:24px'><span style='display:flex;width:24px;height:24px'></span></span>" +
                "</div>");

            Assert.Equal(24f, probe.Rect("line").Height, 1);
            Assert.Equal(probe.Rect("line").Top, probe.Rect("icon").Top, 1);
        }

        [Fact]
        public async Task TextInAZeroLineHeightBlock_TakesNoHeight()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync("<div id='line' style='line-height:0;font-size:14px'>text</div>");

            Assert.Equal(0f, probe.Rect("line").Height, 1);
        }
    }
}
