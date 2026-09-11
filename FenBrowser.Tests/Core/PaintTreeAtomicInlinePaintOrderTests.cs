using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// CSS 2.1 Appendix E steps 5 and 7.2.1: floats and atomic inlines (inline-block etc.)
    /// paint their in-flow contents as one unit at their own place in the parent's float /
    /// inline phase, while positioned descendants still belong to the parent stacking context.
    /// google.com's "Sign in" pill is an inline-block whose label is a display:flex span with
    /// overflow:hidden; the label used to land in the ancestor's block phase and paint under
    /// the pill's own background.
    /// </summary>
    public sealed class PaintTreeAtomicInlinePaintOrderTests
    {
        private const string PillHtml = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #pill { display: inline-block; background: #0b57d0; color: #fff; padding: 10px 12px; min-width: 85px; position: relative; }
    #label { display: flex; overflow: hidden; max-height: 40px; }
    #float { float: left; background: #eee; width: 100px; }
    #floatBlock { display: block; background: #ccc; }
    #escape { position: absolute; left: 0; top: 0; background: #f00; }
  </style>
</head>
<body>
  <div id='float'><div id='floatBlock'>Float child</div></div>
  <a id='pill'><span id='label'>Sign in</span><i id='escape'>abs</i></a>
</body>
</html>";

        [Fact]
        public async System.Threading.Tasks.Task InlineBlockContent_PaintsAfterItsOwnBackground()
        {
            var (doc, nodes) = await BuildAsync(PillHtml);
            var pill = doc.Descendants().OfType<Element>().First(e => e.Id == "pill");

            int backgroundIndex = nodes.FindIndex(n => n is BackgroundPaintNode && ReferenceEquals(n.SourceNode, pill));
            int textIndex = nodes.FindIndex(n => n is TextPaintNode t && t.FallbackText == "Sign in");

            Assert.True(backgroundIndex >= 0, "pill background missing");
            Assert.True(textIndex >= 0, "pill label missing");
            Assert.True(textIndex > backgroundIndex, $"label painted at {textIndex}, before its pill background at {backgroundIndex}");
        }

        [Fact]
        public async System.Threading.Tasks.Task FloatContent_PaintsAfterTheFloatsOwnBackground()
        {
            var (doc, nodes) = await BuildAsync(PillHtml);
            var floatEl = doc.Descendants().OfType<Element>().First(e => e.Id == "float");
            var floatBlock = doc.Descendants().OfType<Element>().First(e => e.Id == "floatBlock");

            int floatBackground = nodes.FindIndex(n => n is BackgroundPaintNode && ReferenceEquals(n.SourceNode, floatEl));
            int childBackground = nodes.FindIndex(n => n is BackgroundPaintNode && ReferenceEquals(n.SourceNode, floatBlock));
            int childText = nodes.FindIndex(n => n is TextPaintNode t && t.FallbackText == "Float child");

            Assert.True(floatBackground >= 0 && childBackground >= 0 && childText >= 0);
            Assert.True(childBackground > floatBackground);
            Assert.True(childText > childBackground);
        }

        [Fact]
        public async System.Threading.Tasks.Task PositionedDescendant_StillEscapesToTheParentStackingContext()
        {
            var (doc, nodes) = await BuildAsync(PillHtml);
            var escape = doc.Descendants().OfType<Element>().First(e => e.Id == "escape");

            int escapeBackground = nodes.FindIndex(n => n is BackgroundPaintNode && ReferenceEquals(n.SourceNode, escape));
            int lastInFlow = Math.Max(
                nodes.FindLastIndex(n => n is TextPaintNode t && t.FallbackText == "Sign in"),
                nodes.FindLastIndex(n => n is TextPaintNode t && t.FallbackText == "Float child"));

            Assert.True(escapeBackground >= 0, "absolute descendant missing");
            // Step 8 (positioned, z-index:auto) comes after every in-flow phase of the root context.
            Assert.True(escapeBackground > lastInFlow);
        }

        private static async System.Threading.Tasks.Task<(Document doc, List<PaintNodeBase> nodes)> BuildAsync(string html)
        {
            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
            return (doc, Flatten(tree.Roots));
        }

        private static List<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
        {
            var list = new List<PaintNodeBase>();
            if (nodes == null) return list;
            foreach (var node in nodes)
            {
                list.Add(node);
                if (node.Children != null) list.AddRange(Flatten(node.Children));
            }
            return list;
        }
    }
}
