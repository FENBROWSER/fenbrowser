using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css; // For CssComputed if needed
using FenBrowser.Core.Css; // For Thickness/Styles
using FenBrowser.Core; 

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Implements CSS 2.1 Section 8.3.1 Collapsing margins.
    /// Default rule: In CSS, the adjoining margins of two or more boxes (which might or might not be siblings) 
    /// can combine to form a single margin. Margins that combine this way are said to collapse, 
    /// and the resulting combined margin is called a collapsed margin.
    /// </summary>
    /// <summary>
    /// Tracks a pair of positive and negative margins for collapsing.
    /// CSS Spec: The collapsed margin is the max(positives) + min(negatives).
    /// </summary>
    public struct MarginPair
    {
        public float Positive;
        public float Negative;

        public float Collapsed => Positive + Negative;

        public void Combine(float margin)
        {
            if (margin > 0) Positive = Math.Max(Positive, margin);
            else Negative = Math.Min(Negative, margin);
        }

        public void Combine(MarginPair other)
        {
            Positive = Math.Max(Positive, other.Positive);
            Negative = Math.Min(Negative, other.Negative);
        }

        public static MarginPair Collapse(float a, float bPos, float bNeg)
        {
            MarginPair res = new MarginPair();
            res.Combine(a);
            res.Positive = Math.Max(res.Positive, bPos);
            res.Negative = Math.Min(res.Negative, bNeg);
            return res;
        }
        
        public static MarginPair FromStyle(Thickness margin, string writingMode = "horizontal-tb")
        {
             string mode = (writingMode ?? "horizontal-tb").Trim().ToLowerInvariant();

             double blockStart;
             double blockEnd;

             // Map block axis margins by writing mode.
             // horizontal-tb: block axis is top -> bottom
             // vertical-rl:   block axis is right -> left
             // vertical-lr:   block axis is left -> right
             switch (mode)
             {
                 case "vertical-rl":
                 case "sideways-rl":
                     blockStart = margin.Right;
                     blockEnd = margin.Left;
                     break;
                 case "vertical-lr":
                 case "sideways-lr":
                     blockStart = margin.Left;
                     blockEnd = margin.Right;
                     break;
                 default:
                     blockStart = margin.Top;
                     blockEnd = margin.Bottom;
                     break;
             }

             var pair = new MarginPair();
             pair.Combine((float)blockStart);
             pair.Combine((float)blockEnd);
             return pair;
        }
    }

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

