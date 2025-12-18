using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;
using SkiaSharp;
using Avalonia;

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
                    Color = new SKColor(color.R, color.G, color.B, (byte)(color.A * opacity / 255)),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                DrawWithRadius(canvas, box, style.BorderRadius, paint);
            }

            // Background from brush (gradients, etc.)
            if (style.Background != null)
            {
                var shader = CreateShaderFromBrush(style.Background, box, opacity / 255f);
                if (shader != null)
                {
                    using var paint = new SKPaint
                    {
                        Shader = shader,
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill
                    };
                    DrawWithRadius(canvas, box, style.BorderRadius, paint);
                    shader.Dispose();
                }
            }
        }

        /// <summary>
        /// Create Skia shader from Avalonia IBrush for gradient rendering.
        /// </summary>
        private SKShader CreateShaderFromBrush(Avalonia.Media.IBrush brush, SKRect bounds, float opacity)
        {
            if (brush == null) return null;

            try
            {
                if (brush is Avalonia.Media.LinearGradientBrush lgb)
                {
                    // Convert relative points to absolute
                    float startX = bounds.Left + (float)(lgb.StartPoint.Point.X * bounds.Width);
                    float startY = bounds.Top + (float)(lgb.StartPoint.Point.Y * bounds.Height);
                    float endX = bounds.Left + (float)(lgb.EndPoint.Point.X * bounds.Width);
                    float endY = bounds.Top + (float)(lgb.EndPoint.Point.Y * bounds.Height);

                    var colors = new List<SKColor>();
                    var positions = new List<float>();

                    foreach (var stop in lgb.GradientStops)
                    {
                        var c = stop.Color;
                        byte a = (byte)(c.A * opacity);
                        colors.Add(new SKColor(c.R, c.G, c.B, a));
                        positions.Add((float)stop.Offset);
                    }

                    if (colors.Count < 2)
                    {
                        // Need at least 2 colors for gradient
                        colors.Add(colors.Count > 0 ? colors[0] : SKColors.Transparent);
                        positions.Add(1f);
                    }

                    var mode = lgb.SpreadMethod switch
                    {
                        Avalonia.Media.GradientSpreadMethod.Reflect => SKShaderTileMode.Mirror,
                        Avalonia.Media.GradientSpreadMethod.Repeat => SKShaderTileMode.Repeat,
                        _ => SKShaderTileMode.Clamp
                    };

                    return SKShader.CreateLinearGradient(
                        new SKPoint(startX, startY),
                        new SKPoint(endX, endY),
                        colors.ToArray(),
                        positions.ToArray(),
                        mode);
                }
                else if (brush is Avalonia.Media.RadialGradientBrush rgb)
                {
                    float cx = bounds.Left + (float)(rgb.Center.Point.X * bounds.Width);
                    float cy = bounds.Top + (float)(rgb.Center.Point.Y * bounds.Height);
                    float radius = (float)(Math.Max(bounds.Width, bounds.Height) * 0.5);

                    var colors = new List<SKColor>();
                    var positions = new List<float>();

                    foreach (var stop in rgb.GradientStops)
                    {
                        var c = stop.Color;
                        byte a = (byte)(c.A * opacity);
                        colors.Add(new SKColor(c.R, c.G, c.B, a));
                        positions.Add((float)stop.Offset);
                    }

                    if (colors.Count < 2)
                    {
                        colors.Add(colors.Count > 0 ? colors[0] : SKColors.Transparent);
                        positions.Add(1f);
                    }

                    var mode = rgb.SpreadMethod switch
                    {
                        Avalonia.Media.GradientSpreadMethod.Reflect => SKShaderTileMode.Mirror,
                        Avalonia.Media.GradientSpreadMethod.Repeat => SKShaderTileMode.Repeat,
                        _ => SKShaderTileMode.Clamp
                    };

                    return SKShader.CreateRadialGradient(
                        new SKPoint(cx, cy),
                        radius,
                        colors.ToArray(),
                        positions.ToArray(),
                        mode);
                }
                else if (brush is Avalonia.Media.SolidColorBrush scb)
                {
                    var c = scb.Color;
                    byte a = (byte)(c.A * opacity);
                    return SKShader.CreateColor(new SKColor(c.R, c.G, c.B, a));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateShaderFromBrush error: {ex.Message}");
            }

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
                borderColor = new SKColor(c.R, c.G, c.B, (byte)(c.A * opacity / 255));
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
