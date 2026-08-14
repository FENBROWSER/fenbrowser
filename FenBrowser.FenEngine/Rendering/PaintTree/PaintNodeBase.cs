using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Abstract base class for paint tree nodes.
    /// </summary>
    public abstract class PaintNodeBase
    {
        public SKRect Bounds { get; init; }
        public float Opacity { get; init; } = 1.0f;
        public FenBrowser.Core.Dom.V2.Node SourceNode { get; init; }
        public ulong StableNodeId { get; set; }
        public bool IsFocused { get; init; }
        public bool IsHovered { get; init; }
        public SKRect? ClipRect { get; init; }
        public SKMatrix? Transform { get; init; }

        /// <summary>
        /// Child paint nodes in z-ordered paint sequence. Once a node is published as
        /// part of an ImmutablePaintTree this list must never be replaced in-place.
        /// Incremental retained-tree updates use <see cref="CloneWithChildren"/> so
        /// previous-frame trees remain stable for diff/raster consumers.
        /// </summary>
        public IReadOnlyList<PaintNodeBase> Children { get; set; } = System.Array.Empty<PaintNodeBase>();

        /// <summary>
        /// Shallow-copy the concrete paint-node subtype while replacing only its child
        /// list. All init-only visual state is preserved by MemberwiseClone; unchanged
        /// descendant/resource references remain shared by copy-on-write design.
        /// </summary>
        internal PaintNodeBase CloneWithChildren(IReadOnlyList<PaintNodeBase> children)
        {
            var clone = (PaintNodeBase)MemberwiseClone();
            clone.Children = children ?? System.Array.Empty<PaintNodeBase>();
            return clone;
        }

        public abstract void Accept(IPaintNodeVisitor visitor);

        public bool IntersectsViewport(SKRect viewport)
        {
            return Bounds.IntersectsWith(viewport);
        }
    }

    public sealed class BackgroundPaintNode : PaintNodeBase
    {
        public SKColor? Color { get; init; }
        public SKShader Gradient { get; init; }
        public SKPoint[] BorderRadius { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

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

    public sealed class BorderPaintNode : PaintNodeBase
    {
        public float[] Widths { get; init; }
        public SKColor[] Colors { get; init; }
        public string[] Styles { get; init; }
        public SKPoint[] BorderRadius { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class TextPaintNode : PaintNodeBase
    {
        public IReadOnlyList<PositionedGlyph> Glyphs { get; init; }
        public SKTypeface Typeface { get; init; }
        public float FontSize { get; init; }
        public SKColor Color { get; init; } = SKColors.Black;
        public string FallbackText { get; init; }
        public SKPoint TextOrigin { get; init; }
        public System.Collections.Generic.IReadOnlyList<string> TextDecorations { get; init; }
        public string WritingMode { get; init; } = "horizontal-tb";
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class ImagePaintNode : PaintNodeBase
    {
        public SKBitmap Bitmap { get; init; }
        public SKRect? SourceRect { get; init; }
        public string ObjectFit { get; init; } = "fill";
        public string ObjectPosition { get; init; } = "50% 50%";
        public bool IsBackgroundImage { get; init; }
        public SKShaderTileMode TileModeX { get; init; } = SKShaderTileMode.Clamp;
        public SKShaderTileMode TileModeY { get; init; } = SKShaderTileMode.Clamp;
        public SKPoint BackgroundPosition { get; init; }
        public SKSize? BackgroundImageSize { get; init; }
        public SKPoint BackgroundOrigin { get; init; }
        public bool BackgroundAttachmentFixed { get; init; }
        public SKPoint FixedViewportOrigin { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class StackingContextPaintNode : PaintNodeBase
    {
        public int ZIndex { get; init; }
        public string Filter { get; init; }
        public string BackdropFilter { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class OpacityGroupPaintNode : PaintNodeBase
    {
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class ClipPaintNode : PaintNodeBase
    {
        public SKPath ClipPath { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class CustomPaintNode : PaintNodeBase
    {
        public System.Action<SKCanvas, SKRect> PaintAction { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class MaskPaintNode : PaintNodeBase
    {
        public SKBitmap MaskBitmap { get; init; }
        public string MaskSize { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class ScrollPaintNode : PaintNodeBase
    {
        public float ScrollX { get; init; }
        public float ScrollY { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }

    public sealed class StickyPaintNode : PaintNodeBase
    {
        public SKPoint StickyOffset { get; init; }
        public override void Accept(IPaintNodeVisitor visitor) => visitor.Visit(this);
    }
}
