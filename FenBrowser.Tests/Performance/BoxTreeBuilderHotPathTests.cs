using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public class BoxTreeBuilderHotPathTests
    {
        [Fact]
        public void Build_FlatTextTree_StaysWithinAllocationBudget()
        {
            var root = new Element("div");
            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block" }
            };

            for (var index = 0; index < 100; index++)
            {
                var child = new Element("span");
                child.AppendChild(new Text("benchmark"));
                root.AppendChild(child);
                styles[child] = new CssComputed { Display = "inline" };
            }

            Assert.NotNull(new BoxTreeBuilder(styles).Build(root));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                Assert.NotNull(new BoxTreeBuilder(styles).Build(root));
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(allocated <= 11_450_000, $"Expected at most 11,450,000 allocated bytes, got {allocated}.");
        }
    }
}
