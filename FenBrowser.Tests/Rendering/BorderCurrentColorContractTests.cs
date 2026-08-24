using System.Collections.Concurrent;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Painting;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering;

public sealed class BorderCurrentColorContractTests
{
    [Fact]
    public void PaintTree_UsesForegroundForUnspecifiedBorderColor()
    {
        var element = new Element("div");
        var style = CreateStyle();
        var styles = new Dictionary<Node, CssComputed> { [element] = style };
        var document = new Document();
        document.AppendChild(element);
        var computer = new LayoutEngineComputer(styles, 100, 100);
        computer.Measure(document, new SKSize(100, 100));
        computer.Arrange(document, new SKRect(0, 0, 100, 100));
        var boxes = new Dictionary<Node, BoxModel>(new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes()));

        var tree = NewPaintTreeBuilder.Build(document, boxes, styles, 100, 100, null);
        var border = Flatten(tree.Roots).OfType<BorderPaintNode>().First(node => ReferenceEquals(node.SourceNode, element));

        Assert.All(border.Colors, color => Assert.Equal(SKColors.Green, color));
    }

    [Fact]
    public void BoxPainter_UsesForegroundForUnspecifiedBorderColor()
    {
        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        new BoxPainter().PaintBorder(canvas, new SKRect(2, 2, 22, 22), CreateStyle());

        Assert.Contains(
            Enumerable.Range(0, bitmap.Width * bitmap.Height),
            index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width) == SKColors.Green);
    }

    private static CssComputed CreateStyle()
    {
        return new CssComputed
        {
            Display = "block",
            Width = 20,
            Height = 20,
            ForegroundColor = SKColors.Green,
            BorderThickness = new Thickness(2),
            BorderStyleTop = "solid",
            BorderStyleRight = "solid",
            BorderStyleBottom = "solid",
            BorderStyleLeft = "solid"
        };
    }

    private static IEnumerable<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children == null) continue;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
