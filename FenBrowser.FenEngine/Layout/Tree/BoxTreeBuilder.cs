using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.DOM;

namespace FenBrowser.FenEngine.Layout.Tree
{
    /// <summary>
    /// Constructs the Layout Tree (Box Tree) from the DOM Tree.
    /// Handles 'display: none', 'display: contents', and initial box generation.
    /// </summary>
    public class BoxTreeBuilder
    {
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly LayoutBoxStore _store;
        
        public BoxTreeBuilder(IReadOnlyDictionary<Node, CssComputed> styles)
            : this(styles, new LayoutBoxStore())
        {
        }

        public BoxTreeBuilder(IReadOnlyDictionary<Node, CssComputed> styles, LayoutBoxStore store)
        {
            _styles = styles;
            _store = store;
        }

        public LayoutBox Build(Node root)
        {
            if (root == null) return null;
            var result = new List<LayoutBox>(1);
            ConstructBoxes(root, null, result);
            return result.Count == 0 ? null : result[0];
        }

        private void ConstructBoxes(Node node, CssComputed parentStyle, List<LayoutBox> result)
        {
            if (node is Document documentNode)
            {
                foreach (var childNode in documentNode.ChildNodes)
                {
                    ConstructBoxes(childNode, parentStyle, result);
                }

                return;
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
                    return;
                }
            }

            // Get style: prefer node.ComputedStyle (single source of truth), fall back to dictionary
            var style = node is PseudoElement pseudoElement
                ? pseudoElement.ComputedStyle
                : node.ComputedStyle;
            if (style == null)
                _styles.TryGetValue(node, out style);

            if (style == null && node is Element) style = new CssComputed();

            // For text nodes, inherit from parent
            if (style == null && node is Text) style = parentStyle ?? new CssComputed();

            LayoutStyleResolver.NormalizeForLayout(style);

            var display = ResolveDisplay(node, style, parentStyle);

            // 1. Handle Display: None and Hidden Tags
            if (display == "none")
            {
                LogLayoutDecision(node, "Box skipped because computed display=none", "none");
                return;
            }
            
            if (node is Element e)
            {
                string tag = e.TagName?.ToUpperInvariant();
                if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "TEMPLATE" || tag == "MAP" || tag == "AREA")
                    return;
            }

