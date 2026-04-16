using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Abstract base class for paint tree nodes.
    /// 
    /// INVARIANTS (NON-NEGOTIABLE):
    /// - NO DOM REFERENCES
    /// - NO LAYOUT REFERENCES
    /// - NO ENGINE STATE
    /// - FULLY IMMUTABLE
    /// </summary>
    public abstract class PaintNodeBase
    {
        /// <summary>
        /// Bounding rectangle in document coordinates.
        /// </summary>
        public SKRect Bounds { get; init; }
        
        /// <summary>
        /// Opacity (1.0 = fully opaque, 0.0 = fully transparent).
        /// </summary>
        public float Opacity { get; init; } = 1.0f;

        /// <summary>
        /// The DOM node that created this paint node.
        /// Used for hit-testing.
        /// </summary>
        public FenBrowser.Core.Dom.V2.Node SourceNode { get; init; }

        /// <summary>
        /// Whether this node represents a focused element.
        /// </summary>
        public bool IsFocused { get; init; }

        /// <summary>
        /// Whether this node represents a hovered element.
        /// </summary>
        public bool IsHovered { get; init; }
        
        /// <summary>
        /// Optional clip rectangle.
        /// </summary>
        public SKRect? ClipRect { get; init; }
        
        /// <summary>
        /// Optional transform matrix.
        /// </summary>
        public SKMatrix? Transform { get; init; }
        
        /// <summary>
        /// Child nodes in paint order.
        /// </summary>
        public IReadOnlyList<PaintNodeBase> Children { get; init; } = System.Array.Empty<PaintNodeBase>();
        
        /// <summary>
        /// Accept a visitor for double dispatch.
        /// </summary>
        public abstract void Accept(IPaintNodeVisitor visitor);
        
        /// <summary>
        /// Whether this node is visible (bounds intersect with viewport).
        /// Used for culling optimization.
        /// </summary>
        public bool IntersectsViewport(SKRect viewport)
        {
            return Bounds.IntersectsWith(viewport);
        }
    }
    
    /// <summary>
    /// Paints a background (solid color or gradient).
    /// </summary>
    public sealed class BackgroundPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Solid background color.
        /// </summary>
        public SKColor? Color { get; init; }
        
        /// <summary>
        /// Optional gradient shader.
        /// </summary>
        public SKShader Gradient { get; init; }
        
        /// <summary>
        /// Border radius for rounded corners [topLeft, topRight, bottomRight, bottomLeft].
        /// </summary>
        public SKPoint[] BorderRadius { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Paints a CSS box-shadow.
    /// </summary>
    public sealed class BoxShadowPaintNode : PaintNodeBase
    {
        public float Blur { get; init; }
        public float Spread { get; init; }
        public SKPoint Offset { get; init; }
        public SKColor Color { get; init; }
        public SKPoint[] BorderRadius { get; init; }
        public bool Inset { get; init; }

        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Paints borders.
    /// </summary>
    public sealed class BorderPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Border widths [top, right, bottom, left].
        /// </summary>
        public float[] Widths { get; init; }
        
        /// <summary>
        /// Border colors [top, right, bottom, left].
        /// </summary>
        public SKColor[] Colors { get; init; }
        
        /// <summary>
        /// Border styles [top, right, bottom, left].
        /// Values: "solid", "dashed", "dotted", "double", "none"
        /// </summary>
        public string[] Styles { get; init; }
        
        /// <summary>
        /// Border radius for rounded corners [topLeft, topRight, bottomRight, bottomLeft].
        /// </summary>
        public SKPoint[] BorderRadius { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Paints text using pre-positioned glyphs.
    /// The renderer does NOT shape text - it receives positioned glyphs.
    /// </summary>
    public sealed class TextPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Pre-positioned glyphs to render.
        /// </summary>
        public IReadOnlyList<PositionedGlyph> Glyphs { get; init; }
        
        /// <summary>
        /// Font typeface for rendering.
        /// </summary>
        public SKTypeface Typeface { get; init; }
        
        /// <summary>
        /// Font size in pixels.
        /// </summary>
        public float FontSize { get; init; }
        
        /// <summary>
        /// Text color.
        /// </summary>
        public SKColor Color { get; init; } = SKColors.Black;
        
        /// <summary>
        /// Fallback: raw text string if glyphs not available.
        /// Used during migration - eventually all text should use glyphs.
        /// </summary>
        public string FallbackText { get; init; }
        
        /// <summary>
        /// Text start position (for fallback rendering).
        /// </summary>
        public SKPoint TextOrigin { get; init; }
        
        /// <summary>
        /// Text decorations: "underline", "line-through", "overline"
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> TextDecorations { get; init; }
        
        /// <summary>
        /// Writing mode for text layout: "horizontal-tb", "vertical-rl", etc.
        /// </summary>
        public string WritingMode { get; init; } = "horizontal-tb";
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Paints an image.
    /// </summary>
    public sealed class ImagePaintNode : PaintNodeBase
    {
        /// <summary>
        /// The bitmap to render.
        /// </summary>
        public SKBitmap Bitmap { get; init; }
        
        /// <summary>
        /// Source rectangle within the bitmap (for sprites/slicing).
        /// Null means use entire bitmap.
        /// </summary>
        public SKRect? SourceRect { get; init; }
        
        /// <summary>
        /// Object-fit mode: "fill", "contain", "cover", "none", "scale-down"
        /// </summary>
        public string ObjectFit { get; init; } = "fill";

        /// <summary>
        /// Whether this image node represents a CSS background image rather than a replaced element.
        /// Background images can tile and anchor to the viewport.
        /// </summary>
        public bool IsBackgroundImage { get; init; }

        /// <summary>
        /// Background tiling mode along the X axis.
        /// </summary>
        public SKShaderTileMode TileModeX { get; init; } = SKShaderTileMode.Clamp;

        /// <summary>
        /// Background tiling mode along the Y axis.
        /// </summary>
        public SKShaderTileMode TileModeY { get; init; } = SKShaderTileMode.Clamp;

        /// <summary>
        /// CSS background-position offset in px.
        /// </summary>
        public SKPoint BackgroundPosition { get; init; }

        /// <summary>
        /// Top-left of the box used for background-position/background-origin.
        /// Defaults to the paint bounds when not explicitly provided.
        /// </summary>
        public SKPoint BackgroundOrigin { get; init; }

        /// <summary>
        /// When true, the image is anchored to the viewport instead of the element box.
        /// </summary>
        public bool BackgroundAttachmentFixed { get; init; }

        /// <summary>
        /// Document-space top-left of the current viewport for fixed backgrounds.
        /// </summary>
        public SKPoint FixedViewportOrigin { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Groups children into a stacking context.
    /// Ensures atomic z-ordering of the group.
    /// </summary>
    public sealed class StackingContextPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Z-index value for ordering.
        /// </summary>
        public int ZIndex { get; init; }

        /// <summary>
        /// CSS filter string applied to this stacking context (front-filter).
        /// Stored as raw text; parsed by renderer to an SKImageFilter per frame.
        /// </summary>
        public string Filter { get; init; }

        /// <summary>
        /// CSS backdrop-filter string applied to the backdrop behind this context.
        /// Stored as raw text; renderer applies via SaveLayer with ImageFilter.
        /// </summary>
        public string BackdropFilter { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Applies opacity to a group of children.
    /// Children are composited together, then opacity applied.
    /// </summary>
    public sealed class OpacityGroupPaintNode : PaintNodeBase
    {
        // Opacity is inherited from base class
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Applies clipping to children.
    /// </summary>
    public sealed class ClipPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Clip path (for non-rectangular clips like border-radius).
        /// </summary>
        public SKPath ClipPath { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Paints custom content using a delegate.
    /// Used for form controls like checkbox, radio, color picker, range slider.
    /// </summary>
    public sealed class CustomPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Custom paint action that draws to the canvas.
        /// Parameters: (canvas, bounds)
        /// </summary>
        public System.Action<SKCanvas, SKRect> PaintAction { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
    
    /// <summary>
    /// Applies an alpha mask to children.
    /// </summary>
    public sealed class MaskPaintNode : PaintNodeBase
    {
        /// <summary>
        /// The mask image (alpha channel used).
        /// </summary>
        public SKBitmap MaskBitmap { get; init; }
        
        /// <summary>
        /// Mask sizing behavior (cover, contain, etc) - for now simplified to fill bounds.
        /// </summary>
        public string MaskSize { get; init; }
        
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    /// <summary>
    /// Applies a scroll offset (translation) to children.
    /// Represents a container with overflow: scroll/auto.
    /// </summary>
    public sealed class ScrollPaintNode : PaintNodeBase
    {
        /// <summary>
        /// Horizontal scroll offset.
        /// </summary>
        public float ScrollX { get; init; }

        /// <summary>
        /// Vertical scroll offset.
        /// </summary>
        public float ScrollY { get; init; }

        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    /// <summary>
    /// Applies a translation to simulate sticky positioning.
    /// </summary>
    public sealed class StickyPaintNode : PaintNodeBase
    {
        /// <summary>
        /// The calculated sticky offset (deltas).
        /// </summary>
        public SKPoint StickyOffset { get; init; }

        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
}

