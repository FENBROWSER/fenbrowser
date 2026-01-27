using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom;
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

            // Get style (handle nulls safely)
            bool found = _styles.TryGetValue(node, out var style);
            
            // Fallback to node.ComputedStyle if dictionary lookup fails (instance mismatch)
            if (style == null && node.ComputedStyle != null) style = node.ComputedStyle;
            
            if (style == null && node is Element) style = new CssComputed();
            
            // For text nodes, inherit from parent
            if (style == null && node is Text) style = parentStyle ?? new CssComputed();

            var display = style?.Display?.ToLowerInvariant() ?? (node is Text ? "inline" : "block");

            // 1. Handle Display: None and Hidden Tags
            if (display == "none") return result;
            
            if (node is Element e)
            {
                string tag = e.TagName?.ToUpperInvariant();
                if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "TEMPLATE")
                    return result;
            }

            // 2. Handle Text Nodes
            if (node is Text textNode)
            {
                // Preserve whitespace-only nodes but normalize them if they are too long? 
                // For now, only drop IF they are totally empty (not even space).
                if (string.IsNullOrEmpty(textNode.Data)) return result;
                
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

                // Recurse on children
                var childBoxes = new List<LayoutBox>();
                foreach (var childNode in GetChildren(element))
                {
                    childBoxes.AddRange(ConstructBox(childNode, style));
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
            if (element.ShadowRoot != null) return element.ShadowRoot.Children;
            return element.Children;
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

            foreach (var child in box.Children)
            {
                if (IsBlockLevel(child)) hasBlockChildren = true;
                if (IsInlineLevel(child)) hasInlineChildren = true;
            }

            // If homogeneous, no fixup needed
            if (!hasBlockChildren || !hasInlineChildren) return;

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
    }
}
