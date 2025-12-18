using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;
using SkiaSharp;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Paints box model elements: backgrounds, borders, and box shadows.
    /// </summary>
    public class BoxPainter
    {
        /// <summary>
        /// Paint background (color, gradient, or image).
        /// </summary>
        public void PaintBackground(SKCanvas canvas, SKRect box, CssComputed style, byte opacity = 255)
        {
            if (style == null) return;

            // Background color
            if (style.BackgroundColor.HasValue)
            {
                var color = style.BackgroundColor.Value;
                using var paint = new SKPaint
                {
                    Color = new SKColor(color.Red, color.Green, color.Blue, (byte)(color.Alpha * opacity / 255)),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                DrawWithRadius(canvas, box, style.BorderRadius, paint);
            }

            // [MIGRATION] Gradient brushes temporarily disabled or need custom Brush implementation
            /*
            if (style.Background != null)
            {
                // ...
            }
            */
        }

        private SKShader CreateShaderFromBrush(object brush, SKRect bounds, float opacity)
        {
            return null; 
        }

        /// <summary>
        /// Paint box shadow.
        /// </summary>
        public void PaintBoxShadow(SKCanvas canvas, SKRect box, CssComputed style)
        {
            if (string.IsNullOrEmpty(style?.BoxShadow)) return;

            // Parse box-shadow: offsetX offsetY blur spread color [inset]
            var shadow = ParseBoxShadow(style.BoxShadow);
            if (shadow == null) return;

            if (!shadow.Inset)
            {
                var shadowRect = new SKRect(
                    box.Left + shadow.OffsetX - shadow.Spread,
                    box.Top + shadow.OffsetY - shadow.Spread,
                    box.Right + shadow.OffsetX + shadow.Spread,
                    box.Bottom + shadow.OffsetY + shadow.Spread);

                using var paint = new SKPaint
                {
                    Color = shadow.Color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    MaskFilter = shadow.Blur > 0 
                        ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.Blur / 2) 
                        : null
                };

                canvas.DrawRect(shadowRect, paint);
            }
        }

        /// <summary>
        /// Paint border.
        /// </summary>
        public void PaintBorder(SKCanvas canvas, SKRect box, CssComputed style, byte opacity = 255)
        {
            if (style == null) return;

            var thickness = style.BorderThickness;
            if (thickness.Left <= 0 && thickness.Top <= 0 && thickness.Right <= 0 && thickness.Bottom <= 0)
                return;

            // Get border color
            SKColor borderColor = SKColors.Black;
            if (style.BorderBrushColor.HasValue)
            {
                var c = style.BorderBrushColor.Value;
                borderColor = new SKColor(c.Red, c.Green, c.Blue, (byte)(c.Alpha * opacity / 255));
            }

            // Draw each side
            using var paint = new SKPaint
            {
                Color = borderColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // Top
            if (thickness.Top > 0)
            {
                paint.StrokeWidth = (float)thickness.Top;
                float y = box.Top + (float)thickness.Top / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, paint);
            }

            // Right
            if (thickness.Right > 0)
            {
                paint.StrokeWidth = (float)thickness.Right;
                float x = box.Right - (float)thickness.Right / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, paint);
            }

            // Bottom
            if (thickness.Bottom > 0)
            {
                paint.StrokeWidth = (float)thickness.Bottom;
                float y = box.Bottom - (float)thickness.Bottom / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, paint);
            }

            // Left
            if (thickness.Left > 0)
            {
                paint.StrokeWidth = (float)thickness.Left;
                float x = box.Left + (float)thickness.Left / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, paint);
            }
        }

        /// <summary>
        /// Paint outline (similar to border but outside).
        /// </summary>
        public void PaintOutline(SKCanvas canvas, SKRect box, CssComputed style)
        {
            // Outline implementation if needed
        }

        /// <summary>
        /// Draw rectangle with border radius.
        /// </summary>
        private void DrawWithRadius(SKCanvas canvas, SKRect rect, CornerRadius radius, SKPaint paint)
        {
            if (radius.TopLeft > 0 || radius.TopRight > 0 || radius.BottomRight > 0 || radius.BottomLeft > 0)
            {
                // Simplified: use uniform radius from top-left
                float r = (float)Math.Max(radius.TopLeft, Math.Max(radius.TopRight, 
                    Math.Max(radius.BottomRight, radius.BottomLeft)));
                canvas.DrawRoundRect(rect, r, r, paint);
            }
            else
            {
                canvas.DrawRect(rect, paint);
            }
        }

        /// <summary>
        /// Parse box-shadow CSS value.
        /// </summary>
        private BoxShadowInfo ParseBoxShadow(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "none") return null;

            try
            {
                // Simple parser: offsetX offsetY blur spread color
                var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var info = new BoxShadowInfo();

                int i = 0;
                if (parts[0].ToLowerInvariant() == "inset")
                {
                    info.Inset = true;
                    i++;
                }

                // Parse numeric values
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float ox))
                {
                    info.OffsetX = ox;
                    i++;
                }
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float oy))
                {
                    info.OffsetY = oy;
                    i++;
                }
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float blur))
                {
                    info.Blur = blur;
                    i++;
                }
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float spread))
                {
                    info.Spread = spread;
                    i++;
                }

                // Rest is color
                if (i < parts.Length)
                {
                    info.Color = ParseColor(string.Join(" ", parts, i, parts.Length - i));
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        private SKColor ParseColor(string value)
        {
            value = value.Trim().ToLowerInvariant();
            
            if (value.StartsWith("#"))
            {
                var hex = value.Substring(1);
                if (hex.Length == 6)
                {
                    return new SKColor(
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }

            // Default fallback
            return new SKColor(0, 0, 0, 128);
        }

        private class BoxShadowInfo
        {
            public float OffsetX;
            public float OffsetY;
            public float Blur;
            public float Spread;
            public SKColor Color = new SKColor(0, 0, 0, 64);
            public bool Inset;
        }
    }
}
