using System;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Tracks margin collapsing state while iterating over block-level children.
    /// Handles sibling collapsing and parent-child collapsing (both start and end).
    /// </summary>
    public class MarginCollapseTracker
    {
        /// <summary>
        /// If true, the parent has a border or padding that prevents its top margin 
        /// from collapsing with the first child's top margin.
        /// </summary>
        public bool PreventParentCollapse { get; set; }

        /// <summary>
        /// Tracks if we have encountered any non-empty content (block with height > 0) so far.
        /// </summary>
        public bool HasContent { get; private set; }

        /// <summary>
        /// The resolved Top Margin for the parent block.
        /// Only valid if !PreventParentCollapse (otherwise it's effectively 0 for the parent's external margin, 
        /// but the internal spacing is handled via AddMargin).
        /// </summary>
        public float ResultMarginStart { get; private set; }

        /// <summary>
        /// The bottom margin of the last non-empty child.
        /// </summary>
        public float LastBlockBottomMargin { get; private set; }
        
        /// <summary>
        /// The margin currently waiting to be collapsed with the next element.
        /// - Before content: Accumulation of empty block margins.
        /// - After content: The bottom margin of the previous sibling.
        /// </summary>
        public float PendingMargin { get; private set; }

        private bool _startMarginResolved = false;

        public float AddMargin(float childMT, float childMB, bool isFirst, bool isEmpty)
        {
            float spacing = 0;

            if (isEmpty)
            {
                // Empty blocks collapse their own top/bottom margins together.
                // Logic: "If the top and bottom margins of a box are adjoining, then it is possible for margins to collapse through it."
                float childCollapsed = MarginCollapseComputer.Collapse(childMT, childMB);

                if (isFirst && PreventParentCollapse)
                {
                    // Special case: Parent has border. Empty first child sits inside.
                    // The child's margin does NOT collapse with parent margin.
                    // Instead, it pushes the child down inside the parent.
                    // Since it's empty, we effectively just emit space.
                    // The Pending Margin for the *next* sibling becomes 0?
                    // No, "The bottom margin of a ... box ... collapses with the top margin of its next ... sibling."
                    // If we treat this space as 'spacing', we've effectively rendered the empty block's "height" (which is just margin).
                    // The spec is subtle here. 
                    // Let's assume the spacing is consumed.
                    spacing = childCollapsed;
                    PendingMargin = 0; 
                }
                else
                {
                    // Collapse with PendingMargin (previous empty blocks or previous sibling bottom)
                    // If isFirst && !PreventParentCollapse, PendingMargin is initially 0 (or parent top? assumed 0).
                    // We just accumulate it.
                    PendingMargin = MarginCollapseComputer.Collapse(PendingMargin, childCollapsed);
                    spacing = 0;
                }
            }
            else
            {
                // Non-Empty Child
                if (!HasContent)
                {
                    // First non-empty child we've seen. 
                    // PendingMargin contains collapsed empty blocks before this one.

                    if (PreventParentCollapse)
                    {
                        // Parent has border. The accumulated PendingMargin (from empty blocks or just 0)
                        // plus this child's MT determines where this child sits.
                        // They collapse together as "inter-sibling" calculation.
                        // (Previous empty blocks are siblings to this one).
                        // Note: If isFirst (really first), PendingMargin is 0.
                        
                        spacing = MarginCollapseComputer.Collapse(PendingMargin, childMT);
                        
                        // ResultMarginStart is irrelevant for PreventParentCollapse scenarios (usually 0 from outside persepctive),
                        // but let's effectively say 0.
                        ResultMarginStart = 0;
                    }
                    else
                    {
                        // Collapses with Parent Top.
                        // PendingMargin + childMT bubble up to become Parent's Top Margin.
                        ResultMarginStart = MarginCollapseComputer.Collapse(PendingMargin, childMT);
                        spacing = 0; // Starts at 0 (parent content edge)
                    }
                    _startMarginResolved = true;
                }
                else
                {
                    // Subsequent non-empty child.
                    // PendingMargin is the previous sibling's local Bottom margin.
                    spacing = MarginCollapseComputer.Collapse(PendingMargin, childMT);
                }

                HasContent = true;
                PendingMargin = childMB; // This child's bottom becomes the new pending
                LastBlockBottomMargin = childMB;
            }

            return spacing;
        }

        public void Finish(out float startMargin, out float endMargin)
        {
            // Start Margin logic
            if (PreventParentCollapse)
            {
                startMargin = 0; // Parent border handles it
            }
            else
            {
                // If we resolved start margin (encountered content), use it.
                // If we never encountered content (all empty), PendingMargin is the full collapsed margin.
                startMargin = HasContent ? ResultMarginStart : PendingMargin;
            }

            // End Margin logic
            if (HasContent)
            {
                // If we have content, end margin is the last child's bottom margin.
                // BUT wait, does it collapse with Parent Bottom?
                // That depends on `ShouldCollapseParentChildBottom`.
                // The Tracker just provides the candidate from the children side.
                // The *caller* (LayoutComputer) checks ShouldCollapseParentChildBottom using the parent style.
                // If yes, it bubbles up. If no, it stays inside.
                // The tracker just provides the value.
                endMargin = LastBlockBottomMargin;
            }
            else
            {
                // If full empty, start and end margins are the same collapsed value?
                // Spec: "If the top and bottom margins of a box are adjoining... margins collapse through it."
                // In this case, the collapsed margin is assigned to *one* of the parent's margins (usually Start?), 
                // or both? 
                // Usually it collapses bubbles to Top.
                // Let's return PendingMargin as endMargin too, caller decides usage.
                endMargin = PendingMargin;
            }
        }
    }
}
