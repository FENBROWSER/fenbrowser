using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

// A text node paints with its parent's style, but transform, opacity and filter
// are not inherited: the parent's own stacking context applies them, once. Taken
// again on the text, Google's results - which flip a block with scaleY(-1) and
// flip each title back - showed every title upside down.
public sealed class TextEffectOwnershipTests
{
    private const string Html = """
<!doctype html>
<html>
<body>
  <div id="flipped" style="transform: scaleY(-1)">
    <h3 id="title" style="display: inline-block; transform: scaleY(-1)">Upright title</h3>
  </div>
  <p id="faded" style="opacity: 0.5">Half opaque</p>
</body>
</html>
""";

    [Fact]
    public async Task ATextNodeNeverFormsItsOwnStackingContextOrOpacityGroup()
    {
        var (paintTree, _) = await BuildAsync();
        var all = Flatten(paintTree.Roots).ToList();

        Assert.DoesNotContain(all.OfType<StackingContextPaintNode>(), node => node.SourceNode is Text);
        Assert.DoesNotContain(all.OfType<OpacityGroupPaintNode>(), node => node.SourceNode is Text);
    }

    [Fact]
    public async Task TwoFlipsLeaveTheTitleUpright()
    {
        var (paintTree, document) = await BuildAsync();
        var title = Assert.IsType<Element>(document.GetElementById("title"));
        var titleText = Assert.IsType<Text>(Assert.Single(title.ChildNodes));

        var path = PathTo(paintTree.Roots, node => node is TextPaintNode && ReferenceEquals(node.SourceNode, titleText));
        Assert.NotNull(path);

        var scaleY = path.OfType<StackingContextPaintNode>()
            .Aggregate(1f, (product, node) => product * (node.Transform?.ScaleY ?? 1f));
        Assert.Equal(1f, scaleY);
        Assert.Equal(2, path.OfType<StackingContextPaintNode>().Count(node => node.Transform?.ScaleY < 0));
    }

    [Fact]
    public async Task OpacityIsAppliedOnceToTheTextInside()
    {
        var (paintTree, document) = await BuildAsync();
        var faded = Assert.IsType<Element>(document.GetElementById("faded"));
        var fadedText = Assert.IsType<Text>(Assert.Single(faded.ChildNodes));

        var path = PathTo(paintTree.Roots, node => node is TextPaintNode && ReferenceEquals(node.SourceNode, fadedText));
        Assert.NotNull(path);

        var opacity = path.OfType<StackingContextPaintNode>().Aggregate(1f, (product, node) => product * node.Opacity) *
                      path.OfType<OpacityGroupPaintNode>().Aggregate(1f, (product, node) => product * node.Opacity);
        Assert.Equal(0.5f, opacity, 3);
    }

    private static async Task<(ImmutablePaintTree Tree, Document Document)> BuildAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(Html, baseUri).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var computed = await CssLoader.ComputeAsync(root, baseUri, null);

        var layout = new LayoutEngineComputer(computed, 800, 600);
        layout.Measure(root, new SKSize(800, 600));
        layout.Arrange(root, new SKRect(0, 0, 800, 600));
        var boxes = layout.GetAllBoxes().ToDictionary(pair => pair.Key, pair => pair.Value);
        return (NewPaintTreeBuilder.Build(root, boxes, computed, 800, 600, null), document);
    }

    private static IReadOnlyList<PaintNodeBase> PathTo(IEnumerable<PaintNodeBase> roots, Func<PaintNodeBase, bool> target)
    {
        foreach (var root in roots)
        {
            if (target(root))
            {
                return new[] { root };
            }

            if (root.Children is { } children && PathTo(children, target) is { } below)
            {
                return new[] { root }.Concat(below).ToList();
            }
        }

        return null;
    }

    private static IEnumerable<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            if (root.Children is null)
            {
                continue;
            }

            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }
}
