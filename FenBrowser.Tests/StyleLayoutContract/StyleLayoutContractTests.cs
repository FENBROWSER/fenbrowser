using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using System;
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
    }
}
