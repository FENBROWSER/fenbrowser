using System;
using System.Collections.Generic;
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
            return ConstructBox(root);
        }

        private LayoutBox ConstructBox(Node node)
        {
            // Get style (handle nulls safely)
            bool found = _styles.TryGetValue(node, out var style);
            if (found) Console.WriteLine($"[BoxTree] Found style for {node.GetType().Name} (Display: {style.Display}, W: {style.Width}, H: {style.Height})");
            else Console.WriteLine($"[BoxTree] No style found for {node.GetType().Name} (Hash: {node.GetHashCode()})");
            
            if (style == null && node is Element) style = new CssComputed(); 

            // 1. Handle Display: None
            if (style?.Display == "none") return null;

            // 2. Handle Text Nodes
            if (node is Text textNode)
            {
                if (string.IsNullOrWhiteSpace(textNode.Data)) return null; // Optimization: skip empty text for now? 
                // Wait, significant whitespace counts.
                // For now, let's keep all text nodes and let InlineLayout decide visibility.
                return new TextLayoutBox(textNode, style);
            }

            // 3. Handle Elements
            if (node is Element element)
            {
                // display: contents -> sub-children become children of parent (handled by recursion flattening)
                // BUT ConstructBox returns a single box. 
                // If this is display: contents, we formally don't produce a box for THIS node,
                // but we return its children? 
                // Return type is single LayoutBox. This suggests we need a different signature 
                // or we handle children loop in the caller.
                
                // Let's stick to standard boxes for now. display: contents is tricky.
                // We'll treat it as a BlockBox for MVP, but with 0 size? No, that breaks semantics.
                // Let's implement correct flatten later.
                
                var display = style?.Display?.ToLowerInvariant() ?? "block"; // Default to block
                
                LayoutBox box;
                if (display == "inline" || display == "inline-block") // Treat inline-block as InlineBox for tree, but it's atomic
                {
                    box = new InlineBox(node, style);
                }
                else
                {
                    box = new BlockBox(node, style);
                }

                // Recurse on children (Shadow DOM vs Light DOM)
                IEnumerable<Node> childrenToProcess = element.Children;
                if (element.ShadowRoot != null)
                {
                     // Shadow DOM Encapsulation: If shadow root exists, render its content instead of light children.
                     childrenToProcess = element.ShadowRoot.Children;
                }

                if (childrenToProcess != null)
                {
                    foreach (var childNode in childrenToProcess)
                    {
                        var childBox = ConstructBox(childNode);
                        if (childBox != null)
                        {
                            box.AddChild(childBox);
                        }
                    }
                }
                
                // Post-process: Anonymous Block Generation
                // If we are a BlockBox and we have mixed inline and block children, we need wrappers.
                // Spec: "In a block formatting context, boxes are laid out one after the other, vertically..."
                // Spec: "If a block container box... contains an inline-level box... then it establishes an inline formatting context."
                // Spec: "A block container either contains only block-level boxes or only inline-level boxes."
                
                if (box is BlockBox blockBox)
                {
                    FixupBlockChildren(blockBox);
                }

                return box;
            }

            return null; // Unknown node type (Comment, DocumentType etc)
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
                        currentAnon = new AnonymousBlockBox();
                        // Synthesize style? For now, leave null (transparent)
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
