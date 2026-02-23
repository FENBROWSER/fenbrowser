using FenBrowser.Core.Css;
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
        public void PaintBackground(SKCanvas canvas, SKRect box, CssComputed style, byte opacity = 255, SKBlendMode blendMode = SKBlendMode.SrcOver, ImagePainter imagePainter = null)
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
                    Style = SKPaintStyle.Fill,
                    BlendMode = blendMode
                };

                DrawWithRadius(canvas, box, style.BorderRadius, paint);
            }

            // Background image
            if (!string.IsNullOrEmpty(style.BackgroundImage) && imagePainter != null)
            {
                if (style.BackgroundImage.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
                {
                    string url = style.BackgroundImage.Substring(4).TrimEnd(')', ' ', '\'', '"').TrimStart(' ', '\'', '"');
                    var bitmap = imagePainter.GetCachedImage(url);
                    if (bitmap != null)
                    {
                        DrawBackgroundImage(canvas, box, style, bitmap, opacity, blendMode);
                    }
                }
            }
        }

        private void DrawBackgroundImage(SKCanvas canvas, SKRect box, CssComputed style, SKBitmap bitmap, byte opacity, SKBlendMode blendMode)
        {
            var bgSize = style.BackgroundSize?.ToLowerInvariant() ?? "auto";
            var bgRepeat = style.BackgroundRepeat?.ToLowerInvariant() ?? "repeat";
            var bgPos = style.BackgroundPosition?.ToLowerInvariant() ?? "0% 0%";

            var tileX = SKShaderTileMode.Repeat;
            var tileY = SKShaderTileMode.Repeat;
            if (bgRepeat == "no-repeat") { tileX = SKShaderTileMode.Decal; tileY = SKShaderTileMode.Decal; }
            else if (bgRepeat == "repeat-x") { tileY = SKShaderTileMode.Decal; }
            else if (bgRepeat == "repeat-y") { tileX = SKShaderTileMode.Decal; }

            float scaleX = 1f;
            float scaleY = 1f;

            float imgAspect = (float)bitmap.Width / bitmap.Height;
            float boxAspect = box.Width / box.Height;

            if (bgSize.Contains("cover"))
            {
                if (imgAspect > boxAspect) { scaleY = box.Height / bitmap.Height; scaleX = scaleY; }
                else { scaleX = box.Width / bitmap.Width; scaleY = scaleX; }
                tileX = SKShaderTileMode.Clamp; tileY = SKShaderTileMode.Clamp;
            }
            else if (bgSize.Contains("contain"))
            {
                if (imgAspect > boxAspect) { scaleX = box.Width / bitmap.Width; scaleY = scaleX; }
                else { scaleY = box.Height / bitmap.Height; scaleX = scaleY; }
                tileX = SKShaderTileMode.Clamp; tileY = SKShaderTileMode.Clamp;
            }
            else if (bgSize.Contains("%") || bgSize.Contains("px"))
            {
                var parts = bgSize.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && float.TryParse(parts[0].Replace("px", "").Replace("%", ""), out float w))
                {
                    if (bgSize.Contains("%")) scaleX = (box.Width * (w / 100f)) / bitmap.Width;
                    else scaleX = w / bitmap.Width;
                    scaleY = scaleX;
                }
            }

            float transX = 0;
            float transY = 0;
            if (bgPos.Contains("center")) 
            { 
               transX = (box.Width - bitmap.Width * scaleX) / 2;
               transY = (box.Height - bitmap.Height * scaleY) / 2;
            }
            else if (bgPos.Contains("right")) { transX = box.Width - bitmap.Width * scaleX; }
            else if (bgPos.Contains("bottom")) { transY = box.Height - bitmap.Height * scaleY; }

            var matrix = SKMatrix.CreateScale(scaleX, scaleY);
            matrix = matrix.PostConcat(SKMatrix.CreateTranslation(box.Left + transX, box.Top + transY));

            using var shader = SKShader.CreateBitmap(bitmap, tileX, tileY, matrix);
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = true,
                BlendMode = blendMode,
            };

            if (opacity < 255)
            {
                paint.Color = new SKColor(255, 255, 255, opacity);
            }

            DrawWithRadius(canvas, box, style.BorderRadius, paint);
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
            if (thickness.Top > 0 && style.BorderStyleTop != "none")
            {
                paint.StrokeWidth = (float)thickness.Top;
                SetupBorderStyle(paint, style.BorderStyleTop, paint.StrokeWidth);
                float y = box.Top + (float)thickness.Top / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, paint);
            }

            // Right
            if (thickness.Right > 0 && style.BorderStyleRight != "none")
            {
                paint.StrokeWidth = (float)thickness.Right;
                SetupBorderStyle(paint, style.BorderStyleRight, paint.StrokeWidth);
                float x = box.Right - (float)thickness.Right / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, paint);
            }

            // Bottom
            if (thickness.Bottom > 0 && style.BorderStyleBottom != "none")
            {
                paint.StrokeWidth = (float)thickness.Bottom;
                SetupBorderStyle(paint, style.BorderStyleBottom, paint.StrokeWidth);
                float y = box.Bottom - (float)thickness.Bottom / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, paint);
            }

            // Left
            if (thickness.Left > 0 && style.BorderStyleLeft != "none")
            {
                paint.StrokeWidth = (float)thickness.Left;
                SetupBorderStyle(paint, style.BorderStyleLeft, paint.StrokeWidth);
                float x = box.Left + (float)thickness.Left / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, paint);
            }
        }

        private void SetupBorderStyle(SKPaint paint, string borderStyle, float strokeWidth)
        {
            paint.PathEffect = null;
            paint.StrokeCap = SKStrokeCap.Butt;
            if (string.IsNullOrEmpty(borderStyle) || borderStyle == "solid") return;
            
            if (borderStyle == "dashed")
            {
                paint.PathEffect = SKPathEffect.CreateDash(new[] { strokeWidth * 3, strokeWidth * 3 }, 0);
            }
            else if (borderStyle == "dotted")
            {
                paint.PathEffect = SKPathEffect.CreateDash(new[] { strokeWidth, strokeWidth * 2 }, 0);
                paint.StrokeCap = SKStrokeCap.Round;
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
        private void DrawWithRadius(SKCanvas canvas, SKRect rect, CssCornerRadius radius, SKPaint paint)
        {
            if (radius.TopLeft.Value > 0 || radius.TopRight.Value > 0 || radius.BottomRight.Value > 0 || radius.BottomLeft.Value > 0)
            {
                // Simplified: use uniform radius from top-left
                float r = (float)Math.Max(radius.TopLeft.Value, Math.Max(radius.TopRight.Value, 
                    Math.Max(radius.BottomRight.Value, radius.BottomLeft.Value)));
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

