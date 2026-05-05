using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using System;
using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class PaintTreeTextColorTests
    {
        [Fact]
        public void TransparentTextColor_RemainsTransparentInPaintTree()
        {
            var body = new Element("div");
            var host = new Element("div");
            var text = new Text("FAIL");

            body.AppendChild(host);
            host.AppendChild(text);

            var styles = new Dictionary<Node, CssComputed>
            {
                [body] = new CssComputed { Display = "block", Width = 200, Height = 80, ForegroundColor = SKColors.Black },
                [host] = new CssComputed { Display = "block", Width = 200, Height = 40, ForegroundColor = SKColors.Transparent }
            };

            var tree = RunPipeline(body, styles);
            var textNode = Flatten(tree.Roots)
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => n.FallbackText == "FAIL");

            Assert.NotNull(textNode);
            Assert.Equal(0, textNode.Color.Alpha);
        }

        [Fact]
        public async Task AuthoredTextColor_PaintsWithComputedForeground()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>#probe { color: rgb(12, 34, 56); }</style>
</head>
<body>
  <p id='probe'>Hello Paint</p>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var boxes = (ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);

            var textNode = Flatten(tree.Roots)
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => !string.IsNullOrEmpty(n.FallbackText) && n.FallbackText.Contains("Hello Paint", StringComparison.Ordinal));

            Assert.NotNull(textNode);
            Assert.Equal((byte)12, textNode.Color.Red);
            Assert.Equal((byte)34, textNode.Color.Green);
            Assert.Equal((byte)56, textNode.Color.Blue);
            Assert.Equal((byte)255, textNode.Color.Alpha);
        }

        private static ImmutablePaintTree RunPipeline(Element root, Dictionary<Node, CssComputed> styles)
        {
            var doc = new Document();
            doc.AppendChild(root);

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var boxes = (ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);

            return NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
        }

        private static List<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
        {
            var list = new List<PaintNodeBase>();
            if (nodes == null)
            {
                return list;
            }

            foreach (var node in nodes)
            {
                list.Add(node);
                if (node.Children != null)
                {
                    list.AddRange(Flatten(node.Children));
                }
            }

            return list;
        }
    }
}
