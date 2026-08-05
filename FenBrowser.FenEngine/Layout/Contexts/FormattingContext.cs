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
            try
            {
                LayoutCore(box, state);
                ArrangeOutsideListMarker(box, state);
            }
            finally { _layoutDepth--; }
        }

        private static void ArrangeOutsideListMarker(LayoutBox box, LayoutState state)
        {
            if (box is not ListItemBox listItem ||
                listItem.Marker == null ||
                string.Equals(listItem.ComputedStyle?.ListStylePosition, "inside", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var marker = listItem.Marker;
            float markerWidth = Math.Max(0f, marker.Geometry.ContentBox.Width);
            float markerBaseline = marker.Geometry.Baseline > 0f
                ? marker.Geometry.Baseline
                : Math.Max(0f, marker.Geometry.ContentBox.Height * 0.8f);
            float itemBaseline = listItem.Geometry.Baseline > 0f
                ? listItem.Geometry.Baseline
                : markerBaseline;
            float markerX = listItem.Geometry.ContentBox.Left - markerWidth - 6f;
            float markerY = listItem.Geometry.ContentBox.Top + itemBaseline - markerBaseline;

            LayoutBoxOps.PositionSubtree(marker, markerX, markerY, state);
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

            // Layout containment establishes an independent formatting context:
            // descendants cannot affect layout outside the element. Applies to
            // any block-level box regardless of its children's display type.
            if (box is BlockBox &&
                ContainmentEvaluator.HasLayoutContainment(box.ComputedStyle))
            {
                return BlockFormattingContext.Instance;
            }

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

                // The context selected here lays out the box's children. A block may
                // establish a BFC externally (for example via overflow:hidden) while
                // still establishing an IFC for inline children.
                if (HasBlockLevelChild(blockBox))
                    return BlockFormattingContext.Instance;

                // Atomic replaced boxes do not lay out their contents as inline text.
                if (IsAtomicReplacedBox(box))
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

        private static bool IsAtomicReplacedBox(LayoutBox box)
        {
            if (box.SourceNode is FenBrowser.Core.Dom.V2.Element element &&
                FenBrowser.FenEngine.Layout.ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element))
            {
                return true;
            }

            return false;
        }

    }
}
