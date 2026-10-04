using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Display 3 §2.7: the children of a flex or grid container are blockified, and
    /// that is their computed display, not only the box the tree builder makes.
    /// </summary>
    public class FlexItemBlockificationTests
    {
        [Fact]
        public async Task InlineAnchorFlexItem_IsSizedAsABlock()
        {
            // YouTube's search-result channel row: an inline <a> wrapping the avatar,
            // then the channel name.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='row' style='display:flex;width:572px;padding:12px 0'>" +
                "<a id='avatar' href='#' style='margin-right:8px'><span style='display:inline-block;width:24px;height:24px'></span></a>" +
                "<span id='name' style='display:flex'>jawed</span>" +
                "</div>");

            Assert.Equal(24f, probe.Rect("avatar").Width, 1);
            Assert.Equal(32f, probe.Rect("name").Left, 1);
        }

        [Theory]
        [InlineData("flex", "inline", "block")]
        [InlineData("grid", "inline-block", "block")]
        [InlineData("flex", "inline-flex", "flex")]
        [InlineData("inline-grid", "inline-grid", "grid")]
        [InlineData("block", "inline", "inline")]
        public async Task ComputedDisplay_IsBlockified(string parentDisplay, string childDisplay, string expected)
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                $"<div style='display:{parentDisplay}'><span id='child' style='display:{childDisplay}'>x</span></div>");

            Assert.Equal(expected, probe.Style("child")?.Display);
        }
    }
}
