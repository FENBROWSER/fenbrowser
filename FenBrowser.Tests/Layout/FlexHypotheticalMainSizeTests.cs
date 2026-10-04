using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Flexbox 1 §9.9.1: a content-sized flex container is the sum of its items'
    /// hypothetical main sizes, i.e. their bases clamped by min/max.
    /// </summary>
    public class FlexHypotheticalMainSizeTests
    {
        [Fact]
        public async Task ColumnContainer_UsesItsItemsMaxHeight()
        {
            // YouTube's watch-page action menu: a wrapping row of buttons capped at
            // max-height:36px inside an auto-height column.
            const string Button = "<div style='width:150px;height:36px;flex:none'></div>";
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='column' style='display:flex;flex-direction:column;width:400px'>" +
                "<div id='menu' style='display:flex;flex-wrap:wrap;max-height:36px;overflow-y:hidden'>" +
                Button + Button + Button + Button + "</div></div>");

            Assert.Equal(36f, probe.Rect("menu").Height, 1);
            Assert.Equal(36f, probe.Rect("column").Height, 1);
        }
    }
}
