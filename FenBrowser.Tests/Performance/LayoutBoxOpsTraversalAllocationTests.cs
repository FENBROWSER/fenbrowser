using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class LayoutBoxOpsTraversalAllocationTests
    {
        [Fact]
        public void ResetSubtreeToOrigin_RepeatedWalkDoesNotAllocate()
        {
            using var store = new LayoutBoxStore();
            var boxes = BuildFlatTree(store, childCount: 100);
            var root = boxes[0];

            LayoutBoxOps.ResetSubtreeToOrigin(root);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                LayoutBoxOps.ResetSubtreeToOrigin(root);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(0, allocated);
            Assert.All(boxes, box => Assert.Equal(0, box.Geometry.ContentBox.Left));
            Assert.All(boxes, box => Assert.Equal(0, box.Geometry.ContentBox.Top));
        }

        [Fact]
        public void ShiftSubtree_RepeatedWalkStaysWithinAllocationBudget()
        {
            using var store = new LayoutBoxStore();
            var boxes = BuildFlatTree(store, childCount: 100);
            var root = boxes[0];

            LayoutBoxOps.ShiftSubtree(root, 1, 1);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                LayoutBoxOps.ShiftSubtree(root, 1, 1);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            // ShiftSubtree intentionally creates one HashSet per call for cycle protection.
            // This ceiling rejects the original 122,240-byte enumerator path.
            const long allocationBudgetBytes = 74_000;
            Assert.InRange(allocated, 0, allocationBudgetBytes);
            Assert.All(boxes, box => Assert.Equal(21, box.Geometry.ContentBox.Left));
            Assert.All(boxes, box => Assert.Equal(31, box.Geometry.ContentBox.Top));
        }

        private static List<LayoutBox> BuildFlatTree(LayoutBoxStore store, int childCount)
        {
            var style = new CssComputed { Display = "block" };
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
            var box = store.GetWrapper(store.CreateBox(new Element(tagName), style, LayoutBoxStore.BoxType.Block));
            box.Geometry.ContentBox = new SKRect(10, 20, 30, 40);
            box.Geometry.PaddingBox = new SKRect(10, 20, 30, 40);
            box.Geometry.BorderBox = new SKRect(10, 20, 30, 40);
            box.Geometry.MarginBox = new SKRect(10, 20, 30, 40);
            return box;
        }
    }
}
