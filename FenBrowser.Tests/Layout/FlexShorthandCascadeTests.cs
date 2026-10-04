using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Flexbox 1 §7.2: flex is a shorthand for flex-grow, flex-shrink and flex-basis,
    /// so a winning flex declaration resets a flex-basis an earlier, weaker rule set.
    /// </summary>
    public class FlexShorthandCascadeTests
    {
        // YouTube's watch-page metadata row: the plain rule gives #owner a near-zero
        // basis, the more specific one restores flex: 0 0 auto at its fit-content width.
        private const string Css =
            "#owner{flex:1;flex-basis:0.000000001px;display:flex}" +
            "#row[wide] #owner{margin-right:32px;width:fit-content;flex:0 0 auto}";

        [Fact]
        public async Task LaterShorthand_ResetsAnEarlierLonghandBasis()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='row' wide style='display:flex;width:880px'>" +
                "<div id='owner'><div style='width:240px;height:40px'></div></div>" +
                "<div id='actions' style='flex:1 1 auto;display:flex;flex-wrap:wrap;min-width:0'>" +
                "<div style='width:400px;height:20px'></div><div style='width:400px;height:20px'></div></div>" +
                "</div>",
                Css);

            Assert.Equal(240f, probe.Rect("owner").Width, 1);
            var actions = probe.Rect("actions");
            Assert.Equal(272f, actions.Left, 1);
            Assert.Equal(880f - 272f, actions.Width, 1);
        }

        [Fact]
        public async Task LaterLonghand_StillOverridesAnEarlierShorthand()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='display:flex;width:600px'>" +
                "<div id='a' style='flex:1;flex-basis:100px'></div>" +
                "<div id='b' style='flex:1'></div>" +
                "</div>");

            // Free space 500px split evenly over a 100px and a 0% basis.
            Assert.Equal(350f, probe.Rect("a").Width, 1);
            Assert.Equal(250f, probe.Rect("b").Width, 1);
        }
    }
}
