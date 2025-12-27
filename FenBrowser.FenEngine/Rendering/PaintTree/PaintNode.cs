using SkiaSharp;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A node in the paint tree representing a single paintable item.
    /// Ordered correctly for z-index and stacking contexts.
    /// </summary>
    public sealed class PaintNode
    {
        /// <summary>
        /// Reference to the DOM node this paint node represents.
        /// </summary>
        public Node DomNode { get; set; }
        
        /// <summary>
        /// Computed box model from layout.
        /// </summary>
        public BoxModel Box { get; set; }
        
        /// <summary>
        /// Computed style for this node.
        /// </summary>
        public CssComputed Style { get; set; }
        
        /// <summary>
        /// CSS z-index value (0 if not specified).
        /// </summary>
        public int ZIndex { get; set; }
        
        /// <summary>
        /// Whether this node creates a new stacking context.
        /// </summary>
        public bool CreatesStackingContext { get; set; }
        
        /// <summary>
        /// Computed opacity (1.0 = fully opaque).
        /// </summary>
        public float Opacity { get; set; } = 1.0f;
        
        /// <summary>
        /// Clip rectangle if overflow:hidden or similar.
        /// </summary>
        public SKRect? ClipRect { get; set; }
        
        /// <summary>
        /// Child paint nodes (already sorted by paint order).
        /// </summary>
        public List<PaintNode> Children { get; set; }
        
        /// <summary>
        /// Computed background color (null if transparent).
        /// </summary>
        public SKColor? BackgroundColor { get; set; }
        
        /// <summary>
        /// Computed border color (null if no border).
        /// </summary>
        public SKColor? BorderColor { get; set; }
        
        /// <summary>
        /// Text content if this is a text node.
        /// </summary>
        public string TextContent { get; set; }
        
        /// <summary>
        /// Whether this node is visible (not display:none or visibility:hidden).
        /// </summary>
        public bool IsVisible { get; set; } = true;
        
        /// <summary>
        /// Whether this is a text node.
        /// </summary>
        public bool IsText { get; set; }
        
        /// <summary>
        /// Background image URL if any.
        /// </summary>
        public string BackgroundImage { get; set; }
        
        /// <summary>
        /// Transform matrix if any CSS transforms are applied.
        /// </summary>
        public SKMatrix? Transform { get; set; }
        
        /// <summary>
        /// Border radius values [topLeft, topRight, bottomRight, bottomLeft].
        /// </summary>
        public float[] BorderRadius { get; set; }

        /// <summary>
        /// List of box shadows to apply.
        /// </summary>
        public List<BoxShadowParsed> BoxShadows { get; set; }

        /// <summary>
        /// Aggregate visual bounds of this node and all its children.
        /// Used for dirty-rect culling.
        /// </summary>
        public SKRect VisualBounds { get; set; }
        
        /// <summary>
        /// Creates an empty paint node.
        /// </summary>
        public PaintNode()
        {
            Children = new List<PaintNode>();
        }
        
        /// <summary>
        /// Creates a paint node from a DOM element.
        /// </summary>
        public static PaintNode FromElement(Element element, BoxModel box, CssComputed style)
        {
            return new PaintNode
            {
                DomNode = element,
                Box = box,
                Style = style,
                IsVisible = style?.Display?.ToLowerInvariant() != "none" && 
                           style?.Visibility?.ToLowerInvariant() != "hidden",
                Opacity = (float)(style?.Opacity ?? 1.0),
                CreatesStackingContext = DetermineCreatesStackingContext(style),
                ZIndex = ResolveZIndex(style)
            };
        }
        
        /// <summary>
        /// Determines if this element creates a stacking context.
        /// </summary>
        private static bool DetermineCreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;
            
            // position: fixed or sticky always creates stacking context
            string pos = style.Position?.ToLowerInvariant();
            if (pos == "fixed" || pos == "sticky") return true;
            
            // position: absolute or relative with z-index creates stacking context
            if ((pos == "absolute" || pos == "relative") && style.ZIndex.HasValue)
                return true;
            
            // opacity < 1 creates stacking context
            if (style.Opacity.HasValue && style.Opacity.Value < 1.0)
                return true;
            
            // transform creates stacking context
            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
                return true;
            
            // will-change creates stacking context
            if (!string.IsNullOrEmpty(style.WillChange) && style.WillChange != "auto")
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Resolves z-index to an integer value.
        /// </summary>
        private static int ResolveZIndex(CssComputed style)
        {
            if (style?.ZIndex.HasValue == true)
                return style.ZIndex.Value;
            return 0;
        }
    }
}
