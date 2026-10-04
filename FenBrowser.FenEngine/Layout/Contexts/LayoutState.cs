using System;
using SkiaSharp;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Represents the mutable state passed down during layout.
    /// Equivalent to 'Constraint' or 'AvailableSpace'.
    /// </summary>
    public struct LayoutState : IEquatable<LayoutState>
    {
        /// <summary>
        /// The containing block size available for children.
        /// Warning: Width/Height can be Infinity (shrink-to-fit context).
        /// </summary>
        public SKSize AvailableSize;
        
        /// <summary>
        /// The containing block's established width.
        /// Used for calculating percentages.
        /// </summary>
        public float ContainingBlockWidth;

        /// <summary>
        /// The containing block's established height.
        /// </summary>
        public float ContainingBlockHeight;
        
        public float ViewportWidth;
        public float ViewportHeight;

        /// <summary>
        /// Optional deadline for the current layout pass.
        /// </summary>
        public FenBrowser.Core.Deadlines.FrameDeadline Deadline { get; set; }

        /// <summary>
        /// The nearest ancestor BFC's float manager, if this box participates in an
        /// inline formatting context that should avoid floats. Null for independent
        /// formatting contexts (flex, grid, nested BFC, etc.).
        /// </summary>
        public FloatManager FloatManager;

        /// <summary>
        /// BFC-relative X offset of this IFC container's content-box left edge.
        /// Used with FloatManager to compute horizontal float intrusions per line.
        /// Only meaningful when FloatManager is non-null.
        /// </summary>
        public float FloatOriginX;

        /// <summary>
        /// BFC-relative Y offset of this IFC container's content-box top. Used with
        /// FloatManager to compute per-line float intrusions. Only meaningful when
        /// FloatManager is non-null.
        /// </summary>
        public float FloatOriginY;

        /// <summary>
        /// Current scroll offset of the nearest scroll container (or viewport).
        /// Used by sticky positioning to compute the stuck/unstuck constraint.
        /// For nested scroll containers, this reflects the nearest ancestor with
        /// overflow != visible (hidden/scroll/auto/clip).
        /// </summary>
        public float ScrollOffsetX;

        /// <summary>
        /// Current scroll offset of the nearest scroll container (or viewport).
        /// Used by sticky positioning to compute the stuck/unstuck constraint.
        /// </summary>
        public float ScrollOffsetY;

        /// <summary>
        /// The nearest ancestor layout box that establishes a scroll container
        /// (overflow != visible). May be null when the viewport is the only
        /// scroll container. Used by sticky positioning to find the scroll port.
        /// </summary>
        public FenBrowser.FenEngine.Layout.Tree.LayoutBox ScrollContainer;

        /// <summary>
        /// Resolved parent grid geometry for a subgrid item, carried from the
        /// parent grid's arrange pass into the child grid's own measure/arrange
        /// so its tracks inherit the parent's lines. Null for non-subgrid boxes.
        /// </summary>
        public GridSubgridContext SubgridContext;

        /// <summary>
        /// True if this layout pass is a forced/synthetic probe (e.g. Flex box forcing widths).
        /// Differentiates caches from normal layout passes.
        /// </summary>
        public bool IsForced;

        /// <summary>
        /// The box a forced pass sizes. The flags describe that box only: its
        /// descendants are laid out unforced (FormattingContext.Layout clears them),
        /// otherwise every box under a flex item that was forced once keyed its
        /// layout cache - and the forced-height path of nested flex containers - on
        /// an ancestor's forced size.
        /// </summary>
        public FenBrowser.FenEngine.Layout.Tree.LayoutBox ForcedBox;

        /// <summary>
        /// The width/height the flex forced-size passes write onto the box's style
        /// before laying it out, or NaN when this pass forces nothing.
        /// </summary>
        /// <remarks>
        /// These belong in the layout key. LayoutWithForcedWidth/Height change the
        /// box's specified size in place and then lay it out, so two passes that
        /// force different sizes are genuinely different layouts even when every
        /// other constraint matches — without this the memo would answer the second
        /// one with the first one's geometry.
        /// </remarks>
        public float ForcedWidth = float.NaN;
        public float ForcedHeight = float.NaN;

        /// <summary>
        /// The width of the containing block an inline formatting context offers
        /// its inline-level children: the context root's content width once that
        /// is resolved, +∞ while the root is still a shrink-to-fit probe (a
        /// percentage is then cyclic and behaves as auto), NaN before the root has
        /// resolved. <see cref="ContainingBlockWidth"/> cannot carry this: inside an
        /// inline context it is the enclosing block formatting context's width,
        /// which the float manager positions against.
        /// </summary>
        public float InlineContainingBlockWidth = float.NaN;

        public LayoutState(SKSize available, float cbWidth, float cbHeight, float vpWidth, float vpHeight, FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            AvailableSize = available;
            ContainingBlockWidth = cbWidth;
            ContainingBlockHeight = cbHeight;
            ViewportWidth = vpWidth;
            ViewportHeight = vpHeight;
            Deadline = deadline;
            FloatManager = null;
            FloatOriginX = 0f;
            FloatOriginY = 0f;
            ScrollOffsetX = 0f;
            ScrollOffsetY = 0f;
            ScrollContainer = null;
            SubgridContext = null;
            IsForced = false;
            ForcedBox = null;
            ForcedWidth = float.NaN;
            ForcedHeight = float.NaN;
            InlineContainingBlockWidth = float.NaN;
        }

        public LayoutState Clone()
        {
            return this; // Struct copy
        }

        public LayoutState CloneWithNewSize(SKSize newSize)
        {
            return new LayoutState(newSize, ContainingBlockWidth, ContainingBlockHeight, ViewportWidth, ViewportHeight, Deadline)
            {
                FloatManager = this.FloatManager,
                FloatOriginX = this.FloatOriginX,
                FloatOriginY = this.FloatOriginY,
                ScrollOffsetX = this.ScrollOffsetX,
                ScrollOffsetY = this.ScrollOffsetY,
                ScrollContainer = this.ScrollContainer,
                SubgridContext = this.SubgridContext,
                IsForced = this.IsForced,
                ForcedBox = this.ForcedBox,
                ForcedWidth = this.ForcedWidth,
                ForcedHeight = this.ForcedHeight
            };
        }

        public bool Equals(LayoutState other)
        {
            var sameFloatManager = object.ReferenceEquals(this.FloatManager, other.FloatManager) ||
                                   (this.FloatManager != null &&
                                    other.FloatManager != null &&
                                    !this.FloatManager.HasFloats &&
                                    !other.FloatManager.HasFloats);
            return this.IsForced == other.IsForced &&
                   SameForcedSize(this.ForcedWidth, other.ForcedWidth) &&
                   SameForcedSize(this.ForcedHeight, other.ForcedHeight) &&
                   this.AvailableSize == other.AvailableSize &&
                   this.ContainingBlockWidth == other.ContainingBlockWidth &&
                   this.ContainingBlockHeight == other.ContainingBlockHeight &&
                   this.ViewportWidth == other.ViewportWidth &&
                   this.ViewportHeight == other.ViewportHeight &&
                   this.FloatOriginX == other.FloatOriginX &&
                   this.FloatOriginY == other.FloatOriginY &&
                   this.ScrollOffsetX == other.ScrollOffsetX &&
                   this.ScrollOffsetY == other.ScrollOffsetY &&
                   sameFloatManager &&
                   object.ReferenceEquals(this.ScrollContainer, other.ScrollContainer) &&
                   object.ReferenceEquals(this.SubgridContext, other.SubgridContext);
        }

        public override bool Equals(object obj) => obj is LayoutState other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(AvailableSize, ContainingBlockWidth, ContainingBlockHeight, IsForced,
                                    ForcedWidth, ForcedHeight);
        }

        /// <summary>NaN means "nothing forced", and NaN != NaN, so compare it explicitly.</summary>
        private static bool SameForcedSize(float left, float right)
        {
            return float.IsNaN(left) ? float.IsNaN(right) : left == right;
        }

        public static bool operator ==(LayoutState left, LayoutState right) => left.Equals(right);
        public static bool operator !=(LayoutState left, LayoutState right) => !left.Equals(right);
    }
}
