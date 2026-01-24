using System;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Base class for all Formatting Contexts (BFC, IFC, FFC, GFC).
    /// Responsible for laying out boxes within a specific context.
    /// </summary>
    public abstract class FormattingContext
    {
        public abstract void Layout(LayoutBox box, LayoutState state);

        /// <summary>
        /// Factory method to determine the correct formatting context for a box.
        /// </summary>
        public static FormattingContext Resolve(LayoutBox box)
        {
            // If the box establishes a new context for its children, use that.
            // E.g., a BlockBox establishes a BFC for its block-level children.
            // Or establishes an IFC if it contains only inline-level children.

            // Currently, we assume BlockBox always uses BlockFormattingContext unless it has inline children?
            // Wait, an AnonymousBlockBox wrapper (BlockBox) containing inlines uses IFC.
            // Normal BlockBox containing Inlines uses IFC.
            // Normal BlockBox containing Blocks uses BFC.

            // NOTE: The 'context' is conceptually what the box ESTABLISHES for its children.
            
            // 1. Check for explicit formatting context triggers
            string display = box.ComputedStyle?.Display?.ToLowerInvariant() ?? "block";
            
            if (display.Contains("grid"))
            {
                return GridFormattingContext.Instance;
            }

            if (display.Contains("flex"))
            {
                return FlexFormattingContext.Instance;
            }

            if (box is BlockBox blockBox)
            {
                // Check children to decide (if not explicit flex/grid).
                bool hasBlockChildren = false;
                foreach(var child in blockBox.Children)
                {
                    if (child is BlockBox) hasBlockChildren = true;
                }

                if (hasBlockChildren)
                {
                    return BlockFormattingContext.Instance;
                }
                else
                {
                   // Even empty block boxes effectively use IFC logic (empty line box) or BFC logic (height 0)?  
                   // Usually InlineFormattingContext handles "Inline Level" boxes.
                   return InlineFormattingContext.Instance;
                }
            }
            
            // Default fallback
            return BlockFormattingContext.Instance;
        }
    }
}
