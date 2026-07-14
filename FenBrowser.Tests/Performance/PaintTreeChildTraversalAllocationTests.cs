using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class PaintTreeChildTraversalAllocationTests
{
    [Fact]
    public void Build_WideTreeStaysWithinChildTraversalAllocationBudget()
    {
        const int childCount = 100;
        const int iterations = 10;
        const long allocationBudget = 390_000;

        var document = Document.CreateHtmlDocument();
        var root = document.CreateElement("main");
        var boxes = new Dictionary<Node, BoxModel>();
        var styles = new Dictionary<Node, CssComputed>();

        AddPaintInputs(root, 0, boxes, styles);
        for (var index = 0; index < childCount; index++)
        {
            var child = document.CreateElement("div");
            root.AppendChild(child);
            AddPaintInputs(child, index + 1, boxes, styles);
        }

        for (var warmup = 0; warmup < 3; warmup++)
        {
            _ = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, null);
        }

        ImmutablePaintTree tree = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            tree = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, null);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var paintedSources = new HashSet<Node>();
        foreach (var paintNode in tree.Roots)
        {
            if (paintNode is BackgroundPaintNode background && background.SourceNode != null)
            {
                paintedSources.Add(background.SourceNode);
            }
        }

        Assert.Equal(childCount + 1, paintedSources.Count);
        Assert.True(
            allocated <= allocationBudget,
            $"Allocated {allocated:N0} bytes; budget is {allocationBudget:N0} bytes.");
    }

    private static void AddPaintInputs(
        Node node,
        int row,
        IDictionary<Node, BoxModel> boxes,
        IDictionary<Node, CssComputed> styles)
    {
        boxes[node] = BoxModel.FromContentBox(0, row * 4, 100, 4);
        styles[node] = new CssComputed
        {
            Display = "block",
            BackgroundColor = new SKColor(0x22, 0x44, 0x66)
        };
    }
}
