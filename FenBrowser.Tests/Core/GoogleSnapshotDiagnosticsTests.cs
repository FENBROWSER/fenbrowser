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
        [Fact(Skip = "Requires a local FenBrowser.Host engine_source_*.html snapshot artifact.")]
        public async Task LatestGoogleSnapshot_MainSearchChrome_HasLayoutAndPaintCoverage()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string logDir = Path.Combine(repoRoot, "FenBrowser.Host", "bin", "Debug", "net8.0", "logs");
            string snapshotPath = Directory
                .GetFiles(logDir, "engine_source_*.html")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();

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

            var paintNodes = new List<PaintNodeBase>();
            CollectNodes(renderContext.PaintTreeRoots, paintNodes);

            int shellPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchShell));
            int wrapperPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextWrapper));
            int textAreaPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchTextArea));
            int aiModePaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, aiModeButton));
            int buttonsBandPaintCount = paintNodes.Count(n => ReferenceEquals(n.SourceNode, searchButtonsBand));

            Assert.True(shellPaintCount > 0, "Expected paint nodes for .RNNXgb");
            Assert.True(wrapperPaintCount > 0, "Expected paint nodes for .a4bIc");
            Assert.True(textAreaPaintCount > 0, "Expected paint nodes for #APjFqb");
            Assert.True(aiModePaintCount > 0, "Expected paint nodes for .plR5qb");
            Assert.True(buttonsBandPaintCount > 0, "Expected paint nodes for .lJ9FBc");
        }

        private static Element FindFirst(Element root, Func<Element, bool> predicate)
        {
            return root.Descendants().OfType<Element>().FirstOrDefault(predicate);
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
    }
}
