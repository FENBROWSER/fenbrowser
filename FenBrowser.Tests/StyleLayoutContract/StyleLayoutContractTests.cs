using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.StyleLayoutContract
{
    public class StyleLayoutContractTests
    {
        [Fact]
        public async Task Test1_UaDefaultDisplay()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div id='a'>Block</div>
  <span id='b'>Inline</span>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            
            var div = doc.GetElementById("a");
            var span = doc.GetElementById("b");
            var body = doc.Descendants().OfType<Element>().First(e => e.TagName == "BODY");

            // div and body get 'block' from UA stylesheet
            Assert.Equal("block", computed[div].Map["display"]);
            Assert.Equal("block", computed[body].Map["display"]);

            // Span should resolve to inline display even when no authored/UA display declaration is present.
            Assert.Equal("inline", computed[span].Display);
        }

        [Fact]
        public async Task Test2_InlineStyleBeatsStylesheet()
        {
            string html = @"
<!doctype html>
<html>
<head>
<style>
#a { color: blue; }
</style>
</head>
<body>
  <div id='a' style='color:red'>Text</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var a = doc.GetElementById("a");

            Assert.Equal("red", computed[a].Map["color"]);
        }

        [Fact]
        public async Task Test3_Specificity()
        {
            string html = @"
<!doctype html>
<html>
<head>
<style>
div { color: black; }
.box { color: blue; }
#a { color: red; }
</style>
</head>
<body>
  <div id='a' class='box'>Text</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var a = doc.GetElementById("a");

            Assert.Equal("red", computed[a].Map["color"]);
        }

        [Fact]
        public async Task Test4_SourceOrder()
        {
            string html = @"
<!doctype html>
<html>
<head>
<style>
.box { color: blue; }
.box { color: green; }
</style>
</head>
<body>
  <div class='box' id='a'>Text</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var a = doc.GetElementById("a");

            Assert.Equal("green", computed[a].Map["color"]);
        }

        [Fact]
        public async Task Test5_ShorthandLonghandOrder()
        {
            string html = @"
<!doctype html>
<html>
<head>
<style>
#a {
  margin: 5px;
  margin-left: 20px;
}
</style>
</head>
<body>
  <div id='a'>Box</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var a = doc.GetElementById("a");

            Assert.Equal("5px", computed[a].Map["margin-top"]);
            Assert.Equal("5px", computed[a].Map["margin-right"]);
            Assert.Equal("5px", computed[a].Map["margin-bottom"]);
            Assert.Equal("20px", computed[a].Map["margin-left"]);
        }

        [Fact]
        public async Task Test6_DisplayNone()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div id='hidden' style='display:none'>Hidden</div>
  <div id='visible'>Visible</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var hidden = doc.GetElementById("hidden");
            var visible = doc.GetElementById("visible");

            Assert.Equal("none", computed[hidden].Map["display"]);
            
            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            // hidden element creates no layout box
            Assert.Null(layoutComputer.GetBox(hidden));
            // hidden text is not painted (no box for text)
            Assert.Null(layoutComputer.GetBox(hidden.ChildNodes[0]));
            
            // visible element creates layout box
            Assert.NotNull(layoutComputer.GetBox(visible));
        }

        [Fact]
        public async Task Test7_InheritedFontColor()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div style='color:red;font-size:30px' id='parent'>
    <span id='child'>Child</span>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var parent = doc.GetElementById("parent");
            var child = doc.GetElementById("child");

            Assert.True(computed[parent].Map.TryGetValue("color", out var parentColor));
            Assert.True(computed[child].Map.TryGetValue("color", out var childColor));
            Assert.Equal(parentColor, childColor);

            Assert.True(computed[parent].Map.TryGetValue("font-size", out var parentFontSize));
            Assert.True(computed[child].Map.TryGetValue("font-size", out var childFontSize));
            Assert.Equal(parentFontSize, childFontSize);
        }

        [Fact]
        public async Task Test8_WidthPercentIsCanonical()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div id='parent' style='width:400px'>
    <div id='child' style='width:50%'>Child</div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var child = doc.GetElementById("child");

            Assert.Equal("50%", computed[child].Map["width"]);
            
            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var childBox = layoutComputer.GetBox(child);
            Assert.NotNull(childBox);
            // Used layout width should be 200px
            Assert.Equal(200f, childBox.ContentBox.Width);
        }

        [Fact]
        public async Task Test9_ScriptStyleHeadNotPainted()
        {
            string html = @"
<!doctype html>
<html>
<head id='head'>
  <style id='style'>#a { color:red; }</style>
  <script id='script'>console.log('x')</script>
</head>
<body>
  <div id='a'>Visible</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            
            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var style = doc.GetElementById("style");
            var script = doc.GetElementById("script");
            var head = doc.GetElementById("head");
            var a = doc.GetElementById("a");

            Assert.Null(layoutComputer.GetBox(style));
            Assert.Null(layoutComputer.GetBox(script));
            Assert.Null(layoutComputer.GetBox(head));
            Assert.NotNull(layoutComputer.GetBox(a));
        }

        [Fact]
        public async Task Test10_UnknownElementFallback()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <custom-card id='x'>Hello</custom-card>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var x = doc.GetElementById("x");

            // Unknown/custom elements default to inline in computed styles.
            Assert.Equal("inline", computed[x].Display);
        }

        [Fact]
        public async Task Test11_EmWidthUsesInheritedFontSize()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div id='parent' style='font-size:25px'>
    <div id='child' style='width:2em'>Child</div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var child = doc.GetElementById("child");

            Assert.Equal("2em", computed[child].Map["width"]);
            Assert.InRange(computed[child].Width ?? 0d, 49.999d, 50.001d);

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var childBox = layoutComputer.GetBox(child);
            Assert.NotNull(childBox);
            Assert.Equal(50f, childBox.ContentBox.Width);
        }

        [Fact]
        public async Task Test12_HeadMetadataNodesDoNotCreateLayoutBoxes()
        {
            string html = @"
<!doctype html>
<html>
<head id='head'>
  <meta id='meta' charset='utf-8'>
  <link id='link' rel='stylesheet' href='noop.css'>
</head>
<body>
  <p id='visible'>Visible</p>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local/"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local/"), _ => Task.FromResult<string>(null));

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var head = doc.GetElementById("head");
            var meta = doc.GetElementById("meta");
            var link = doc.GetElementById("link");
            var visible = doc.GetElementById("visible");

            Assert.Null(layoutComputer.GetBox(head));
            Assert.Null(layoutComputer.GetBox(meta));
            Assert.Null(layoutComputer.GetBox(link));
            Assert.NotNull(layoutComputer.GetBox(visible));
        }

        [Fact]
        public async Task Test13_BlockFlowSiblingsStackVertically()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <div id='first' style='height:40px'>A</div>
  <div id='second' style='height:30px'>B</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var first = doc.GetElementById("first");
            var second = doc.GetElementById("second");
            var firstBox = layoutComputer.GetBox(first);
            var secondBox = layoutComputer.GetBox(second);

            Assert.NotNull(firstBox);
            Assert.NotNull(secondBox);
            Assert.InRange(secondBox.ContentBox.Top - firstBox.ContentBox.Bottom, -0.001f, 0.001f);
        }

        [Fact]
        public async Task Test14_DisplayNoneHidesEntireSubtreeFromLayout()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <section id='hidden-root' style='display:none'>
    <div id='hidden-child'>
      <span id='hidden-leaf'>Leaf</span>
    </div>
  </section>
  <section id='visible-root'>Visible</section>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var hiddenRoot = doc.GetElementById("hidden-root");
            var hiddenChild = doc.GetElementById("hidden-child");
            var hiddenLeaf = doc.GetElementById("hidden-leaf");
            var visibleRoot = doc.GetElementById("visible-root");

            Assert.Equal("none", computed[hiddenRoot].Map["display"]);
            Assert.Null(layoutComputer.GetBox(hiddenRoot));
            Assert.Null(layoutComputer.GetBox(hiddenChild));
            Assert.Null(layoutComputer.GetBox(hiddenLeaf));
            Assert.NotNull(layoutComputer.GetBox(visibleRoot));
        }

        [Fact]
        public async Task Test15_PaintOrderFollowsStackingBuckets()
        {
            const string html = @"
<!doctype html>
<html>
<body>
  <div id='neg' style='position:relative;z-index:-1;width:30px;height:20px;background:#ff0000'></div>
  <div id='block' style='width:30px;height:20px;background:#00ff00'></div>
  <div id='auto' style='position:relative;width:30px;height:20px;background:#0000ff'></div>
  <div id='z1' style='position:relative;z-index:1;width:30px;height:20px;background:#ffff00'></div>
  <div id='z2' style='position:relative;z-index:2;width:30px;height:20px;background:#000'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var tree = NewPaintTreeBuilder.Build(
                root,
                layoutComputer.GetAllBoxes().ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                computed,
                800,
                600,
                null);

            var nodes = FlattenPaintNodes(tree.Roots);
            var neg = doc.GetElementById("neg");
            var block = doc.GetElementById("block");
            var auto = doc.GetElementById("auto");
            var z1 = doc.GetElementById("z1");
            var z2 = doc.GetElementById("z2");

            int negIndex = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, neg));
            int blockIndex = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, block));
            int autoIndex = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, auto));
            int z1Index = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, z1));
            int z2Index = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, z2));

            Assert.True(negIndex >= 0, "Expected negative z-index layer to paint.");
            Assert.True(blockIndex >= 0, "Expected normal-flow block to paint.");
            Assert.True(autoIndex >= 0, "Expected positioned auto-z layer to paint.");
            Assert.True(z1Index >= 0, "Expected positive z-index (1) layer to paint.");
            Assert.True(z2Index >= 0, "Expected positive z-index (2) layer to paint.");

            Assert.True(negIndex < blockIndex, $"Negative z-index should paint before normal flow. neg={negIndex}, block={blockIndex}");
            Assert.True(blockIndex < autoIndex, $"Normal flow should paint before positioned auto-z. block={blockIndex}, auto={autoIndex}");
            Assert.True(autoIndex < z1Index, $"Positioned auto-z should paint before positive z-index contexts. auto={autoIndex}, z1={z1Index}");
            Assert.True(z1Index < z2Index, $"Positive z-index contexts should paint in ascending z-index order. z1={z1Index}, z2={z2Index}");
        }

        [Fact]
        public async Task Test16_ScrollBasicsEmitClipAndScrollPaintNodes()
        {
            const string html = @"
<!doctype html>
<html>
<body>
  <div id='scroller' style='width:120px;height:40px;overflow-y:auto;overflow-x:auto;background:#eee'>
    <div id='content' style='width:120px;height:140px;background:#f00'></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var scroller = doc.GetElementById("scroller");
            var content = doc.GetElementById("content");

            var layoutComputer = new MinimalLayoutComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SkiaSharp.SKSize(800, 600));
            layoutComputer.Arrange(root, new SkiaSharp.SKRect(0, 0, 800, 600));

            var boxes = layoutComputer.GetAllBoxes().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var scrollManager = new FenBrowser.FenEngine.Rendering.Interaction.ScrollManager();

            _ = NewPaintTreeBuilder.Build(root, boxes, computed, 800, 600, scrollManager);
            var state = scrollManager.GetScrollState(scroller);
            Assert.True(state.MaxScrollY > 0, $"Expected scrollable content. maxY={state.MaxScrollY}");

            scrollManager.SetScrollPosition(scroller, 0, state.MaxScrollY);

            var tree = NewPaintTreeBuilder.Build(root, boxes, computed, 800, 600, scrollManager);
            var nodes = FlattenPaintNodes(tree.Roots);

            var scrollNode = nodes.OfType<ScrollPaintNode>().OrderByDescending(n => n.ScrollY).FirstOrDefault();
            var clipNode = nodes.OfType<ClipPaintNode>().FirstOrDefault();
            int contentBackgroundIndex = nodes.FindIndex(n => n is BackgroundPaintNode bg && ReferenceEquals(bg.SourceNode, content));

            Assert.NotNull(clipNode);
            Assert.NotNull(scrollNode);
            Assert.True(scrollNode.ScrollY > 0, $"Expected positive scroll offset after SetScrollPosition. y={scrollNode.ScrollY}");
            Assert.True(contentBackgroundIndex >= 0, "Expected scroll content background node to be present in paint tree.");
        }

        private static List<PaintNodeBase> FlattenPaintNodes(IEnumerable<PaintNodeBase> nodes)
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
                    list.AddRange(FlattenPaintNodes(node.Children));
                }
            }

            return list;
        }
    }
}