            // 2. Handle Text Nodes
            if (node is Text textNode)
            {
                // Preserve whitespace-only nodes but normalize them if they are too long? 
                // For now, only drop IF they are totally empty (not even space).
                if (string.IsNullOrEmpty(textNode.Data)) return;

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
                        return;
                    }
                }
                
                // [Optimization] We could drop leading/trailing whitespace in blocks, 
                // but for now let's be safe for IFC.
                int id = _store.CreateBox(textNode, style, LayoutBoxStore.BoxType.Text);
                result.Add(_store.GetWrapper(id));
                LogLayoutDecision(textNode, "Box created", "inline");
                return;
            }

            // 3. Handle Elements
            if (node is Element element)
            {
                // Handle display: contents
                if (display == "contents")
                {
                    foreach (var childNode in GetChildren(element))
                    {
                        ConstructBoxes(childNode, style, result);
                    }
                    return;
                }

                LayoutBox box;
                bool isInline = display == "inline";
                bool isInlineLevel = isInline || display == "inline-block" || display == "inline-flex" || display == "inline-grid";

                if (isInlineLevel && !isInline) // Atomic inline
                {
                    int id = _store.CreateBox(node, style, LayoutBoxStore.BoxType.Inline);
                    box = _store.GetWrapper(id);
                }
                else if (isInline)
                {
                    int id = _store.CreateBox(node, style, LayoutBoxStore.BoxType.Inline);
                    box = _store.GetWrapper(id);
                }
                else if (display == "list-item")
                {
                    int id = _store.CreateBox(node, style, LayoutBoxStore.BoxType.ListItem);
                    box = _store.GetWrapper(id);
                }
                else
                {
                    int id = _store.CreateBox(node, style, LayoutBoxStore.BoxType.Block);
                    box = _store.GetWrapper(id);
                }

                List<LayoutBox>? childBoxes = null;

                // Prepend ::before pseudo-element
                if (style.Before != null && IsVisiblePseudo(style.Before))
                {
                    childBoxes = new List<LayoutBox>();
                    if (style.Before.PseudoElementInstance == null)
                        style.Before.PseudoElementInstance = new PseudoElement(element, "before", style.Before);
                    EnsurePseudoTextContent(style.Before.PseudoElementInstance, style.Before.Content);
                    ConstructBoxes(style.Before.PseudoElementInstance, style.Before, childBoxes);
                }

                // Recurse on children
                foreach (var childNode in GetChildren(element))
                {
                    childBoxes ??= new List<LayoutBox>();
                    ConstructBoxes(childNode, style, childBoxes);
                }

                // Append ::after pseudo-element
                if (style.After != null && IsVisiblePseudo(style.After))
                {
                    childBoxes ??= new List<LayoutBox>();
                    if (style.After.PseudoElementInstance == null)
                        style.After.PseudoElementInstance = new PseudoElement(element, "after", style.After);
                    EnsurePseudoTextContent(style.After.PseudoElementInstance, style.After.Content);
                    ConstructBoxes(style.After.PseudoElementInstance, style.After, childBoxes);
                }

                // Handle Block-in-Inline Splitting (CSS 2.1 Section 9.2.1.1)
                if (childBoxes != null && isInline && HasBlockLevelBox(childBoxes))
                {
                    result.AddRange(SplitInlineBox(element, style, childBoxes));
                    return;
                }

                // Normal child adding
                if (childBoxes != null)
                {
                    foreach (var childBox in childBoxes)
                    {
                        box.AddChild(childBox);
                    }
                }

                if (box is BlockBox blockBox)
                {
                    FixupBlockChildren(blockBox);
                }

                result.Add(box);
                LogLayoutDecision(element, $"Box created type={box.GetType().Name}", display);
                return;
            }

            return;
        }

        private IEnumerable<Node> GetChildren(Element element)
        {
            if (element != null)
            {
                string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
                if (tag == "IFRAME")
                {
                    // The frame document belongs to a separate browsing context. It is
                    // laid out against the iframe viewport after the atomic host box.
                    return Array.Empty<Node>();
                }

                if (tag == "OBJECT")
                {
                    // Preserve structural fallback descendants (nested elements) so
                    // selector/layout behavior can target them, while skipping raw
                    // text fallback payload nodes.
                    return element.ChildNodes.OfType<Node>().Where(static n => n is Element);
                }
            }

            if (element != null && ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element))
            {
                return Array.Empty<Node>();
            }

            if (element.ShadowRoot != null) return element.ShadowRoot.ChildNodes;
            return element.ChildNodes;
        }

        private static string ResolveDisplay(Node node, CssComputed style, CssComputed parentStyle)
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

            // CSS display blockification: flex/grid items, floated boxes, and
            // absolutely positioned boxes use block-level outer display.
            string floatVal = style?.Float?.Trim().ToLowerInvariant();
            string posVal = LayoutStyleResolver.GetEffectivePosition(style);
            bool isFlexOrGridItem = IsFlexOrGridContainerDisplay(parentStyle?.Display);
            bool isFloated = !isFlexOrGridItem && (floatVal == "left" || floatVal == "right");
            bool isAbsFixed = posVal == "absolute" || posVal == "fixed";

            if (isFlexOrGridItem || isFloated || isAbsFixed)
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

        private static bool IsFlexOrGridContainerDisplay(string display)
        {
            if (string.IsNullOrWhiteSpace(display))
            {
                return false;
            }

            switch (display.Trim().ToLowerInvariant())
            {
                case "flex":
                case "inline-flex":
                case "-webkit-flex":
                case "-webkit-inline-flex":
                case "grid":
                case "inline-grid":
                    return true;
                default:
                    return false;
            }
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
                    int id = _store.CreateBox(element, style, LayoutBoxStore.BoxType.Inline);
                    var part = _store.GetWrapper(id);
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
                        if (inlineFlow == null)
                        {
                            int id = _store.CreateBox(null, box.ComputedStyle, LayoutBoxStore.BoxType.AnonymousBlock, true);
                            inlineFlow = (AnonymousBlockBox)_store.GetWrapper(id);
                        }
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
                        int id = _store.CreateBox(null, box.ComputedStyle, LayoutBoxStore.BoxType.AnonymousBlock, true);
                        currentAnon = (AnonymousBlockBox)_store.GetWrapper(id);
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
            foreach (var childBox in newChildren)
            {
                box.Children.Add(childBox);
            }
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

        private static void LogLayoutDecision(Node node, string decision, string display)
        {
            if (!LayoutEngine.LayoutDebugLogEnabled ||
                !EngineLog.IsEnabled(LogSubsystem.Layout, LogSeverity.Debug))
            {
                return;
            }

            EngineLog.Write(
                LogSubsystem.Layout,
                LogSeverity.Debug,
                $"[LAYOUT][DEBUG] {decision}",
                LogMarker.None,
                new EngineLogContext(NodeDescription: DescribeNode(node)),
                new Dictionary<string, object>
                {
                    ["display"] = display
                });
        }

        private static string DescribeNode(Node node)
        {
            if (node is Text)
            {
                return "#text";
            }

            if (node is not Element element)
            {
                return node?.GetType().Name ?? "<null>";
            }

            var tag = string.IsNullOrWhiteSpace(element.TagName) ? "node" : element.TagName.ToLowerInvariant();
            var id = element.GetAttribute("id");
            var cls = element.GetAttribute("class");
            var classToken = string.Empty;
            if (!string.IsNullOrWhiteSpace(cls))
            {
                var firstClass = cls.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstClass))
                {
                    classToken = "." + firstClass;
                }
            }

            return $"<{tag}{(string.IsNullOrWhiteSpace(id) ? string.Empty : "#" + id)}{classToken}>";
        }
    }
}

