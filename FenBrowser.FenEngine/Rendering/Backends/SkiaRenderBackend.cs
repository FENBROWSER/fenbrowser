using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Backends
{
    /// <summary>
    /// Skia-based implementation of IRenderBackend.
    /// </summary>
    public class SkiaRenderBackend : IRenderBackend
    {
        private readonly SKCanvas _canvas;

        public SkiaRenderBackend(SKCanvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        }

        public SKCanvas Canvas => _canvas;

        #region Primitives

        public void DrawRect(SKRect rect, SKColor color, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            _canvas.DrawRect(rect, paint);
        }

        public void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                IsAntialias = true
            };
            _canvas.DrawRect(rect, paint);
        }

        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKColor color, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            _canvas.DrawRoundRect(rect, radiusX, radiusY, paint);
        }

        public void DrawPath(SKPath path, SKColor color, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            _canvas.DrawPath(path, paint);
        }

        public void DrawRect(SKRect rect, SKShader shader, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Shader = shader,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = SKColors.White,
                BlendMode = SKBlendMode.SrcOver
            };
            if (opacity < 1f)
            {
                paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
            }
            _canvas.DrawRect(rect, paint);
        }

        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKShader shader, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Shader = shader,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = SKColors.White,
                BlendMode = SKBlendMode.SrcOver
            };
            if (opacity < 1f)
            {
                paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
            }
            _canvas.DrawRoundRect(rect, radiusX, radiusY, paint);
        }

        public void DrawPath(SKPath path, SKShader shader, float opacity = 1f)
        {
            using var paint = new SKPaint
            {
                Shader = shader,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = SKColors.White,
                BlendMode = SKBlendMode.SrcOver
            };
            if (opacity < 1f)
            {
                paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
            }
            _canvas.DrawPath(path, paint);
        }

        #endregion

        #region Borders

        public void DrawBorder(SKRect rect, BorderStyle border)
        {
            bool paintTop = IsPaintableBorderStyle(border.TopStyle) && border.TopWidth > 0;
            bool paintRight = IsPaintableBorderStyle(border.RightStyle) && border.RightWidth > 0;
            bool paintBottom = IsPaintableBorderStyle(border.BottomStyle) && border.BottomWidth > 0;
            bool paintLeft = IsPaintableBorderStyle(border.LeftStyle) && border.LeftWidth > 0;

            if (!paintTop && !paintRight && !paintBottom && !paintLeft)
            {
                return;
            }

            bool isUniformColor = border.TopColor == border.RightColor &&
                                  border.TopColor == border.BottomColor &&
                                  border.TopColor == border.LeftColor;

            bool isUniformWidth = Math.Abs(border.TopWidth - border.RightWidth) < 0.01f &&
                                  Math.Abs(border.TopWidth - border.BottomWidth) < 0.01f &&
                                  Math.Abs(border.TopWidth - border.LeftWidth) < 0.01f;
            bool isUniformStyle = string.Equals(border.TopStyle, border.RightStyle, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(border.TopStyle, border.BottomStyle, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(border.TopStyle, border.LeftStyle, StringComparison.OrdinalIgnoreCase);

            bool hasRadius = border.TopLeftRadius.X > 0 || border.TopLeftRadius.Y > 0 ||
                             border.TopRightRadius.X > 0 || border.TopRightRadius.Y > 0 ||
                             border.BottomRightRadius.X > 0 || border.BottomRightRadius.Y > 0 ||
                             border.BottomLeftRadius.X > 0 || border.BottomLeftRadius.Y > 0;

            if (isUniformColor &&
                isUniformWidth &&
                isUniformStyle &&
                !IsDoubleBorderStyle(border.TopStyle) &&
                paintTop &&
                paintRight &&
                paintBottom &&
                paintLeft)
            {
                using var paint = CreateBorderPaint(border.TopColor, border.TopWidth, border.TopStyle);
                float inset = border.TopWidth / 2.0f;
                var drawRect = rect;
                drawRect.Inflate(-inset, -inset);

                if (hasRadius)
                {
                    SKPoint[] radii =
                    {
                        new(Math.Max(0, border.TopLeftRadius.X - inset), Math.Max(0, border.TopLeftRadius.Y - inset)),
                        new(Math.Max(0, border.TopRightRadius.X - inset), Math.Max(0, border.TopRightRadius.Y - inset)),
                        new(Math.Max(0, border.BottomRightRadius.X - inset), Math.Max(0, border.BottomRightRadius.Y - inset)),
                        new(Math.Max(0, border.BottomLeftRadius.X - inset), Math.Max(0, border.BottomLeftRadius.Y - inset))
                    };

                    using var path = CreateRoundedRectPath(drawRect, radii);
                    _canvas.DrawPath(path, paint);
                }
                else
                {
                    _canvas.DrawRect(drawRect, paint);
                }

                return;
            }

            int saveCount = 0;
            if (hasRadius)
            {
                saveCount = _canvas.Save();
                using var clipPath = CreateRoundedRectPath(rect, new[] { border.TopLeftRadius, border.TopRightRadius, border.BottomRightRadius, border.BottomLeftRadius });
                _canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
            }

            try
            {
                DrawBorderSide(rect, BorderSide.Top, border.TopColor, border.TopWidth, border.TopStyle, paintTop);
                DrawBorderSide(rect, BorderSide.Right, border.RightColor, border.RightWidth, border.RightStyle, paintRight);
                DrawBorderSide(rect, BorderSide.Bottom, border.BottomColor, border.BottomWidth, border.BottomStyle, paintBottom);
                DrawBorderSide(rect, BorderSide.Left, border.LeftColor, border.LeftWidth, border.LeftStyle, paintLeft);
            }
            finally
            {
                if (hasRadius)
                {
                    _canvas.RestoreToCount(saveCount);
                }
            }
        }

        private void DrawBorderSide(SKRect rect, BorderSide side, SKColor color, float width, string style, bool enabled)
        {
            if (!enabled || width <= 0 || rect.Width <= 0 || rect.Height <= 0 || color.Alpha == 0)
            {
                return;
            }

            if (IsDoubleBorderStyle(style))
            {
                DrawDoubleBorderSide(rect, side, color, width);
                return;
            }

            using var paint = CreateBorderPaint(color, width, style);
            using var path = new SKPath();
            AddBorderSideLine(path, rect, side, width / 2f);
            _canvas.DrawPath(path, paint);
        }

        private void DrawDoubleBorderSide(SKRect rect, BorderSide side, SKColor color, float width)
        {
            float strokeWidth = Math.Max(1f, width / 3f);
            using var paint = CreateBorderPaint(color, strokeWidth, "solid");

            using var outerPath = new SKPath();
            AddBorderSideLine(outerPath, rect, side, strokeWidth / 2f);
            _canvas.DrawPath(outerPath, paint);

            using var innerPath = new SKPath();
            AddBorderSideLine(innerPath, rect, side, width - strokeWidth / 2f);
            _canvas.DrawPath(innerPath, paint);
        }

        private static void AddBorderSideLine(SKPath path, SKRect rect, BorderSide side, float offset)
        {
            switch (side)
            {
                case BorderSide.Top:
                    path.MoveTo(rect.Left, rect.Top + offset);
                    path.LineTo(rect.Right, rect.Top + offset);
                    break;
                case BorderSide.Right:
                    path.MoveTo(rect.Right - offset, rect.Top);
                    path.LineTo(rect.Right - offset, rect.Bottom);
                    break;
                case BorderSide.Bottom:
                    path.MoveTo(rect.Left, rect.Bottom - offset);
                    path.LineTo(rect.Right, rect.Bottom - offset);
                    break;
                case BorderSide.Left:
                    path.MoveTo(rect.Left + offset, rect.Top);
                    path.LineTo(rect.Left + offset, rect.Bottom);
                    break;
            }
        }

        private static bool IsPaintableBorderStyle(string style)
        {
            return !string.IsNullOrWhiteSpace(style) &&
                   !string.Equals(style, "none", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(style, "hidden", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDoubleBorderStyle(string style)
        {
            return string.Equals(style, "double", StringComparison.OrdinalIgnoreCase);
        }

        private static SKPath CreateRoundedRectPath(SKRect bounds, SKPoint[] radius)
        {
            // CSS §5.3: proportionally reduce corner radii to prevent overlap.
            if (radius != null && radius.Length >= 4 && bounds.Width > 0 && bounds.Height > 0)
            {
                float topSumX = radius[0].X + radius[1].X;
                float rightSumY = radius[1].Y + radius[2].Y;
                float bottomSumX = radius[2].X + radius[3].X;
                float leftSumY = radius[0].Y + radius[3].Y;
                float f = 1.0f;
                if (topSumX > 0) f = Math.Min(f, bounds.Width / topSumX);
                if (rightSumY > 0) f = Math.Min(f, bounds.Height / rightSumY);
                if (bottomSumX > 0) f = Math.Min(f, bounds.Width / bottomSumX);
                if (leftSumY > 0) f = Math.Min(f, bounds.Height / leftSumY);
                if (f < 1.0f)
                {
                    for (int i = 0; i < 4; i++)
                        radius[i] = new SKPoint(radius[i].X * f, radius[i].Y * f);
                }
            }

            var path = new SKPath();
            var rrect = new SKRoundRect();
            rrect.SetRectRadii(bounds, radius);
            path.AddRoundRect(rrect);
            return path;
        }

        private static SKPaint CreateBorderPaint(SKColor color, float width, string style)
        {
            var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width,
                IsAntialias = true
            };

            var normalized = style?.Trim().ToLowerInvariant();
            if (normalized == "dashed")
            {
                paint.PathEffect = SKPathEffect.CreateDash(new[] { Math.Max(1f, width * 3f), Math.Max(1f, width * 2f) }, 0);
                paint.StrokeCap = SKStrokeCap.Butt;
            }
            else if (normalized == "dotted")
            {
                paint.PathEffect = SKPathEffect.CreateDash(new[] { 0.1f, Math.Max(1f, width * 2f) }, 0);
                paint.StrokeCap = SKStrokeCap.Round;
            }

            return paint;
        }

        private enum BorderSide
        {
            Top,
            Right,
            Bottom,
            Left
        }

        #endregion

        #region Text

        public void DrawGlyphRun(SKPoint origin, GlyphRun glyphs, SKColor color, float opacity = 1f)
        {
            if (glyphs == null || glyphs.Count == 0)
            {
                return;
            }

            var resolvedColor = ApplyOpacity(color, opacity);
            using var paint = new SKPaint
            {
                Color = resolvedColor,
                IsAntialias = true
            };

            using var font = new SKFont(glyphs.Typeface, glyphs.FontSize)
            {
                Subpixel = true
            };

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, glyphs.Count);
            var glyphSpan = run.Glyphs;
            var posSpan = run.Positions;

            for (int i = 0; i < glyphs.Count; i++)
            {
                glyphSpan[i] = glyphs.Glyphs[i].GlyphId;
                posSpan[i] = new SKPoint(origin.X + glyphs.Glyphs[i].X, origin.Y + glyphs.Glyphs[i].Y);
            }

            using var blob = builder.Build();
            _canvas.DrawText(blob, 0, 0, paint);
        }

        public void DrawText(string text, SKPoint origin, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            using var font = new SKFont(typeface ?? SKTypeface.Default, fontSize)
            {
                Subpixel = true,
                // LcdRender removed: no longer available on SKFont in SkiaSharp 4.x
            };
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                IsAntialias = true
            };

            if (text.Equals("Sign in", StringComparison.Ordinal))
            {
                SKRect clip = _canvas.LocalClipBounds;
                EngineLogCompat.Log(
                    $"[GOOGLE-SIGNIN-CLIP] origin=({origin.X:F1},{origin.Y:F1}) clip=({clip.Left:F1},{clip.Top:F1},{clip.Width:F1}x{clip.Height:F1})",
                    LogCategory.Paint);
            }

            _canvas.DrawText(text, origin.X, origin.Y, SKTextAlign.Left, font, paint);
        }

        #endregion

        #region Images

        public void DrawImage(SKImage image, SKRect destRect, float opacity = 1f)
        {
            if (image == null) return;
            using var paint = opacity < 1f ? new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) } : null;
            _canvas.DrawImage(image, destRect, paint);
        }

        public void DrawImage(SKImage image, SKRect destRect, SKRect srcRect, float opacity = 1f)
        {
            if (image == null) return;
            using var paint = opacity < 1f ? new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) } : null;
            _canvas.DrawImage(image, srcRect, destRect, paint);
        }

        public void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f)
        {
            if (picture == null) return;

            _canvas.Save();
            if (opacity < 1f)
            {
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) };
                _canvas.SaveLayer(paint);
            }

            var pictureBounds = picture.CullRect;
            if (pictureBounds.Width > 0 && pictureBounds.Height > 0)
            {
                float scaleX = destRect.Width / pictureBounds.Width;
                float scaleY = destRect.Height / pictureBounds.Height;
                _canvas.Translate(destRect.Left, destRect.Top);
                _canvas.Scale(scaleX, scaleY);
                _canvas.Translate(-pictureBounds.Left, -pictureBounds.Top);
            }

            _canvas.DrawPicture(picture);
            _canvas.Restore();
            if (opacity < 1f)
            {
                _canvas.Restore();
            }
        }

        #endregion

        #region Clipping & Layers

        public void PushClip(SKRect clipRect)
        {
            _canvas.Save();
            _canvas.ClipRect(clipRect, SKClipOperation.Intersect, true);
        }

        public void PushClip(SKRect clipRect, float radiusX, float radiusY)
        {
            _canvas.Save();
            _canvas.ClipRoundRect(new SKRoundRect(clipRect, radiusX, radiusY), SKClipOperation.Intersect, true);
        }

        public void PushClip(SKPath clipPath)
        {
            _canvas.Save();
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
        }

        public void PopClip()
        {
            _canvas.Restore();
        }

        public void PushLayer(float opacity)
        {
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) };
            _canvas.SaveLayer(paint);
        }

        public void PushTransform(SKMatrix transform)
        {
            _canvas.Save();
            _canvas.Concat(ref transform);
        }

        public void PopLayer()
        {
            _canvas.Restore();
        }

        public void ApplyMask(SKImage mask, SKRect bounds)
        {
            if (mask == null) return;
            using var paint = new SKPaint
            {
                BlendMode = SKBlendMode.DstIn,
                IsAntialias = true
            };
            _canvas.DrawImage(mask, bounds, paint);
        }

        public void PushFilter(SKImageFilter filter)
        {
            if (filter == null)
            {
                return;
            }

            using var paint = new SKPaint { ImageFilter = filter };
            _canvas.SaveLayer(paint);
        }

        public void PopFilter()
        {
            _canvas.Restore();
        }

        public void ApplyBackdropFilter(SKRect bounds, SKImageFilter filter)
        {
            if (filter == null || _canvas == null)
            {
                return;
            }

            // Backdrop-filter (CSS Filter Effects Level 2): captures the pixels
            // behind the element, applies the filter chain, and composites the
            // filtered backdrop beneath the element's own content.
            //
            // Implementation: save the current canvas content, apply the filter
            // via SaveLayer with an ImageFilter paint, then restore. The
            // SaveLayer captures the backdrop at save time and the filter is
            // applied when Restore() composites the layer.
            //
            // Note: Skia applies ImageFilter on SaveLayer as a POST-INPUT filter,
            // so the canvas content at save time becomes the input to the filter.
            // The filtered result is composited back at Restore() time.
            _canvas.Save();

            // Clip to the element's bounds so the filter only affects this region.
            _canvas.ClipRect(bounds);

            // SaveLayer with an ImageFilter paint. The layer captures current
            // canvas content (the backdrop), applies the filter chain when the
            // layer is restored, and composites the result.
            using var layerPaint = new SKPaint
            {
                ImageFilter = filter,
                IsAntialias = true
            };
            _canvas.SaveLayer(bounds, layerPaint);

            // Draw nothing — the layer already captured the backdrop. When
            // Restore() is called, the filter is applied to the captured
            // backdrop and composited back.
            _canvas.Restore(); // composites filtered backdrop
            _canvas.Restore(); // restores clip + pre-filter state
        }

        #endregion

        #region Shadows

        public void DrawBoxShadow(SKRect rect, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color)
        {
            var shadowRect = new SKRect(
                rect.Left + offsetX - spreadRadius,
                rect.Top + offsetY - spreadRadius,
                rect.Right + offsetX + spreadRadius,
                rect.Bottom + offsetY + spreadRadius);

            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                MaskFilter = blurRadius > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurRadius / 2) : null
            };

            _canvas.DrawRect(shadowRect, paint);
        }

        public void DrawShadow(SKPath path, float offsetX, float offsetY, float blurRadius, SKColor color)
        {
            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                MaskFilter = blurRadius > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurRadius / 2) : null
            };

            _canvas.Save();
            _canvas.Translate(offsetX, offsetY);
            _canvas.DrawPath(path, paint);
            _canvas.Restore();
        }

        public void DrawInsetBoxShadow(SKRect rect, SKPoint[] borderRadius, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color)
        {
            _canvas.Save();
            if (HasNonZeroRadius(borderRadius))
            {
                using var clipPath = CreateRoundedRectPath(rect, borderRadius);
                _canvas.ClipPath(clipPath);
            }
            else
            {
                _canvas.ClipRect(rect);
            }

            var insetRect = new SKRect(
                rect.Left + offsetX + spreadRadius,
                rect.Top + offsetY + spreadRadius,
                rect.Right + offsetX - spreadRadius,
                rect.Bottom + offsetY - spreadRadius);

            using var shadowPath = new SKPath();
            shadowPath.AddRect(new SKRect(rect.Left - 200, rect.Top - 200, rect.Right + 200, rect.Bottom + 200));

            if (HasNonZeroRadius(borderRadius))
            {
                using var innerPath = CreateRoundedRectPath(insetRect, borderRadius);
                shadowPath.Op(innerPath, SKPathOp.Difference, shadowPath);
            }
            else
            {
                using var innerPath = new SKPath();
                innerPath.AddRect(insetRect);
                shadowPath.Op(innerPath, SKPathOp.Difference, shadowPath);
            }

            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            if (blurRadius > 0)
            {
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurRadius / 2);
            }

            _canvas.DrawPath(shadowPath, paint);
            _canvas.Restore();
        }

        #endregion

        #region State

        public int SaveDepth => _canvas.SaveCount;

        public void Save()
        {
            _canvas.Save();
        }

        public void Restore()
        {
            _canvas.Restore();
        }

        public void RestoreToSaveDepth(int saveDepth)
        {
            _canvas.RestoreToCount(saveDepth);
        }

        public void Clear(SKColor color)
        {
            _canvas.Clear(color);
        }

        public void ExecuteCustomPaint(Action<SKCanvas, SKRect> paintAction, SKRect bounds)
        {
            paintAction?.Invoke(_canvas, bounds);
        }

        #endregion

        private static bool HasNonZeroRadius(SKPoint[] radius)
        {
            if (radius == null || radius.Length < 4)
            {
                return false;
            }

            return radius[0].X > 0 || radius[0].Y > 0 ||
                   radius[1].X > 0 || radius[1].Y > 0 ||
                   radius[2].X > 0 || radius[2].Y > 0 ||
                   radius[3].X > 0 || radius[3].Y > 0;
        }

        private static SKColor ApplyOpacity(SKColor color, float opacity)
        {
            if (opacity >= 1f)
            {
                return color;
            }

            return new SKColor(color.Red, color.Green, color.Blue, (byte)(color.Alpha * opacity));
        }
    }
}
