using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 15: verify that full-document scan operations are cached or scoped
/// to dirty subtrees instead of walking the entire DOM every frame.
/// </summary>
public class Phase15NoOpWorkEliminationTests
{
    [Fact]
    public void CollectPaintDirtyRoots_FindsOnlyLeafDirtyNodes()
    {
        var root = new Element("div");
        var child = new Element("span");
        root.AppendChild(child);

        // Mark only the child as paint-dirty; root will get ChildPaintDirty.
        root.ClearDirty(InvalidationKind.Paint);
        child.MarkDirty(InvalidationKind.Paint);
        child.ClearDirty(InvalidationKind.Paint);  // clear child, root still has ChildPaintDirty via MarkDirty propagation
        child.MarkDirty(InvalidationKind.Paint);    // re-mark child + propagate to root

        var dirtyRoots = typeof(SkiaDomRenderer)
            .GetMethod("CollectPaintDirtyRoots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, new object[] { root }) as System.Collections.Generic.List<Node>;

        Assert.NotNull(dirtyRoots);
        // The child is the leaf dirty node; root is skipped because only its child is dirty.
        Assert.Single(dirtyRoots);
        Assert.Same(child, dirtyRoots[0]);
    }

    [Fact]
    public void CollectPaintDirtyRoots_ReturnsEmpty_WhenTreeIsClean()
    {
        var root = new Element("div");
        root.AppendChild(new Element("span"));

        // Never set any dirty flags.
        var dirtyRoots = typeof(SkiaDomRenderer)
            .GetMethod("CollectPaintDirtyRoots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, new object[] { root }) as System.Collections.Generic.List<Node>;

        Assert.NotNull(dirtyRoots);
        Assert.Empty(dirtyRoots);
    }

    [Fact]
    public void CompositedLayerCache_GenerationIncrementsOnNextGeneration()
    {
        var cache = new CompositedLayerCache();
        Assert.Equal(0, cache.Generation);

        var gen1 = cache.NextGeneration();
        Assert.Equal(1, gen1);
        Assert.Equal(1, cache.Generation);

        var gen2 = cache.NextGeneration();
        Assert.Equal(2, gen2);
    }

    [Fact]
    public void CompositedLayerCache_GetCachedLayer_ReturnsNull_WhenEmpty()
    {
        var cache = new CompositedLayerCache();
        cache.NextGeneration();
        Assert.Null(cache.GetCachedLayer("nonexistent", cache.Generation));
    }

    [Fact]
    public void StableNodeId_DefaultIsZero()
    {
        var node = new BackgroundPaintNode { Bounds = new SkiaSharp.SKRect(0, 0, 100, 100) };
        Assert.Equal(0UL, node.StableNodeId);
    }
}
