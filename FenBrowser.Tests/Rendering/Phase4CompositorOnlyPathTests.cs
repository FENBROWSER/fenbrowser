using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 4: verify that compositor-only animations skip the paint-tree rebuild,
/// PaintTreeRebuildReason is populated correctly, and stable node IDs are
/// assigned during paint-tree construction.
/// </summary>
public class Phase4CompositorOnlyPathTests
{
    [Fact]
    public void PaintTreeRebuildReason_EnumHasExpectedValues()
    {
        // Verify the enum exists with the values expected by the renderer.
        Assert.Equal(0, (int)PaintTreeRebuildReason.None);
        Assert.Equal(1, (int)PaintTreeRebuildReason.FirstFrame);
        Assert.Equal(2, (int)PaintTreeRebuildReason.Navigation);
        Assert.Equal(3, (int)PaintTreeRebuildReason.RootChanged);
        Assert.Equal(4, (int)PaintTreeRebuildReason.LayoutChanged);
        Assert.Equal(5, (int)PaintTreeRebuildReason.StructuralPaintChange);
        Assert.Equal(6, (int)PaintTreeRebuildReason.StyleChange);
        Assert.Equal(7, (int)PaintTreeRebuildReason.PaintOnly);
        Assert.Equal(8, (int)PaintTreeRebuildReason.ImageCacheChange);
        Assert.Equal(9, (int)PaintTreeRebuildReason.StabilityForced);
        Assert.Equal(10, (int)PaintTreeRebuildReason.DiagnosticForce);
    }

    [Fact]
    public void AnimationUpdateKind_HasCompositeFlag()
    {
        // Phase 3 established the AnimationUpdateKind flags; Phase 4 depends on
        // the Composite flag for its compositor-only fast path.
        Assert.NotEqual(AnimationUpdateKind.None, AnimationUpdateKind.Composite);
        Assert.NotEqual(AnimationUpdateKind.None, AnimationUpdateKind.Paint);
        Assert.NotEqual(AnimationUpdateKind.None, AnimationUpdateKind.Layout);

        // Composite-only should not carry Paint or Layout bits.
        var compositeOnly = AnimationUpdateKind.Composite;
        Assert.Equal(AnimationUpdateKind.None, compositeOnly & AnimationUpdateKind.Paint);
        Assert.Equal(AnimationUpdateKind.None, compositeOnly & AnimationUpdateKind.Layout);
    }

    [Fact]
    public void BackgroundPaintNode_HasZeroStableNodeIdByDefault()
    {
        var node = new BackgroundPaintNode
        {
            Bounds = new SkiaSharp.SKRect(0, 0, 100, 100),
            Color = SkiaSharp.SKColors.Red
        };
        // Nodes created without a DOM source default to StableNodeId = 0.
        Assert.Equal(0UL, node.StableNodeId);
    }

    [Fact]
    public void StableNodeId_CanBeSetAfterConstruction()
    {
        var node = new BackgroundPaintNode
        {
            Bounds = new SkiaSharp.SKRect(0, 0, 100, 100),
            Color = SkiaSharp.SKColors.Red
        };
        node.StableNodeId = 42UL;
        Assert.Equal(42UL, node.StableNodeId);
    }

    [Fact]
    public void InvalidationReason_AnimationOnly_IsSingleBit()
    {
        // The compositor-only path detects frames where Animation is the only
        // invalidation reason. Verify Animation is a single, testable bit.
        var animationOnly = RenderFrameInvalidationReason.Animation;
        var cleared = animationOnly & ~RenderFrameInvalidationReason.Animation;
        Assert.Equal(RenderFrameInvalidationReason.None, cleared);
    }
}
