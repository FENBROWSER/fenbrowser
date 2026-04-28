using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
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
        public async Task ExternalAppleLikeStylesheets_ApplyBodyVariablesAndGlobalNavLayout()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
    <link rel='stylesheet' href='base.css'>
    <link rel='stylesheet' href='globalnav.css'>
</head>
<body>
    <nav id='globalnav'>
        <ul class='globalnav-list'>
            <li><a class='globalnav-link' href='/store'>Store</a></li>
        </ul>
    </nav>
</body>
</html>";

            const string baseCss = @"
@charset ""UTF-8"";
:root {
    --sk-body-text-color: rgb(29, 29, 31);
    --sk-body-background-color: rgb(255, 255, 255);
}
html {
    font-size: 106.25%;
}
body {
    font-size: 17px;
    line-height: 1.4705882353;
    font-weight: 400;
    letter-spacing: -0.022em;
    font-family: ""SF Pro Text"", ""SF Pro Icons"", ""Helvetica Neue"", ""Helvetica"", ""Arial"", sans-serif;
    background-color: var(--sk-body-background-color, rgb(255, 255, 255));
    color: var(--sk-body-text-color, rgb(29, 29, 31));
    font-style: normal;
}";

            const string globalNavCss = @"
html {
    --r-globalnav-height: 44px;
}
#globalnav {
    --r-globalnav-color: rgba(0, 0, 0, 0.8);
    --r-globalnav-height: 44px;
}
#globalnav .globalnav-list {
    cursor: default;
    margin: 0 -8px;
    width: auto;
    height: 44px;
    display: flex;
    justify-content: space-between;
    list-style: none;
}
#globalnav .globalnav-link {
    line-height: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--r-globalnav-color);
    height: 44px;
    text-decoration: none;
}";

            var parser = new HtmlParser(html, new Uri("https://www.apple.com/"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(
                root,
                new Uri("https://www.apple.com/"),
                uri => Task.FromResult(
                    uri.AbsolutePath.EndsWith("base.css", StringComparison.OrdinalIgnoreCase)
                        ? baseCss
                        : uri.AbsolutePath.EndsWith("globalnav.css", StringComparison.OrdinalIgnoreCase)
                            ? globalNavCss
                            : null));

            var body = doc.Descendants().OfType<Element>().First(e => e.TagName == "BODY");
            var list = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("globalnav-list"));
            var link = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("globalnav-link"));

            Assert.Equal("17px", computed[body].Map["font-size"]);
            Assert.Equal("rgb(29, 29, 31)", computed[body].Map["color"]);
            Assert.Equal("rgb(29, 29, 31)", computed[root].CustomProperties["--sk-body-text-color"]);
            Assert.Equal("44px", computed[root].CustomProperties["--r-globalnav-height"]);
            Assert.Equal("flex", computed[list].Map["display"]);
            Assert.Equal("flex", computed[link].Map["display"]);
        }

        [Fact]
        public async Task ComputeAsync_WorkerThreadStyles_AreVisibleAcrossThreads()
        {
            CssLoader.ClearCaches();

            var previousCache = FenBrowser.Core.Css.NodeStyleExtensions.GetDefaultStyleCache();
            FenBrowser.Core.Css.NodeStyleExtensions.SetDefaultStyleCache(new FenBrowser.Core.Css.StyleCache());
            try
            {
                const string html = @"
<!doctype html>
<html>
<head>
    <style>
        body { color: rgb(29, 29, 31); }
        .nav { display: flex; }
    </style>
</head>
<body>
    <div class='nav'>Test</div>
</body>
</html>";

                var parser = new HtmlParser(html, new Uri("https://test.local/"));
                var doc = parser.Parse();
                var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
                var body = doc.Descendants().OfType<Element>().First(e => e.TagName == "BODY");
                var nav = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("nav"));

                await Task.Run(async () =>
                {
                    var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local/"), null);
                    Assert.Equal("rgb(29, 29, 31)", computed[body].Map["color"]);
                    Assert.Equal("flex", computed[nav].Map["display"]);
                });

                Assert.Equal("rgb(29, 29, 31)", body.GetComputedStyle()?.Map["color"]);
                Assert.Equal("flex", nav.GetComputedStyle()?.Map["display"]);
            }
            finally
            {
                FenBrowser.Core.Css.NodeStyleExtensions.SetDefaultStyleCache(previousCache);
            }
        }

        [Fact]
        public async Task HasSelector_WithAttributeArgument_AppliesInCascade()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        .section-hero:has([data-tile-id='mothers-day-2026']) { position: relative; }
        .mothers-day-icons { position: absolute; }
    </style>
</head>
<body>
    <section class='section-hero' id='hero'>
        <div data-tile-id='mothers-day-2026'></div>
        <div class='mothers-day-icons'></div>
    </section>
    <section class='section-hero' id='other'>
        <div data-tile-id='other'></div>
    </section>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var hero = doc.GetElementById("hero");
            var other = doc.GetElementById("other");
            var icons = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("mothers-day-icons"));

            Assert.Equal("relative", computed[hero].Map["position"]);
            Assert.Equal("absolute", computed[icons].Map["position"]);
            Assert.False(computed[other].Map.TryGetValue("position", out var otherPosition) && otherPosition == "relative");
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
