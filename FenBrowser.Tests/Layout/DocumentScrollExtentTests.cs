using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Overflow 3 §2.2: content a box clips is not part of the document's scrollable
    /// overflow. CodeMirror hides its scrollbars with a scroller 50px taller than the
    /// overflow:hidden editor around it; counting that made w3schools' tryit page
    /// scrollable, and scrolling it moved the navbar out of view.
    /// </summary>
    public sealed class DocumentScrollExtentTests
    {
        private static async Task<float> ContentHeightAsync(string body)
        {
            var html = "<!doctype html><html><head><style>body{margin:0}</style></head><body>" + body + "</body></html>";
            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 800, viewportHeight: 400);
            var engine = new LayoutEngine(styles, 800, 400);
            return engine.ComputeLayout(root, 800, 400).ContentHeight;
        }

        [Fact]
        public async Task ContentClippedByAnOverflowHiddenBox_DoesNotExtendTheDocument()
        {
            var height = await ContentHeightAsync(
                "<div style='height:300px;overflow:hidden'><div style='height:300px;padding-bottom:250px'></div></div>");

            Assert.Equal(400f, height, 1);
        }

        [Fact]
        public async Task ContentOfAScrollContainer_DoesNotExtendTheDocument()
        {
            var height = await ContentHeightAsync(
                "<div style='height:200px;overflow:auto'><div style='height:900px'></div></div>");

            Assert.Equal(400f, height, 1);
        }

        [Fact]
        public async Task VisibleOverflow_StillExtendsTheDocument()
        {
            var height = await ContentHeightAsync(
                "<div style='height:100px'><div style='height:700px'></div></div>");

            Assert.Equal(700f, height, 1);
        }
    }
}
