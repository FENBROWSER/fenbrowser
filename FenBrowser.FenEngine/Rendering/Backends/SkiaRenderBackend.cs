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

            // Sides that differ on a rounded box are not straight lines. Each one
            // is its share of the ring between the border box and the padding box,
            // so it curves through the corners it owns. Clipping a straight line to
            // the outer shape - what this used to do - replaces that curve with a
            // chord, which is why the canonical CSS spinner (a round box whose
            // bottom and left border-colors are transparent, spun by a keyframe)
            // came out as two flat bars instead of an arc.
            if (hasRadius &&
                TryDrawRoundedNonUniformBorder(rect, border, paintTop, paintRight, paintBottom, paintLeft))
            {
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
                DrawBorderSide(rect, BorderSide.Top, border.TopColor, border.TopWidth, border.TopStyle, paintTop, border);
                DrawBorderSide(rect, BorderSide.Right, border.RightColor, border.RightWidth, border.RightStyle, paintRight, border);
                DrawBorderSide(rect, BorderSide.Bottom, border.BottomColor, border.BottomWidth, border.BottomStyle, paintBottom, border);
                DrawBorderSide(rect, BorderSide.Left, border.LeftColor, border.LeftWidth, border.LeftStyle, paintLeft, border);
            }
            finally
            {
                if (hasRadius)
                {
                    _canvas.RestoreToCount(saveCount);
                }
            }
        }

        /// <summary>
        /// Paints a rounded border whose sides differ, by filling each side's
        /// share of the ring between the border box and the padding box.
        /// Returns false when a side asks for something a filled ring cannot
        /// express (dashes, dots, a double rule), leaving the caller's
        /// straight-line path to handle it.
        /// </summary>
        private bool TryDrawRoundedNonUniformBorder(
            SKRect rect,
            BorderStyle border,
            bool paintTop,
            bool paintRight,
            bool paintBottom,
            bool paintLeft)
        {
            if (rect.Width <= 0 || rect.Height <= 0 ||
                IsSegmentedBorderStyle(border.TopStyle) ||
                IsSegmentedBorderStyle(border.RightStyle) ||
                IsSegmentedBorderStyle(border.BottomStyle) ||
                IsSegmentedBorderStyle(border.LeftStyle))
            {
                return false;
            }

            // CreateRoundedRectPath applies the CSS §5.3 overlap reduction to the
            // array it is given, so read the radii back afterwards: the inner curve
            // has to be derived from the radii actually drawn, not the declared
            // ones. A 36px radius on a 36px box is 'border-radius: 50%' after that
            // reduction, and deriving from 36 would not give a circle.
            var radii = new[]
            {
                border.TopLeftRadius,
                border.TopRightRadius,
                border.BottomRightRadius,
                border.BottomLeftRadius
            };

            using var outer = CreateRoundedRectPath(rect, radii);

            var innerRect = new SKRect(
                rect.Left + border.LeftWidth,
                rect.Top + border.TopWidth,
                rect.Right - border.RightWidth,
                rect.Bottom - border.BottomWidth);

            // Borders thick enough to meet leave no padding box. The wedges still
            // have to tile the whole shape, so collapse the inner edge onto the
            // centre line rather than letting it invert.
            if (innerRect.Width <= 0)
            {
                float centreX = (rect.Left + rect.Right) / 2f;
                innerRect.Left = centreX;
                innerRect.Right = centreX;
            }

            if (innerRect.Height <= 0)
            {
                float centreY = (rect.Top + rect.Bottom) / 2f;
                innerRect.Top = centreY;
                innerRect.Bottom = centreY;
            }

            SKPath ring;
            if (innerRect.Width > 0 && innerRect.Height > 0)
            {
                // CSS Backgrounds & Borders §5.2: the inner curve's radius is the
                // outer radius less that side's border width, floored at zero.
                var innerRadii = new[]
                {
                    new SKPoint(
                        Math.Max(0, radii[0].X - border.LeftWidth),
                        Math.Max(0, radii[0].Y - border.TopWidth)),
                    new SKPoint(
                        Math.Max(0, radii[1].X - border.RightWidth),
                        Math.Max(0, radii[1].Y - border.TopWidth)),
                    new SKPoint(
                        Math.Max(0, radii[2].X - border.RightWidth),
                        Math.Max(0, radii[2].Y - border.BottomWidth)),
                    new SKPoint(
                        Math.Max(0, radii[3].X - border.LeftWidth),
                        Math.Max(0, radii[3].Y - border.BottomWidth))
                };

                using var inner = CreateRoundedRectPath(innerRect, innerRadii);
                ring = outer.Op(inner, SKPathOp.Difference);
            }
            else
            {
                ring = new SKPath(outer);
            }

            if (ring is null)
            {
                return false;
            }

            // Each corner is split along the miter - the line from the border-box
            // corner to the padding-box corner behind it - carried on to the middle
            // of the box. Stopping the split at the padding-box corner leaves the
            // stretch of ring between that corner and the curve belonging to
            // neither side, and on a circle that stretch is most of the corner.
            var miterTopLeft = MiterSplitPoint(rect, new SKPoint(rect.Left, rect.Top), border.LeftWidth, border.TopWidth, -1f, 1f);
            var miterTopRight = MiterSplitPoint(rect, new SKPoint(rect.Right, rect.Top), border.RightWidth, border.TopWidth, 1f, 1f);
            var miterBottomRight = MiterSplitPoint(rect, new SKPoint(rect.Right, rect.Bottom), border.RightWidth, border.BottomWidth, 1f, -1f);
            var miterBottomLeft = MiterSplitPoint(rect, new SKPoint(rect.Left, rect.Bottom), border.LeftWidth, border.BottomWidth, -1f, -1f);

            using (ring)
            {
                FillBorderSideOfRing(
                    ring, new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
                    miterTopRight, miterTopLeft, border.TopColor, paintTop);
                FillBorderSideOfRing(
                    ring, new SKPoint(rect.Right, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                    miterBottomRight, miterTopRight, border.RightColor, paintRight);
                FillBorderSideOfRing(
                    ring, new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom),
                    miterBottomLeft, miterBottomRight, border.BottomColor, paintBottom);
                FillBorderSideOfRing(
                    ring, new SKPoint(rect.Left, rect.Bottom), new SKPoint(rect.Left, rect.Top),
                    miterTopLeft, miterBottomLeft, border.LeftColor, paintLeft);
            }

            return true;
        }

        /// <summary>
        /// Where a corner's miter ends: it starts at the border-box corner, runs
        /// in the direction of the padding-box corner behind it, and stops on
        /// whichever centre line of the box it reaches first. Ending on a centre
        /// line is what makes the four sides tile the whole box, so no part of a
        /// rounded corner is left to no side at all.
        /// </summary>
        private static SKPoint MiterSplitPoint(
            SKRect rect,
            SKPoint corner,
            float horizontalWidth,
            float verticalWidth,
            float inwardX,
            float inwardY)
        {
            float travel = float.MaxValue;
            if (horizontalWidth > 0)
            {
                travel = Math.Min(travel, rect.Width / 2f / horizontalWidth);
            }

            if (verticalWidth > 0)
            {
                travel = Math.Min(travel, rect.Height / 2f / verticalWidth);
            }

            if (travel == float.MaxValue)
            {
                // No border on either side of this corner: nothing is painted
                // there, so any point on the centre will do.
                return new SKPoint(rect.MidX, rect.MidY);
            }

            return new SKPoint(
                corner.X - inwardX * horizontalWidth * travel,
                corner.Y + inwardY * verticalWidth * travel);
        }

        /// <summary>
        /// Fills one side's share of a border ring: the area swept from the two
        /// border-box corners the side runs between, inward along each corner's
        /// miter. Filling that region rather than stroking a line is what lets the
        /// side follow the corner curve.
        /// </summary>
        private void FillBorderSideOfRing(
            SKPath ring,
            SKPoint startCorner,
            SKPoint endCorner,
            SKPoint endMiter,
            SKPoint startMiter,
            SKColor color,
            bool enabled)
        {
            if (!enabled || color.Alpha == 0)
            {
                return;
            }

            using var wedge = PathBuilderHelper.Build(p =>
            {
                p.MoveTo(startCorner.X, startCorner.Y);
                p.LineTo(endCorner.X, endCorner.Y);
                p.LineTo(endMiter.X, endMiter.Y);
                p.LineTo(startMiter.X, startMiter.Y);
                p.Close();
            });

            using var slice = ring.Op(wedge, SKPathOp.Intersect);
            if (slice is null || slice.IsEmpty)
            {
                return;
            }

            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            _canvas.DrawPath(slice, paint);
        }

        // dashed/dotted repeat along the edge and double is two rules with a gap:
        // none of them is a solid fill of the ring.
        private static bool IsSegmentedBorderStyle(string style)
        {
            var normalized = style?.Trim();
            return string.Equals(normalized, "dashed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "dotted", StringComparison.OrdinalIgnoreCase) ||
                   IsDoubleBorderStyle(style);
        }

        private void DrawBorderSide(SKRect rect, BorderSide side, SKColor color, float width, string style, bool enabled, BorderStyle border)
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
            using var path = PathBuilderHelper.Build(p =>
            {
                float offset = width / 2f;
                // Each side spans its full edge extent: the side drawn later owns the
                // shared corner (matching the uniform-border fast path). Ending sides
                // at the adjacent side's inner edge left mixed-border corners unpainted
                // — under a transform (e.g. a rotated checkmark built from
                // border-right + border-bottom) the missing corner visibly split the
                // two arms apart.
                switch (side)
                {
                    case BorderSide.Top:
                        p.MoveTo(rect.Left, rect.Top + offset);
                        p.LineTo(rect.Right, rect.Top + offset);
                        break;
                    case BorderSide.Right:
                        p.MoveTo(rect.Right - offset, rect.Top);
                        p.LineTo(rect.Right - offset, rect.Bottom);
                        break;
                    case BorderSide.Bottom:
                        p.MoveTo(rect.Left, rect.Bottom - offset);
                        p.LineTo(rect.Right, rect.Bottom - offset);
                        break;
                    case BorderSide.Left:
                        p.MoveTo(rect.Left + offset, rect.Top);
                        p.LineTo(rect.Left + offset, rect.Bottom);
                        break;
                }
            });
            _canvas.DrawPath(path, paint);
        }

        private void DrawDoubleBorderSide(SKRect rect, BorderSide side, SKColor color, float width)
        {
            float strokeWidth = Math.Max(1f, width / 3f);
            using var paint = CreateBorderPaint(color, strokeWidth, "solid");

            using var outerPath = CreateBorderSideLine(rect, side, strokeWidth / 2f);
            _canvas.DrawPath(outerPath, paint);

            using var innerPath = CreateBorderSideLine(rect, side, width - strokeWidth / 2f);
            _canvas.DrawPath(innerPath, paint);
        }

        private static SKPath CreateBorderSideLine(SKRect rect, BorderSide side, float offset)
        {
            return PathBuilderHelper.Build(path =>
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
            });
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

            var rrect = new SKRoundRect();
            rrect.SetRectRadii(bounds, radius);
            return PathBuilderHelper.Build(path => path.AddRoundRect(rrect));
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
            _canvas.DrawImage(image, destRect, SKSamplingOptions.Default, paint);
        }

        public void DrawImage(SKImage image, SKRect destRect, SKRect srcRect, float opacity = 1f)
        {
            if (image == null) return;
            using var paint = opacity < 1f ? new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) } : null;
            _canvas.DrawImage(image, srcRect, destRect, SKSamplingOptions.Default, paint);
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
            _canvas.Concat(in transform);
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
            _canvas.DrawImage(mask, bounds, SKSamplingOptions.Default, paint);
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

            using var shadowPath = PathBuilderHelper.Build(path =>
                path.AddRect(new SKRect(rect.Left - 200, rect.Top - 200, rect.Right + 200, rect.Bottom + 200)));

            if (HasNonZeroRadius(borderRadius))
            {
                using var innerPath = CreateRoundedRectPath(insetRect, borderRadius);
                shadowPath.Op(innerPath, SKPathOp.Difference, shadowPath);
            }
            else
            {
                using var innerPath = PathBuilderHelper.Build(path => path.AddRect(insetRect));
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
