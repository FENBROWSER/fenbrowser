using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class PaintTreeLayerizerAllocationTests
{
    private readonly ITestOutputHelper _output;

    public PaintTreeLayerizerAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Layerize_UnpromotedTree_AvoidsPerNodeReasonAllocations()
    {
        const int childCount = 512;
        const int iterations = 10;
        var children = new PaintNodeBase[childCount];
        for (var index = 0; index < children.Length; index++)
        {
            children[index] = new BackgroundPaintNode();
        }

        var tree = new ImmutablePaintTree(
            new PaintNodeBase[] { new BackgroundPaintNode { Children = children } });
        var layerizer = new PaintTreeLayerizer();
        Assert.Same(LayerizationResult.Empty, layerizer.Layerize(tree, styles: null));

        LayerizationResult result = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            result = layerizer.Layerize(tree, styles: null);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Layerizing {childCount + 1:N0} unpromoted paint nodes {iterations:N0} times allocated {allocated:N0} B.");

        Assert.Same(LayerizationResult.Empty, result);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Layerize_PromotedNode_PreservesAllReasons()
    {
        var source = new Element("div");
        var styles = new Dictionary<Node, CssComputed>
        {
            [source] = new CssComputed { WillChange = "transform, opacity" }
        };
        var node = new StackingContextPaintNode
        {
            SourceNode = source,
            Bounds = new SKRect(1, 2, 11, 12),
            Opacity = 0.5f,
            Transform = SKMatrix.CreateTranslation(3, 4)
        };
        var synthetic = new ScrollPaintNode
        {
            Bounds = new SKRect(20, 20, 30, 30)
        };
        var tree = new ImmutablePaintTree(
            new PaintNodeBase[]
            {
                new BackgroundPaintNode
                {
                    Children = new PaintNodeBase[] { node, synthetic }
                }
            });

        var result = new PaintTreeLayerizer().Layerize(tree, styles);

        Assert.Equal(2, result.PromotedLayerCount);
        Assert.Equal(2, result.Layers.Count);
        var layer = result.Layers[0];
        Assert.Same(source, layer.SourceNode);
        Assert.Equal(node.Bounds, layer.Bounds);
        Assert.Equal(node.Opacity, layer.Opacity);
        Assert.Equal(
            new[]
            {
                "opacity",
                "stacking-context",
                "transform",
                "will-change:opacity",
                "will-change:transform"
            },
            layer.PromotionReasons);
        Assert.Null(result.Layers[1].SourceNode);
        Assert.Equal(new[] { "scroll" }, result.Layers[1].PromotionReasons);
    }
}
