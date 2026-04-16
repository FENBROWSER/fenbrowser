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
        public async Task InterleavedStyleAndLinkSheets_PreserveDomSourceOrder()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>.box { color: red; }</style>
    <link rel='stylesheet' href='middle.css'>
    <style>.box { color: green; }</style>
</head>
<body>
    <div class='box'>Test</div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://acid2.acidtests.org/"),
                uri => Task.FromResult(".box { color: blue; }"));
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.Equal("green", computed[box].Map["color"]);
        }

        [Fact]
        public async Task ImportedStylesheets_PreserveAuthoredImportOrder()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        @import url('a.css');
        @import url('b.css');
    </style>
</head>
<body>
    <div class='box'>Test</div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://acid2.acidtests.org/"),
                async uri =>
                {
                    if (uri.AbsolutePath.EndsWith("/a.css", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(40);
                        return ".box { color: red; }";
                    }

                    if (uri.AbsolutePath.EndsWith("/b.css", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(5);
                        return ".box { color: blue; }";
                    }

                    return null;
                });
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.Equal("blue", computed[box].Map["color"]);
        }

        [Fact]
        public async Task FontShorthand_OnRoot_PreservesComputedFontSizeAndInheritedEmOffsets()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font: 12px sans-serif; }
        #bar { position: fixed; top: 9em; left: 11em; }
    </style>
</head>
<body>
    <p id='bar'>bar</p>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var bar = doc.Descendants().OfType<Element>().First(e => e.Id == "bar");

            Assert.InRange(computed[root].FontSize ?? 0d, 11.999d, 12.001d);
            Assert.InRange(computed[bar].FontSize ?? 0d, 11.999d, 12.001d);
            Assert.InRange(computed[bar].Top ?? 0d, 107.999d, 108.001d);
            Assert.InRange(computed[bar].Left ?? 0d, 131.999d, 132.001d);
        }

        [Fact]
        public async Task FontShorthand_OnRoot_UpdatesRemBasisForDescendantLengths()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font: 12px sans-serif; }
        .box { width: 2rem; }
    </style>
</head>
<body>
    <div class='box'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.InRange(computed[root].FontSize ?? 0d, 11.999d, 12.001d);
            Assert.InRange(computed[box].Width ?? 0d, 23.999d, 24.001d);
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

        [Fact]
        public async Task FontInheritShorthand_UsesResolvedParentLonghands()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font: 12px sans-serif; }
        .intro { font: 2em sans-serif; }
        .intro * { font: inherit; }
    </style>
</head>
<body>
    <div class='intro'>
        <p id='copy'><a id='lead' href='#top'>Take The Acid2 Test</a> and compare it to <a id='tail' href='reference.html'>the reference rendering</a>.</p>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);

            var intro = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("intro"));
            var paragraph = doc.Descendants().OfType<Element>().First(e => e.Id == "copy");
            var lead = doc.Descendants().OfType<Element>().First(e => e.Id == "lead");
            var tail = doc.Descendants().OfType<Element>().First(e => e.Id == "tail");

            Assert.InRange(computed[intro].FontSize ?? 0d, 23.9d, 24.1d);
            Assert.InRange(computed[paragraph].FontSize ?? 0d, 23.9d, 24.1d);
            Assert.InRange(computed[lead].FontSize ?? 0d, 23.9d, 24.1d);
            Assert.InRange(computed[tail].FontSize ?? 0d, 23.9d, 24.1d);
        }

        [Fact]
        public async Task InvalidUnitlessWidth_DoesNotOverrideEarlierValidWidth()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .parser {
            width: 2em;
            width: 200;
        }
    </style>
</head>
<body>
    <div class='parser'>Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("parser"));

            Assert.Equal("2em", computed[target].Map["width"]);
        }

        [Fact]
        public async Task InvalidBackgroundShorthand_DoesNotOverrideEarlierValidColor()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .parser {
            background: yellow;
            background: red pink;
        }
    </style>
