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
        private const int MaxLayoutDepth = 40;

        public void Layout(LayoutBox box, LayoutState state)
        {
            if (_layoutDepth >= MaxLayoutDepth)
            {
                FenBrowser.Core.FenLogger.Warn($"[Layout] Max depth {MaxLayoutDepth} exceeded for {box.SourceNode?.NodeName}. Skipping.", FenBrowser.Core.Logging.LogCategory.Layout);
                return;
            }
            _layoutDepth++;
            try { LayoutCore(box, state); }
            finally { _layoutDepth--; }
        }

        protected abstract void LayoutCore(LayoutBox box, LayoutState state);

        /// <summary>
        /// Factory method to determine the correct formatting context for a box.
        /// </summary>
        public static FormattingContext Resolve(LayoutBox box)
        {
            // Text runs always participate in inline formatting.
            if (box is TextLayoutBox)
            {
                return InlineFormattingContext.Instance;
            }

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

            // Inline-block/table-cell establish block containers.
            // If they only contain inline-level content, use IFC; otherwise BFC.
            if (display == "inline-block" || display == "table-cell")
            {
                if (box.SourceNode is FenBrowser.Core.Dom.V2.Element controlElement)
                {
                    string controlTag = controlElement.TagName?.ToUpperInvariant() ?? string.Empty;
                    if (controlTag == "INPUT" || controlTag == "BUTTON" || controlTag == "TEXTAREA" || controlTag == "SELECT")
                    {
                        return InlineFormattingContext.Instance;
                    }
                }

                bool hasBlockChildren = false;
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
                        hasBlockChildren = true;
                        break;
                    }
                }

                return hasBlockChildren ? BlockFormattingContext.Instance : InlineFormattingContext.Instance;
            }

            // Contents - children are laid out as if the element doesn't exist
            // (treat as if parent establishes the context)
            if (display == "contents")
            {
                return BlockFormattingContext.Instance; // Simplified handling
            }

            if (box is BlockBox blockBox)
            {
                if (box.SourceNode is FenBrowser.Core.Dom.V2.Element rootElement)
                {
                    string rootTag = rootElement.TagName?.ToUpperInvariant() ?? string.Empty;
                    if (rootTag == "HTML" || rootTag == "BODY")
                    {
                        return BlockFormattingContext.Instance;
                    }

                    // Atomic controls/replaced elements can be block-level externally
                    // without participating in inline line construction internally.
                    // Routing authored display:block inputs through the inline formatter
                    // collapses percent widths back to intrinsic control fallbacks.
                    if (IsAtomicReplacedOrControlTag(rootTag))
                    {
                        return BlockFormattingContext.Instance;
                    }
                }

                // Normal block or anonymous block
                bool hasBlockChildren = false;
                foreach(var child in blockBox.Children)
                {
                    if (child is BlockBox) hasBlockChildren = true;
                }

                if (hasBlockChildren) return BlockFormattingContext.Instance;
                return InlineFormattingContext.Instance; 
            }

            if (box is InlineBox)
            {
                // Normal inline boxes (span, a, etc.) establish/participate in IFC
                return InlineFormattingContext.Instance;
            }
            
            // Default fallback
            return BlockFormattingContext.Instance;
        }

        private static bool IsAtomicReplacedOrControlTag(string tag)
        {
            return tag == "INPUT" ||
                   tag == "SELECT" ||
                   tag == "TEXTAREA" ||
                   tag == "BUTTON" ||
                   tag == "IMG" ||
                   tag == "SVG" ||
                   tag == "CANVAS" ||
                   tag == "VIDEO" ||
                   tag == "IFRAME" ||
                   tag == "EMBED" ||
                   tag == "OBJECT";
        }

    }
}
