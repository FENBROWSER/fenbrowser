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
        public FenBrowser.FenEngine.Core.RenderDeadline Deadline { get; set; }

        public LayoutState(SKSize available, float cbWidth, float cbHeight, float vpWidth, float vpHeight, FenBrowser.FenEngine.Core.RenderDeadline deadline = null)
        {
            AvailableSize = available;
            ContainingBlockWidth = cbWidth;
            ContainingBlockHeight = cbHeight;
            ViewportWidth = vpWidth;
            ViewportHeight = vpHeight;
            Deadline = deadline;
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
