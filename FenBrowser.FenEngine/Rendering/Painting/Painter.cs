using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
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
            Element element,
            SKRect box,
            CssComputed style,
            Dictionary<Element, SKRect> boxes)
        {
            if (style?.Display?.ToLowerInvariant() == "none") return;

            canvas.Save();

            // Apply transform if present
            ApplyTransform(canvas, box, style);

            // Apply mask-image if present
            bool hasMask = false;
            string maskUrl = null;
            if (!string.IsNullOrEmpty(style?.MaskImage) && style.MaskImage.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                maskUrl = style.MaskImage.Substring(4).TrimEnd(')', ' ', '\'', '"').TrimStart(' ', '\'', '"');
                var maskBitmap = _imagePainter.GetCachedImage(maskUrl);
                if (maskBitmap != null)
                {
                    hasMask = true;
                    canvas.SaveLayer(paint: null);
                }
            }

            // Apply clip-path if present
            SKPath clipPath = null;
            if (!string.IsNullOrEmpty(style?.ClipPath) && style.ClipPath != "none")
            {
                clipPath = Css.CssClipPathParser.Parse(style.ClipPath, box);
                if (clipPath != null)
                {
                    canvas.ClipPath(clipPath);
                }
            }

            // Apply CSS filter if present
            SKImageFilter filter = null;
            bool hasFilter = false;
            if (!string.IsNullOrEmpty(style?.Filter) && style.Filter != "none")
            {
                filter = Css.CssFilterParser.Parse(style.Filter);
                if (filter != null)
                {
                    hasFilter = true;
                    // Use SaveLayer with filter to apply effects to all content
                    using var paint = new SKPaint { ImageFilter = filter };
                    canvas.SaveLayer(paint);
                }
            }

            // Apply mix-blend-mode if present
            SKBlendMode blendMode = SKBlendMode.SrcOver;
            if (!string.IsNullOrEmpty(style?.MixBlendMode))
            {
                blendMode = ParseBlendMode(style.MixBlendMode);
            }

            // Apply opacity
            byte opacity = 255;
            if (style?.Opacity.HasValue == true)
            {
                opacity = (byte)(style.Opacity.Value * 255);
            }

            // 1. Box shadows (painted behind)
            _boxPainter.PaintBoxShadow(canvas, box, style);

            // 2. Backdrop filter (applied before background, affects content behind)
            if (!string.IsNullOrEmpty(style?.BackdropFilter) && style.BackdropFilter != "none")
            {
                ApplyBackdropFilter(canvas, box, style.BackdropFilter);
            }

            // 3. Background (with blend mode)
            _boxPainter.PaintBackground(canvas, box, style, opacity, blendMode, _imagePainter);

            // 4. Border
            _boxPainter.PaintBorder(canvas, box, style, opacity);

            // 5. Content (text or image)
            if (element.TagName?.ToLowerInvariant() == "img")
            {
                _imagePainter.PaintImage(canvas, element, box, style);
            }
            else if (element.IsText() || !string.IsNullOrEmpty(element.Text))
            {
                _textPainter.PaintText(canvas, element.Text ?? "", box, style);
            }

            // Restore filter layer if we used one
            if (hasFilter)
            {
                canvas.Restore();
                filter?.Dispose();
            }

            if (hasMask)
            {
                var maskBitmap = _imagePainter.GetCachedImage(maskUrl);
                if (maskBitmap != null)
                {
                    using var maskPaint = new SKPaint
                    {
                        BlendMode = SKBlendMode.DstIn,
                        IsAntialias = true
                    };
                    canvas.DrawBitmap(maskBitmap, box, maskPaint);
                }
                canvas.Restore();
            }

            canvas.Restore();
        }

        /// <summary>
        /// Apply backdrop-filter effect (blur, etc. applied to content behind element)
        /// </summary>
        private void ApplyBackdropFilter(SKCanvas canvas, SKRect box, string backdropFilter)
        {
            var filter = Css.CssFilterParser.Parse(backdropFilter);
            if (filter == null) return;

            // Create a temporary surface to capture the background
            // Note: This is a simplified implementation - full impl would need access to frame buffer
            using var paint = new SKPaint { ImageFilter = filter };
            
            // Draw the backdrop effect by saving layer with the filter
            // In a full implementation, we'd sample from the existing frame buffer
            canvas.SaveLayer(box, paint);
            canvas.ClipRect(box);
            // The actual backdrop sampling would happen here in a full impl
            canvas.Restore();
            
            filter.Dispose();
        }

        /// <summary>
        /// Parse CSS mix-blend-mode to SKBlendMode
        /// </summary>
        private static SKBlendMode ParseBlendMode(string mode)
        {
            switch (mode?.Trim().ToLowerInvariant())
            {
                case "multiply": return SKBlendMode.Multiply;
                case "screen": return SKBlendMode.Screen;
                case "overlay": return SKBlendMode.Overlay;
                case "darken": return SKBlendMode.Darken;
                case "lighten": return SKBlendMode.Lighten;
                case "color-dodge": return SKBlendMode.ColorDodge;
                case "color-burn": return SKBlendMode.ColorBurn;
                case "hard-light": return SKBlendMode.HardLight;
                case "soft-light": return SKBlendMode.SoftLight;
                case "difference": return SKBlendMode.Difference;
                case "exclusion": return SKBlendMode.Exclusion;
                case "hue": return SKBlendMode.Hue;
                case "saturation": return SKBlendMode.Saturation;
                case "color": return SKBlendMode.Color;
                case "luminosity": return SKBlendMode.Luminosity;
                default: return SKBlendMode.SrcOver;
            }
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
        public void PaintImage(SKCanvas canvas, Element element, SKRect box, CssComputed style, SKBitmap bitmap = null)
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
            var radius = style?.BorderRadius ?? new CssCornerRadius(0);
            if (radius.TopLeft.Value > 0 || radius.TopRight.Value > 0 || radius.BottomRight.Value > 0 || radius.BottomLeft.Value > 0)
            {
                using var path = new SKPath();
                var r = (float)radius.TopLeft.Value;
                path.AddRoundRect(clipRect, r, r);
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





