using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Flexbox 1 §9.4 step 11: align-self: stretch only stretches an item whose
    /// computed cross size is auto and neither of whose cross-axis margins is auto.
    /// </summary>
    public class FlexStretchCrossSizeTests
    {
        [Fact]
        public async Task ItemWithDefiniteHeight_IsNotStretchedToTheLine()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:500px;height:56px'>" +
                "<div id='fixed' style='width:50px;height:40px'></div>" +
                "<span id='flexfixed' style='display:flex;width:50px;height:40px'></span>" +
                "<div id='auto' style='width:50px'></div>" +
                "</div>");

            Assert.Equal(40f, probe.Rect("fixed").Height, 1);
            Assert.Equal(40f, probe.Rect("flexfixed").Height, 1);
            Assert.Equal(56f, probe.Rect("auto").Height, 1);
        }

        [Fact]
        public async Task ItemWithAutoCrossMargin_IsNotStretched()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:500px;height:100px'>" +
                "<div id='item' style='width:50px;margin-top:auto'><div style='height:20px'></div></div>" +
                "</div>");

            var item = probe.Rect("item");
            Assert.Equal(20f, item.Height, 1);
            Assert.Equal(80f, item.Top, 1);
        }

        [Fact]
        public async Task StretchedItem_LaysOutPercentageDescendantsAgainstTheLine()
        {
            // WPT css/css-flexbox/percentage-heights-001.html: the column is stretched to
            // 50px, which makes its height definite even though it measured 50px already.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;height:50px'>" +
                "<div style='display:flex;flex-direction:column'>" +
                "<div style='flex:1'><div id='half' style='height:50%'><div style='width:50px;height:50px'></div></div></div>" +
                "</div></div>");

            Assert.Equal(25f, probe.Rect("half").Height, 1);
        }

        [Fact]
        public async Task ColumnItemWithDefiniteWidth_IsNotStretchedAcrossTheContainer()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;flex-direction:column;width:300px'>" +
                "<div id='fixed' style='width:120px;height:10px'></div>" +
                "<div id='auto' style='height:10px'></div>" +
                "</div>");

            Assert.Equal(120f, probe.Rect("fixed").Width, 1);
            Assert.Equal(300f, probe.Rect("auto").Width, 1);
        }
    }
}
