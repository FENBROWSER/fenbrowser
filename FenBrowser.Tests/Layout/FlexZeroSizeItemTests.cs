using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// A flex item's size comes from the flex algorithm (CSS Flexbox 1 §9.7) alone: an
    /// item that is legitimately 0 wide stays 0 wide, and an empty growing item takes
    /// its share of the free space rather than a pre-flex guess.
    /// </summary>
    public class FlexZeroSizeItemTests
    {
        [Fact]
        public async Task ZeroWidthItem_StaysZeroWide()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:500px;height:56px'>" +
                "<div style='width:50px'></div>" +
                "<div id='zero' style='display:inline-block;width:0;height:10px'></div>" +
                "<div id='after' style='width:20px'></div>" +
                "</div>");

            Assert.Equal(0f, probe.Rect("zero").Width, 1);
            Assert.Equal(50f, probe.Rect("after").Left, 1);
        }

        [Fact]
        public async Task EmptyGrowingItem_TakesTheFreeSpace()
        {
            // WPT css/css-flexbox/dynamic-stretch-change.html.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:100px;height:200px;align-items:start'>" +
                "<div id='middle' style='display:flex;flex-grow:1;min-height:100px'>" +
                "<div id='green' style='flex:1'></div>" +
                "</div></div>");

            var middle = probe.Rect("middle");
            Assert.Equal(100f, middle.Width, 1);
            Assert.Equal(100f, middle.Height, 1);
            var green = probe.Rect("green");
            Assert.Equal(100f, green.Width, 1);
            Assert.Equal(100f, green.Height, 1);
        }

        [Fact]
        public async Task ColumnContainerBeingShrinkWrapped_SizesItsItemsToContent()
        {
            // WPT css/css-flexbox/flexbox-min-height-auto-003.html: a floated column
            // flexbox has no width to stretch its items to while it is being sized.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='flexbox' style='display:flex;flex-direction:column;height:30px;float:left'>" +
                "<div id='item' style='border:2px solid;overflow-y:hidden'><div style='width:40px;height:80px'></div></div>" +
                "</div>");

            Assert.Equal(44f, probe.Rect("flexbox").Width, 1);
            Assert.Equal(44f, probe.Rect("item").Width, 1);
        }
    }
}
