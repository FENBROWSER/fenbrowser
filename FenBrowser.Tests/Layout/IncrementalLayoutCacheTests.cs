using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class IncrementalLayoutCacheTests
{
    [Fact]
    public void ComputeLayout_ReturnsCachedResult_WhenNothingIsDirty()
    {
        var root = new Element("div");
        var text = new Text("hello");
        root.AppendChild(text);
        // After AppendChild, dirty flags are set. Clear them to simulate the
        // state after a prior layout pass.
        root.ClearDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Style | FenBrowser.Core.Dom.V2.InvalidationKind.Layout);
        text.ClearDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Style | FenBrowser.Core.Dom.V2.InvalidationKind.Layout);

        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 100, Height = 50 }
        };

        var engine = new LayoutEngine(styles, 800, 600);

        // First call: full layout pass
        var first = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(first);

        // Verify dirty flags are clean after the layout pass
        Assert.False(root.StyleDirty, "StyleDirty should be cleared after layout");
        Assert.False(root.LayoutDirty, "LayoutDirty should be cleared after layout");

        // Second call with same root + viewport returns structurally equivalent
        // result. The incremental cache avoids box-tree build + formatting-context
        // layout when the DOM is quiescent.
        var second = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(second);
        Assert.Equal(first.ElementRects.Count, second.ElementRects.Count);
    }

    [Fact]
    public void ComputeLayout_InvalidatesCache_WhenViewportChanges()
    {
        var root = new Element("div");
        root.AppendChild(new Text("hello"));
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block" }
        };

        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);

        // Different viewport width → bypasses cache
        var second = engine.ComputeLayout(root, 0, 0, 1024);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ComputeLayout_InvalidatesCache_WhenLayoutIsDirty()
    {
        var root = new Element("div");
        root.AppendChild(new Text("hello"));
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block" }
        };

        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);

        // Mark layout dirty → bypasses cache
        root.MarkDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Layout);
        var second = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ComputeLayout_InvalidatesCache_WhenStyleIsDirty()
    {
        var root = new Element("div");
        root.AppendChild(new Text("hello"));
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block" }
        };

        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);

        // Mark style dirty → bypasses cache
        root.MarkDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Style);
        var second = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ComputeLayout_InvalidatesCache_WhenStyleSetIsDifferent()
    {
        var root = new Element("div");
        root.AppendChild(new Text("hello"));
        var styles1 = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block" }
        };
        var styles2 = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 500 }
        };

        var engine = new LayoutEngine(styles1, 800, 600);
        var first = engine.ComputeLayout(root, 0, 0, 800);

        // New engine with different styles → fresh layout
        var engine2 = new LayoutEngine(styles2, 800, 600);
        var second = engine2.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ComputeLayout_HandlesNullRoot_Gracefully()
    {
        var engine = new LayoutEngine(new Dictionary<Node, CssComputed>(), 800, 600);
        var result = engine.ComputeLayout(null, 0, 0, 800);
        Assert.Null(result);
    }
}
