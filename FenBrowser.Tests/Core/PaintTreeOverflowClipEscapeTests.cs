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
    /// css-overflow-3 §3.1: an overflow clip applies to descendants whose containing
    /// block is the clipping element or a descendant of it. An absolutely positioned box
    /// whose containing block is above a non-positioned overflow:hidden ancestor is not
    /// clipped by it; one under a positioned clipper is. google.com's menus live in a
    /// body-level layer under a 0px-tall overflow:hidden wrapper.
    /// </summary>
    public sealed class PaintTreeOverflowClipEscapeTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #wrap { overflow: hidden; height: 0; }
  #layer { position: absolute; top: 0; left: 0; }
  #menu { position: absolute; left: 100px; top: 100px; width: 50px; height: 50px; background: #f00; }
  #relwrap { overflow: hidden; height: 0; position: relative; }
  #contained { position: absolute; left: 10px; top: 10px; width: 20px; height: 20px; background: #00f; }
</style></head>
<body>
  <div id='wrap'><div id='layer'><div id='menu'>menu</div></div></div>
  <div id='relwrap'><div id='contained'>c</div></div>
</body></html>";

        [Fact]
        public async System.Threading.Tasks.Task AbsoluteBoxContainedAboveAStaticClipper_IsNotClipped()
        {
            var (doc, roots) = await BuildAsync(Html);
            var menu = Find(doc, "menu");
            var contained = Find(doc, "contained");

            Assert.False(IsUnderClip(roots, menu, Find(doc, "wrap")), "menu must escape the static overflow:hidden wrapper");
            Assert.True(IsUnderClip(roots, contained, Find(doc, "relwrap")), "a positioned clipper still contains its absolute descendants");
        }

        private static bool IsUnderClip(IEnumerable<PaintNodeBase> nodes, Element target, Element clipper)
        {
            foreach (var node in nodes)
            {
                if (node is ClipPaintNode clip && ReferenceEquals(clip.SourceNode, clipper))
                {
                    if (Flatten(clip.Children).Any(n => n is BackgroundPaintNode && ReferenceEquals(n.SourceNode, target)))
                    {
                        return true;
                    }
                }
                if (node.Children != null && IsUnderClip(node.Children, target, clipper))
                {
                    return true;
                }
            }
            return false;
        }

        private static Element Find(Document doc, string id) => doc.Descendants().OfType<Element>().First(e => e.Id == id);

        private static async System.Threading.Tasks.Task<(Document, List<PaintNodeBase>)> BuildAsync(string html)
        {
            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
            return (doc, tree.Roots.ToList());
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
