using System;
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
                IsAntialias = true
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
                IsAntialias = true
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
                IsAntialias = true
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
            bool isUniformColor = border.TopColor == border.RightColor &&
                                  border.TopColor == border.BottomColor &&
                                  border.TopColor == border.LeftColor;

            bool isUniformWidth = Math.Abs(border.TopWidth - border.RightWidth) < 0.01f &&
                                  Math.Abs(border.TopWidth - border.BottomWidth) < 0.01f &&
                                  Math.Abs(border.TopWidth - border.LeftWidth) < 0.01f;

            bool hasRadius = border.TopLeftRadius.X > 0 || border.TopLeftRadius.Y > 0 ||
                             border.TopRightRadius.X > 0 || border.TopRightRadius.Y > 0 ||
                             border.BottomRightRadius.X > 0 || border.BottomRightRadius.Y > 0 ||
                             border.BottomLeftRadius.X > 0 || border.BottomLeftRadius.Y > 0;

            if (isUniformColor && isUniformWidth && border.TopWidth > 0)
            {
                using var paint = CreateBorderPaint(border.TopColor, border.TopWidth);
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
                if (border.TopWidth > 0)
                {
                    using var paint = CreateBorderPaint(border.TopColor, border.TopWidth);
                    float y = rect.Top + border.TopWidth / 2;
                    _canvas.DrawLine(rect.Left, y, rect.Right, y, paint);
                }

                if (border.RightWidth > 0)
                {
                    using var paint = CreateBorderPaint(border.RightColor, border.RightWidth);
                    float x = rect.Right - border.RightWidth / 2;
                    _canvas.DrawLine(x, rect.Top, x, rect.Bottom, paint);
                }

                if (border.BottomWidth > 0)
                {
                    using var paint = CreateBorderPaint(border.BottomColor, border.BottomWidth);
                    float y = rect.Bottom - border.BottomWidth / 2;
                    _canvas.DrawLine(rect.Right, y, rect.Left, y, paint);
                }

                if (border.LeftWidth > 0)
                {
                    using var paint = CreateBorderPaint(border.LeftColor, border.LeftWidth);
                    float x = rect.Left + border.LeftWidth / 2;
                    _canvas.DrawLine(x, rect.Bottom, x, rect.Top, paint);
                }
            }
            finally
            {
                if (hasRadius)
                {
                    _canvas.RestoreToCount(saveCount);
                }
            }
        }

        private static SKPath CreateRoundedRectPath(SKRect bounds, SKPoint[] radius)
        {
            var path = new SKPath();
            var rrect = new SKRoundRect();
            rrect.SetRectRadii(bounds, radius);
            path.AddRoundRect(rrect);
            return path;
        }

        private static SKPaint CreateBorderPaint(SKColor color, float width)
        {
            return new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width,
                IsAntialias = true
            };
        }

        #endregion

        #region Text

        public void DrawGlyphRun(SKPoint origin, GlyphRun glyphs, SKColor color, float opacity = 1f)
        {
            if (glyphs == null || glyphs.Count == 0)
            {
                return;
            }

            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                TextSize = glyphs.FontSize,
                Typeface = glyphs.Typeface,
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = true
            };

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(paint.ToFont(), glyphs.Count);
            var glyphSpan = run.GetGlyphSpan();
            var posSpan = run.GetPositionSpan();

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

            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                TextSize = fontSize,
                Typeface = typeface ?? SKTypeface.Default,
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = true
            };

            _canvas.DrawText(text, origin.X, origin.Y, paint);
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
            if (filter == null)
            {
                return;
            }

            using var paint = new SKPaint { ImageFilter = filter };
            _canvas.SaveLayer(bounds, paint);
            _canvas.ClipRect(bounds);
            _canvas.Restore();
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
