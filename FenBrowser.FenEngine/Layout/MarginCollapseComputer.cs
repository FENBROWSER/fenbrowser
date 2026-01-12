using System;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Rendering.Css; // For CssComputed if needed
using FenBrowser.Core.Css; // For Thickness/Styles

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Implements CSS 2.1 Section 8.3.1 Collapsing margins.
    /// Default rule: In CSS, the adjoining margins of two or more boxes (which might or might not be siblings) 
    /// can combine to form a single margin. Margins that combine this way are said to collapse, 
    /// and the resulting combined margin is called a collapsed margin.
    /// </summary>
    public static class MarginCollapseComputer
    {
        /// <summary>
        /// Collapses two adjoining margins according to spec rules:
        /// - If both positive, max(a, b)
        /// - If both negative, min(a, b) (most negative)
        /// - If one positive, one negative, a + b
        /// </summary>
        public static float Collapse(float marginA, float marginB)
        {
            if (marginA >= 0 && marginB >= 0)
            {
                return Math.Max(marginA, marginB);
            }
            else if (marginA < 0 && marginB < 0)
            {
                return Math.Min(marginA, marginB);
            }
            else
            {
                return marginA + marginB;
            }
        }

        /// <summary>
        /// Determines if a parent and its first child's top margins should collapse.
        /// Rules: Collapses if no top border, no top padding, and the parent is in-flow block-level.
        /// </summary>
        public static bool ShouldCollapseParentChildTop(CssComputed parentStyle)
        {
            if (parentStyle == null) return true; // Default behavior
            
            // Cannot collapse if parent has border or padding
            if (parentStyle.BorderThickness.Top > 0) return false;
            if (parentStyle.Padding.Top > 0) return false;
            
            // Only blocks collapse
            // Note: Flex/Grid/Table don't collapse margins with children
            string display = parentStyle.Display?.ToLowerInvariant();
            if (display == "flex" || display == "inline-flex" || 
                display == "grid" || display == "inline-grid" ||
                display == "table" || display == "inline-block")
            {
                return false;
            }
            
            // Clearance, BFC creation also prevent collapse (simplified here)
            if (parentStyle.Overflow != null && parentStyle.Overflow != "visible") return false;
            if (parentStyle.Position == "absolute" || parentStyle.Position == "fixed") return false;
            if (parentStyle.Float == "left" || parentStyle.Float == "right") return false;

            return true;
        }

        /// <summary>
        /// Determines if a parent and its last child's bottom margins should collapse.
        /// Rules: Collapses if no bottom border/padding, and parent height is auto.
        /// </summary>
        public static bool ShouldCollapseParentChildBottom(CssComputed parentStyle)
        {
            if (parentStyle == null) return true;

            if (parentStyle.BorderThickness.Bottom > 0) return false;
            if (parentStyle.Padding.Bottom > 0) return false;
            
            // If parent has explicit height, margins don't collapse with bottom child 
            // (Wait, spec says: "The bottom margin of an in-flow block-level element with a 'height' of 'auto' collapses with its last in-flow block-level child's bottom margin")
            if (parentStyle.Height.HasValue || parentStyle.HeightPercent.HasValue || !string.IsNullOrEmpty(parentStyle.HeightExpression)) 
                return false;

            // Check BFC/Display types
            string display = parentStyle.Display?.ToLowerInvariant();
            if (display == "flex" || display == "inline-flex" || 
                display == "grid" || display == "inline-grid" ||
                display == "table" || display == "inline-block")
            {
                return false;
            }

            if (parentStyle.Overflow != null && parentStyle.Overflow != "visible") return false;
            if (parentStyle.Position == "absolute" || parentStyle.Position == "fixed") return false;
            if (parentStyle.Float == "left" || parentStyle.Float == "right") return false;

            return true;
        }

        /// <summary>
        /// Determines if an empty block's top and bottom margins collapse together (collapse through).
        /// </summary>
        public static bool ShouldCollapseThrough(CssComputed style, float contentHeight)
        {
            if (style == null) return false;
            
            // Must have zero height, zero padding, zero border
            // (Technically "computed" height 0, but we use measured content height here)
            if (contentHeight > 0) return false;
            
            if (style.Padding.Top > 0 || style.Padding.Bottom > 0) return false;
            if (style.BorderThickness.Top > 0 || style.BorderThickness.Bottom > 0) return false;
            
            if (style.MinHeight.HasValue && style.MinHeight.Value > 0) return false;
            // Also explicit height > 0
            if (style.Height.HasValue && style.Height.Value > 0) return false;

            return true;
        }
    }
}
