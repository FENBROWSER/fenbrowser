using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout.Tree
{
    /// <summary>
    /// Constructs the Layout Tree (Box Tree) from the DOM Tree.
    /// Handles 'display: none', 'display: contents', and initial box generation.
    /// </summary>
    public class BoxTreeBuilder
    {
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        
        public BoxTreeBuilder(IReadOnlyDictionary<Node, CssComputed> styles)
        {
            _styles = styles;
        }

        public LayoutBox Build(Node root)
        {
            if (root == null) return null;
            return ConstructBox(root, null).FirstOrDefault();
        }

        private List<LayoutBox> ConstructBox(Node node, CssComputed parentStyle)
        {
            var result = new List<LayoutBox>();

            if (node is Document documentNode)
            {
                foreach (var childNode in documentNode.ChildNodes)
                {
                    result.AddRange(ConstructBox(childNode, parentStyle));
                }

                return result;
            }

            // HTML details/summary behavior:
            // details:not([open]) > :not(summary) must not generate layout boxes.
            var parentElement = node.ParentElement ?? node.ParentNode as Element;
            if (parentElement is Element detailsParent &&
                string.Equals(detailsParent.TagName, "DETAILS", StringComparison.OrdinalIgnoreCase) &&
                !detailsParent.HasAttribute("open"))
            {
                bool isSummaryElement = node is Element elementNode &&
                    string.Equals(elementNode.TagName, "SUMMARY", StringComparison.OrdinalIgnoreCase);
                if (!isSummaryElement)
                {
                    return result;
                }
            }

            // Get style: prefer node.ComputedStyle (single source of truth), fall back to dictionary
            var style = node.ComputedStyle;
            if (style == null)
                _styles.TryGetValue(node, out style);

            if (style == null && node is Element) style = new CssComputed();

            // For text nodes, inherit from parent
            if (style == null && node is Text) style = parentStyle ?? new CssComputed();

            var display = ResolveDisplay(node, style);

            // 1. Handle Display: None and Hidden Tags
            if (display == "none") return result;
            
            if (node is Element e)
            {
                string tag = e.TagName?.ToUpperInvariant();
                if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "TEMPLATE" || tag == "MAP" || tag == "AREA")
                    return result;
            }

            // 2. Handle Text Nodes
            if (node is Text textNode)
            {
                // Preserve whitespace-only nodes but normalize them if they are too long? 
                // For now, only drop IF they are totally empty (not even space).
                if (string.IsNullOrEmpty(textNode.Data)) return result;

                // In normal flow, indentation/newline-only text under block/flex/grid containers
                // should not create standalone layout boxes.
                if (FenBrowser.FenEngine.Layout.Contexts.TextWhitespaceClassifier.IsCollapsibleWhitespaceOnly(textNode.Data))
                {
                    string whiteSpace = parentStyle?.WhiteSpace?.ToLowerInvariant() ?? "normal";
                    bool preserveWhitespace =
                        whiteSpace == "pre" ||
                        whiteSpace == "pre-wrap" ||
                        whiteSpace == "break-spaces";

                    string parentDisplay = parentStyle?.Display?.ToLowerInvariant() ?? "inline";
                    bool inlineParent =
                        parentDisplay == "inline" ||
                        parentDisplay == "inline-block" ||
                        parentDisplay == "inline-flex" ||
                        parentDisplay == "inline-grid" ||
                        parentDisplay == "contents";

                    if (!preserveWhitespace && !inlineParent)
                    {
                        return result;
                    }
                }
                
                // [Optimization] We could drop leading/trailing whitespace in blocks, 
                // but for now let's be safe for IFC.
                result.Add(new TextLayoutBox(textNode, style));
                return result;
            }

            // 3. Handle Elements
            if (node is Element element)
            {
                // Handle display: contents
                if (display == "contents")
                {
                    foreach (var childNode in GetChildren(element))
                    {
                        result.AddRange(ConstructBox(childNode, style));
                    }
                    return result;
                }

                LayoutBox box;
                bool isInline = display == "inline";
                bool isInlineLevel = isInline || display == "inline-block" || display == "inline-flex" || display == "inline-grid";

                if (isInlineLevel && !isInline) // Atomic inline
                {
                    box = new InlineBox(node, style);
                }
                else if (isInline)
                {
                    box = new InlineBox(node, style);
                }
                else
                {
                    box = new BlockBox(node, style);
                }

                var childBoxes = new List<LayoutBox>();

                // Prepend ::before pseudo-element
                if (style.Before != null && IsVisiblePseudo(style.Before))
                {
                    if (style.Before.PseudoElementInstance == null)
                        style.Before.PseudoElementInstance = new PseudoElement(element, "before", style.Before);
                    EnsurePseudoTextContent(style.Before.PseudoElementInstance, style.Before.Content);
                    childBoxes.AddRange(ConstructBox(style.Before.PseudoElementInstance, style));
                }

                // Recurse on children
                foreach (var childNode in GetChildren(element))
                {
                    childBoxes.AddRange(ConstructBox(childNode, style));
                }

                // Append ::after pseudo-element
                if (style.After != null && IsVisiblePseudo(style.After))
                {
                    if (style.After.PseudoElementInstance == null)
                        style.After.PseudoElementInstance = new PseudoElement(element, "after", style.After);
                    EnsurePseudoTextContent(style.After.PseudoElementInstance, style.After.Content);
                    childBoxes.AddRange(ConstructBox(style.After.PseudoElementInstance, style));
                }

                // Handle Block-in-Inline Splitting (CSS 2.1 Section 9.2.1.1)
                if (isInline && HasBlockLevelBox(childBoxes))
                {
                    return SplitInlineBox(element, style, childBoxes);
                }

                // Normal child adding
                foreach (var childBox in childBoxes)
                {
                    box.AddChild(childBox);
                }

                if (box is BlockBox blockBox)
                {
                    FixupBlockChildren(blockBox);
                }

                result.Add(box);
                return result;
            }

            return result;
        }

        private IEnumerable<Node> GetChildren(Element element)
        {
            if (element != null && ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element))
            {
                return Array.Empty<Node>();
            }

            if (element.ShadowRoot != null) return element.ShadowRoot.ChildNodes;
            return element.ChildNodes;
        }

        private static string ResolveDisplay(Node node, CssComputed style)
        {
            if (node is Text) return "inline";

            if (node is Element hiddenElement && hiddenElement.HasAttribute("hidden"))
            {
                return "none";
            }

            if (node is Element hiddenInputElement &&
                string.Equals(hiddenInputElement.TagName, "INPUT", StringComparison.OrdinalIgnoreCase))
            {
                string typeValue = hiddenInputElement.GetAttribute("type")?.Trim();
                if (string.Equals(typeValue, "hidden", StringComparison.OrdinalIgnoreCase))
                {
                    return "none";
                }
            }

            string display = style?.Display?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(display))
            {
                display = (node is Element element)
                    ? GetDefaultDisplay(element.TagName?.ToUpperInvariant())
                    : "block";
            }

            if (display == "none") return display;

            // CSS 2.1 Section 9.7: Blockify display for floated or absolutely-positioned elements.
            // float: left/right or position: absolute/fixed converts inline-level display to block-level.
            string floatVal = style?.Float?.Trim().ToLowerInvariant();
            string posVal = LayoutStyleResolver.GetEffectivePosition(style);
            bool isFloated = floatVal == "left" || floatVal == "right";
            bool isAbsFixed = posVal == "absolute" || posVal == "fixed";

            if (isFloated || isAbsFixed)
            {
                switch (display)
                {
                    case "inline":
                    case "inline-block":
                        return "block";
                    case "inline-flex":
                        return "flex";
                    case "inline-grid":
                        return "grid";
                    case "inline-table":
                        return "table";
                }
            }

            return display;
        }

        private static string GetDefaultDisplay(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "block";

            // HTML custom elements default to inline in the UA default style model unless author CSS changes it.
            if (tag.Contains("-", StringComparison.Ordinal))
            {
                return "inline";
            }

            return tag switch
            {
                // Hidden metadata/script nodes.
                "HEAD" or "SCRIPT" or "STYLE" or "META" or "LINK" or "TITLE" or "NOSCRIPT" or "TEMPLATE" => "none",

                // Table defaults.
                "TABLE" => "table",
                "TR" => "table-row",
                "THEAD" => "table-header-group",
                "TBODY" => "table-row-group",
                "TFOOT" => "table-footer-group",
                "COL" => "table-column",
                "COLGROUP" => "table-column-group",
                "TD" or "TH" => "table-cell",
                "CAPTION" => "table-caption",

                // List defaults.
                "LI" => "list-item",

                // Inline form controls/replaced.
                "INPUT" or "SELECT" or "TEXTAREA" or "BUTTON" => "inline-block",
                // SVG is a replaced element and must establish its own formatting context
                // (like inline-block) to contain its child elements (circle, path, rect, etc.)
                "SVG" => "inline-block",
                "IMG" or "CANVAS" or "IFRAME" or "OBJECT" => "inline",

                // Common inline content.
                "A" or "ABBR" or "ACRONYM" or "B" or "BDI" or "BDO" or "BIG" or
                "BR" or "CITE" or "CODE" or "DATA" or "DEL" or "DFN" or "EM" or
                "I" or "INS" or "KBD" or "LABEL" or "MAP" or "MARK" or
                "METER" or "OUTPUT" or "PICTURE" or "PROGRESS" or "Q" or "RUBY" or
                "S" or "SAMP" or "SMALL" or "SPAN" or "STRONG" or "SUB" or "SUP" or
                "TIME" or "TT" or "U" or "VAR" or "WBR" => "inline",

                // Default block-level.
                _ => "block"
            };
        }

        private bool HasBlockLevelBox(IEnumerable<LayoutBox> boxes)
        {
            foreach (var box in boxes)
            {
                if (IsBlockLevel(box)) return true;
            }
            return false;
        }

        private List<LayoutBox> SplitInlineBox(Element element, CssComputed style, List<LayoutBox> childBoxes)
        {
            var result = new List<LayoutBox>();
            var currentInlineRun = new List<LayoutBox>();

            void FlushRun()
            {
                if (currentInlineRun.Count > 0)
                {
                    var part = new InlineBox(element, style);
                    foreach (var b in currentInlineRun) part.AddChild(b);
                    result.Add(part);
                    currentInlineRun.Clear();
                }
            }

            foreach (var child in childBoxes)
            {
                if (IsBlockLevel(child))
                {
                    FlushRun();
                    result.Add(child);
                }
                else
                {
                    currentInlineRun.Add(child);
                }
            }
            FlushRun();

            return result;
        }

        /// <summary>
        /// Enforces the rule: A block container must have ONLY block children OR ONLY inline children.
        /// Wraps sequences of inline children in AnonymousBlockBoxes.
        /// </summary>
        private void FixupBlockChildren(BlockBox box)
        {
            if (box.Children.Count == 0) return;

            bool hasBlockChildren = false;
            bool hasInlineChildren = false;
            bool hasInFlowBlockChildren = false;
            bool hasFloatChildren = false;

            foreach (var child in box.Children)
            {
                if (IsBlockLevel(child)) hasBlockChildren = true;
                if (IsInlineLevel(child)) hasInlineChildren = true;
                if (IsFloated(child)) hasFloatChildren = true;
                if (IsBlockLevel(child) && !IsFloated(child) && !child.IsOutOfFlow) hasInFlowBlockChildren = true;
            }

            // If homogeneous, no fixup needed
            if (!hasBlockChildren || !hasInlineChildren) return;

            // Floats are blockified for layout, but inline text around them should still
            // participate in one anonymous inline flow rather than being split into
            // separate anonymous blocks before and after the float.
            if (!hasInFlowBlockChildren && hasFloatChildren)
            {
                var floatChildren = new List<LayoutBox>();
                AnonymousBlockBox inlineFlow = null;

                foreach (var child in box.Children)
                {
                    if (IsFloated(child))
                    {
                        floatChildren.Add(child);
                        continue;
                    }

                    if (IsInlineLevel(child))
                    {
                        inlineFlow ??= new AnonymousBlockBox(box.ComputedStyle);
                        inlineFlow.AddChild(child);
                        child.Parent = inlineFlow;
                    }
                }

                box.Children.Clear();
                foreach (var floatedChild in floatChildren)
                {
                    floatedChild.Parent = box;
                    box.Children.Add(floatedChild);
                }

                if (inlineFlow != null)
                {
                    inlineFlow.Parent = box;
                    box.Children.Add(inlineFlow);
                }

                return;
            }

            // Mixed content found!
            // Strategy: Group consecutive inline children into an AnonymousBlockBox
            var newChildren = new List<LayoutBox>();
            AnonymousBlockBox currentAnon = null;

            foreach (var child in box.Children)
            {
                if (IsInlineLevel(child))
                {
                    if (currentAnon == null)
                    {
                        currentAnon = new AnonymousBlockBox(box.ComputedStyle);
                        newChildren.Add(currentAnon);
                    }
                    currentAnon.AddChild(child);
                    // Update parent to be the anon box
                    child.Parent = currentAnon; 
                }
                else
                {
                    // It's a block
                    currentAnon = null; // Close current run
                    newChildren.Add(child);
                }
            }
            
            // Replace children
            box.Children.Clear();
            box.Children.AddRange(newChildren);
            // Parent links for newChildren are already set (for anon) or preserved (for blocks)?
            // We need to ensure newChildren's parent is 'box'.
            foreach(var c in box.Children) c.Parent = box;
        }

        private bool IsBlockLevel(LayoutBox box) => box is BlockBox; // Includes AnonymousBlockBox
        private bool IsInlineLevel(LayoutBox box) => box is InlineBox || box is TextLayoutBox;
        private static bool IsFloated(LayoutBox box)
        {
            var floatValue = box.ComputedStyle?.Float?.Trim().ToLowerInvariant();
            return floatValue == "left" || floatValue == "right";
        }

        private static bool IsVisiblePseudo(CssComputed pseudoStyle)
        {
            if (pseudoStyle == null)
            {
                return false;
            }

            string content = pseudoStyle.Content;
            if (content == null)
            {
                return false;
            }

            return !string.Equals(content, "none", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(content, "normal", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsurePseudoTextContent(PseudoElement pseudoElement, string rawContent)
        {
            if (pseudoElement == null) return;

            var text = NormalizePseudoText(rawContent);
            if (string.IsNullOrEmpty(text)) return;

            if (pseudoElement.ChildNodes.Length == 0)
            {
                pseudoElement.AppendChild(new Text(text));
                return;
            }

            if (pseudoElement.ChildNodes[0] is Text existingText && pseudoElement.ChildNodes.Length == 1)
            {
                if (!string.Equals(existingText.Data, text, StringComparison.Ordinal))
                {
                    existingText.Data = text;
                }
                return;
            }

            // Fallback clear and append
            for (int i = 0; i < pseudoElement.ChildNodes.Length; i++)
            {
                if (pseudoElement.ChildNodes[i] is Text textNode)
                {
                    textNode.Data = ""; // Clear existing, but really should detach
                }
            }
            pseudoElement.AppendChild(new Text(text));
        }

        private static string NormalizePseudoText(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return null;
            if (string.Equals(rawContent, "none", StringComparison.OrdinalIgnoreCase)) return null;
            if (rawContent.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return rawContent.Trim().Trim('"', '\'');
        }
    }
}

