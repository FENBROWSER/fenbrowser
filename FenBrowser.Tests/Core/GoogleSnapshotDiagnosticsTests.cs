using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class GoogleSnapshotDiagnosticsTests
    {
        [Fact]
        public async Task LatestGoogleSnapshot_MainSearchChrome_HasLayoutAndPaintCoverage()
        {
            string snapshotPath = GetLatestSnapshotPaths(1, requiredUrlPrefix: "https://www.google.com/").FirstOrDefault();

            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                return;
            }

            string html = await File.ReadAllTextAsync(snapshotPath);
            var baseUri = new Uri("https://www.google.com/");

            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));

            var computed = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1600, viewportHeight: 900);
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

            Element searchShell = FindFirst(root, e => HasClass(e, "RNNXgb"));
            Element searchShellInnerBand = FindFirst(root, e => HasClass(e, "SDkEP"));
            Element searchTextWrapper = FindFirst(root, e => HasClass(e, "a4bIc"));
            Element searchTextArea = FindFirst(root, e => string.Equals(e.GetAttribute("id"), "APjFqb", StringComparison.Ordinal));
            Element aiModeButton = FindFirst(root, e => HasClass(e, "plR5qb"));
            Element searchButtonsBand = FindFirst(root, e => HasClass(e, "lJ9FBc") && HasClass(e, "FPdoLc"));
            Element searchFormWrapper = FindFirst(root, e => HasClass(e, "A8SBwf"));
            Element mainColumn = FindFirst(root, e => HasClass(e, "L3eUgb"));
            Element topNav = FindFirst(root, e => HasClass(e, "Ne6nSd"));
            Element appsButton = FindFirst(root, e => string.Equals(e.GetAttribute("aria-label"), "Google apps", StringComparison.OrdinalIgnoreCase));
            Element signInButton = FindFirst(root, e => string.Equals(e.GetAttribute("aria-label"), "Sign in", StringComparison.OrdinalIgnoreCase));
            Element languagesPromptContainer = FindFirst(root, e => string.Equals(e.GetAttribute("id"), "SIvCob", StringComparison.OrdinalIgnoreCase))
                ?? FindFirst(root, e =>
                    e.Descendants().OfType<Text>().Any(t => (t.Data?.IndexOf("Google offered in", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) &&
                    e.Descendants().OfType<Element>().Any(IsAnchorWithText));
            Element languageOfferLink = languagesPromptContainer?
                .ChildNodes
                .OfType<Element>()
                .FirstOrDefault(IsAnchorWithText)
                ?? languagesPromptContainer?
                    .Descendants()
                    .OfType<Element>()
                    .FirstOrDefault(IsAnchorWithText);
            Element doodleBand = FindFirst(root, e => HasClass(e, "LS8OJ"));
            Element formBand = FindFirst(root, e => HasClass(e, "ikrT4e"));
            Element nativeForm = FindFirst(root, e => string.Equals(e.TagName, "FORM", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(searchShell);
            Assert.NotNull(searchTextWrapper);
            Assert.NotNull(searchTextArea);
            Assert.NotNull(aiModeButton);
            Assert.NotNull(searchButtonsBand);
            Assert.NotNull(searchShellInnerBand);
            Assert.NotNull(appsButton);
            Assert.NotNull(signInButton);
            Assert.NotNull(languageOfferLink);

            Assert.True(renderer.LastLayout.TryGetElementRect(searchShell, out var searchShellRect), "Missing layout rect for .RNNXgb");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchTextWrapper, out var searchTextWrapperRect), "Missing layout rect for .a4bIc");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchTextArea, out var searchTextAreaRect), "Missing layout rect for #APjFqb");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchShellInnerBand, out var searchShellInnerBandRect), "Missing layout rect for .SDkEP");
            Assert.True(renderer.LastLayout.TryGetElementRect(aiModeButton, out var aiModeButtonRect), "Missing layout rect for .plR5qb");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchButtonsBand, out var searchButtonsBandRect), "Missing layout rect for .lJ9FBc");
            Assert.True(renderer.LastLayout.TryGetElementRect(topNav, out var topNavRect), "Missing layout rect for .Ne6nSd");
            Assert.True(renderer.LastLayout.TryGetElementRect(appsButton, out var appsButtonRect), "Missing layout rect for Google apps button");
            Assert.True(renderer.LastLayout.TryGetElementRect(signInButton, out var signInButtonRect), "Missing layout rect for Sign in button");
            Assert.True(computed.TryGetValue(signInButton, out var signInStyle), "Missing computed style for Sign in button");
            computed.TryGetValue(searchTextArea, out var searchTextAreaStyle);
            computed.TryGetValue(searchTextWrapper, out var searchTextWrapperStyle);
            computed.TryGetValue(searchShellInnerBand, out var searchShellInnerBandStyle);
            int searchTextAreaTextLength = string.Concat(searchTextArea.Descendants().OfType<Text>().Select(t => t.Data ?? string.Empty)).Length;
            string searchStyleContext =
                $"textarea-style={DescribeStyleSizing(searchTextAreaStyle)} " +
                $"textWrap-style={DescribeStyleSizing(searchTextWrapperStyle)} " +
                $"shellInner-style={DescribeStyleSizing(searchShellInnerBandStyle)} " +
                $"textarea-map={DescribeStyleMapSizing(searchTextAreaStyle)} " +
                $"textWrap-map={DescribeStyleMapSizing(searchTextWrapperStyle)} " +
                $"shellInner-map={DescribeStyleMapSizing(searchShellInnerBandStyle)}";

            string layoutContext =
                $"main={DescribeRect(renderer.LastLayout, mainColumn)} nav={DescribeRect(renderer.LastLayout, topNav)} " +
                $"doodle={DescribeRect(renderer.LastLayout, doodleBand)} form={DescribeRect(renderer.LastLayout, formBand)} " +
                $"wrapper={DescribeRect(renderer.LastLayout, searchFormWrapper)} shell={DescribeRect(searchShellRect)} shellInner={DescribeRect(searchShellInnerBandRect)} " +
                $"textWrap={DescribeRect(searchTextWrapperRect)} textArea={DescribeRect(searchTextAreaRect)} textAreaTextLen={searchTextAreaTextLength} " +
                $"buttons={DescribeRect(searchButtonsBandRect)} {searchStyleContext}";

            var renderContext = renderer.CreateRenderContext();
            string rawBoxContext =
                $" raw-form={DescribeBox(renderContext.Boxes, formBand)} raw-wrapper={DescribeBox(renderContext.Boxes, searchFormWrapper)} raw-shell={DescribeBox(renderContext.Boxes, searchShell)} " +
                $"raw-buttons={DescribeBox(renderContext.Boxes, searchButtonsBand)}";
            string ancestryContext =
                $" shell-chain={DescribeAncestorChain(renderer.LastLayout, searchShell)}" +
                $" wrapper-chain={DescribeAncestorChain(renderer.LastLayout, searchFormWrapper)}";
            string childContext =
                $" form-children={DescribeImmediateChildren(renderer.LastLayout, formBand)}" +
                $" native-form-children={DescribeImmediateChildren(renderer.LastLayout, nativeForm)}" +
                $" wrapper-children={DescribeImmediateChildren(renderer.LastLayout, searchFormWrapper)}" +
                $" shell-children={DescribeImmediateChildren(renderer.LastLayout, searchShell)}" +
                $" shell-inner-children={DescribeImmediateChildren(renderer.LastLayout, searchShellInnerBand)}";

            Assert.True(searchShellRect.Width >= 400f, $"Expected .RNNXgb width >= 400, got {searchShellRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchShellRect.Height >= 48f, $"Expected .RNNXgb height >= 48, got {searchShellRect.Height}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchShellRect.Top < 450f, $"Expected .RNNXgb to remain within the first half of the viewport, got top={searchShellRect.Top}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchTextWrapperRect.Width >= 220f, $"Expected .a4bIc width >= 220, got {searchTextWrapperRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchTextAreaRect.Width >= 180f, $"Expected #APjFqb width >= 180, got {searchTextAreaRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(aiModeButtonRect.Width >= 60f, $"Expected .plR5qb width >= 60, got {aiModeButtonRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchButtonsBandRect.Height >= 30f, $"Expected .lJ9FBc height >= 30, got {searchButtonsBandRect.Height}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchButtonsBandRect.Top < 520f, $"Expected visible .FPdoLc.lJ9FBc within viewport, got top={searchButtonsBandRect.Top}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            float navBandTop = Math.Max(0f, topNavRect.Top - 1f);
            float navBandBottom = topNavRect.Bottom + Math.Max(appsButtonRect.Height, signInButtonRect.Height);
            Assert.True(appsButtonRect.Top >= Math.Max(0f, topNavRect.Top - 1f), $"Expected Google apps button to stay within nav top band, got {DescribeRect(appsButtonRect)}. {layoutContext}");
            Assert.True(signInButtonRect.Top >= Math.Max(0f, topNavRect.Top - 1f), $"Expected Sign in button to stay within nav top band, got {DescribeRect(signInButtonRect)}. {layoutContext}");
            Assert.InRange(appsButtonRect.Top, navBandTop, navBandBottom);
            Assert.InRange(signInButtonRect.Top, navBandTop, navBandBottom);
            Assert.InRange(appsButtonRect.Height, 24f, 64f);
            Assert.InRange(appsButtonRect.Width, 24f, 64f);
            Assert.InRange(signInButtonRect.Height, 36f, 72f);
            Assert.InRange(signInButtonRect.Width, 80f, 160f);
            Assert.True(aiModeButtonRect.Top > topNavRect.Bottom + 8f, $"Expected AI mode button to remain below top nav band. ai={DescribeRect(aiModeButtonRect)} nav={DescribeRect(topNavRect)}. {layoutContext}");
            Assert.True(searchShellRect.Top > topNavRect.Bottom + 8f, $"Expected search shell to remain below top nav band. shell={DescribeRect(searchShellRect)} nav={DescribeRect(topNavRect)}. {layoutContext}");
            float shellCenterY = searchShellRect.Top + (searchShellRect.Height * 0.5f);
            float textCenterY = searchTextAreaRect.Top + (searchTextAreaRect.Height * 0.5f);
            Assert.InRange(Math.Abs(textCenterY - shellCenterY), 0f, 12f);

            string signInStyleContext = BuildSignInStyleContext(signInStyle, signInButtonRect);
            Assert.Equal("inline-block", signInStyle.Display);
            Assert.Equal("border-box", signInStyle.BoxSizing);
            Assert.Equal("relative", signInStyle.Position);
            Assert.Equal(14d, signInStyle.FontSize.GetValueOrDefault(), 1);
            Assert.Equal(40d, signInStyle.MinHeight.GetValueOrDefault(), 1);
            Assert.Equal(85d, signInStyle.MinWidth.GetValueOrDefault(), 1);
            Assert.Equal(10d, signInStyle.Padding.Top, 1);
            Assert.Equal(10d, signInStyle.Padding.Bottom, 1);
            Assert.Equal(12d, signInStyle.Padding.Left, 1);
            Assert.Equal(12d, signInStyle.Padding.Right, 1);
            Assert.Equal(18d, signInStyle.LineHeight.GetValueOrDefault(), 1);
            Assert.Equal(100d, signInStyle.BorderRadius.TopLeft.Value, 1);
            Assert.Equal(100d, signInStyle.BorderRadius.TopRight.Value, 1);
            Assert.Equal(100d, signInStyle.BorderRadius.BottomRight.Value, 1);
            Assert.Equal(100d, signInStyle.BorderRadius.BottomLeft.Value, 1);

            var topNavInteractiveRects = root
                .Descendants()
                .OfType<Element>()
                .Where(e => topNav.Contains(e))
                .Select(e =>
                {
                    bool hasRect = renderer.LastLayout.TryGetElementRect(e, out var rect);
                    return (hasRect, rect, element: e);
                })
                .Where(x => x.hasRect &&
                            x.rect.Width >= 20f &&
                            x.rect.Height >= 20f &&
                            x.rect.Top < 120f &&
                            x.rect.Left >= 0f)
                .Where(x =>
                {
                    float overlap = Math.Min(x.rect.Bottom, topNavRect.Bottom) - Math.Max(x.rect.Top, topNavRect.Top);
                    return overlap >= (x.rect.Height * 0.5f);
                })
                .Select(x => x.rect)
                .ToList();

            Assert.True(topNavInteractiveRects.Count >= 5, $"Expected at least 5 top-nav interactive boxes, got {topNavInteractiveRects.Count}. {layoutContext}");
            Assert.All(topNavInteractiveRects, rect => Assert.True(rect.Top >= 0f, $"Expected top-nav boxes to stay within viewport top band, got {DescribeRect(rect)}. {layoutContext}"));
            Assert.True(topNavInteractiveRects.Max(r => r.Right) > 1400f, $"Expected right-aligned header controls to remain near viewport edge. MaxRight={topNavInteractiveRects.Max(r => r.Right)}. {layoutContext}");

            var topNavDescendantRects = root
                .Descendants()
                .OfType<Element>()
                .Where(e => topNav.Contains(e))
                .Select(e =>
                {
                    bool hasRect = renderer.LastLayout.TryGetElementRect(e, out var rect);
                    return (hasRect, rect, e);
                })
                .Where(x => x.hasRect && x.rect.Width >= 20f && x.rect.Height >= 20f)
                .Select(x => x.rect)
                .ToList();
            Assert.All(
                topNavDescendantRects,
                rect => Assert.True(
                    rect.Top >= -1f,
                    $"Expected top-nav descendants to avoid negative-top clipping. bad={DescribeRect(rect)} nav={DescribeRect(topNavRect)} {signInStyleContext}"));

            var paintNodes = new List<PaintNodeBase>();
            CollectNodes(renderContext.PaintTreeRoots, paintNodes);

            int shellPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchShell));
            int wrapperPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextWrapper));
            int textAreaPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextArea));
            int aiModePaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, aiModeButton));
            int buttonsBandPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchButtonsBand));
            var topBandArtifactTextNodes = FindTopBandArtifactTextNodes(paintNodes, navBandTop, navBandBottom);
            var shellSampleRect = ToSampleRect(searchShellRect, bitmap.Width, bitmap.Height, inset: 2, expand: 4);
            int shellVisiblePixels = CountNonWhitePixels(bitmap, shellSampleRect);
            string paintContext =
                $" shell-paint={DescribePaintNodes(paintNodes, searchShell)}" +
                $" wrapper-paint={DescribePaintNodes(paintNodes, searchTextWrapper)}" +
                $" textarea-paint={DescribePaintNodes(paintNodes, searchTextArea)}" +
                $" ai-paint={DescribePaintNodes(paintNodes, aiModeButton)}" +
                $" buttons-paint={DescribePaintNodes(paintNodes, searchButtonsBand)}" +
                $" roots={DescribeRootNodes(renderContext.PaintTreeRoots)}" +
                $" watchdog={renderer.LastFrameWatchdogTriggered}:{renderer.LastFrameWatchdogReason}" +
                $" shell-sample={DescribeRect(ElementGeometry.FromSKRect(shellSampleRect))}" +
                $" shell-visible={shellVisiblePixels}" +
                $" shell-region={DescribeIntersectingNodes(paintNodes, shellSampleRect)}" +
                $" shell-path={DescribePaintPath(renderContext.PaintTreeRoots, searchShell)}";

            Assert.True(shellPaintCount > 0, $"Expected paint nodes for .RNNXgb.{paintContext}");
            Assert.True(textAreaPaintCount > 0, $"Expected paint nodes for #APjFqb.{paintContext}");
            Assert.True(aiModePaintCount > 0, $"Expected paint nodes for .plR5qb.{paintContext}");
            Assert.True(wrapperPaintCount > 0 || textAreaPaintCount > 0, $"Expected search wrapper descendants to materialize into paint coverage.{paintContext}");
            Assert.True(buttonsBandPaintCount > 0 || paintNodes.Any(n => searchButtonsBand.Contains(n.SourceNode as Node)), $"Expected .lJ9FBc descendants to materialize into paint coverage.{paintContext}");
            Assert.True(
                topBandArtifactTextNodes.Count == 0,
                $"Expected no leaked top-band artifact text nodes. found={string.Join(" | ", topBandArtifactTextNodes.Take(6).Select(DescribePaintNode))}. {paintContext}");
            Assert.True(shellVisiblePixels > 250, $"Expected the rendered search shell region to contain visible non-white pixels.{paintContext}");
            Assert.True(TryResolveRenderedTextColor(paintNodes, languageOfferLink, out var languageOfferColor), $"Missing rendered text color for language offer link. {paintContext}");
            Assert.False(IsNearBlack(languageOfferColor), $"Expected language offer link color to not be near black, got {DescribeColor(languageOfferColor)}. {paintContext}");
            Assert.True(
                languageOfferColor.Blue >= languageOfferColor.Red + 15 &&
                languageOfferColor.Blue >= languageOfferColor.Green + 15,
                $"Expected language offer link color to remain blue-toned, got {DescribeColor(languageOfferColor)}. {paintContext}");
        }

        [Fact]
        public async Task LatestGoogleSnapshots_FirstLoadAndRefresh_KeyChromeRegionsRemainStable()
        {
            var snapshots = GetLatestSnapshotPaths(2, requiredUrlPrefix: "https://www.google.com/");
            if (snapshots.Count < 2)
            {
                return;
            }

            GoogleSnapshotMetrics latest = await AnalyzeSnapshotAsync(snapshots[0]);
            GoogleSnapshotMetrics previous = await AnalyzeSnapshotAsync(snapshots[1]);

            Assert.True(previous.HasSignInText && latest.HasSignInText, $"Expected both snapshots to include Sign in text. previous={previous.SnapshotPath} latest={latest.SnapshotPath}");
            Assert.True(previous.HasLanguagesPrompt && latest.HasLanguagesPrompt, $"Expected both snapshots to include Google offered in prompt. previous={previous.SnapshotPath} latest={latest.SnapshotPath}");
            Assert.True(previous.TopNavInteractiveCount >= 5 && latest.TopNavInteractiveCount >= 5, $"Expected >=5 interactive top-nav boxes on both snapshots. previous={previous.TopNavInteractiveCount} latest={latest.TopNavInteractiveCount}");
            Assert.True(previous.TopNavMaxRight > 1400f && latest.TopNavMaxRight > 1400f, $"Expected top-nav right alignment near viewport edge. previous={previous.TopNavMaxRight:0.##} latest={latest.TopNavMaxRight:0.##}");
            Assert.True(previous.ShellVisiblePixels > 200 && latest.ShellVisiblePixels > 200, $"Expected visible search shell paint in both snapshots. previous={previous.ShellVisiblePixels} latest={latest.ShellVisiblePixels}");

            AssertRectClose(previous.SearchShellRect, latest.SearchShellRect, maxDeltaX: 24f, maxDeltaY: 30f, maxDeltaW: 40f, maxDeltaH: 18f, "search shell");
            AssertRectClose(previous.AiModeRect, latest.AiModeRect, maxDeltaX: 24f, maxDeltaY: 20f, maxDeltaW: 40f, maxDeltaH: 16f, "AI mode button");
            AssertRectClose(previous.SearchButtonsBandRect, latest.SearchButtonsBandRect, maxDeltaX: 24f, maxDeltaY: 28f, maxDeltaW: 48f, maxDeltaH: 16f, "search buttons band");
            AssertRectClose(previous.TopNavRect, latest.TopNavRect, maxDeltaX: 24f, maxDeltaY: 20f, maxDeltaW: 36f, maxDeltaH: 16f, "top nav band");

            double shellRatio = previous.ShellVisiblePixels == 0
                ? 0d
                : (double)latest.ShellVisiblePixels / previous.ShellVisiblePixels;
            Assert.InRange(shellRatio, 0.55d, 1.80d);
        }

        private static Element FindFirst(Element root, Func<Element, bool> predicate)
        {
            return root.Descendants().OfType<Element>().FirstOrDefault(predicate);
        }

        private static IReadOnlyList<string> GetLatestSnapshotPaths(int count, string requiredUrlPrefix = null)
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string[] candidateLogDirs =
            {
                Path.Combine(repoRoot, "logs"),
                Path.Combine(repoRoot, "FenBrowser.Host", "bin", "Debug", "net8.0", "logs")
            };

            return candidateLogDirs
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "engine_source_*.html"))
                .Where(path => SnapshotMatchesUrl(path, requiredUrlPrefix))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(Math.Max(1, count))
                .ToArray();
        }

        private static bool SnapshotMatchesUrl(string path, string requiredUrlPrefix)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(requiredUrlPrefix))
            {
                return true;
            }

            try
            {
                foreach (string line in File.ReadLines(path).Take(2))
                {
                    if (line.IndexOf(requiredUrlPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static async Task<GoogleSnapshotMetrics> AnalyzeSnapshotAsync(string snapshotPath)
        {
            string html = await File.ReadAllTextAsync(snapshotPath);
            var baseUri = new Uri("https://www.google.com/");

            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var computed = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1600, viewportHeight: 900);

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

            Element searchShell = FindFirst(root, e => HasClass(e, "RNNXgb"));
            Element aiModeButton = FindFirst(root, e => HasClass(e, "plR5qb"));
            Element searchButtonsBand = FindFirst(root, e => HasClass(e, "lJ9FBc") && HasClass(e, "FPdoLc"));
            Element topNav = FindFirst(root, e => HasClass(e, "Ne6nSd"));

            Assert.NotNull(searchShell);
            Assert.NotNull(aiModeButton);
            Assert.NotNull(searchButtonsBand);
            Assert.NotNull(topNav);

            Assert.True(renderer.LastLayout.TryGetElementRect(searchShell, out var searchShellRect), $"Missing layout rect for .RNNXgb ({snapshotPath})");
            Assert.True(renderer.LastLayout.TryGetElementRect(aiModeButton, out var aiModeRect), $"Missing layout rect for .plR5qb ({snapshotPath})");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchButtonsBand, out var searchButtonsBandRect), $"Missing layout rect for .lJ9FBc.FPdoLc ({snapshotPath})");
            Assert.True(renderer.LastLayout.TryGetElementRect(topNav, out var topNavRect), $"Missing layout rect for .Ne6nSd ({snapshotPath})");

            var topNavInteractiveRects = root
                .Descendants()
                .OfType<Element>()
                .Where(e => topNav.Contains(e))
                .Select(e =>
                {
                    bool hasRect = renderer.LastLayout.TryGetElementRect(e, out var rect);
                    return (hasRect, rect);
                })
                .Where(x => x.hasRect &&
                            x.rect.Width >= 20f &&
                            x.rect.Height >= 20f &&
                            x.rect.Top < 120f &&
                            x.rect.Left >= 0f)
                .Where(x =>
                {
                    float overlap = Math.Min(x.rect.Bottom, topNavRect.Bottom) - Math.Max(x.rect.Top, topNavRect.Top);
                    return overlap >= (x.rect.Height * 0.5f);
                })
                .Select(x => x.rect)
                .ToList();

            var shellSampleRect = ToSampleRect(searchShellRect, bitmap.Width, bitmap.Height, inset: 2, expand: 4);
            int shellVisiblePixels = CountNonWhitePixels(bitmap, shellSampleRect);
            bool hasSignInText = root.Descendants().OfType<Text>()
                .Any(t => string.Equals(t.Data?.Trim(), "Sign in", StringComparison.OrdinalIgnoreCase));
            bool hasLanguagesPrompt = root.Descendants().OfType<Text>()
                .Any(t => (t.Data?.IndexOf("Google offered in", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

            return new GoogleSnapshotMetrics
            {
                SnapshotPath = snapshotPath,
                SearchShellRect = searchShellRect,
                AiModeRect = aiModeRect,
                SearchButtonsBandRect = searchButtonsBandRect,
                TopNavRect = topNavRect,
                TopNavInteractiveCount = topNavInteractiveRects.Count,
                TopNavMaxRight = topNavInteractiveRects.Count > 0 ? topNavInteractiveRects.Max(r => r.Right) : 0f,
                ShellVisiblePixels = shellVisiblePixels,
                HasSignInText = hasSignInText,
                HasLanguagesPrompt = hasLanguagesPrompt
            };
        }

        private static void AssertRectClose(
            ElementGeometry previous,
            ElementGeometry latest,
            float maxDeltaX,
            float maxDeltaY,
            float maxDeltaW,
            float maxDeltaH,
            string region)
        {
            Assert.InRange(Math.Abs(latest.Left - previous.Left), 0f, maxDeltaX);
            Assert.InRange(Math.Abs(latest.Top - previous.Top), 0f, maxDeltaY);
            Assert.InRange(Math.Abs(latest.Width - previous.Width), 0f, maxDeltaW);
            Assert.InRange(Math.Abs(latest.Height - previous.Height), 0f, maxDeltaH);
            Assert.True(latest.Width > 0f && latest.Height > 0f, $"Expected non-empty {region} rect in latest snapshot: {DescribeRect(latest)}");
            Assert.True(previous.Width > 0f && previous.Height > 0f, $"Expected non-empty {region} rect in previous snapshot: {DescribeRect(previous)}");
        }

        private sealed class GoogleSnapshotMetrics
        {
            public string SnapshotPath { get; init; }
            public ElementGeometry SearchShellRect { get; init; }
            public ElementGeometry AiModeRect { get; init; }
            public ElementGeometry SearchButtonsBandRect { get; init; }
            public ElementGeometry TopNavRect { get; init; }
            public int TopNavInteractiveCount { get; init; }
            public float TopNavMaxRight { get; init; }
            public int ShellVisiblePixels { get; init; }
            public bool HasSignInText { get; init; }
            public bool HasLanguagesPrompt { get; init; }
        }

        private static string BuildSignInStyleContext(CssComputed style, ElementGeometry rect)
        {
            if (style == null)
            {
                return $"signInRect={DescribeRect(rect)} style=(null)";
            }

            static string F(double? v) => v.HasValue ? v.Value.ToString("0.##") : "null";

            return
                $"signInRect={DescribeRect(rect)} " +
                $"display={style.Display} width={F(style.Width)} height={F(style.Height)} min-width={F(style.MinWidth)} min-height={F(style.MinHeight)} " +
                $"padding-top={style.Padding.Top:0.##} padding-bottom={style.Padding.Bottom:0.##} padding-left={style.Padding.Left:0.##} padding-right={style.Padding.Right:0.##} " +
                $"line-height={F(style.LineHeight)} font-size={F(style.FontSize)} " +
                $"border-radius={style.BorderRadius.TopLeft.Value:0.##}/{style.BorderRadius.TopRight.Value:0.##}/{style.BorderRadius.BottomRight.Value:0.##}/{style.BorderRadius.BottomLeft.Value:0.##} " +
                $"box-sizing={style.BoxSizing} align-items={style.AlignItems} justify-content={style.JustifyContent}";
        }

        private static string DescribeStyleSizing(CssComputed style)
        {
            if (style == null)
            {
                return "(null)";
            }

            static string F(double? v) => v.HasValue ? v.Value.ToString("0.##") : "null";
            return
                $"display={style.Display} position={style.Position} width={F(style.Width)} widthPct={F(style.WidthPercent)} " +
                $"height={F(style.Height)} heightPct={F(style.HeightPercent)} minH={F(style.MinHeight)} minHPct={F(style.MinHeightPercent)} " +
                $"maxH={F(style.MaxHeight)} maxHPct={F(style.MaxHeightPercent)} lineHeight={F(style.LineHeight)} " +
                $"flexBasis={F(style.FlexBasis)} flexGrow={F(style.FlexGrow)} flexShrink={F(style.FlexShrink)}";
        }

        private static string DescribeStyleMapSizing(CssComputed style)
        {
            if (style?.Map == null)
            {
                return "(null)";
            }

            static string Read(IDictionary<string, string> map, string key)
            {
                return map.TryGetValue(key, out var value) ? value : "-";
            }

            return
                $"h={Read(style.Map, "height")} minH={Read(style.Map, "min-height")} maxH={Read(style.Map, "max-height")} " +
                $"inline={Read(style.Map, "inline-size")} block={Read(style.Map, "block-size")} " +
                $"flex={Read(style.Map, "flex")} basis={Read(style.Map, "flex-basis")} " +
                $"grow={Read(style.Map, "flex-grow")} shrink={Read(style.Map, "flex-shrink")}";
        }

        private static bool HasClass(Element element, string className)
        {
            return element?.ClassList?.Contains(className) == true;
        }

        private static bool IsAnchorWithText(Element element)
        {
            if (element == null || !string.Equals(element.TagName, "A", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string text = string.Concat(element.Descendants().OfType<Text>().Select(t => t.Data ?? string.Empty)).Trim();
            return text.Length > 0;
        }

        private static bool TryResolveRenderedTextColor(IEnumerable<PaintNodeBase> nodes, Element element, out SKColor color)
        {
            color = default;
            if (nodes == null || element == null)
            {
                return false;
            }

            var linkTextPaint = nodes
                .OfType<TextPaintNode>()
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.FallbackText))
                .FirstOrDefault(n =>
                    (n.SourceNode is Text textNode && element.Contains(textNode)) ||
                    (n.SourceNode is Element sourceElement && ReferenceEquals(sourceElement, element)));

            if (linkTextPaint != null)
            {
                color = linkTextPaint.Color;
                return true;
            }

            return false;
        }

        private static bool IsNearBlack(SKColor color)
        {
            return color.Red <= 48 && color.Green <= 48 && color.Blue <= 48;
        }

        private static string DescribeRect(LayoutResult layout, Element element)
        {
            if (layout == null || element == null)
            {
                return "missing";
            }

            return layout.TryGetElementRect(element, out var geometry)
                ? DescribeRect(geometry)
                : "no-rect";
        }

        private static string DescribeRect(ElementGeometry geometry)
        {
            return $"[{geometry.Left:0.##},{geometry.Top:0.##},{geometry.Width:0.##},{geometry.Height:0.##}]";
        }

        private static string DescribeBox(Dictionary<Node, BoxModel> boxes, Element element)
        {
            if (boxes == null || element == null)
            {
                return "missing";
            }

            return boxes.TryGetValue(element, out var box) && box != null
                ? DescribeRect(ElementGeometry.FromSKRect(box.BorderBox))
                : "no-box";
        }

        private static string DescribeAncestorChain(LayoutResult layout, Element element)
        {
            if (layout == null || element == null)
            {
                return "missing";
            }

            var nodes = new List<string>();
            for (Element current = element; current != null; current = current.ParentElement)
            {
                string rect = DescribeRect(layout, current);
                string tag = current.TagName?.ToLowerInvariant() ?? "?";
                string id = current.GetAttribute("id");
                string classes = current.ClassList != null
                    ? string.Join(".", current.ClassList)
                    : string.Empty;
                string display = current.ComputedStyle?.Display ?? "(default)";
                string position = current.ComputedStyle?.Position ?? "static";
                nodes.Add($"{tag}#{id}.{classes}[{display}/{position}]={rect}");
            }

            return string.Join(" <= ", nodes);
        }

        private static string DescribeImmediateChildren(LayoutResult layout, Element element)
        {
            if (layout == null || element == null)
            {
                return "missing";
            }

            var children = new List<string>();
            foreach (var child in element.ChildNodes.OfType<Element>())
            {
                string tag = child.TagName?.ToLowerInvariant() ?? "?";
                string id = child.GetAttribute("id");
                string classes = child.ClassList != null
                    ? string.Join(".", child.ClassList)
                    : string.Empty;
                string rect = DescribeRect(layout, child);
                string display = child.ComputedStyle?.Display ?? "(default)";
                children.Add($"{tag}#{id}.{classes}[{display}]={rect}");
            }

            return children.Count > 0
                ? string.Join(" | ", children)
                : "(none)";
        }

        private static void CollectNodes(IReadOnlyList<PaintNodeBase> nodes, List<PaintNodeBase> buffer)
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
                CollectNodes(node.Children, buffer);
            }
        }

        private static string DescribePaintNodes(IEnumerable<PaintNodeBase> nodes, Node source)
        {
            if (nodes == null || source == null)
            {
                return "missing";
            }

            var matches = nodes
                .Where(n => ReferenceEquals(n.SourceNode, source))
                .Take(6)
                .Select(DescribePaintNode)
                .ToList();

            return matches.Count > 0
                ? string.Join("|", matches)
                : "none";
        }

        private static string DescribeRootNodes(IReadOnlyList<PaintNodeBase> roots)
        {
            if (roots == null)
            {
                return "missing";
            }

            return string.Join(
                " | ",
                roots.Take(10).Select((node, index) => $"{index}:{DescribePaintNode(node)}"));
        }

        private static string DescribeIntersectingNodes(IEnumerable<PaintNodeBase> nodes, SKRect area)
        {
            if (nodes == null)
            {
                return "missing";
            }

            var matches = nodes
                .Where(n => n != null && n.Bounds.IntersectsWith(area))
                .Take(12)
                .Select(DescribePaintNode)
                .ToList();

            return matches.Count > 0
                ? string.Join(" | ", matches)
                : "none";
        }

        private static string DescribePaintPath(IReadOnlyList<PaintNodeBase> roots, Node source)
        {
            if (roots == null || source == null)
            {
                return "missing";
            }

            var path = new List<PaintNodeBase>();
            return TryFindPaintPath(roots, source, path)
                ? string.Join(" <= ", path.Select(DescribePaintNode))
                : "none";
        }

        private static string DescribePaintNode(PaintNodeBase node)
        {
            if (node == null)
            {
                return "null";
            }

            string bounds = DescribeRect(ElementGeometry.FromSKRect(node.Bounds));
            return node switch
            {
                BackgroundPaintNode bg => $"{node.GetType().Name}{bounds}[bg={DescribeColor(bg.Color)}]",
                BorderPaintNode border => $"{node.GetType().Name}{bounds}[widths={DescribeWidths(border.Widths)} colors={DescribeColors(border.Colors)}]",
                TextPaintNode text => $"{node.GetType().Name}{bounds}[color={DescribeColor(text.Color)} text={TrimText(text.FallbackText)}]",
                ImagePaintNode image => $"{node.GetType().Name}{bounds}[bitmap={(image.Bitmap != null ? $"{image.Bitmap.Width}x{image.Bitmap.Height}" : "null")}]",
                BoxShadowPaintNode shadow => $"{node.GetType().Name}{bounds}[shadow={DescribeColor(shadow.Color)} blur={shadow.Blur:0.##} spread={shadow.Spread:0.##}]",
                ClipPaintNode clip => $"{node.GetType().Name}{bounds}[clip={DescribeRect(ElementGeometry.FromSKRect(clip.ClipRect ?? SKRect.Empty))} children={clip.Children?.Count ?? 0}]",
                _ => $"{node.GetType().Name}{bounds}"
            };
        }

        private static string DescribeColor(SKColor? color)
        {
            if (!color.HasValue)
            {
                return "null";
            }

            var value = color.Value;
            return $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}{value.Alpha:X2}";
        }

        private static string DescribeColors(IReadOnlyList<SKColor> colors)
        {
            if (colors == null)
            {
                return "null";
            }

            return string.Join("/", colors.Select(c => DescribeColor(c)));
        }

        private static string DescribeWidths(IReadOnlyList<float> widths)
        {
            if (widths == null)
            {
                return "null";
            }

            return string.Join("/", widths.Select(w => w.ToString("0.##")));
        }

        private static string TrimText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "\"\"";
            }

            string collapsed = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return collapsed.Length <= 24
                ? $"\"{collapsed}\""
                : $"\"{collapsed.Substring(0, 24)}...\"";
        }

        private static SKRect ToSampleRect(ElementGeometry geometry, int bitmapWidth, int bitmapHeight, int inset, int expand)
        {
            float left = Math.Max(0, geometry.Left - expand);
            float top = Math.Max(0, geometry.Top - expand);
            float right = Math.Min(bitmapWidth, geometry.Left + geometry.Width + expand);
            float bottom = Math.Min(bitmapHeight, geometry.Top + geometry.Height + expand);

            if (right - left > inset * 2)
            {
                left += inset;
                right -= inset;
            }

            if (bottom - top > inset * 2)
            {
                top += inset;
                bottom -= inset;
            }

            return new SKRect(left, top, right, bottom);
        }

        private static int CountNonWhitePixels(SKBitmap bitmap, SKRect area)
        {
            if (bitmap == null)
            {
                return 0;
            }

            int left = Math.Max(0, (int)Math.Floor(area.Left));
            int top = Math.Max(0, (int)Math.Floor(area.Top));
            int right = Math.Min(bitmap.Width, (int)Math.Ceiling(area.Right));
            int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(area.Bottom));
            int count = 0;

            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha == 0)
                    {
                        continue;
                    }

                    if (pixel.Red < 250 || pixel.Green < 250 || pixel.Blue < 250)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static List<TextPaintNode> FindTopBandArtifactTextNodes(IEnumerable<PaintNodeBase> nodes, float navBandTop, float navBandBottom)
        {
            if (nodes == null)
            {
                return new List<TextPaintNode>();
            }

            float bandTop = Math.Max(0f, navBandTop - 24f);
            float bandBottom = navBandBottom + 48f;

            return nodes
                .OfType<TextPaintNode>()
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.FallbackText))
                .Where(n => n.Bounds.Bottom >= bandTop && n.Bounds.Top <= bandBottom)
                .Where(n => ContainsTopBandArtifactToken(n.FallbackText))
                .ToList();
        }

        private static bool ContainsTopBandArtifactToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string compact = text.Trim().ToLowerInvariant();
            if (compact == "gmail" || compact == "images" || compact == "sign in" || compact == "google apps")
            {
                return false;
            }

            return compact == "close" ||
                   compact.Contains("spchx", StringComparison.Ordinal) ||
                   compact.Contains("wuxmqc", StringComparison.Ordinal) ||
                   compact.Contains("job8vb", StringComparison.Ordinal) ||
                   compact.Contains("jsl.dh(this.id", StringComparison.Ordinal) ||
                   compact.Contains("close choose what you're giving feedback on", StringComparison.Ordinal) ||
                   compact.Contains("permission-bar-gradient", StringComparison.Ordinal);
        }

        private static bool TryFindPaintPath(IReadOnlyList<PaintNodeBase> nodes, Node source, List<PaintNodeBase> path)
        {
            if (nodes == null)
            {
                return false;
            }

            foreach (var node in nodes)
            {
                if (node == null)
                {
                    continue;
                }

                path.Add(node);
                if (ReferenceEquals(node.SourceNode, source))
                {
                    return true;
                }

                if (TryFindPaintPath(node.Children, source, path))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
            }

            return false;
        }
    }
}
