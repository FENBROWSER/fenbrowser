using FenBrowser.Core.Css;
using System;
using System.Collections.Generic;
using System.Linq;
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

            // Handle background clip
            SKRect bgBox = box;
            if (!string.IsNullOrEmpty(style.BackgroundClip))
            {
                var clip = style.BackgroundClip.ToLowerInvariant();
                var border = style.BorderThickness;
                var pad = style.Padding;
                
                if (clip == "padding-box")
                {
                    bgBox = new SKRect(
                        box.Left + (float)border.Left,
                        box.Top + (float)border.Top,
                        box.Right - (float)border.Right,
                        box.Bottom - (float)border.Bottom);
                }
                else if (clip == "content-box")
                {
                    bgBox = new SKRect(
                        box.Left + (float)border.Left + (float)pad.Left,
                        box.Top + (float)border.Top + (float)pad.Top,
                        box.Right - (float)border.Right - (float)pad.Right,
                        box.Bottom - (float)border.Bottom - (float)pad.Bottom);
                }
            }

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

                DrawWithRadius(canvas, bgBox, style.BorderRadius, paint);
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
                        DrawBackgroundImage(canvas, bgBox, box, style, bitmap, opacity, blendMode);
                    }
                }
            }
        }

        private void DrawBackgroundImage(SKCanvas canvas, SKRect bgBox, SKRect borderBox, CssComputed style, SKBitmap bitmap, byte opacity, SKBlendMode blendMode)
        {
            var box = bgBox;
            var bgSize = style.BackgroundSize?.ToLowerInvariant() ?? "auto";
            var bgAttachment = style.BackgroundAttachment?.ToLowerInvariant() ?? "scroll";
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
            
            // Handle background origin
            SKRect originBox = borderBox;
            var origin = style.BackgroundOrigin?.ToLowerInvariant();
            if (bgAttachment == "fixed")
            {
                originBox = canvas.LocalClipBounds;
            }
            else if (origin == "padding-box")
            {
                var b = style.BorderThickness;
                originBox = new SKRect(borderBox.Left + (float)b.Left, borderBox.Top + (float)b.Top, borderBox.Right - (float)b.Right, borderBox.Bottom - (float)b.Bottom);
            }
            else if (origin == "content-box")
            {
                var b = style.BorderThickness;
                var p = style.Padding;
                originBox = new SKRect(borderBox.Left + (float)b.Left + (float)p.Left, borderBox.Top + (float)b.Top + (float)p.Top, borderBox.Right - (float)b.Right - (float)p.Right, borderBox.Bottom - (float)b.Bottom - (float)p.Bottom);
            }

            ResolveBackgroundPosition(
                bgPos,
                originBox,
                bitmap.Width * scaleX,
                bitmap.Height * scaleY,
                out transX,
                out transY);

            var matrix = SKMatrix.CreateScale(scaleX, scaleY);
            matrix = matrix.PostConcat(SKMatrix.CreateTranslation(originBox.Left - transX, originBox.Top - transY));

            using var shader = SKShader.CreateBitmap(bitmap, tileX, tileY, matrix);
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = true,
                BlendMode = blendMode,
            };

            var radius = style.BorderRadius.ClampNonNegative();
            if (radius.IsZero)
            {
                 radius = CssCornerRadius.Empty;
            }
            DrawWithRadius(canvas, bgBox, radius, paint);
        }

        private static void ResolveBackgroundPosition(
            string bgPos,
            SKRect originBox,
            float renderedWidth,
            float renderedHeight,
            out float transX,
            out float transY)
        {
            transX = 0f;
            transY = 0f;

            if (string.IsNullOrWhiteSpace(bgPos))
            {
                return;
            }

            var parts = bgPos
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().ToLowerInvariant())
                .ToArray();

            if (parts.Length == 0)
            {
                return;
            }

            if (TryResolveBackgroundPositionComponent(parts[0], originBox.Width, renderedWidth, isHorizontal: true, out var resolvedX))
            {
                transX = resolvedX;
            }

            string verticalToken = parts.Length > 1 ? parts[1] : null;
            if (!string.IsNullOrEmpty(verticalToken) &&
                TryResolveBackgroundPositionComponent(verticalToken, originBox.Height, renderedHeight, isHorizontal: false, out var resolvedY))
            {
                transY = resolvedY;
            }
            else if (parts.Length == 1)
            {
                if (parts[0] == "top" || parts[0] == "bottom")
                {
                    if (TryResolveBackgroundPositionComponent(parts[0], originBox.Height, renderedHeight, isHorizontal: false, out resolvedY))
                    {
                        transY = resolvedY;
                        transX = 0f;
                    }
                }
                else if (parts[0] == "center")
                {
                    transX = (originBox.Width - renderedWidth) / 2f;
                    transY = (originBox.Height - renderedHeight) / 2f;
                }
            }
        }

        private static bool TryResolveBackgroundPositionComponent(
            string token,
            float axisLength,
            float renderedLength,
            bool isHorizontal,
            out float translation)
        {
            translation = 0f;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            switch (token)
            {
                case "left":
                case "top":
                    translation = 0f;
                    return true;
                case "center":
                    translation = (axisLength - renderedLength) / 2f;
                    return true;
                case "right":
                case "bottom":
                    translation = axisLength - renderedLength;
                    return true;
            }

            if (token.EndsWith("%", StringComparison.Ordinal) &&
                float.TryParse(token[..^1], out var percent))
            {
                translation = (axisLength - renderedLength) * (percent / 100f);
                return true;
            }

            if (TryParseCssLengthPx(token, out var px))
            {
                translation = px;
                return true;
            }

            // Single-keyword vertical positions like `top`/`bottom` should not be consumed
            // as horizontal offsets.
            if (isHorizontal && (token == "top" || token == "bottom"))
            {
                return false;
            }

            return false;
        }

        private static bool TryParseCssLengthPx(string token, out float px)
        {
            px = 0f;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string normalized = token.Trim().ToLowerInvariant();
            if (normalized.EndsWith("px", StringComparison.Ordinal))
            {
                normalized = normalized[..^2];
            }

            return float.TryParse(normalized, out px);
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

            var radius = style.BorderRadius.ClampNonNegative();
            bool hasRadius = !radius.IsZero;

            // When border-radius is present, draw a single stroked rounded rect
            // so corners are continuous curves instead of straight-line segments.
            if (hasRadius)
            {
                // Use the average border width for uniform stroke since we have a
                // single border color. CSS spec says when all sides differ the
                // border follows the padding edge offset; using the average is a
                // reasonable approximation for the uniform-color case.
                float avgStroke = 0;
                int sideCount = 0;
                if (thickness.Top > 0 && IsPaintableBorderStyle(style.BorderStyleTop)) { avgStroke += (float)thickness.Top; sideCount++; }
                if (thickness.Right > 0 && IsPaintableBorderStyle(style.BorderStyleRight)) { avgStroke += (float)thickness.Right; sideCount++; }
                if (thickness.Bottom > 0 && IsPaintableBorderStyle(style.BorderStyleBottom)) { avgStroke += (float)thickness.Bottom; sideCount++; }
                if (thickness.Left > 0 && IsPaintableBorderStyle(style.BorderStyleLeft)) { avgStroke += (float)thickness.Left; sideCount++; }
                if (sideCount == 0) return;
                avgStroke /= sideCount;

                // Inset the rect by half the stroke so the stroke lands exactly
                // on the border box edge (Skia strokes centered on the path).
                float halfStroke = avgStroke / 2f;
                var strokeRect = new SKRect(
                    box.Left + halfStroke,
                    box.Top + halfStroke,
                    box.Right - halfStroke,
                    box.Bottom - halfStroke);

                using var paint = new SKPaint
                {
                    Color = borderColor,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = avgStroke
                };

                // Pick the dominant border style for the path effect
                string dominantStyle = style.BorderStyleTop ?? style.BorderStyleRight ?? style.BorderStyleBottom ?? style.BorderStyleLeft ?? "solid";
                SetupBorderStyle(paint, dominantStyle, avgStroke);

                // Build per-corner radii: Skia expects [tl.x, tl.y, tr.x, tr.y, br.x, br.y, bl.x, bl.y]
                float rtl = Math.Max(0, (float)radius.TopLeft.Value - halfStroke);
                float rtr = Math.Max(0, (float)radius.TopRight.Value - halfStroke);
                float rbr = Math.Max(0, (float)radius.BottomRight.Value - halfStroke);
                float rbl = Math.Max(0, (float)radius.BottomLeft.Value - halfStroke);

                using var rrect = new SKRoundRect();
                rrect.SetRectRadii(strokeRect, new[]
                {
                    new SKPoint(rtl, rtl),
                    new SKPoint(rtr, rtr),
                    new SKPoint(rbr, rbr),
                    new SKPoint(rbl, rbl)
                });

                canvas.DrawRoundRect(rrect, paint);
                return;
            }

            // No border-radius: draw each side as a straight line (original path).
            using var linePaint = new SKPaint
            {
                Color = borderColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // Top
            if (thickness.Top > 0 && IsPaintableBorderStyle(style.BorderStyleTop))
            {
                linePaint.StrokeWidth = (float)thickness.Top;
                SetupBorderStyle(linePaint, style.BorderStyleTop, linePaint.StrokeWidth);
                float y = box.Top + (float)thickness.Top / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, linePaint);
            }

            // Right
            if (thickness.Right > 0 && IsPaintableBorderStyle(style.BorderStyleRight))
            {
                linePaint.StrokeWidth = (float)thickness.Right;
                SetupBorderStyle(linePaint, style.BorderStyleRight, linePaint.StrokeWidth);
                float x = box.Right - (float)thickness.Right / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, linePaint);
            }

            // Bottom
            if (thickness.Bottom > 0 && IsPaintableBorderStyle(style.BorderStyleBottom))
            {
                linePaint.StrokeWidth = (float)thickness.Bottom;
                SetupBorderStyle(linePaint, style.BorderStyleBottom, linePaint.StrokeWidth);
                float y = box.Bottom - (float)thickness.Bottom / 2;
                canvas.DrawLine(box.Left, y, box.Right, y, linePaint);
            }

            // Left
            if (thickness.Left > 0 && IsPaintableBorderStyle(style.BorderStyleLeft))
            {
                linePaint.StrokeWidth = (float)thickness.Left;
                SetupBorderStyle(linePaint, style.BorderStyleLeft, linePaint.StrokeWidth);
                float x = box.Left + (float)thickness.Left / 2;
                canvas.DrawLine(x, box.Top, x, box.Bottom, linePaint);
            }
        }

        private static bool IsPaintableBorderStyle(string borderStyle)
        {
            if (string.IsNullOrWhiteSpace(borderStyle))
            {
                return false;
            }

            return !string.Equals(borderStyle, "none", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(borderStyle, "hidden", StringComparison.OrdinalIgnoreCase);
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
            if (style == null || string.IsNullOrEmpty(style.OutlineWidth) || style.OutlineStyle == "none") return;

            float width = 0;
            if (float.TryParse(style.OutlineWidth.Replace("px", ""), out float w)) width = w;
            else if (style.OutlineWidth == "thin") width = 1;
            else if (style.OutlineWidth == "medium") width = 3;
            else if (style.OutlineWidth == "thick") width = 5;

            if (width <= 0) return;

            float offset = 0;
            if (!string.IsNullOrEmpty(style.OutlineOffset) && float.TryParse(style.OutlineOffset.Replace("px", ""), out float o))
            {
                offset = o;
            }

            SKColor color = SKColors.Black;
            if (!string.IsNullOrEmpty(style.OutlineColor))
            {
                color = ParseColor(style.OutlineColor);
            }

            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width
            };

            SetupBorderStyle(paint, style.OutlineStyle ?? "solid", width);

            var outlineRect = new SKRect(
                box.Left - width / 2 - offset,
                box.Top - width / 2 - offset,
                box.Right + width / 2 + offset,
                box.Bottom + width / 2 + offset);

            canvas.DrawRect(outlineRect, paint);
        }

        /// <summary>
        /// Draw rectangle with border radius.
        /// </summary>
        private void DrawWithRadius(SKCanvas canvas, SKRect rect, CssCornerRadius radius, SKPaint paint)
        {
            radius = radius.ClampNonNegative();
            if (!radius.IsZero)
            {
                float rtl = (float)radius.TopLeft.Value;
                float rtr = (float)radius.TopRight.Value;
                float rbr = (float)radius.BottomRight.Value;
                float rbl = (float)radius.BottomLeft.Value;

                // Use SKRoundRect with per-corner radii for accurate rendering
                using var rrect = new SKRoundRect();
                rrect.SetRectRadii(rect, new[]
                {
                    new SKPoint(rtl, rtl),
                    new SKPoint(rtr, rtr),
                    new SKPoint(rbr, rbr),
                    new SKPoint(rbl, rbl)
                });
                canvas.DrawRoundRect(rrect, paint);
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

