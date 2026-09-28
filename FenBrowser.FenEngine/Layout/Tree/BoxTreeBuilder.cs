using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
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
        private readonly Dictionary<Node, Dictionary<Element, int>> _listItemCounters = new();
        private readonly Dictionary<(Node Root, string Name), Dictionary<Element, int[]>> _counterChains = new();
        
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
                : node.GetComputedStyle();
            if (style == null)
                _styles.TryGetValue(node, out style);

            if (style == null && node is Element) style = new CssComputed();

            // Text nodes inherit typography and other inherited CSS properties, not
            // the parent's entire box style. Reusing the parent object here applied
            // padding and positioned insets a second time to direct text children.
            if (style == null && node is Text) style = CreateInheritedTextStyle(parentStyle);

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

                if (tag?.Contains('-', StringComparison.Ordinal) == true &&
                    HasNoRenderableCustomElementContent(e) &&
                    !HasOwnLayoutDimensions(style))
                {
                    return;
                }
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

                    // Collapsible whitespace at either edge of an inline formatting
                    // container does not generate a line box. Pretty-printed markup
                    // commonly puts newlines around a lone icon/control.
                    if (!preserveWhitespace &&
                        (textNode.PreviousSibling == null || textNode.NextSibling == null))
                    {
                        return;
                    }

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

                if (box is ListItemBox listItemBox)
                {
                    var markerBox = CreateListMarkerBox(element, style);
                    if (markerBox != null)
                    {
                        childBoxes = new List<LayoutBox> { markerBox };
                        listItemBox.Marker = markerBox;
                    }
                }

                // Prepend ::before pseudo-element (after the ::marker, which a new list
                // here used to discard)
                if (style.Before != null && IsVisiblePseudo(style.Before))
                {
                    childBoxes ??= new List<LayoutBox>();
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

                if (tag == "SLOT" && element.GetRootNode() is ShadowRoot shadowRoot)
                {
                    var assignedNodes = shadowRoot.GetAssignedNodesForSlot(element);
                    return assignedNodes.Count > 0 ? assignedNodes : element.ChildNodes;
                }
            }

            if (element != null && ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element))
            {
                return Array.Empty<Node>();
            }

            // A closed shadow root is hidden from script, not from rendering.
            var attachedShadowRoot = element.GetAttachedShadowRoot();
            if (attachedShadowRoot != null)
            {
                return attachedShadowRoot.ChildNodes;
            }
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
                "RT" or "RB" or "RP" or "RTC" or
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

            // Snapshot before any work. Reparenting an inline child into a fresh
            // anonymous block calls LayoutBoxStore.AddChild(anon, child), which
            // removes the child from this box's LIVE store-backed children list.
            // Iterating that live list while it shrinks makes foreach silently skip
            // every sibling that follows a moved inline child, dropping whole
            // subtrees (e.g. the <form> after <br> on the Google /sorry CAPTCHA
            // page) out of the layout tree. The snapshot keeps iteration stable;
            // the store remains the single source of truth for the final relink.
            var children = new List<LayoutBox>(box.Children);

            bool hasBlockChildren = false;
            bool hasInlineChildren = false;
            bool hasInFlowBlockChildren = false;
            bool hasFloatChildren = false;

            foreach (var child in children)
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

                foreach (var child in children)
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

            foreach (var child in children)
            {
                if (currentAnon == null && child.IsOutOfFlow)
                {
                    // An out-of-flow box (an outside ::marker, an absolutely positioned
                    // span) that would open a run stays a direct child: it has the same
                    // static position there, and an anonymous block holding nothing in
                    // flow would stop margins collapsing through the container (CSS 2.1
                    // §8.3.1; css/css-lists/list-and-margin-collapse-001).
                    newChildren.Add(child);
                    continue;
                }

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
                else if (currentAnon != null && IsFloated(child))
                {
                    // CSS 2.1 §9.5.1 rules 6 and 7: a float that follows inline content may
                    // start on that content's line, not only below it. As a block sibling after
                    // the run it started where the run ended, so bing.com's profile menu
                    // (float:right after the inline-block search form, before the block scope
                    // bar) dropped below the search box. Place it before the run it follows, as
                    // the float-only path above does, and keep the run open so inline content
                    // after the float stays on the same lines. The float starts at the run's
                    // first line, which is exact for a one-line run.
                    newChildren.Insert(newChildren.IndexOf(currentAnon), child);
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

        private static CssComputed CreateInheritedTextStyle(CssComputed parent)
        {
            var style = new CssComputed
            {
                Display = "inline",
                Position = "static"
            };

            if (parent == null)
            {
                return style;
            }

            style.ForegroundColor = parent.ForegroundColor;
            style.FontSize = parent.FontSize;
            style.FontWeight = parent.FontWeight;
            style.FontStyle = parent.FontStyle;
            style.FontFamilyName = parent.FontFamilyName;
            style.TextAlign = parent.TextAlign;
            style.Hyphens = parent.Hyphens;
            style.TextDecoration = parent.TextDecoration;
            style.ListStyleType = parent.ListStyleType;
            style.WordSpacing = parent.WordSpacing;
            style.LetterSpacing = parent.LetterSpacing;
            style.LineHeight = parent.LineHeight;
            style.WhiteSpace = parent.WhiteSpace;
            style.Cursor = parent.Cursor;
            style.Direction = parent.Direction;
            style.UnicodeBidi = parent.UnicodeBidi;
            style.TextShadow = parent.TextShadow;
            style.TextTransform = parent.TextTransform;
            style.TextIndent = parent.TextIndent;
            style.Visibility = parent.Visibility;
            style.ListStylePosition = parent.ListStylePosition;
            style.ListStyleImage = parent.ListStyleImage;

            foreach (var property in CssComputed.InheritedProperties)
            {
                if (parent.Map.TryGetValue(property, out var value))
                {
                    style.Map[property] = value;
                }
            }

            style.Map["display"] = "inline";
            style.Map["position"] = "static";
            style.InheritCustomProperties(parent);
            return style;
        }

        private bool HasNoRenderableCustomElementContent(Element element)
        {
            foreach (var child in GetChildren(element))
            {
                if (child is Text text)
                {
                    if (!FenBrowser.FenEngine.Layout.Contexts.TextWhitespaceClassifier.IsCollapsibleWhitespaceOnly(text.Data))
                    {
                        return false;
                    }
                    continue;
                }

                if (child is not Element childElement)
                {
                    continue;
                }

                _styles.TryGetValue(childElement, out var childStyle);
                if (string.Equals(childStyle?.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (childElement.TagName?.Contains('-', StringComparison.Ordinal) == true &&
                    HasNoRenderableCustomElementContent(childElement) &&
                    !HasOwnLayoutDimensions(childStyle))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool HasOwnLayoutDimensions(CssComputed style)
        {
            if (style == null)
            {
                return false;
            }

            return style.Width.HasValue ||
                   style.WidthPercent.HasValue ||
                   !string.IsNullOrWhiteSpace(style.WidthExpression) ||
                   style.Height.HasValue ||
                   style.HeightPercent.HasValue ||
                   !string.IsNullOrWhiteSpace(style.HeightExpression) ||
                   style.Padding.Left > 0 ||
                   style.Padding.Top > 0 ||
                   style.Padding.Right > 0 ||
                   style.Padding.Bottom > 0 ||
                   style.BorderThickness.Left > 0 ||
                   style.BorderThickness.Top > 0 ||
                   style.BorderThickness.Right > 0 ||
                   style.BorderThickness.Bottom > 0;
        }
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

        // ::marker content (CSS Lists 3 §3.2): strings, attr(), counter(list-item)
        // with its counter style, and quotes drawn from the 'quotes' pairs - the
        // marker's own, else the list item's, which inherits them from the list.
        private string ResolveMarkerContent(CssComputed markerStyle, CssComputed listStyle, Element element)
        {
            var raw = markerStyle?.Content;
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw.Trim(), "normal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var quotes = ParseQuotes(ReadMapValue(markerStyle, "quotes") ?? ReadMapValue(listStyle, "quotes"));
            var depth = 0;
            var text = new System.Text.StringBuilder();
            foreach (var item in PseudoBoxFactory.ParseContent(raw.Trim(), element))
            {
                switch (item)
                {
                    case CounterContentItem counter:
                        text.Append(FormatCounterItem(counter, element));
                        break;
                    case QuoteContentItem quote:
                        switch (quote.QuoteType)
                        {
                            case QuoteType.OpenQuote:
                                text.Append(QuoteAt(quotes, depth, open: true));
                                depth++;
                                break;
                            case QuoteType.CloseQuote:
                                if (depth > 0) depth--;
                                text.Append(QuoteAt(quotes, depth, open: false));
                                break;
                            case QuoteType.NoOpenQuote:
                                depth++;
                                break;
                            case QuoteType.NoCloseQuote:
                                if (depth > 0) depth--;
                                break;
                        }
                        break;
                    case StringContentItem or AttrContentItem:
                        text.Append(item.GetText());
                        break;
                }
            }

            return text.Length == 0 ? null : text.ToString();
        }

        private static string ReadMapValue(CssComputed style, string property) =>
            style?.Map != null && style.Map.TryGetValue(property, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

        private static string QuoteAt(List<(string Open, string Close)> quotes, int depth, bool open)
        {
            if (quotes.Count == 0)
            {
                return string.Empty;
            }

            var pair = quotes[Math.Min(depth, quotes.Count - 1)];
            return open ? pair.Open : pair.Close;
        }

        // The 'quotes' value: "none", "auto", or pairs of CSS strings (escapes such
        // as \201C decoded). auto uses the typographic double and single quotes.
        private static List<(string Open, string Close)> ParseQuotes(string value)
        {
            var pairs = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                pairs.Add(("“", "”"));
                pairs.Add(("‘", "’"));
                return pairs;
            }

            if (string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return pairs;
            }

            var strings = new List<string>();
            for (var i = 0; i < value.Length; i++)
            {
                var delimiter = value[i];
                if (delimiter != '"' && delimiter != '\'')
                {
                    continue;
                }

                var current = new System.Text.StringBuilder();
                i++;
                while (i < value.Length && value[i] != delimiter)
                {
                    if (value[i] == '\\' && i + 1 < value.Length)
                    {
                        i++;
                        var hexStart = i;
                        while (i < value.Length && i - hexStart < 6 && Uri.IsHexDigit(value[i]))
                        {
                            i++;
                        }

                        if (i > hexStart)
                        {
                            var codePoint = Convert.ToInt32(value.Substring(hexStart, i - hexStart), 16);
                            current.Append(codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                                ? char.ConvertFromUtf32(codePoint)
                                : "�");
                            if (i < value.Length && value[i] == ' ')
                            {
                                i++;
                            }

                            continue;
                        }

                        current.Append(value[i]);
                        i++;
                        continue;
                    }

                    current.Append(value[i]);
                    i++;
                }

                strings.Add(current.ToString());
            }

            for (var i = 0; i + 1 < strings.Count; i += 2)
            {
                pairs.Add((strings[i], strings[i + 1]));
            }

            return pairs;
        }

        private void EnsurePseudoTextContent(PseudoElement pseudoElement, string rawContent)
        {
            if (pseudoElement == null) return;

            SetPseudoText(pseudoElement, NormalizePseudoText(rawContent, pseudoElement));
        }

        // Puts already-resolved text in the pseudo-element. A 'content' value goes
        // through EnsurePseudoTextContent to be parsed first; formatted list-marker
        // text ("1. ", "• ") is not CSS and would parse to nothing.
        private static void SetPseudoText(PseudoElement pseudoElement, string text)
        {
            if (pseudoElement == null || string.IsNullOrEmpty(text)) return;

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

        private ListMarkerBox CreateListMarkerBox(Element element, CssComputed listStyle)
        {
            string listStyleType = listStyle?.ListStyleType ?? "disc";
            string listStyleImage = listStyle?.ListStyleImage ?? "none";
            var markerStyle = listStyle?.Marker;
            string markerText = ResolveMarkerContent(markerStyle, listStyle, element);
            bool authoredContent = !string.IsNullOrEmpty(markerText);

            if (string.IsNullOrEmpty(markerText))
            {
                markerText = ListMarkerFormatter.Format(ResolveListOrdinal(element), listStyleType, element.OwnerDocument);
            }

            bool hasImage = !string.IsNullOrWhiteSpace(listStyleImage) &&
                            !string.Equals(listStyleImage, "none", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(markerText) && !hasImage)
            {
                return null;
            }

            // CSS Lists 3 §3.1: a list-style-image replaces the list-style-type text,
            // so the marker box is the same whatever the type (the image is painted
            // into this placeholder).
            if (hasImage && !authoredContent)
            {
                markerText = "■ ";
            }

            if (markerStyle == null)
            {
                markerStyle = listStyle?.Clone() ?? new CssComputed();
                markerStyle.Before = null;
                markerStyle.After = null;
                markerStyle.Marker = null;
                markerStyle.Content = null;
                markerStyle.PseudoElementInstance = null;
                listStyle.Marker = markerStyle;
            }

            markerStyle.Display = "inline";
            var pseudoElement = markerStyle.PseudoElementInstance ?? new PseudoElement(element, "marker", markerStyle);
            markerStyle.PseudoElementInstance = pseudoElement;
            SetPseudoText(pseudoElement, markerText);
            var textNode = pseudoElement.ChildNodes.OfType<Text>().FirstOrDefault();
            if (textNode == null)
            {
                return null;
            }

            var layoutStyle = markerStyle.Clone();
            layoutStyle.Display = "inline";
            layoutStyle.Position = string.Equals(listStyle?.ListStylePosition, "inside", StringComparison.OrdinalIgnoreCase)
                ? "static"
                : "absolute";
            layoutStyle.Margin = new Thickness();
            layoutStyle.Padding = new Thickness();
            layoutStyle.BorderThickness = new Thickness();

            int markerId = _store.CreateBox(textNode, layoutStyle, LayoutBoxStore.BoxType.ListMarker);
            return (ListMarkerBox)_store.GetWrapper(markerId);
        }

        // The list-item counter value for this item (CSS Lists 3), computed once per
        // tree for every list in it: reversed lists, value/start, and author
        // counter-reset/-increment/-set on list-item all take part.
        private int ResolveListOrdinal(Element item)
        {
            var root = CounterRoot(item);
            if (!_listItemCounters.TryGetValue(root, out var values))
            {
                values = ListItemCounters.Compute(root, CounterStyleOf, FlatChildren);
                _listItemCounters[root] = values;
            }

            return values.TryGetValue(item, out var ordinal) ? ordinal : 1;
        }

        // Counters are numbered over the flat tree, the tree boxes are built from: a
        // generated ::before/::after in its originating element's tree, and shadow
        // content in its host's.
        private static Node CounterRoot(Element at)
        {
            Node root = at is PseudoElement pseudo && pseudo.OriginatingElement != null
                ? pseudo.OriginatingElement
                : at;
            while (true)
            {
                if (root.ParentNode != null)
                {
                    root = root.ParentNode;
                }
                else if (root is ShadowRoot shadowRoot && shadowRoot.Host != null)
                {
                    root = shadowRoot.Host;
                }
                else
                {
                    return root;
                }
            }
        }

        private CssComputed CounterStyleOf(Element element) =>
            _styles != null && _styles.TryGetValue(element, out var style) ? style : null;

        private IEnumerable<Node> FlatChildren(Node node) =>
            node is Element element ? GetChildren(element) : node.ChildNodes;

        /// <summary>
        /// CSS Generated Content §2 ('content'): the value is a list of strings, attr()
        /// references and quote keywords concatenated in order, so
        /// `attr(data-replicated-value) " "` yields the attribute text plus a space, never
        /// the declaration text. Counters show their value at the pseudo-element; url()
        /// images contribute no text here.
        /// </summary>
        private string NormalizePseudoText(string rawContent, PseudoElement pseudoElement)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return null;

            var text = new System.Text.StringBuilder();
            foreach (var item in PseudoBoxFactory.ParseContent(rawContent.Trim(), pseudoElement.OriginatingElement))
            {
                if (item is CounterContentItem counter)
                {
                    text.Append(FormatCounterItem(counter, pseudoElement));
                }
                else if (item is StringContentItem or AttrContentItem or QuoteContentItem)
                {
                    text.Append(item.GetText());
                }
            }

            return text.Length == 0 ? null : text.ToString();
        }

        // counter() shows the innermost instance of the counter in scope at the
        // element, counters() every instance joined by the separator, outermost
        // first (CSS Lists 3 §4.6). A counter nobody reset is instantiated at 0.
        private string FormatCounterItem(CounterContentItem counter, Element at)
        {
            var chain = ResolveCounterChain(at, counter.CounterName);
            var document = (at as PseudoElement)?.OriginatingElement?.OwnerDocument ?? at.OwnerDocument;
            var style = string.IsNullOrWhiteSpace(counter.ListStyleType) ? "decimal" : counter.ListStyleType;
            if (counter.Separator == null)
            {
                return ListMarkerFormatter.FormatCounterValue(chain[^1], style, document);
            }

            return string.Join(counter.Separator, chain.Select(value => ListMarkerFormatter.FormatCounterValue(value, style, document)));
        }

        private int[] ResolveCounterChain(Element at, string name)
        {
            var root = CounterRoot(at);
            if (!_counterChains.TryGetValue((root, name), out var chains))
            {
                chains = ListItemCounters.ComputeCounter(root, CounterStyleOf, name, FlatChildren);
                _counterChains[(root, name)] = chains;
            }

            return chains.TryGetValue(at, out var chain) && chain.Length > 0 ? chain : new[] { 0 };
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
