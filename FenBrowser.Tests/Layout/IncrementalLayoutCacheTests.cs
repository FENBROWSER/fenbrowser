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
    public void ComputeLayout_ReusesCachedResult_WithAHiddenIframe()
    {
        // A display:none iframe never gets a box. Taking its missing rect for a frame
        // not laid out yet threw the cached result away on every request (YouTube keeps
        // one on every page), so each geometry read cost a full layout.
        var root = new Element("div");
        var frame = new Element("iframe");
        root.AppendChild(frame);
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 100, Height = 50 },
            [frame] = new CssComputed { Display = "none" }
        };

        var engine = new LayoutEngine(styles, 800, 600);
        var first = engine.ComputeLayout(root, 0, 0, 800);
        var second = engine.ComputeLayout(root, 0, 0, 800);

        Assert.NotNull(first);
        Assert.Same(first, second);
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
    public void ComputeLayout_KeepsCache_UntilARestyleMarksLayout()
    {
        // A style mark alone says a restyle is pending, not that geometry changed: the
        // restyle marks layout when it changes something boxes depend on. Laying out
        // again before it runs would only reuse the same, stale styles.
        var root = new Element("div");
        root.AppendChild(new Text("hello"));
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block" }
        };

        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);

        root.MarkDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Style);
        Assert.Same(first, engine.ComputeLayout(root, 0, 0, 800));

        root.MarkDirty(FenBrowser.Core.Dom.V2.InvalidationKind.Layout);
        var relaid = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(relaid);
        Assert.NotSame(first, relaid);
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
    public void ComputeLayout_InvalidatesCache_WhenLateIframeHostWasNotMaterialized()
    {
        var root = new Element("div");
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 400, Height = 200 }
        };
        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);
        Assert.NotNull(first);

        var iframe = new Element("iframe");
        iframe.SetAttribute("width", "304");
        iframe.SetAttribute("height", "78");
        root.AppendChild(iframe);

        // Model a late DOM insertion observed between renderer snapshots. The
        // materialization guard must still reject a cache entry with no host box.
        root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);
        iframe.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);

        var second = engine.ComputeLayout(root, 0, 0, 800);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second.TryGetElementRect(iframe, out var rect));
        Assert.Equal(304f, rect.Width, 1f);
        Assert.Equal(78f, rect.Height, 1f);
    }

    [Fact]
    public void ComputeLayout_RescansFramesAfterADomChange_NotOnEveryReuse()
    {
        // The frame scan walks the whole tree, so its answer is kept between reuses of
        // the same result - script geometry reads call in once each. A DOM change has
        // to drop it: an iframe inserted after the answer was taken still needs a box.
        var root = new Element("div");
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 400, Height = 200 }
        };
        var engine = new LayoutEngine(styles, 800, 600);

        var first = engine.ComputeLayout(root, 0, 0, 800);
        Assert.Same(first, engine.ComputeLayout(root, 0, 0, 800));
        Assert.Same(first, engine.ComputeLayout(root, 0, 0, 800));

        var iframe = new Element("iframe");
        iframe.SetAttribute("width", "304");
        iframe.SetAttribute("height", "78");
        root.AppendChild(iframe);
        root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);
        iframe.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);

        var afterInsert = engine.ComputeLayout(root, 0, 0, 800);

        Assert.NotSame(first, afterInsert);
        Assert.True(afterInsert.TryGetElementRect(iframe, out _));
    }

    [Fact]
    public void ComputeLayout_PreservesStyleInvalidationForUnstyledLateIframe()
    {
        var root = new Element("div");
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 400, Height = 200 }
        };
        var engine = new LayoutEngine(styles, 800, 600);
        engine.ComputeLayout(root, 0, 0, 800);

        var iframe = new Element("iframe");
        iframe.SetAttribute("width", "304");
        iframe.SetAttribute("height", "78");
        root.AppendChild(iframe);

        engine.ComputeLayout(root, 0, 0, 800);

        Assert.True(iframe.StyleDirty);
        Assert.True(root.ChildStyleDirty);
        Assert.False(iframe.LayoutDirty);
    }

    [Fact]
    public void ComputeLayout_HandlesNullRoot_Gracefully()
    {
        var engine = new LayoutEngine(new Dictionary<Node, CssComputed>(), 800, 600);
        var result = engine.ComputeLayout(null, 0, 0, 800);
        Assert.Null(result);
    }
}
