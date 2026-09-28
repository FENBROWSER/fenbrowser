using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Text 3 §4.1.1: under white-space pre / pre-wrap spaces are preserved, including a
    /// text node that is nothing but a space and sits alone in its own inline element. The
    /// span inherits white-space from the pre (CSS Cascade 4 §7.2); the typed field layout
    /// reads was taken before inherited values reached the style, so the space was dropped
    /// and CodeMirror, which renders every space between tokens this way, ran tokens together.
    /// </summary>
    public sealed class PreservedWhitespaceSpanTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; font: 14px monospace; }
  pre { margin: 0; }
</style></head>
<body>
<pre style='white-space:pre-wrap'><span><span id='a'>video</span><span id='sp'> </span><span id='b'>width</span></span></pre>
<pre style='white-space:pre'><span><span id='c'>video</span><span> </span><span id='d'>width</span></span></pre>
</body></html>";

        [Fact]
        public async Task LoneSpaceInsideAnInline_AdvancesTheLine()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 800, viewportHeight: 600);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var a = Box("a");
            var space = Box("sp");
            var b = Box("b");
            Assert.True(space.Width > 1f, $"preserved space width {space.Width}");
            Assert.Equal(a.Right + space.Width, b.Left, 1);

            Assert.True(Box("d").Left - Box("c").Right > 1f, "white-space:pre keeps the space too");
        }
    }
}
