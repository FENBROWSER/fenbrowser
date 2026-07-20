using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class MediaWikiHorizontalListRegressionTests
{
    [Fact]
    public async Task ListStyleShorthand_ResetsLonghandsAndBuildsAListItemBox()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    ul { list-style-position: inside; list-style-image: url(parent.png); }
    #square { list-style: square; }
    #none { list-style: none inside; }
    #image { list-style: circle outside url("marker wide.png"); }
  </style>
</head>
<body>
  <div id="initial">Initial values</div>
  <ul>
    <li id="square">Square</li>
    <li id="none">None</li>
    <li id="image">Image</li>
  </ul>
</body>
</html>
""";

        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null);
        var initial = Assert.IsType<Element>(document.GetElementById("initial"));
        var square = Assert.IsType<Element>(document.GetElementById("square"));
        var none = Assert.IsType<Element>(document.GetElementById("none"));
        var image = Assert.IsType<Element>(document.GetElementById("image"));

        Assert.Equal("disc", computed[initial].ListStyleType);
        Assert.Equal("outside", computed[initial].ListStylePosition);
        Assert.Equal("none", computed[initial].ListStyleImage);
        Assert.Equal("square", computed[square].ListStyleType);
        Assert.Equal("outside", computed[square].ListStylePosition);
        Assert.Equal("none", computed[square].ListStyleImage);
        Assert.Equal("none", computed[none].ListStyleType);
        Assert.Equal("inside", computed[none].ListStylePosition);
        Assert.Equal("none", computed[none].ListStyleImage);
        Assert.Equal("circle", computed[image].ListStyleType);
        Assert.Equal("outside", computed[image].ListStylePosition);
        Assert.Contains("marker wide.png", computed[image].ListStyleImage, StringComparison.Ordinal);

        var boxRoot = new BoxTreeBuilder(computed).Build(root);
        var squareBox = FindBox(boxRoot, square);
        Assert.NotNull(squareBox);
        Assert.Equal("ListItemBox", squareBox.GetType().Name);
    }

    [Fact]
    public async Task TemplateStyleDedupeKey_IsIngestedOnlyOnceInDomOrder()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <style data-mw-deduplicate="mw-data:TemplateStyles:r1">
    .dedupe-target { color: red; }
  </style>
</head>
<body>
  <link rel="mw-deduplicated-inline-style" href="mw-data:TemplateStyles:r1">
  <style data-mw-deduplicate="TemplateStyles:r1">
    .dedupe-target { color: blue; }
  </style>
  <div id="target" class="dedupe-target">Target</div>
</body>
</html>
""";

        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var result = await CssLoader.ComputeWithResultAsync(root, new Uri("https://example.test/"), null);
        var target = Assert.IsType<Element>(document.GetElementById("target"));

        Assert.Single(result.Sources, source => source.CssText.Contains(".dedupe-target", StringComparison.Ordinal));
        Assert.Equal<SKColor?>(SKColors.Red, result.Computed[target].ForegroundColor);
    }

    [Fact]
    public async Task GroupedSelector_ChoosesTheBranchForTheRequestedPseudoContext()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    #target, .scope li::after { content: "separator"; }
    .scope li::before { content: "prefix"; display: block; }
  </style>
