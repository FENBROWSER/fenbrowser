// =============================================================================
// MarginCollapseComputer.cs
// CSS 2.1 Margin Collapsing Algorithm
// 
// SPEC REFERENCE: CSS 2.1 §8.3.1 - Collapsing Margins
//                 https://www.w3.org/TR/CSS21/box.html#collapsing-margins
// 
// MARGIN COLLAPSE RULES (per CSS 2.1):
//   1. Adjoining margins of two or more boxes collapse
//   2. Collapsed margin = max of participating margins (or algebraic sum if negative)
//   3. Parent-first-child margins collapse if no separating content/padding/border
//   4. Parent-last-child margins collapse if no separating content/padding/border/height
//   5. Empty boxes collapse margins through themselves
//   6. Clearance prevents collapse
//   7. Margin collapse happens only for vertical margins in normal flow
// 
// STATUS: ✅ Fully Implemented
// =============================================================================

using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Result of margin collapse computation for an element.
    /// </summary>
    public struct CollapsedMargins
    {
        /// <summary>Top margin after collapse consideration.</summary>
        public float Top;

        /// <summary>Bottom margin after collapse consideration.</summary>
        public float Bottom;

        /// <summary>True if top margin is potentially collapsible with parent.</summary>
        public bool TopCollapsesWithParent;

        /// <summary>True if bottom margin is potentially collapsible with parent.</summary>
        public bool BottomCollapsesWithParent;

        /// <summary>True if this element "collapses through" (empty box).</summary>
        public bool CollapsesThrough;

        /// <summary>Effective margin value (after all collapses).</summary>
        public float EffectiveTop => TopCollapsesWithParent ? 0 : Top;
        public float EffectiveBottom => BottomCollapsesWithParent ? 0 : Bottom;

        public override string ToString()
        {
            return $"[Margins T={Top:F1} B={Bottom:F1} " +
                   $"Tw/P={TopCollapsesWithParent} Bw/P={BottomCollapsesWithParent} " +
                   $"Through={CollapsesThrough}]";
        }
    }

    /// <summary>
    /// Context for margin collapse computation.
    /// </summary>
    public class MarginCollapseContext
    {
        /// <summary>Previous sibling's bottom margin (for sibling collapse).</summary>
        public float? PreviousSiblingBottomMargin;

        /// <summary>Parent's top margin (for parent-child collapse).</summary>
        public float ParentTopMargin;

        /// <summary>True if parent has border-top or padding-top.</summary>
        public bool ParentHasTopBorderOrPadding;

        /// <summary>True if parent has border-bottom or padding-bottom.</summary>
        public bool ParentHasBottomBorderOrPadding;

        /// <summary>True if parent has explicit height.</summary>
        public bool ParentHasExplicitHeight;

        /// <summary>Accumulated collapsed margin from parent-first-child chain.</summary>
        public float AccumulatedTopMargin;

        /// <summary>True if there's any in-flow content before this element.</summary>
        public bool HasPrecedingContent;
    }

    /// <summary>
    /// Computes margin collapse per CSS 2.1 §8.3.1.
    /// </summary>
    public static class MarginCollapseComputer
    {
        /// <summary>
        /// Compute collapsed margins for an element.
        /// </summary>
        public static CollapsedMargins ComputeCollapsedMargins(
            Element element,
            CssComputed style,
            MarginCollapseContext context,
            List<CollapsedMargins> childrenMargins = null)
        {
            var result = new CollapsedMargins();

            // Parse margins from Thickness
            float marginTop = (float)(style?.Margin.Top ?? 0);
            float marginBottom = (float)(style?.Margin.Bottom ?? 0);

            // Check if element establishes block formatting context (prevents collapse)
            if (EstablishesBlockFormattingContext(style))
            {
                result.Top = marginTop;
                result.Bottom = marginBottom;
                result.TopCollapsesWithParent = false;
                result.BottomCollapsesWithParent = false;
                result.CollapsesThrough = false;
                return result;
            }

            // Check if element is empty (collapses through)
            bool isEmpty = IsEmptyElement(element, style);
            
            if (isEmpty)
            {
                // Empty boxes collapse margins through themselves
                float collapsedMargin = CollapseMargin(marginTop, marginBottom);
                result.Top = collapsedMargin;
                result.Bottom = 0;
                result.CollapsesThrough = true;
                result.TopCollapsesWithParent = !context.ParentHasTopBorderOrPadding && !context.HasPrecedingContent;
                result.BottomCollapsesWithParent = !context.ParentHasBottomBorderOrPadding;
                return result;
            }

            // PARENT-FIRST-CHILD COLLAPSE
            if (!context.ParentHasTopBorderOrPadding && !context.HasPrecedingContent)
            {
                result.TopCollapsesWithParent = true;
                result.Top = CollapseMargin(context.AccumulatedTopMargin, marginTop);
            }
            else
            {
                result.Top = marginTop;
                result.TopCollapsesWithParent = false;
            }

            // SIBLING MARGIN COLLAPSE
            if (context.PreviousSiblingBottomMargin.HasValue)
            {
                float collapsed = CollapseMargin(context.PreviousSiblingBottomMargin.Value, marginTop);
                result.Top = collapsed;
            }

            // PARENT-LAST-CHILD COLLAPSE
            if (childrenMargins != null && childrenMargins.Count > 0)
            {
                var lastChildMargin = childrenMargins[^1];
                if (lastChildMargin.BottomCollapsesWithParent)
                {
                    result.Bottom = CollapseMargin(marginBottom, lastChildMargin.Bottom);
                }
                else
                {
                    result.Bottom = marginBottom;
                }
            }
            else
            {
                result.Bottom = marginBottom;
            }

            result.BottomCollapsesWithParent = !context.ParentHasBottomBorderOrPadding && 
                                               !context.ParentHasExplicitHeight;

            return result;
        }

        /// <summary>
        /// Collapse two margin values according to CSS 2.1 rules.
        /// </summary>
        public static float CollapseMargin(float m1, float m2)
        {
            if (m1 >= 0 && m2 >= 0)
                return Math.Max(m1, m2);
            else if (m1 < 0 && m2 < 0)
                return Math.Min(m1, m2);
            else
                return m1 + m2;
        }

        /// <summary>
        /// Collapse multiple margin values.
        /// </summary>
        public static float CollapseMargins(params float[] margins)
        {
            if (margins == null || margins.Length == 0) return 0;
            if (margins.Length == 1) return margins[0];

            float result = margins[0];
            for (int i = 1; i < margins.Length; i++)
            {
                result = CollapseMargin(result, margins[i]);
            }
            return result;
        }

        /// <summary>
        /// Compute space between two adjacent siblings considering margin collapse.
        /// </summary>
        public static float ComputeSiblingGap(
            float previousBottomMargin,
            float currentTopMargin,
            bool previousHasClearance = false)
        {
            if (previousHasClearance)
                return previousBottomMargin + currentTopMargin;
            return CollapseMargin(previousBottomMargin, currentTopMargin);
        }

        /// <summary>
        /// Determines if clearance prevents margin collapse.
        /// </summary>
        public static bool HasClearance(CssComputed style, bool hasFloatsToClear)
        {
            if (style == null) return false;
            var clear = style.Map?.GetValueOrDefault("clear", "none")?.ToLowerInvariant() ?? "none";
            if (clear == "none") return false;
            return hasFloatsToClear;
        }

        #region Private Helpers

        private static bool EstablishesBlockFormattingContext(CssComputed style)
        {
            if (style == null) return false;

            var floatVal = style.Float?.ToLowerInvariant() ?? "none";
            if (floatVal != "none") return true;

            var position = style.Position?.ToLowerInvariant() ?? "static";
            if (position == "absolute" || position == "fixed") return true;

            var display = style.Display?.ToLowerInvariant() ?? "block";
            if (display == "inline-block" || display == "flow-root") return true;
            if (display == "flex" || display == "inline-flex" || 
                display == "grid" || display == "inline-grid") return true;
            if (display == "table-caption" || display == "table-cell") return true;

            var overflow = style.Overflow?.ToLowerInvariant() ?? "visible";
            if (overflow != "visible" && overflow != "clip") return true;

            if (style.Contain != null)
            {
                if (style.Contain.Contains("layout") || style.Contain.Contains("content") || 
                    style.Contain.Contains("strict"))
                    return true;
            }

            return false;
        }

        private static bool IsEmptyElement(Element element, CssComputed style)
        {
            if (style == null) return true;

            // Has explicit height?
            if ((style.Height ?? 0) > 0) return false;
            if ((style.MinHeight ?? 0) > 0) return false;

            // Has padding or border using Thickness?
            if (style.Padding.Top > 0 || style.Padding.Bottom > 0) return false;
            if (style.BorderThickness.Top > 0 || style.BorderThickness.Bottom > 0) return false;

            // Has visible content?
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (child is Text text && !string.IsNullOrWhiteSpace(text.Data))
                        return false;
                    if (child is Element)
                        return false;
                }
            }

            return true;
        }

        #endregion
    }

    /// <summary>
    /// Tracks margin state during block layout for proper collapse handling.
    /// </summary>
    public class MarginCollapseTracker
    {
        public float PendingMargin { get; private set; }
        public bool HasContent { get; private set; }
        public float LastBlockBottomMargin { get; private set; }

        public float AddMargin(float topMargin, float bottomMargin, bool isFirstChild, bool isEmpty)
        {
            float spacing = 0;

            if (isFirstChild && !HasContent)
            {
                PendingMargin = MarginCollapseComputer.CollapseMargin(PendingMargin, topMargin);
                spacing = 0;
            }
            else if (!HasContent)
            {
                PendingMargin = MarginCollapseComputer.CollapseMargin(PendingMargin, topMargin);
                spacing = 0;
            }
            else
            {
                spacing = MarginCollapseComputer.CollapseMargin(LastBlockBottomMargin, topMargin);
                LastBlockBottomMargin = 0;
            }

            LastBlockBottomMargin = bottomMargin;

            if (!isEmpty)
                HasContent = true;

            return spacing;
        }

        public float FlushPendingMargin()
        {
            var margin = PendingMargin;
            PendingMargin = 0;
            HasContent = true;
            return margin;
        }

        public void Reset()
        {
            PendingMargin = 0;
            HasContent = false;
            LastBlockBottomMargin = 0;
        }
    }
}
