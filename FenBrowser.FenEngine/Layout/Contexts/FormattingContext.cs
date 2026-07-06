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
        [ThreadStatic] private static int _layoutDepth;
        private const int MaxLayoutDepth = 120;

        public void Layout(LayoutBox box, LayoutState state)
        {
            state.Deadline?.Check();

            if (_layoutDepth >= MaxLayoutDepth)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[Layout] Max depth {MaxLayoutDepth} exceeded for {box.SourceNode?.NodeName}. Skipping.", FenBrowser.Core.Logging.LogCategory.Layout);
                return;
            }
            _layoutDepth++;
            try { LayoutCore(box, state); }
            finally { _layoutDepth--; }
        }

        protected abstract void LayoutCore(LayoutBox box, LayoutState state);

        /// <summary>
        /// Factory method to determine the correct formatting context for a box.
        /// Dispatch is driven by computed display, overflow, and contain properties —
        /// NOT by HTML tag name. This ensures custom elements and non-standard markup
        /// receive correct formatting contexts.
        /// </summary>
        public static FormattingContext Resolve(LayoutBox box)
        {
            // Text runs always participate in inline formatting.
            if (box is TextLayoutBox)
            {
                return InlineFormattingContext.Instance;
            }

            // 1. Check for explicit formatting context triggers via display value.
            string display = box.ComputedStyle?.Display?.ToLowerInvariant() ?? "block";

            // Grid contexts (grid and inline-grid)
            if (display == "grid" || display == "inline-grid")
                return GridFormattingContext.Instance;

            // Flex contexts (flex and inline-flex)
            if (display == "flex" || display == "inline-flex")
                return FlexFormattingContext.Instance;

            // Table contexts
            if (display == "table" ||
                display == "inline-table" ||
                display == "table-row-group" ||
                display == "table-header-group" ||
                display == "table-footer-group" ||
                display == "table-row")
            {
                return TableFormattingContext.Instance;
            }

            // Flow-root establishes a new BFC
            if (display == "flow-root")
                return BlockFormattingContext.Instance;

            // Inline-block / table-cell: block containers that may establish
            // either BFC or IFC depending on their children.
            if (display == "inline-block" || display == "table-cell")
            {
                bool hasBlockChildren = HasBlockLevelChild(box);
                return hasBlockChildren ? BlockFormattingContext.Instance : InlineFormattingContext.Instance;
            }

            // Contents — children are laid out as if the element doesn't exist.
            if (display == "contents")
            {
                return BlockFormattingContext.Instance;
            }

            if (box is BlockBox blockBox)
            {
                // Root elements always establish a BFC.
                if (box.SourceNode is FenBrowser.Core.Dom.V2.Element rootElement)
                {
                    string rootTag = rootElement.TagName?.ToUpperInvariant() ?? string.Empty;
                    if (rootTag == "HTML" || rootTag == "BODY")
                    {
                        return BlockFormattingContext.Instance;
                    }
                }

                // BlockBox with block-level children → BFC.
                // Leaf BlockBoxes go to IFC unless they establish an independent
                // formatting context via overflow, contain, or replaced-element status.
                if (HasBlockLevelChild(blockBox))
                    return BlockFormattingContext.Instance;

                // A leaf block box that establishes its own formatting context
                // (overflow != visible, contain: layout/paint, or replaced element)
                // must use BFC. Otherwise, it contains only inline text → IFC.
                if (EstablishesIndependentBlockContext(box))
                    return BlockFormattingContext.Instance;

                return InlineFormattingContext.Instance;
            }

            if (box is InlineBox)
            {
                return InlineFormattingContext.Instance;
            }

            // Default fallback
            return BlockFormattingContext.Instance;
        }

        private static bool HasBlockLevelChild(LayoutBox box)
        {
            foreach (var child in box.Children)
            {
                if (child is TextLayoutBox) continue;

                string childDisplay = child.ComputedStyle?.Display?.ToLowerInvariant() ?? "inline";
                if (child is BlockBox ||
                    childDisplay == "block" ||
                    childDisplay == "flex" ||
                    childDisplay == "grid" ||
                    childDisplay == "table" ||
                    childDisplay == "flow-root" ||
                    childDisplay == "list-item")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true when the box establishes an independent block formatting
        /// context via CSS properties (not HTML tag identity). Covers:
        ///   - overflow: hidden | auto | scroll | clip
        ///   - contain: layout | paint | strict | content
        ///   - Replaced elements with intrinsic dimensions
        /// </summary>
        private static bool EstablishesIndependentBlockContext(LayoutBox box)
        {
            if (box?.ComputedStyle == null)
                return false;

            // overflow != visible establishes a new BFC (CSS 2.1 §9.4.1 / CSS Overflow 3 §3.3)
            string overflow = (box.ComputedStyle.Overflow ?? "visible").Trim().ToLowerInvariant();
            if (overflow != "visible")
                return true;

            // CSS Containment Level 1: contain:layout and contain:paint each establish
            // an independent formatting context.
            string contain = (box.ComputedStyle.Contain ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(contain) && contain != "none")
            {
                if (contain == "strict" || contain == "content")
                    return true;
                if (contain.Contains("layout") || contain.Contains("paint"))
                    return true;
            }

            // Replaced elements (img, video, input, etc.) have intrinsic dimensions
            // and must not participate in inline line construction.
            if (box.SourceNode is FenBrowser.Core.Dom.V2.Element element &&
                FenBrowser.FenEngine.Layout.ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element))
            {
                return true;
            }

            return false;
        }

    }
}
