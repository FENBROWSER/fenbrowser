using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Shared context for layout computation.
    /// Holds all state needed during a single layout pass.
    /// </summary>
    public class LayoutContext
    {
        /// <summary>
        /// Computed box models for all laid-out elements.
        /// </summary>
        public Dictionary<Node, BoxModel> Boxes { get; } = new();
        
        /// <summary>
        /// Parent tracking for layout tree.
        /// </summary>
        public Dictionary<Node, Node> Parents { get; } = new();
        
        /// <summary>
        /// Computed styles for all elements.
        /// </summary>
        public IReadOnlyDictionary<Node, CssComputed> Styles { get; init; }
        
        /// <summary>
        /// Viewport width for percentage calculations.
        /// </summary>
        public float ViewportWidth { get; init; }
        
        /// <summary>
        /// Viewport height for percentage calculations.
        /// </summary>
        public float ViewportHeight { get; init; }
        
        /// <summary>
        /// Creates a new layout context.
        /// </summary>
        public LayoutContext(
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight)
        {
            Styles = styles ?? throw new ArgumentNullException(nameof(styles));
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
        }
        
        /// <summary>
        /// Gets the style for a node, or null if not found.
        /// </summary>
        public CssComputed GetStyle(Node node)
        {
            if (node != null && Styles.TryGetValue(node, out var style))
                return style;
            return null;
        }
        
        /// <summary>
        /// Gets the box model for a node, or null if not computed.
        /// </summary>
        public BoxModel GetBox(Node node)
        {
            if (node != null && Boxes.TryGetValue(node, out var box))
                return box;
            return null;
        }
        
        /// <summary>
        /// Gets the parent of a node.
        /// </summary>
        public Node GetParent(Node node)
        {
            if (node != null && Parents.TryGetValue(node, out var parent))
                return parent;
            return null;
        }
        
        /// <summary>
        /// Sets the box model for a node.
        /// </summary>
        public void SetBox(Node node, BoxModel box)
        {
            if (node != null && box != null)
                Boxes[node] = box;
        }
        
        /// <summary>
        /// Sets the parent of a node.
        /// </summary>
        public void SetParent(Node child, Node parent)
        {
            if (child != null)
                Parents[child] = parent;
        }
    }
}
