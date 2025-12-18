using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Main painting orchestrator.
    /// Coordinates BoxPainter, TextPainter, and ImagePainter.
    /// </summary>
    public class Painter
    {
        private readonly BoxPainter _boxPainter;
        private readonly TextPainter _textPainter;
        private readonly ImagePainter _imagePainter;

        public Painter()
        {
            _boxPainter = new BoxPainter();
            _textPainter = new TextPainter();
            _imagePainter = new ImagePainter();
        }

        /// <summary>
        /// Paint an element and its content.
        /// </summary>
        public void PaintElement(
            SKCanvas canvas,
            LiteElement element,
            SKRect box,
            CssComputed style,
            Dictionary<LiteElement, SKRect> boxes)
        {
            if (style?.Display?.ToLowerInvariant() == "none") return;

            canvas.Save();

            // Apply transform if present
            ApplyTransform(canvas, box, style);

            // Apply opacity
            byte opacity = 255;
            if (style?.Opacity.HasValue == true)
            {
                opacity = (byte)(style.Opacity.Value * 255);
            }

            // 1. Box shadows (painted behind)
            _boxPainter.PaintBoxShadow(canvas, box, style);

            // 2. Background
            _boxPainter.PaintBackground(canvas, box, style, opacity);

            // 3. Border
            _boxPainter.PaintBorder(canvas, box, style, opacity);

            // 4. Content (text or image)
            if (element.Tag?.ToLowerInvariant() == "img")
            {
                _imagePainter.PaintImage(canvas, element, box, style);
            }
            else if (element.IsText || !string.IsNullOrEmpty(element.Text))
            {
                _textPainter.PaintText(canvas, element.Text ?? "", box, style);
            }

            canvas.Restore();
        }

        /// <summary>
        /// Paint text content.
        /// </summary>
        public void PaintText(SKCanvas canvas, string text, SKRect box, CssComputed style)
        {
            _textPainter.PaintText(canvas, text, box, style);
        }

        /// <summary>
        /// Paint image content.
        /// </summary>
        public void PaintImage(SKCanvas canvas, LiteElement element, SKRect box, CssComputed style, SKBitmap bitmap = null)
        {
            _imagePainter.PaintImage(canvas, element, box, style, bitmap);
        }

        /// <summary>
        /// Apply CSS transform to canvas.
        /// </summary>
        private void ApplyTransform(SKCanvas canvas, SKRect box, CssComputed style)
        {
            if (string.IsNullOrEmpty(style?.Transform) || style.Transform == "none")
                return;

            // Use existing CssTransform3D for parsing
            var transform = CssTransform3D.Parse(style.Transform);
            var matrix = transform.ToSKMatrix();

            // Apply transform origin (default: center)
            float ox = box.Left + box.Width / 2;
            float oy = box.Top + box.Height / 2;

            canvas.Translate(ox, oy);
            canvas.Concat(ref matrix);
            canvas.Translate(-ox, -oy);
        }

        /// <summary>
        /// Begin a clip region.
        /// </summary>
        public void BeginClip(SKCanvas canvas, SKRect clipRect, CssComputed style)
        {
            var radius = style?.BorderRadius ?? new CornerRadius(0);
            if (radius.TopLeft > 0 || radius.TopRight > 0 || radius.BottomRight > 0 || radius.BottomLeft > 0)
            {
                using var path = new SKPath();
                path.AddRoundRect(clipRect, 
                    (float)radius.TopLeft, (float)radius.TopLeft);
                canvas.ClipPath(path);
            }
            else
            {
                canvas.ClipRect(clipRect);
            }
        }

        /// <summary>
        /// Get sub-painters for direct access.
        /// </summary>
        public BoxPainter BoxPainter => _boxPainter;
        public TextPainter TextPainter => _textPainter;
        public ImagePainter ImagePainter => _imagePainter;
    }
}
