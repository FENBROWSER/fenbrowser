using System;
using System.Collections.Generic;
using FenBrowser.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Box tree node, distinct from DOM tree.
    /// Represents the layout box for a DOM element.
    /// 
    /// Pipeline position:
    /// Computed Styles → LayoutEngine → BoxTree → Painter
    /// </summary>
    public class BoxNode
    {
        /// <summary>
        /// The DOM element this box represents (null for anonymous boxes).
        /// </summary>
        public LiteElement Element { get; set; }

        /// <summary>
        /// Box type for layout purposes.
        /// </summary>
        public BoxType Type { get; set; } = BoxType.Block;

        /// <summary>
        /// Parent box node.
        /// </summary>
        public BoxNode Parent { get; set; }

        /// <summary>
        /// Child box nodes.
        /// </summary>
        public List<BoxNode> Children { get; } = new List<BoxNode>();

        #region Box Model Dimensions

        /// <summary>
        /// Content box (innermost rectangle, excludes padding).
        /// </summary>
        public SKRect ContentBox { get; set; }

        /// <summary>
        /// Padding box (content + padding).
        /// </summary>
        public SKRect PaddingBox { get; set; }

        /// <summary>
        /// Border box (content + padding + border).
        /// </summary>
        public SKRect BorderBox { get; set; }

        /// <summary>
        /// Margin box (content + padding + border + margin).
        /// </summary>
        public SKRect MarginBox { get; set; }

        /// <summary>
        /// Margin values.
        /// </summary>
        public Avalonia.Thickness Margin { get; set; }

        /// <summary>
        /// Padding values.
        /// </summary>
        public Avalonia.Thickness Padding { get; set; }

        /// <summary>
        /// Border values.
        /// </summary>
        public Avalonia.Thickness Border { get; set; }

        #endregion

        #region Layout Properties

        /// <summary>
        /// Computed styles for this box.
        /// </summary>
        public CssComputed Style { get; set; }

        /// <summary>
        /// Is this box out of normal flow (absolute, fixed)?
        /// </summary>
        public bool IsOutOfFlow { get; set; }

        /// <summary>
        /// Is this a sticky positioned element?
        /// </summary>
        public bool IsSticky { get; set; }

        /// <summary>
        /// Z-index for stacking context.
        /// </summary>
        public int ZIndex { get; set; }

        /// <summary>
        /// Baseline offset for inline alignment.
        /// </summary>
        public float Baseline { get; set; }

        /// <summary>
        /// Does this box establish a new stacking context?
        /// </summary>
        public bool CreatesStackingContext { get; set; }

        /// <summary>
        /// Does this box create a new block formatting context?
        /// </summary>
        public bool CreatesBlockFormattingContext { get; set; }

        #endregion

        #region Tree Navigation

        /// <summary>
        /// Add a child box.
        /// </summary>
        public void AddChild(BoxNode child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>
        /// Get all descendants (depth-first).
        /// </summary>
        public IEnumerable<BoxNode> GetDescendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var desc in child.GetDescendants())
                {
                    yield return desc;
                }
            }
        }

        /// <summary>
        /// Get ancestors from immediate parent to root.
        /// </summary>
        public IEnumerable<BoxNode> GetAncestors()
        {
            var current = Parent;
            while (current != null)
            {
                yield return current;
                current = current.Parent;
            }
        }

        #endregion

        public override string ToString()
        {
            return $"BoxNode[{Element?.Tag ?? "anon"}] {MarginBox.Width}x{MarginBox.Height}";
        }
    }

    /// <summary>
    /// Box type for layout categorization.
    /// </summary>
    public enum BoxType
    {
        Block,
        Inline,
        InlineBlock,
        Flex,
        FlexItem,
        Grid,
        GridItem,
        Table,
        TableRow,
        TableCell,
        ListItem,
        Anonymous,
        Replaced,  // img, video, etc.
        Fixed,
        Absolute
    }

    /// <summary>
    /// Box tree representing the layout structure.
    /// </summary>
    public class BoxTree
    {
        /// <summary>
        /// Root box node (typically the viewport or html element).
        /// </summary>
        public BoxNode Root { get; set; }

        /// <summary>
        /// Map from DOM elements to their box nodes.
        /// </summary>
        private readonly Dictionary<LiteElement, BoxNode> _elementToBox = new();

        /// <summary>
        /// Create a box for a DOM element.
        /// </summary>
        public BoxNode CreateBox(LiteElement element, CssComputed style)
        {
            var box = new BoxNode
            {
                Element = element,
                Style = style,
                Type = DetermineBoxType(style)
            };

            _elementToBox[element] = box;
            return box;
        }

        /// <summary>
        /// Get the box for a DOM element.
        /// </summary>
        public BoxNode GetBox(LiteElement element)
        {
            return _elementToBox.TryGetValue(element, out var box) ? box : null;
        }

        /// <summary>
        /// Check if element has a box.
        /// </summary>
        public bool HasBox(LiteElement element)
        {
            return _elementToBox.ContainsKey(element);
        }

        /// <summary>
        /// Get all boxes.
        /// </summary>
        public IEnumerable<BoxNode> AllBoxes => _elementToBox.Values;

        /// <summary>
        /// Clear all boxes.
        /// </summary>
        public void Clear()
        {
            Root = null;
            _elementToBox.Clear();
        }

        private static BoxType DetermineBoxType(CssComputed style)
        {
            if (style == null) return BoxType.Block;

            var display = style.Display?.ToLowerInvariant() ?? "block";
            var position = style.Position?.ToLowerInvariant() ?? "static";

            if (position == "fixed") return BoxType.Fixed;
            if (position == "absolute") return BoxType.Absolute;

            return display switch
            {
                "inline" => BoxType.Inline,
                "inline-block" => BoxType.InlineBlock,
                "flex" => BoxType.Flex,
                "inline-flex" => BoxType.Flex,
                "grid" => BoxType.Grid,
                "inline-grid" => BoxType.Grid,
                "table" => BoxType.Table,
                "table-row" => BoxType.TableRow,
                "table-cell" => BoxType.TableCell,
                "list-item" => BoxType.ListItem,
                "none" => BoxType.Anonymous,
                _ => BoxType.Block
            };
        }
    }
}
