using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class SkiaDomRendererPaintTreeTraversalTests
    {
        [Fact]
        public void CollectAllNodes_PreSizedResult_DoesNotAllocatePerNode()
        {
            var children = new PaintNodeBase[100];
            for (var index = 0; index < children.Length; index++)
            {
                children[index] = new BackgroundPaintNode();
            }

            var root = new BackgroundPaintNode { Children = children };
            PaintNodeBase[] roots = { root };
            var result = new List<PaintNodeBase>(children.Length + 1);
            var renderer = new SkiaDomRenderer();

            renderer.CollectAllNodes(roots, result);
            Assert.Equal(children.Length + 1, result.Count);
            Assert.Same(root, result[0]);
            for (var index = 0; index < children.Length; index++)
            {
                Assert.Same(children[index], result[index + 1]);
            }

            result.Clear();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                renderer.CollectAllNodes(roots, result);
                result.Clear();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(0, allocated);
        }
    }
}
