using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Sizing 3 §5 content-based widths: min-content, max-content and fit-content.
    /// </summary>
    public class ContentBasedSizeKeywordTests
    {
        private const string Text = "hello wonderful world";

        [Fact]
        public async Task MinContentWidth_IsTheWidestUnbreakablePiece()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='blocks' style='width:min-content'><div style='width:50px;height:20px'></div><div style='width:30px;height:20px'></div></div>" +
                "<div id='text' style='width:min-content'>" + Text + "</div>" +
                "<div id='word' style='width:max-content'>wonderful</div>");

            Assert.Equal(50f, probe.Rect("blocks").Width, 1);
            var word = probe.Rect("word").Width;
            Assert.True(word > 40f && word < 120f, $"one word should be tens of px wide, was {word}");
            Assert.Equal(word, probe.Rect("text").Width, 1);
        }

        [Fact]
        public async Task MaxContentAndFitContent_ShrinkWrapAShortLine()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div id='max' style='width:max-content;padding:3px'>" + Text + "</div>" +
                "<div id='fit' style='width:fit-content'>" + Text + "</div>" +
                "<div id='flex' style='display:flex;width:fit-content'><div style='width:200px;height:40px'></div></div>" +
                "<div style='display:flex;width:880px'><div id='item' style='flex:0 0 auto;width:fit-content;display:flex'><div style='width:200px;height:40px'></div></div></div>");

            float line = probe.Rect("fit").Width;
            Assert.True(line > 100f && line < 400f, $"the line should shrink-wrap, was {line}");
            Assert.Equal(line + 6f, probe.Rect("max").Width, 1);
            Assert.Equal(200f, probe.Rect("flex").Width, 1);
            Assert.Equal(200f, probe.Rect("item").Width, 1);
        }

        [Fact]
        public async Task FitContent_IsCappedByTheAvailableWidth()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='width:150px'><div id='fit' style='width:fit-content'>" + Text + " " + Text + "</div></div>");

            Assert.Equal(150f, probe.Rect("fit").Width, 1);
        }
    }
}
