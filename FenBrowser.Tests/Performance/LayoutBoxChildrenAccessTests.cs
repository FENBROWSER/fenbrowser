using System;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class LayoutBoxChildrenAccessTests
    {
        [Fact]
        public void ChildrenProperty_RepeatedAccess_DoesNotAllocate()
        {
            using var store = new LayoutBoxStore();
            var style = new CssComputed { Display = "block" };
            var parent = store.GetWrapper(store.CreateBox(new Element("div"), style, LayoutBoxStore.BoxType.Block));
            var child = store.GetWrapper(store.CreateBox(new Element("span"), style, LayoutBoxStore.BoxType.Inline));
            parent.AddChild(child);

            _ = parent.Children.Count;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var observedCount = 0;
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                observedCount += parent.Children.Count;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(10_000, observedCount);
            Assert.Equal(0, allocated);

            var cachedChildren = parent.Children;
            var secondChild = store.GetWrapper(store.CreateBox(new Element("em"), style, LayoutBoxStore.BoxType.Inline));
            parent.AddChild(secondChild);

            Assert.Same(cachedChildren, parent.Children);
            Assert.Equal(2, cachedChildren.Count);
            Assert.Same(secondChild, cachedChildren[1]);
        }
    }
}
