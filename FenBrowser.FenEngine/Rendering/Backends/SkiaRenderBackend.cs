using SkiaSharp;
using FenBrowser.FenEngine.Typography;

namespace FenBrowser.FenEngine.Rendering.Backends
{
    /// <summary>
    /// Skia-based implementation of IRenderBackend.
    /// 
    /// RULE 4: All Skia/OpenGL types stay inside this class.
    /// The rest of the engine uses only IRenderBackend.
    /// </summary>
    public class SkiaRenderBackend : IRenderBackend
    {
        private readonly SKCanvas _canvas;
        
        public SkiaRenderBackend(SKCanvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        }
        
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
            if (opacity < 1f) paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
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
            if (opacity < 1f) paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
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
            if (opacity < 1f) paint.Color = paint.Color.WithAlpha((byte)(opacity * 255));
            _canvas.DrawPath(path, paint);
        }

        #endregion
        
        #region Borders
        
        public void DrawBorder(SKRect rect, BorderStyle border)
        {
            // Top border
            if (border.TopWidth > 0)
            {
                using var paint = CreateBorderPaint(border.TopColor, border.TopWidth);
                _canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, paint);
            }
            
            // Right border
            if (border.RightWidth > 0)
            {
                using var paint = CreateBorderPaint(border.RightColor, border.RightWidth);
                _canvas.DrawLine(rect.Right, rect.Top, rect.Right, rect.Bottom, paint);
            }
            
            // Bottom border
            if (border.BottomWidth > 0)
            {
                using var paint = CreateBorderPaint(border.BottomColor, border.BottomWidth);
                _canvas.DrawLine(rect.Right, rect.Bottom, rect.Left, rect.Bottom, paint);
            }
            
            // Left border
            if (border.LeftWidth > 0)
            {
                using var paint = CreateBorderPaint(border.LeftColor, border.LeftWidth);
                _canvas.DrawLine(rect.Left, rect.Bottom, rect.Left, rect.Top, paint);
            }
        }
        
        private SKPaint CreateBorderPaint(SKColor color, float width)
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
            if (glyphs == null || glyphs.Count == 0) return;
            
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                TextSize = glyphs.FontSize,
                Typeface = glyphs.Typeface,
                IsAntialias = true,
                SubpixelText = true
            };
            
            // Draw as text blob for efficiency
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
            if (string.IsNullOrEmpty(text)) return;
            
            using var paint = new SKPaint
            {
                Color = ApplyOpacity(color, opacity),
                TextSize = fontSize,
                Typeface = typeface ?? SKTypeface.Default,
                IsAntialias = true,
                SubpixelText = true
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
        
        public void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f)
        {
            if (picture == null) return;
            
            _canvas.Save();
            
            if (opacity < 1f)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, (byte)(opacity * 255))
                };
                _canvas.SaveLayer(paint);
            }
            
            // Scale picture to fit destRect
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
            if (opacity < 1f) _canvas.Restore();
        }
        
        #endregion
        
        #region Clipping & Layers
        
        public void PushClip(SKRect clipRect)
        {
            _canvas.Save();
            _canvas.ClipRect(clipRect);
        }
        
        public void PushClip(SKRect clipRect, float radiusX, float radiusY)
        {
            _canvas.Save();
            _canvas.ClipRoundRect(new SKRoundRect(clipRect, radiusX, radiusY));
        }
        
        public void PushClip(SKPath clipPath)
        {
            _canvas.Save();
            _canvas.ClipPath(clipPath);
        }

        public void PopClip()
        {
            _canvas.Restore();
        }
        
        public void PushLayer(float opacity)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)(opacity * 255))
            };
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
        
        #endregion
        
        #region State
        
        public void Save()
        {
            _canvas.Save();
        }
        
        public void Restore()
        {
            _canvas.Restore();
        }
        
        public void Clear(SKColor color)
        {
            _canvas.Clear(color);
        }
        
        #endregion
        
        #region Helpers
        
        private SKColor ApplyOpacity(SKColor color, float opacity)
        {
            if (opacity >= 1f) return color;
            return new SKColor(color.Red, color.Green, color.Blue, (byte)(color.Alpha * opacity));
        }
        
        #endregion
    }
}
