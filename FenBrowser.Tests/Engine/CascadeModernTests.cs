using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace FenBrowser.Tests.Engine
{
    public class CascadeModernTests
    {
        [Fact]
        public async Task TestLayerPriority()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        @layer base, theme;
        @layer theme {
            .box { background-color: blue; color: white; }
        }
        @layer base {
            .box { background-color: red; color: yellow; }
        }
        .box { background-color: green; } /* Unlayered should win */
    </style>
</head>
<body>
    <div class=""box"">Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.True(computed.ContainsKey(box));
            var style = computed[box];
            
            // Normal: Unlayered (green) > theme (blue) > base (red)
            Assert.Equal("green", style.Map["background-color"]);
        }

        [Fact]
        public async Task TestImportantLayerPriority()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        @layer base, theme;
        @layer theme {
            .box { background-color: blue !important; }
        }
        @layer base {
            .box { background-color: red !important; }
        }
        .box { background-color: green !important; }
    </style>
</head>
<body>
    <div class=""box"">Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            var style = computed[box];
            
            // Important: Base (red) !important > theme (blue) !important > unlayered (green) !important
            // Because base is the first declared layer.
            Assert.Equal("red", style.Map["background-color"]);
        }

        [Fact]
        public async Task TestScopeProximity()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        @scope (.outer) {
            .target { color: blue; }
        }
        @scope (.inner) {
            .target { color: red; }
        }
    </style>
</head>
<body>
    <div class=""outer"">
        <div class=""inner"">
            <div class=""target"">Test</div>
        </div>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("target"));

            var style = computed[target];
            
            // Red should win because .inner is closer to .target than .outer is.
            Assert.Equal("red", style.Map["color"]);
        }

        [Fact]
        public async Task TestMarginShorthandExpansion_PreservesExplicitLonghand()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        .box {
            margin: 10px 20px;
            margin-left: 5px;
        }
    </style>
</head>
<body>
    <div class=""box"">Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            var style = computed[box];

            Assert.Equal("10px", style.Map["margin-top"]);
            Assert.Equal("20px", style.Map["margin-right"]);
            Assert.Equal("10px", style.Map["margin-bottom"]);
            Assert.Equal("5px", style.Map["margin-left"]);
        }

        [Fact]
        public async Task TestOverflowShorthandExpansion_SetsBothAxes()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        .box { overflow: hidden auto; }
    </style>
</head>
<body>
    <div class=""box"">Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            var style = computed[box];

            Assert.Equal("hidden", style.Map["overflow-x"]);
            Assert.Equal("auto", style.Map["overflow-y"]);
        }

        [Fact]
        public async Task TestBorderRadiusShorthandExpansion_SetsCornerLonghands()
        {
            string html = @"
<!doctype html>
<html>
<head>
    <style>
        .box { border-radius: 2px 4px 6px 8px; }
    </style>
</head>
<body>
    <div class=""box"">Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            var style = computed[box];

            Assert.Equal("2px", style.Map["border-top-left-radius"]);
            Assert.Equal("4px", style.Map["border-top-right-radius"]);
            Assert.Equal("6px", style.Map["border-bottom-right-radius"]);
            Assert.Equal("8px", style.Map["border-bottom-left-radius"]);
        }
    }
}
