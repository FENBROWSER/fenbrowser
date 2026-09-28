using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class SkiaDomRendererDirtyTraversalTests
    {
        [Fact]
        public void RecursivelyClearDirty_FlatTree_DoesNotAllocate()
        {
            var root = new Element("div");
            var children = new Element[100];

            for (var index = 0; index < children.Length; index++)
            {
                var child = new Element("span");
                root.AppendChild(child);
                children[index] = child;
            }

            var renderer = new SkiaDomRenderer();
            renderer.RecursivelyClearDirty(root, InvalidationKind.Paint);

            root.MarkDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            foreach (var child in children)
            {
                child.MarkDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            }

            renderer.RecursivelyClearDirty(root, InvalidationKind.Paint);

            // Only the requested kind is cleared: style invalidation stays pending for
            // the cascade that owns it (7a1424e1), layout for the next layout pass.
            Assert.True(root.StyleDirty);
            Assert.True(root.LayoutDirty);
            Assert.False(root.PaintDirty);
            foreach (var child in children)
            {
                Assert.True(child.StyleDirty);
                Assert.True(child.LayoutDirty);
                Assert.False(child.PaintDirty);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                renderer.RecursivelyClearDirty(root, InvalidationKind.Paint);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(0, allocated);
        }
    }
}
