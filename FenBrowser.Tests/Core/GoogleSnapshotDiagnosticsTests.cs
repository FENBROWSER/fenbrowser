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
            string snapshotPath = GetLatestSnapshotPaths(1).FirstOrDefault();

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
            Element searchTextWrapper = FindFirst(root, e => HasClass(e, "a4bIc"));
            Element searchTextArea = FindFirst(root, e => string.Equals(e.GetAttribute("id"), "APjFqb", StringComparison.Ordinal));
            Element aiModeButton = FindFirst(root, e => HasClass(e, "plR5qb"));
            Element searchButtonsBand = FindFirst(root, e => HasClass(e, "lJ9FBc") && HasClass(e, "FPdoLc"));
            Element searchFormWrapper = FindFirst(root, e => HasClass(e, "A8SBwf"));
            Element mainColumn = FindFirst(root, e => HasClass(e, "L3eUgb"));
            Element topNav = FindFirst(root, e => HasClass(e, "Ne6nSd"));
            Element doodleBand = FindFirst(root, e => HasClass(e, "LS8OJ"));
            Element formBand = FindFirst(root, e => HasClass(e, "ikrT4e"));
            Element nativeForm = FindFirst(root, e => string.Equals(e.TagName, "FORM", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(searchShell);
            Assert.NotNull(searchTextWrapper);
            Assert.NotNull(searchTextArea);
            Assert.NotNull(aiModeButton);
            Assert.NotNull(searchButtonsBand);

            Assert.True(renderer.LastLayout.TryGetElementRect(searchShell, out var searchShellRect), "Missing layout rect for .RNNXgb");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchTextWrapper, out var searchTextWrapperRect), "Missing layout rect for .a4bIc");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchTextArea, out var searchTextAreaRect), "Missing layout rect for #APjFqb");
            Assert.True(renderer.LastLayout.TryGetElementRect(aiModeButton, out var aiModeButtonRect), "Missing layout rect for .plR5qb");
            Assert.True(renderer.LastLayout.TryGetElementRect(searchButtonsBand, out var searchButtonsBandRect), "Missing layout rect for .lJ9FBc");
            Assert.True(renderer.LastLayout.TryGetElementRect(topNav, out var topNavRect), "Missing layout rect for .Ne6nSd");

            string layoutContext =
                $"main={DescribeRect(renderer.LastLayout, mainColumn)} nav={DescribeRect(renderer.LastLayout, topNav)} " +
                $"doodle={DescribeRect(renderer.LastLayout, doodleBand)} form={DescribeRect(renderer.LastLayout, formBand)} " +
                $"wrapper={DescribeRect(renderer.LastLayout, searchFormWrapper)} shell={DescribeRect(searchShellRect)} buttons={DescribeRect(searchButtonsBandRect)}";

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
                $" wrapper-children={DescribeImmediateChildren(renderer.LastLayout, searchFormWrapper)}";

            Assert.True(searchShellRect.Width >= 400f, $"Expected .RNNXgb width >= 400, got {searchShellRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchShellRect.Height >= 48f, $"Expected .RNNXgb height >= 48, got {searchShellRect.Height}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchShellRect.Top < 450f, $"Expected .RNNXgb to remain within the first half of the viewport, got top={searchShellRect.Top}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchTextWrapperRect.Width >= 220f, $"Expected .a4bIc width >= 220, got {searchTextWrapperRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchTextAreaRect.Width >= 180f, $"Expected #APjFqb width >= 180, got {searchTextAreaRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(aiModeButtonRect.Width >= 60f, $"Expected .plR5qb width >= 60, got {aiModeButtonRect.Width}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchButtonsBandRect.Height >= 30f, $"Expected .lJ9FBc height >= 30, got {searchButtonsBandRect.Height}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");
            Assert.True(searchButtonsBandRect.Top < 520f, $"Expected visible .FPdoLc.lJ9FBc within viewport, got top={searchButtonsBandRect.Top}. {layoutContext}{rawBoxContext}{ancestryContext}{childContext}");

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

            var paintNodes = new List<PaintNodeBase>();
            CollectNodes(renderContext.PaintTreeRoots, paintNodes);

            int shellPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchShell));
            int wrapperPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextWrapper));
            int textAreaPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextArea));
            int aiModePaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, aiModeButton));
            int buttonsBandPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchButtonsBand));
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
            Assert.True(shellVisiblePixels > 250, $"Expected the rendered search shell region to contain visible non-white pixels.{paintContext}");
        }

        [Fact]
        public async Task LatestGoogleSnapshots_FirstLoadAndRefresh_KeyChromeRegionsRemainStable()
        {
            var snapshots = GetLatestSnapshotPaths(2);
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

        private static IReadOnlyList<string> GetLatestSnapshotPaths(int count)
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
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(Math.Max(1, count))
                .ToArray();
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

        private static bool HasClass(Element element, string className)
        {
            return element?.ClassList?.Contains(className) == true;
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