</head>
<body class="scope"><ul><li id="target">Item</li></ul></body>
</html>
""";

        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null);
        var item = Assert.IsType<Element>(document.GetElementById("target"));

        Assert.Contains("separator", computed[item].After?.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("inline", computed[item].After?.Display);
        Assert.Contains("prefix", computed[item].Before?.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("block", computed[item].Before?.Display);
    }

    [Fact]
    public async Task DeduplicatedTemplateStyle_KeepsItemsAndSeparatorsInOneInlineFlow()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <style data-mw-deduplicate="mw-data:TemplateStyles:r1">
    .mw-parser-output .hlist dl,
    .mw-parser-output .hlist ol,
    .mw-parser-output .hlist ul {
      margin: 0;
      padding: 0;
    }

    .mw-parser-output .hlist dd,
    .mw-parser-output .hlist dt,
    .mw-parser-output .hlist li {
      display: inline;
      margin: 0;
    }

    .mw-parser-output .hlist.inline,
    .mw-parser-output .hlist.inline dl,
    .mw-parser-output .hlist.inline ol,
    .mw-parser-output .hlist.inline ul,
    .mw-parser-output .hlist dl dl,
    .mw-parser-output .hlist dl ol,
    .mw-parser-output .hlist dl ul,
    .mw-parser-output .hlist ol dl,
    .mw-parser-output .hlist ol ol,
    .mw-parser-output .hlist ol ul,
    .mw-parser-output .hlist ul dl,
    .mw-parser-output .hlist ul ol,
    .mw-parser-output .hlist ul ul {
      display: inline;
    }

    .mw-parser-output .hlist li {
      list-style: none;
    }

    .mw-parser-output .hlist li:not(:last-child)::after {
      content: " · ";
    }
  </style>
</head>
<body>
  <main class="mw-parser-output">
    <div class="hlist inline">
      <ul id="recent-list">
        <li id="first">The Halo Graphic Novel</li>
        <li id="second">Hamilcar's defeat</li>
        <li id="third">Black hole</li>
      </ul>
    </div>
    <link rel="mw-deduplicated-inline-style" href="mw-data:TemplateStyles:r1">
  </main>
</body>
</html>
""";

        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null);
        var items = new[] { "first", "second", "third" }
            .Select(id => Assert.IsType<Element>(document.GetElementById(id)))
            .ToArray();
        var list = Assert.IsType<Element>(document.GetElementById("recent-list"));
        var hlist = Assert.IsType<Element>(list.ParentElement);

        Assert.Equal("inline", computed[hlist].Display);
        Assert.Equal("inline", computed[list].Display);
        Assert.All(items, item => Assert.Equal("inline", computed[item].Display));
        Assert.All(items, item => Assert.Equal("none", computed[item].ListStyleType));
        Assert.Equal(0d, computed[list].Margin.Left);
        Assert.Equal(0d, computed[list].Padding.Left);
        Assert.Equal("inline", computed[items[0]].After?.Display);
        Assert.Equal("inline", computed[items[1]].After?.Display);
        Assert.Contains("·", computed[items[0]].After?.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("·", computed[items[1]].After?.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.True(computed[items[2]].After == null ||
                    string.Equals(computed[items[2]].After.Content, "none", StringComparison.OrdinalIgnoreCase));

        var layout = new LayoutEngineComputer(computed, 600, 300);
        layout.Measure(root, new SKSize(600, 300));
        layout.Arrange(root, new SKRect(0, 0, 600, 300));

        var itemBoxes = items.Select(item => Assert.IsType<BoxModel>(layout.GetBox(item))).ToArray();
        Assert.All(itemBoxes, box => Assert.InRange(box.ContentBox.Top, itemBoxes[0].ContentBox.Top - 3.5f, itemBoxes[0].ContentBox.Top + 3.5f));

        var pseudoElements = items.Take(2)
            .Select(item => Assert.IsType<PseudoElement>(computed[item].After?.PseudoElementInstance))
            .ToArray();
        var pseudoBoxes = pseudoElements
            .Select(pseudo => Assert.IsType<BoxModel>(layout.GetBox(pseudo)))
            .ToArray();
        Assert.All(pseudoBoxes, box => Assert.InRange(box.ContentBox.Top, itemBoxes[0].ContentBox.Top - 3.5f, itemBoxes[0].ContentBox.Top + 3.5f));

        var boxes = layout.GetAllBoxes().ToDictionary(pair => pair.Key, pair => pair.Value);
        var paintTree = NewPaintTreeBuilder.Build(root, boxes, computed, 600, 300, null);
        var paintNodes = Flatten(paintTree.Roots).ToArray();
        Assert.DoesNotContain(
            paintNodes.OfType<TextPaintNode>(),
            node => items.Contains(node.SourceNode) &&
                    (node.FallbackText == "•" || node.FallbackText?.EndsWith(".", StringComparison.Ordinal) == true));
    }

    private static IEnumerable<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private static LayoutBox FindBox(LayoutBox root, Node source)
    {
        if (root == null || ReferenceEquals(root.SourceNode, source))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindBox(child, source);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

}
