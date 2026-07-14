using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class GridNodeMappingAllocationTests
    {
        [Fact]
        public void CollectNodeMappings_RepeatedWalkDoesNotAllocate()
        {
            using var store = new LayoutBoxStore();
            var style = new CssComputed { Display = "block" };
            var boxes = BuildFlatTree(store, style, childCount: 100);
            var nodeToBox = new Dictionary<Node, LayoutBox>(boxes.Count);
            var styles = new Dictionary<Node, CssComputed>(boxes.Count);

            GridFormattingContext.CollectNodeMappings(boxes[0], nodeToBox, styles);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                GridFormattingContext.CollectNodeMappings(boxes[0], nodeToBox, styles);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(0, allocated);
            Assert.Equal(boxes.Count, nodeToBox.Count);
            Assert.Equal(boxes.Count, styles.Count);
            Assert.All(boxes, box => Assert.Same(box, nodeToBox[box.SourceNode]));
            Assert.All(boxes, box => Assert.Same(style, styles[box.SourceNode]));
        }

        private static List<LayoutBox> BuildFlatTree(LayoutBoxStore store, CssComputed style, int childCount)
        {
            var boxes = new List<LayoutBox>(childCount + 1);
            var root = CreateBox(store, "main", style);
            boxes.Add(root);

            for (var index = 0; index < childCount; index++)
            {
                var child = CreateBox(store, "div", style);
                root.AddChild(child);
                boxes.Add(child);
            }

            return boxes;
        }

        private static LayoutBox CreateBox(LayoutBoxStore store, string tagName, CssComputed style)
        {
            int id = store.CreateBox(new Element(tagName), style, LayoutBoxStore.BoxType.Block);
            return store.GetWrapper(id);
        }
    }
}
