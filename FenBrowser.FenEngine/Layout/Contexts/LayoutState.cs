using SkiaSharp;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Represents the mutable state passed down during layout.
    /// Equivalent to 'Constraint' or 'AvailableSpace'.
    /// </summary>
    public struct LayoutState
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
        /// </summary>
        public float ScrollOffsetX;

        /// <summary>
        /// Current scroll offset of the nearest scroll container (or viewport).
        /// Used by sticky positioning to compute the stuck/unstuck constraint.
        /// </summary>
        public float ScrollOffsetY;

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
        }

        public LayoutState Clone()
        {
            return this; // Struct copy
        }

        public LayoutState CloneWithNewSize(SKSize newSize)
        {
            return new LayoutState(newSize, ContainingBlockWidth, ContainingBlockHeight, ViewportWidth, ViewportHeight, Deadline);
        }
    }
}
