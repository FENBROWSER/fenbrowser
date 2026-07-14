using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class GridAutoPlacementAllocationTests
    {
        [Fact]
        public void Arrange_RepeatedAutoPlacement_HasBoundedAllocations()
        {
            var container = new Element("main");
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "grid",
                    GridTemplateColumns = "repeat(4, 1fr)",
                    GridAutoFlow = "row"
                }
            };

            for (var index = 0; index < 100; index++)
            {
                var child = new Element("div");
                container.AppendChild(child);
                styles[child] = new CssComputed();
            }

            var boxes = new Dictionary<Node, BoxModel>();
            var arrangedCount = 0;
            Action<Node, SKRect, int> countArrangement = (_, _, _) => arrangedCount++;
            Arrange(container, styles, boxes, countArrangement);
            arrangedCount = 0;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                Arrange(container, styles, boxes, countArrangement);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.InRange(allocated, 1, 394_000);
            Assert.Equal(1_000, arrangedCount);
        }

        private static void Arrange(
            Element container,
            IReadOnlyDictionary<Node, CssComputed> styles,
            IDictionary<Node, BoxModel> boxes,
            Action<Node, SKRect, int> arrangeChild)
        {
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 800, 600),
                styles,
                boxes,
                depth: 0,
                arrangeChild,
                static (_, _, _) => default);
        }
    }
}