</head>
<body>
    <div class='parser'>Test</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("parser"));

            Assert.Equal("yellow", computed[target].Map["background"]);
            Assert.Equal("yellow", computed[target].Map["background-color"]);
        }

        [Fact]
        public async Task Acid2Nose_HeightConstraints_PreservePercentAndEmValues()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font: 12px sans-serif; }
        .nose {
            float: left;
            margin: -2em 2em -1em;
            border: solid 1em black;
            border-top: 0;
            min-height: 80%;
            height: 60%;
            max-height: 3em;
            padding: 0;
            width: 12em;
        }
    </style>
</head>
<body>
    <div class='nose'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("nose"));

            Assert.Equal("60%", computed[target].Map["height"]);
            Assert.Equal("80%", computed[target].Map["min-height"]);
            Assert.Equal("3em", computed[target].Map["max-height"]);
            Assert.Equal(60d, computed[target].HeightPercent);
            Assert.Equal(80d, computed[target].MinHeightPercent);
            Assert.Equal(36d, computed[target].MaxHeight);
            Assert.Equal(144d, computed[target].Width);
        }

        [Fact]
        public async Task FloatInherit_UsesParentComputedFloatValue()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        span { float: right; }
        em { float: inherit; }
    </style>
</head>
<body>
    <span><em id='target'></em></span>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "target");

            Assert.Equal("right", computed[target].Map["float"]);
            Assert.Equal("right", computed[target].Float);
        }

        [Fact]
        public async Task BackgroundShorthand_WithImageAttachmentAndPixelPosition_ExpandsLonghands()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .eyes {
            background: red url(data:image/png;base64,AAAA) fixed 1px 0;
        }
    </style>
</head>
<body>
    <div class='eyes'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("eyes"));

            Assert.Equal("red", computed[target].Map["background-color"]);
            Assert.Equal("url(data:image/png;base64,AAAA)", computed[target].Map["background-image"]);
            Assert.Equal("fixed", computed[target].Map["background-attachment"]);
            Assert.Equal("1px 0", computed[target].Map["background-position"]);
            Assert.Equal("url(data:image/png;base64,AAAA)", computed[target].BackgroundImage);
        }

        [Fact]
        public async Task BackgroundShorthand_WithImageAndNoRepeat_PreservesRepeatAndAttachment()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .chin {
            background: yellow url(data:image/png;base64,BBBB) no-repeat fixed;
        }
    </style>
</head>
<body>
    <div class='chin'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("chin"));

            Assert.Equal("yellow", computed[target].Map["background-color"]);
            Assert.Equal("url(data:image/png;base64,BBBB)", computed[target].Map["background-image"]);
            Assert.Equal("no-repeat", computed[target].Map["background-repeat"]);
            Assert.Equal("fixed", computed[target].Map["background-attachment"]);
            Assert.Equal("url(data:image/png;base64,BBBB)", computed[target].BackgroundImage);
        }

        [Fact]
        public async Task Acid2NestedObjectSelector_AppliesBackgroundAndPaddingToInnermostObject()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font: 12px sans-serif; }
        #eyes-a object object object {
            border-right: solid 1em black;
            padding: 0 12px 0 11px;
            background: url(data:image/png;base64,AAAA) fixed 1px 0;
        }
    </style>
</head>
<body>
    <div id='eyes-a'>
        <object data='outer'>
            <object data='middle'>
                <object id='target' data='inner'></object>
            </object>
        </object>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "target");

            Assert.Equal("url(data:image/png;base64,AAAA)", computed[target].Map["background-image"]);
            Assert.Equal("fixed", computed[target].Map["background-attachment"]);
            Assert.Equal("1px 0", computed[target].Map["background-position"]);
            Assert.Equal(12d, computed[target].BorderThickness.Right);
            Assert.Equal(11d, computed[target].Padding.Left);
            Assert.Equal(12d, computed[target].Padding.Right);
        }

        [Fact]
        public async Task InvalidEscapedSelector_DoesNotCollapseIntoClassSelector()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .parser { padding: 0 1em; }
        \\.parser { padding: 2em; }
    </style>
</head>
<body>
    <div id='target' class='parser'></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "target");

            Assert.Equal(16d, computed[target].Padding.Left);
            Assert.Equal(16d, computed[target].Padding.Right);
            Assert.Equal("0 1em", computed[target].Map["padding"]);
        }
    }
}
