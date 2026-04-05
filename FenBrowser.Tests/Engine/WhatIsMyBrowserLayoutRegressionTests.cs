using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class WhatIsMyBrowserLayoutRegressionTests
    {
        [Fact]
        public async Task Cascade_Applies_WimbFlexSelectors_ToHeaderAndSettingsRows()
        {
            var (doc, styles) = await ComputeStylesAsync();

            var nav = ById(doc, "site");
            var logoAndName = ById(doc, "top-logo-and-name");
            var mainNav = ById(doc, "main-nav");
            var mainNavItem = mainNav.Children.OfType<Element>().First();
            var mainNavLink = mainNavItem.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "A", StringComparison.OrdinalIgnoreCase));
            var mainNavText = Assert.IsType<Text>(mainNavLink.ChildNodes.First());
            var corset = FirstByClass(ById(doc, "section-readout-secondary"), "corset");
            var contentBlockMain = FirstByClass(corset, "content-block-main");
            var contentBlockExtra = FirstByClass(corset, "content-block-extra");
            var settingsList = ById(doc, "your-browsers-settings");
            var settingsRow = ById(doc, "your-browsers-settings-javascript");
            var settingDescription = FirstByClass(settingsRow, "setting-description");
            var settingDetection = FirstByClass(settingsRow, "setting-detection");
            var settingGuide = FirstByClass(settingsRow, "setting-change-guide");

            Assert.Equal("flex", styles[nav].Display);
            Assert.Equal("flex", styles[logoAndName].Display);
            Assert.Equal("flex", styles[mainNav].Display);
            Assert.Equal("flex", styles[mainNavItem].Display);
            Assert.Equal("flex", styles[mainNavLink].Display);
            Assert.Equal("flex", styles[corset].Display);
            Assert.Equal("My browser", mainNavText.Data.Trim());
            Assert.Equal("flex", styles[settingsRow].Display);
            Assert.Equal("flex", styles[settingDescription].Display);
            Assert.Equal("flex", styles[settingDetection].Display);
            Assert.Equal("flex", styles[settingGuide].Display);

            Assert.Equal("row", styles[nav].FlexDirection);
            Assert.Equal("nowrap", styles[mainNav].FlexWrap);
            Assert.Equal("column", styles[settingDescription].FlexDirection);
            Assert.Equal("column", styles[settingDetection].FlexDirection);
            Assert.Equal("column", styles[settingGuide].FlexDirection);

            Assert.Equal(156, styles[settingDescription].Width);
            Assert.Equal(156, styles[settingGuide].Width);
            Assert.Equal(100, styles[settingDetection].WidthPercent);
            Assert.Equal(56, styles[settingDetection].MinHeight);
            Assert.Equal(728, styles[contentBlockMain].Width);
            Assert.Equal(336, styles[contentBlockExtra].Width);
            Assert.Equal(336, styles[contentBlockExtra].MinWidth);
            Assert.Equal(336, styles[contentBlockExtra].MaxWidth);
            Assert.Equal("block", styles[settingsList].Display);
        }

        [Fact]
        public async Task Layout_Keeps_WimbHeaderAndSettingsControlsOnSingleRow()
        {
            var (doc, styles) = await ComputeStylesAsync();
            var body = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));

            var computer = new MinimalLayoutComputer(styles, 1024, 700, "https://www.whatismybrowser.com/");
            computer.Measure(body, new SKSize(1024, 700));
            computer.Arrange(body, new SKRect(0, 0, 1024, 700));

            var mainNav = ById(doc, "main-nav");
            var mainNavItems = mainNav.Children.OfType<Element>().ToArray();
            Assert.True(mainNavItems.Length >= 4);

            var navFirst = computer.GetBox(mainNavItems[0]);
            var navSecond = computer.GetBox(mainNavItems[1]);
            var navThird = computer.GetBox(mainNavItems[2]);
            var navFourth = computer.GetBox(mainNavItems[3]);

            Assert.NotNull(navFirst);
            Assert.NotNull(navSecond);
            Assert.NotNull(navThird);
            Assert.NotNull(navFourth);

            Assert.True(
                navFirst.ContentBox.Left < navSecond.ContentBox.Left,
                $"Expected nav item 2 to be to the right of item 1, got {navFirst.ContentBox} and {navSecond.ContentBox}");
            Assert.True(
                navSecond.ContentBox.Left < navThird.ContentBox.Left,
                $"Expected nav item 3 to be to the right of item 2, got {navSecond.ContentBox} and {navThird.ContentBox}");
            Assert.True(
                navThird.ContentBox.Left < navFourth.ContentBox.Left,
                $"Expected nav item 4 to be to the right of item 3, got {navThird.ContentBox} and {navFourth.ContentBox}");
            Assert.InRange(Math.Abs(navFirst.ContentBox.Top - navSecond.ContentBox.Top), 0, 2);
            Assert.InRange(Math.Abs(navSecond.ContentBox.Top - navThird.ContentBox.Top), 0, 2);
            Assert.InRange(Math.Abs(navThird.ContentBox.Top - navFourth.ContentBox.Top), 0, 2);

            var settingsRow = ById(doc, "your-browsers-settings-javascript");
            var settingDescription = FirstByClass(settingsRow, "setting-description");
            var settingDetection = FirstByClass(settingsRow, "setting-detection");
            var settingGuide = FirstByClass(settingsRow, "setting-change-guide");

            var rowBox = computer.GetBox(settingsRow);
            var descriptionBox = computer.GetBox(settingDescription);
            var detectionBox = computer.GetBox(settingDetection);
            var guideBox = computer.GetBox(settingGuide);

            Assert.NotNull(rowBox);
            Assert.NotNull(descriptionBox);
            Assert.NotNull(detectionBox);
            Assert.NotNull(guideBox);

            Assert.True(
                descriptionBox.ContentBox.Left < detectionBox.ContentBox.Left,
                $"Expected middle settings cell to be to the right of description, got {descriptionBox.ContentBox} and {detectionBox.ContentBox}");
            Assert.True(
                detectionBox.ContentBox.Left < guideBox.ContentBox.Left,
                $"Expected guide cell to be to the right of middle cell, got {detectionBox.ContentBox} and {guideBox.ContentBox}");
            Assert.True(descriptionBox.ContentBox.Right <= detectionBox.ContentBox.Left + 1);
            Assert.True(detectionBox.ContentBox.Right <= guideBox.ContentBox.Left + 1);
            Assert.InRange(Math.Abs(descriptionBox.ContentBox.Top - detectionBox.ContentBox.Top), 0, 4);
            Assert.InRange(Math.Abs(detectionBox.ContentBox.Top - guideBox.ContentBox.Top), 0, 4);
            Assert.True(detectionBox.ContentBox.Width > 200, $"Expected middle settings cell to expand, got {detectionBox.ContentBox.Width}");
            Assert.True(rowBox.ContentBox.Height < 140, $"Expected compact settings row height, got {rowBox.ContentBox.Height}");

            var copyOne = ById(doc, "settings-copy");
            var copyTwo = ById(doc, "settings-copy-two");
            var copyOneText = Assert.IsType<Text>(copyOne.ChildNodes.First());
            var copyTwoText = Assert.IsType<Text>(copyTwo.ChildNodes.First());

            var copyOneBox = computer.GetBox(copyOne);
            var copyTwoBox = computer.GetBox(copyTwo);
            var copyOneTextBox = computer.GetBox(copyOneText);
            var copyTwoTextBox = computer.GetBox(copyTwoText);

            Assert.NotNull(copyOneBox);
            Assert.NotNull(copyTwoBox);
            Assert.NotNull(copyOneTextBox);
            Assert.NotNull(copyTwoTextBox);
            Assert.True(
                copyTwoBox.ContentBox.Top >= copyOneBox.ContentBox.Bottom - 1,
                $"Expected second paragraph below first, got {copyOneBox.ContentBox} and {copyTwoBox.ContentBox}");
            Assert.True(
                copyOneTextBox.ContentBox.Top >= copyOneBox.ContentBox.Top - 1 &&
                copyOneTextBox.ContentBox.Bottom <= copyOneBox.ContentBox.Bottom + 1,
                $"Expected first text node inside paragraph box, got text {copyOneTextBox.ContentBox} and paragraph {copyOneBox.ContentBox}");
            Assert.True(
                copyTwoTextBox.ContentBox.Top >= copyTwoBox.ContentBox.Top - 1 &&
                copyTwoTextBox.ContentBox.Bottom <= copyTwoBox.ContentBox.Bottom + 1,
                $"Expected second text node inside paragraph box, got text {copyTwoTextBox.ContentBox} and paragraph {copyTwoBox.ContentBox}");
            Assert.NotNull(copyOneTextBox.Lines);
            Assert.NotEmpty(copyOneTextBox.Lines);
            Assert.True(
                copyOneTextBox.Lines[0].Origin.Y < copyOneTextBox.Lines[0].Baseline,
                $"Expected inline text origin to be line-top, got originY={copyOneTextBox.Lines[0].Origin.Y} baseline={copyOneTextBox.Lines[0].Baseline}");
        }

        [Fact]
        public async Task LatestSnapshot_Renderer_KeepsNavWidthsAndParagraphTextInsideParents()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string[] candidateLogDirs =
            {
                Path.Combine(repoRoot, "logs"),
                Path.Combine(repoRoot, "FenBrowser.Host", "bin", "Debug", "net8.0", "logs")
            };

            string snapshotPath = candidateLogDirs
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "engine_source_*.html"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                return;
            }

            string html = await File.ReadAllTextAsync(snapshotPath);
            var baseUri = new Uri("https://www.whatismybrowser.com/");
            string repoCssPath = Path.Combine(repoRoot, "wimb_site.min.css");
            string siteCss = File.Exists(repoCssPath) ? await File.ReadAllTextAsync(repoCssPath) : null;
            if (!string.IsNullOrWhiteSpace(siteCss))
            {
                html = html.Replace("</head>", $"<style>{siteCss}</style></head>", StringComparison.OrdinalIgnoreCase);
            }

            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            if (!doc.Descendants().OfType<Element>().Any(e => string.Equals(e.Id, "site", StringComparison.Ordinal)) ||
                !doc.Descendants().OfType<Element>().Any(e => string.Equals(e.Id, "main-nav", StringComparison.Ordinal)))
            {
                return;
            }

            var computed = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1600, viewportHeight: 900);

            var body = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));
            var siteNav = ById(doc, "site");
            var snapshotMainNav = ById(doc, "main-nav");
            Console.WriteLine($"SNAPSHOT-STYLES body.margin={computed[body].Margin.Top}/{computed[body].Margin.Left} nav.display={computed[siteNav].Display} nav.flexDir={computed[siteNav].FlexDirection} mainNav.display={computed[snapshotMainNav].Display} mainNav.flexWrap={computed[snapshotMainNav].FlexWrap}");

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(1600, 900);
            using var canvas = new SKCanvas(bitmap);

            renderer.Render(
                root,
                canvas,
                computed,
                new SKRect(0, 0, 1600, 900),
                baseUri.AbsoluteUri,
                (size, overlays) => { });
            canvas.Flush();

            var mainNav = ById(doc, "main-nav");
            var navItems = mainNav.Children.OfType<Element>().Take(4).ToArray();
            Assert.Equal(4, navItems.Length);

            var mainNavStyle = computed[mainNav];
            Console.WriteLine($"MAIN-NAV-STYLE display={mainNavStyle.Display} flexWrap={mainNavStyle.FlexWrap} flexDir={mainNavStyle.FlexDirection}");

            float navWidthSum = 0f;
            var navRectDiagnostics = new List<string>();
            foreach (var item in navItems)
            {
                Assert.True(renderer.LastLayout.TryGetElementRect(item, out var navRect), $"Missing rect for nav item '{item.ClassName}'.");
                navWidthSum += navRect.Width;
                computed.TryGetValue(item, out var itemStyle);
                navRectDiagnostics.Add($"{item.ClassName}@({navRect.X:0.##},{navRect.Y:0.##},{navRect.Width:0.##},{navRect.Height:0.##}) display={itemStyle?.Display} flexGrow={itemStyle?.FlexGrow} flexWrap={itemStyle?.FlexWrap}");
            }
            Console.WriteLine("MAIN-NAV-RECTS: " + string.Join(" | ", navRectDiagnostics));

            Assert.True(
                navWidthSum > 260f,
                $"Expected header nav items to keep intrinsic width, got cumulative width {navWidthSum}.");

            var settingsCopyParagraph = FirstByClass(ById(doc, "readout-secondary"), "now-you-know")
                .ChildNodes
                .OfType<Element>()
                .First(e => string.Equals(e.TagName, "P", StringComparison.OrdinalIgnoreCase));
            var settingsCopyText = Assert.IsType<Text>(settingsCopyParagraph.ChildNodes.First(n => n is Text));

            var renderContext = renderer.CreateRenderContext();
            Assert.True(renderContext.TryGetBox(settingsCopyParagraph, out var paragraphBox), "Missing paragraph box for settings copy.");
            Assert.True(renderContext.TryGetBox(settingsCopyText, out var textBox), "Missing text box for settings copy.");

            Assert.True(
                textBox.ContentBox.Top >= paragraphBox.ContentBox.Top - 1f &&
                textBox.ContentBox.Bottom <= paragraphBox.ContentBox.Bottom + 1f,
                $"Expected settings copy text inside its paragraph. Paragraph={paragraphBox.ContentBox} Text={textBox.ContentBox}");

            var paintNodes = new System.Collections.Generic.List<PaintNodeBase>();
            CollectPaintNodes(renderContext.PaintTreeRoots, paintNodes);

            var navPaintDiagnostics = paintNodes
                .OfType<TextPaintNode>()
                .Where(n => n.FallbackText == "My browser" ||
                            n.FallbackText == "Guides" ||
                            n.FallbackText == "Detect my settings" ||
                            n.FallbackText == "Tools")
                .Select(n =>
                {
                    renderContext.TryGetBox(n.SourceNode, out var textNodeBox);
                    renderContext.TryGetBox(n.SourceNode?.ParentNode, out var anchorBox);
                    return $"{n.FallbackText}@({n.TextOrigin.X:0.##},{n.TextOrigin.Y:0.##}) bounds={n.Bounds} textBox={textNodeBox?.ContentBox} anchorBox={anchorBox?.ContentBox} path={DescribeAncestorPath(n.SourceNode)}";
                })
                .ToArray();
            Console.WriteLine("NAV-PAINT: " + string.Join(" | ", navPaintDiagnostics));

            var navTextNodes = paintNodes
                .OfType<TextPaintNode>()
                .Where(n => n.FallbackText == "My browser" ||
                            n.FallbackText == "Guides" ||
                            n.FallbackText == "Detect my settings" ||
                            n.FallbackText == "Tools")
                .GroupBy(n => n.FallbackText)
                .Select(g => g.OrderBy(n => n.TextOrigin.X).First())
                .OrderBy(n => n.TextOrigin.X)
                .ToArray();

            Assert.True(
                navTextNodes.Length == 4,
                $"Expected exactly four unique nav text paint nodes. Found={navTextNodes.Length} Nodes={string.Join(", ", navTextNodes.Select(n => $"{n.FallbackText}@({n.TextOrigin.X:0.##},{n.TextOrigin.Y:0.##})"))}");
            Assert.True(
                navTextNodes[1].TextOrigin.X - navTextNodes[0].TextOrigin.X > 50f &&
                navTextNodes[2].TextOrigin.X - navTextNodes[1].TextOrigin.X > 70f &&
                navTextNodes[3].TextOrigin.X - navTextNodes[2].TextOrigin.X > 35f,
                $"Expected nav text origins to stay separated. Origins={string.Join(", ", navTextNodes.Select(n => $"{n.FallbackText}@{n.TextOrigin.X:0.##}"))}");

            var settingsCopyPaint = paintNodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => n.FallbackText != null &&
                                     n.FallbackText.StartsWith("Now that you know what browser you're using", StringComparison.Ordinal));

            Assert.NotNull(settingsCopyPaint);
            Assert.True(
                settingsCopyPaint.TextOrigin.Y >= paragraphBox.ContentBox.Top &&
                settingsCopyPaint.TextOrigin.Y <= paragraphBox.ContentBox.Bottom + 20f,
                $"Expected settings copy paint origin inside paragraph band. Paragraph={paragraphBox.ContentBox} Origin={settingsCopyPaint.TextOrigin}");

            var jsDetectionAnchor = ById(doc, "javascript-detection");
            var jsDetectionText = Assert.IsType<Text>(
                jsDetectionAnchor.Descendants().First(n => n is Text));
            var jsDetectionSpan = jsDetectionAnchor.Descendants().OfType<Element>().First(e => e.ClassList.Contains("detection-message"));
            Assert.True(renderContext.TryGetBox(jsDetectionText, out var jsDetectionTextBox), "Missing text box for JavaScript detection label.");

            var jsDetectionPaint = paintNodes
                .OfType<TextPaintNode>()
                .Where(n =>
                    string.Equals(n.FallbackText, "No - JavaScript is not enabled", StringComparison.Ordinal) ||
                    string.Equals(n.FallbackText, "Yes - JavaScript is enabled", StringComparison.Ordinal))
                .ToArray();

            Console.WriteLine("JS-DETECTION-PAINT: " + string.Join(" | ", jsDetectionPaint.Select(n => $"origin=({n.TextOrigin.X:0.##},{n.TextOrigin.Y:0.##}) bounds={n.Bounds} color={n.Color}")));
            Console.WriteLine($"JS-DETECTION-TEXTBOX: {jsDetectionTextBox.ContentBox}");
            Console.WriteLine($"JS-DETECTION-COLOR anchor={computed[jsDetectionAnchor].ForegroundColor} span={computed[jsDetectionSpan].ForegroundColor}");
            Assert.NotEmpty(jsDetectionPaint);
            Assert.Contains(
                jsDetectionPaint,
                n => n.Glyphs != null && n.Glyphs.Count > 0);
        }

        [Fact]
        public async Task Layout_Keeps_WimbVersionBadge_OnSingleLine()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        body{margin:0;font-family:-apple-system,BlinkMacSystemFont,'San Francisco','Segoe UI',Roboto,Ubuntu,'Helvetica Neue',Arial,sans-serif}
        .detection-primary{background:#428bae;color:#fff;text-align:center;padding:16px 0}
        .version-check{margin-top:8px}
        .detection-primary .version-check .judgment{font-size:1.4em;display:inline-block}
        .judgment{border-radius:0;border:0}
        .judgment-good{background:#3b9210;color:#fff}
        .judgment a,a.judgment{color:#fff;text-decoration:underline}
        .symbol{display:inline}
    </style>
</head>
<body>
    <div class='detection-primary'>
        <div class='version-check'>
            <div class='judgment judgment-good' id='wimb-badge'>
                <a href='/guides/how-to-update-your-browser/' id='wimb-badge-link'>
                    <span class='symbol'>✓</span> Your web browser is up to date
                </a>
            </div>
        </div>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://www.whatismybrowser.com/"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://www.whatismybrowser.com/"), null, viewportWidth: 1024, viewportHeight: 700);
            var body = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));

            var computer = new MinimalLayoutComputer(styles, 1024, 700, "https://www.whatismybrowser.com/");
            computer.Measure(body, new SKSize(1024, 700));
            computer.Arrange(body, new SKRect(0, 0, 1024, 700));

            var badge = ById(doc, "wimb-badge");
            var badgeLink = ById(doc, "wimb-badge-link");
            var badgeText = badgeLink.ChildNodes.OfType<Text>().First(t => !string.IsNullOrWhiteSpace(t.Data));

            var badgeBox = computer.GetBox(badge);
            var badgeTextBox = computer.GetBox(badgeText);

            Assert.NotNull(badgeBox);
            Assert.NotNull(badgeTextBox);
            Assert.NotNull(badgeTextBox.Lines);
            Assert.Single(badgeTextBox.Lines);
            Assert.True(
                badgeBox.ContentBox.Height < 45f,
                $"Expected compact single-line badge height, got {badgeBox.ContentBox.Height} with text box {badgeTextBox.ContentBox}.");
        }

        private static async Task<(Document Doc, System.Collections.Generic.IReadOnlyDictionary<Node, CssComputed> Styles)> ComputeStylesAsync()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        nav#site{display:flex;flex-direction:row;flex-wrap:nowrap;justify-content:center;align-content:flex-start;align-items:center}
        nav#site #top-logo-and-name{flex:0 1 auto;display:flex;align-items:center}
        nav#site ul#main-nav{display:flex;flex-wrap:nowrap;flex-grow:2;flex-shrink:1;flex-basis:auto;padding:0;margin:0}
        nav#site ul#main-nav li{display:flex;flex-wrap:nowrap;list-style-type:none}
        nav#site ul#main-nav li a{display:flex;justify-content:center;align-items:center;text-align:center}
        .corset{display:flex;max-width:1074px;margin:0 auto}
        .section-block.section-block-main-extra .content-block-main{width:728px;margin-right:10px}
        .section-block.section-block-main-extra .content-block-extra{width:336px;min-width:336px;max-width:336px}
        .content-block-main p{line-height:1.6;font-size:.875rem}
        .detection-secondary ul#your-browsers-settings{display:block;list-style-type:none;margin:0;width:100%;border-top:1px solid #ddd;margin-bottom:18px}
        .detection-secondary ul#your-browsers-settings li{display:flex;border-bottom:1px solid #ddd;padding-top:20px;padding-bottom:20px}
        .detection-secondary ul#your-browsers-settings li .setting-description,
        .detection-secondary ul#your-browsers-settings li .setting-detection,
        .detection-secondary ul#your-browsers-settings li .setting-change-guide{display:flex;flex-direction:column;justify-content:center}
        .detection-secondary ul#your-browsers-settings li .setting-description,
        .detection-secondary ul#your-browsers-settings li .setting-change-guide{width:156px;min-width:156px;max-width:156px}
        .detection-secondary ul#your-browsers-settings li .setting-detection{width:100%;margin-left:10px;margin-right:10px;min-height:56px}
    </style>
</head>
<body class='section_homepage page lang-en'>
    <nav id='site'>
        <div id='top-logo-and-name'>
            <div id='top-name'><span id='top-name-site'>WhatIsMyBrowser.com</span></div>
        </div>
        <ul id='main-nav'>
            <li><a href='/'>My browser</a></li>
            <li><a href='/guides/'>Guides</a></li>
            <li><a href='/detect/'>Detect my settings</a></li>
            <li><a href='/developers/tools/'>Tools</a></li>
        </ul>
    </nav>
    <section class='section-block section-block-main-extra' id='section-readout-secondary'>
        <div class='corset'>
            <div class='content-block-main detection-secondary' id='readout-secondary'>
                <p id='settings-copy'>Now that you know what browser you're using, here is a list of your browser's settings.</p>
                <p id='settings-copy-two'>This information can be helpful when you're trying to solve problems using the internet.</p>
                <ul id='your-browsers-settings'>
                    <li id='your-browsers-settings-javascript'>
                        <div class='setting-description'><a href='/detect/is-javascript-enabled/'>Is JavaScript enabled?</a></div>
                        <div class='setting-detection judgment judgment-brand'><a href='/detect/is-javascript-enabled/' id='javascript-detection'>Yes - JavaScript is enabled</a></div>
                        <div class='setting-change-guide'><a href='/guides/how-to-enable-javascript/' class='further-info-link'>How to enable JavaScript</a></div>
                    </li>
                </ul>
            </div>
            <div class='content-block-extra'></div>
        </div>
    </section>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://www.whatismybrowser.com/"), null);
            return (doc, styles);
        }

        private static string DescribeAncestorPath(Node? node)
        {
            var parts = new List<string>();
            var current = node;
            int guard = 0;
            while (current != null && guard++ < 8)
            {
                if (current is Element el)
                {
                    parts.Add($"{el.TagName}#{el.Id}.{el.ClassName}");
                }
                else
                {
                    parts.Add(current.NodeName);
                }

                current = current.ParentNode;
            }

            return string.Join(" <- ", parts);
        }

        private static Element ById(Document doc, string id)
        {
            return doc.Descendants().OfType<Element>().First(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }

        private static Element FirstByClass(Element root, string className)
        {
            return root.Descendants().OfType<Element>().First(e => e.ClassList.Contains(className));
        }

        private static void CollectPaintNodes(System.Collections.Generic.IReadOnlyList<PaintNodeBase> nodes, System.Collections.Generic.List<PaintNodeBase> buffer)
        {
            if (nodes == null)
            {
                return;
            }

            foreach (var node in nodes)
            {
                if (node == null)
                {
                    continue;
                }

                buffer.Add(node);
                CollectPaintNodes(node.Children, buffer);
            }
        }
    }
}
