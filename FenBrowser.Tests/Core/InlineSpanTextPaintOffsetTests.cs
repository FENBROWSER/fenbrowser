using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// Text inside a non-atomic inline paints where layout put it. The paint tree's
    /// side-bearing inset for text that tightly fits its parent treated an inline span as
    /// that parent; a span's box is its own text, so every such run moved 3.5px right:
    /// w3schools' `Get your&lt;span&gt; own&lt;/span&gt; website` painted "ownwebsite".
    /// </summary>
    public sealed class InlineSpanTextPaintOffsetTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>body { margin: 0; font: 18px sans-serif; }</style></head>
<body><div>Get your<span id='own'> own</span> website</div></body></html>";

        [Fact]
        public async Task TextInAnInlineSpan_PaintsAtItsLayoutPosition()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);

            var span = doc.Descendants().OfType<Element>().First(e => e.Id == "own");
            var textNode = span.ChildNodes.OfType<Text>().Single();
            var run = Flatten(tree.Roots).OfType<TextPaintNode>().Single(n => ReferenceEquals(n.SourceNode, textNode));

            var textBox = boxes[textNode];
            float laidOutX = textBox.ContentBox.Left + textBox.Lines[0].Origin.X;
            Assert.Equal(laidOutX, run.TextOrigin.X, 1);
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
