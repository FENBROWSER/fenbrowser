using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class InlineTextLineCapacityTests
{
    [Fact]
    public void WrappedText_PreallocatesExactComputedLineStorage()
    {
        var root = new Element("div");
        var text = new Text(
            "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau");
        root.AppendChild(text);

        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed
            {
                Display = "block",
                Width = 72,
                FontSize = 16,
                LineHeight = 20
            }
        };

        var rootBox = new BoxTreeBuilder(styles).Build(root);
        Assert.NotNull(rootBox);

        var state = new LayoutState(
            new SKSize(72, 600),
            72,
            600,
            72,
            600);
        FormattingContext.Resolve(rootBox).Layout(rootBox, state);

        var textBox = FindBox(rootBox, text);
        Assert.NotNull(textBox);
        Assert.NotNull(textBox.Geometry.Lines);
        Assert.True(textBox.Geometry.Lines.Count > 4);
        Assert.Equal(textBox.Geometry.Lines.Count, textBox.Geometry.Lines.Capacity);
    }

    private static LayoutBox FindBox(LayoutBox box, Node node)
    {
        if (ReferenceEquals(box.SourceNode, node))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            var match = FindBox(child, node);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
