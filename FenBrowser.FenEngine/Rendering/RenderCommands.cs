using SkiaSharp;
using System;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Base class for all render commands in the Display List.
    /// Commands record paint operations for deferred execution.
    /// </summary>
    public abstract class RenderCommand
    {
        public SKRect Bounds { get; set; }
        public float Opacity { get; set; } = 1f;
        public int ZIndex { get; set; }
        public int StackingContextId { get; set; }
        
        public abstract void Execute(SKCanvas canvas);
        
        public bool IntersectsWith(SKRect viewport)
        {
            return Bounds.IntersectsWith(viewport);
        }
    }

    /// <summary>
    /// Draw a filled or stroked rectangle
    /// </summary>
    public class DrawRectCommand : RenderCommand
    {
        public SKRect Rect { get; set; }
        public SKColor Color { get; set; }
        public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
        public float StrokeWidth { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = Style,
                StrokeWidth = StrokeWidth,
                IsAntialias = true
            };
            canvas.DrawRect(Rect, paint);
        }
    }

    /// <summary>
    /// Draw a rounded rectangle
    /// </summary>
    public class DrawRoundRectCommand : RenderCommand
    {
        public SKRect Rect { get; set; }
        public float RadiusX { get; set; }
        public float RadiusY { get; set; }
        public SKColor Color { get; set; }
        public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
        public float StrokeWidth { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = Style,
                StrokeWidth = StrokeWidth,
                IsAntialias = true
            };
            canvas.DrawRoundRect(Rect, RadiusX, RadiusY, paint);
        }
    }

    /// <summary>
    /// Draw text at a position
    /// </summary>
    public class DrawTextCommand : RenderCommand
    {
        public string Text { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public SKColor Color { get; set; }
        public float FontSize { get; set; } = 16f;
        public SKTypeface Typeface { get; set; }
        public SKTextAlign TextAlign { get; set; } = SKTextAlign.Left;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                TextSize = FontSize,
                Typeface = Typeface,
                TextAlign = TextAlign,
                IsAntialias = true
            };
            canvas.DrawText(Text ?? "", X, Y, paint);
        }
    }

    /// <summary>
    /// Draw an image/bitmap
    /// </summary>
    public class DrawImageCommand : RenderCommand
    {
        public SKBitmap Bitmap { get; set; }
        public SKRect DestRect { get; set; }
        public SKRect? SourceRect { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            if (Bitmap == null) return;
            
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(255 * Opacity)),
                IsAntialias = true,
                FilterQuality = SKFilterQuality.Medium
            };
            
            if (SourceRect.HasValue)
                canvas.DrawBitmap(Bitmap, SourceRect.Value, DestRect, paint);
            else
                canvas.DrawBitmap(Bitmap, DestRect, paint);
        }
    }

    /// <summary>
    /// Draw an arbitrary path
    /// </summary>
    public class DrawPathCommand : RenderCommand
    {
        public SKPath Path { get; set; }
        public SKColor Color { get; set; }
        public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
        public float StrokeWidth { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            if (Path == null) return;
            
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = Style,
                StrokeWidth = StrokeWidth,
                IsAntialias = true
            };
            canvas.DrawPath(Path, paint);
        }
    }

    /// <summary>
    /// Draw a line between two points
    /// </summary>
    public class DrawLineCommand : RenderCommand
    {
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float X2 { get; set; }
        public float Y2 { get; set; }
        public SKColor Color { get; set; }
        public float StrokeWidth { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = StrokeWidth,
                IsAntialias = true
            };
            canvas.DrawLine(X1, Y1, X2, Y2, paint);
        }
    }

    /// <summary>
    /// Draw a circle
    /// </summary>
    public class DrawCircleCommand : RenderCommand
    {
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float Radius { get; set; }
        public SKColor Color { get; set; }
        public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = Style,
                IsAntialias = true
            };
            canvas.DrawCircle(CenterX, CenterY, Radius, paint);
        }
    }

    /// <summary>
    /// Save canvas state (push onto stack)
    /// </summary>
    public class SaveCommand : RenderCommand
    {
        public override void Execute(SKCanvas canvas)
        {
            canvas.Save();
        }
    }

    /// <summary>
    /// Save canvas with layer (for opacity/blend effects)
    /// </summary>
    public class SaveLayerCommand : RenderCommand
    {
        public SKRect? LayerBounds { get; set; }
        public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
        public float LayerOpacity { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(255 * LayerOpacity * Opacity)),
                BlendMode = BlendMode
            };
            
            if (LayerBounds.HasValue)
                canvas.SaveLayer(LayerBounds.Value, paint);
            else
                canvas.SaveLayer(paint);
        }
    }

    /// <summary>
    /// Restore canvas state (pop from stack)
    /// </summary>
    public class RestoreCommand : RenderCommand
    {
        public override void Execute(SKCanvas canvas)
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// Draw a rounded rectangle with individual corner radii
    /// </summary>
    public class DrawComplexRoundRectCommand : RenderCommand
    {
        public SKRect Rect { get; set; }
        public float[] Radii { get; set; } // TL, TR, BR, BL
        public SKColor Color { get; set; }
        public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
        public float StrokeWidth { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                Style = Style,
                StrokeWidth = StrokeWidth,
                IsAntialias = true
            };
            
            if (Radii == null || Radii.Length < 4)
            {
                 canvas.DrawRect(Rect, paint);
                 return;
            }

            using var rr = new SKRoundRect();
            var radii = new SKPoint[4];
            for (int i = 0; i < 4; i++)
                radii[i] = new SKPoint(Radii[i], Radii[i]);
            
            rr.SetRectRadii(Rect, radii);
            canvas.DrawRoundRect(rr, paint);
        }
    }

    /// <summary>
    /// Draw a box shadow
    /// </summary>
    public class DrawBoxShadowCommand : RenderCommand
    {
        public SKRect Box { get; set; }
        public float[] BorderRadius { get; set; }
        public SKColor Color { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float BlurRadius { get; set; }
        public float SpreadRadius { get; set; }
        public bool Inset { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            if (Inset)
            {
                // Inset shadow: drawn inside the box, clipped to box bounds
                canvas.Save();

                // Clip to the box shape
                if (BorderRadius != null && BorderRadius.Length >= 4 &&
                    (BorderRadius[0] > 0 || BorderRadius[1] > 0 || BorderRadius[2] > 0 || BorderRadius[3] > 0))
                {
                    using var clipRR = new SKRoundRect();
                    var clipRadii = new SKPoint[4];
                    for (int i = 0; i < 4; i++)
                        clipRadii[i] = new SKPoint(BorderRadius[i], BorderRadius[i]);
                    clipRR.SetRectRadii(Box, clipRadii);
                    canvas.ClipRoundRect(clipRR);
                }
                else
                {
                    canvas.ClipRect(Box);
                }

                // Draw a shadow ring around the OUTSIDE of a rect that is inset
                // The trick: draw a large filled rect with blur, but punch out the center
                // so only the edges (inside the clip) show the shadow
                var insetRect = new SKRect(
                    Box.Left + OffsetX + SpreadRadius,
                    Box.Top + OffsetY + SpreadRadius,
                    Box.Right + OffsetX - SpreadRadius,
                    Box.Bottom + OffsetY - SpreadRadius
                );

                // Create a path that is a large outer rect minus the inset rect
                using var shadowPath = new SKPath();
                shadowPath.AddRect(new SKRect(Box.Left - 100, Box.Top - 100, Box.Right + 100, Box.Bottom + 100));

                if (BorderRadius != null && BorderRadius.Length >= 4 &&
                    (BorderRadius[0] > 0 || BorderRadius[1] > 0 || BorderRadius[2] > 0 || BorderRadius[3] > 0))
                {
                    using var innerRR = new SKRoundRect();
                    var innerRadii = new SKPoint[4];
                    for (int i = 0; i < 4; i++)
                        innerRadii[i] = new SKPoint(Math.Max(0, BorderRadius[i] - SpreadRadius), Math.Max(0, BorderRadius[i] - SpreadRadius));
                    innerRR.SetRectRadii(insetRect, innerRadii);
                    using var innerPath = new SKPath();
                    innerPath.AddRoundRect(innerRR);
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
                    Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                if (BlurRadius > 0)
                    paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurRadius / 2);

                canvas.DrawPath(shadowPath, paint);
                canvas.Restore();
                return;
            }

            var shadowRect = new SKRect(
                Box.Left + OffsetX - SpreadRadius,
                Box.Top + OffsetY - SpreadRadius,
                Box.Right + OffsetX + SpreadRadius,
                Box.Bottom + OffsetY + SpreadRadius
            );

            using var outPaint = new SKPaint
            {
                Color = Color.WithAlpha((byte)(Color.Alpha * Opacity)),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            if (BlurRadius > 0)
                outPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurRadius / 2);

            if (BorderRadius != null && BorderRadius.Length >= 4)
            {
                using var rr = new SKRoundRect();
                var radii = new SKPoint[4];
                for (int i = 0; i < 4; i++)
                    radii[i] = new SKPoint(BorderRadius[i], BorderRadius[i]);
                rr.SetRectRadii(shadowRect, radii);
                canvas.DrawRoundRect(rr, outPaint);
            }
            else
            {
                canvas.DrawRect(shadowRect, outPaint);
            }
        }
    }

    /// <summary>
    /// Clip to a rectangle
    /// </summary>
    public class ClipRectCommand : RenderCommand
    {
        public SKRect ClipRect { get; set; }
        public SKClipOperation Operation { get; set; } = SKClipOperation.Intersect;

        public override void Execute(SKCanvas canvas)
        {
            canvas.ClipRect(ClipRect, Operation);
        }
    }

    /// <summary>
    /// Clip to a rounded rectangle
    /// </summary>
    public class ClipRoundRectCommand : RenderCommand
    {
        public SKRect ClipRect { get; set; }
        public float RadiusX { get; set; }
        public float RadiusY { get; set; }
        public SKClipOperation Operation { get; set; } = SKClipOperation.Intersect;

        public override void Execute(SKCanvas canvas)
        {
            using var path = new SKPath();
            path.AddRoundRect(ClipRect, RadiusX, RadiusY);
            canvas.ClipPath(path, Operation);
        }
    }

    /// <summary>
    /// Clip to an arbitrary path
    /// </summary>
    public class ClipPathCommand : RenderCommand
    {
        public SKPath Path { get; set; }
        public SKClipOperation Operation { get; set; } = SKClipOperation.Intersect;

        public override void Execute(SKCanvas canvas)
        {
            if (Path != null)
                canvas.ClipPath(Path, Operation);
        }
    }

    /// <summary>
    /// Apply translation transform
    /// </summary>
    public class TranslateCommand : RenderCommand
    {
        public float Dx { get; set; }
        public float Dy { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            canvas.Translate(Dx, Dy);
        }
    }

    /// <summary>
    /// Apply scale transform
    /// </summary>
    public class ScaleCommand : RenderCommand
    {
        public float Sx { get; set; } = 1f;
        public float Sy { get; set; } = 1f;

        public override void Execute(SKCanvas canvas)
        {
            canvas.Scale(Sx, Sy);
        }
    }

    /// <summary>
    /// Apply rotation transform
    /// </summary>
    public class RotateCommand : RenderCommand
    {
        public float Degrees { get; set; }
        public float? PivotX { get; set; }
        public float? PivotY { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            if (PivotX.HasValue && PivotY.HasValue)
                canvas.RotateDegrees(Degrees, PivotX.Value, PivotY.Value);
            else
                canvas.RotateDegrees(Degrees);
        }
    }

    /// <summary>
    /// Apply skew transform
    /// </summary>
    public class SkewCommand : RenderCommand
    {
        public float SkewX { get; set; }
        public float SkewY { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            var matrix = SKMatrix.CreateSkew(
                (float)Math.Tan(SkewX * Math.PI / 180),
                (float)Math.Tan(SkewY * Math.PI / 180)
            );
            canvas.Concat(ref matrix);
        }
    }

    /// <summary>
    /// Apply a complete matrix transform
    /// </summary>
    public class MatrixCommand : RenderCommand
    {
        public SKMatrix Matrix { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            var m = Matrix;
            canvas.Concat(ref m);
        }
    }

    /// <summary>
    /// Draw with a shader (gradient, etc.)
    /// </summary>
    public class DrawShaderRectCommand : RenderCommand
    {
        public SKRect Rect { get; set; }
        public SKShader Shader { get; set; }
        public float BorderRadius { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            if (Shader == null) return;
            
            using var paint = new SKPaint
            {
                Shader = Shader,
                IsAntialias = true
            };
            
            if (BorderRadius > 0)
                canvas.DrawRoundRect(Rect, BorderRadius, BorderRadius, paint);
            else
                canvas.DrawRect(Rect, paint);
        }
    }

    /// <summary>
    /// Apply blur/other image filters
    /// </summary>
    public class FilterCommand : RenderCommand
    {
        public SKImageFilter Filter { get; set; }
        public SKRect TargetRect { get; set; }

        public override void Execute(SKCanvas canvas)
        {
            if (Filter == null) return;
            
            using var paint = new SKPaint { ImageFilter = Filter };
            canvas.SaveLayer(TargetRect, paint);
            canvas.Restore();
        }
    }
}
