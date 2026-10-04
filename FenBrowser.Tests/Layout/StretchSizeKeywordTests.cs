using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Sizing 4 §3.1: `stretch` (and its -webkit-fill-available / -moz-available
    /// spellings) fills the containing block - or, for a flex item's cross axis, the
    /// flex line - minus the box's margins.
    /// </summary>
    public class StretchSizeKeywordTests
    {
        private const string Item = "margin-left:2px;margin-right:3px;border:3px solid;padding:2px;height:20px;";

        [Theory]
        [InlineData("min-width:stretch;width:0")]
        [InlineData("min-inline-size:stretch;width:0")]
        [InlineData("min-width:-webkit-fill-available;width:0")]
        [InlineData("width:stretch")]
        [InlineData("width:-moz-available")]
        public async Task BlockStretchesToItsContainingBlock(string sizing)
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                $"<div style='width:50px'><div id='item' style='{Item}{sizing}'></div></div>");

            Assert.Equal(45f, probe.Rect("item").Width, 1);
        }

        [Fact]
        public async Task FlexItemMinCrossStretch_FillsTheFlexLine()
        {
            // WPT css/css-sizing/stretch/stretch-min-inline-size-001.html.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:inline-flex;flex-direction:column;width:50px'><div id='single' style='" + Item + "min-width:stretch;width:0'></div></div>" +
                "<div style='display:inline-flex;flex-flow:column wrap;width:50px;height:40px'><div id='wide' style='" + Item + "min-width:stretch;width:0'></div><div style='width:60px'></div></div>");

            Assert.Equal(45f, probe.Rect("single").Width, 1);
            Assert.Equal(55f, probe.Rect("wide").Width, 1);
        }

        [Theory]
        [InlineData("height:stretch")]
        [InlineData("height:0;min-height:stretch")]
        [InlineData("height:500px;max-height:stretch")]
        public async Task FlexItemHeightStretch_FillsTheContainer(string sizing)
        {
            // WPT css/css-sizing/keyword-sizes-on-flex-item-001.html.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:100px;height:100px'>" +
                $"<div id='item' style='flex:none;margin:5px;border:3px solid;padding:2px;{sizing}'>X</div></div>");

            Assert.Equal(90f, probe.Rect("item").Height, 1);
        }
    }
}
