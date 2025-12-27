using SkiaSharp;
using FenBrowser.Core.Logging;
using System.Linq;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Stateless Skia renderer - the final step in the rendering pipeline.
    /// 
    /// ═══════════════════════════════════════════════════════════════════════════
    /// RENDERER INVARIANTS (REVIEW GATES, NOT GUIDELINES)
    /// ═══════════════════════════════════════════════════════════════════════════
    /// 
    /// 1. STATELESS: No fields, no caches, no mutable state
    /// 2. NO MUTATIONS: Cannot mutate engine state, DOM, or layout
    /// 3. NO SCHEDULING: Cannot schedule work or trigger callbacks
    /// 4. NO JS: Cannot execute JavaScript
    /// 5. NON-FATAL: Must never throw due to content - invalid geometry → skip, not crash
    /// 6. DETERMINISTIC: Same PaintTree → identical pixels
    /// 7. RECURSIVE ONLY: Uses recursive traversal, NOT visitor pattern (visitor is for tooling)
    /// 8. GROUP OPACITY: Opacity only applied at OpacityGroupNode boundaries, never compounded
    /// 9. FROZEN CLIPS: ClipRect is pre-resolved in layout space, no intersection math here
    /// 10. NO BRANCHING ON FRAMEID: FrameId is diagnostic only, never used for caching/branching
    /// 
    /// ═══════════════════════════════════════════════════════════════════════════
    /// TEXTNODE CONTRACT
    /// ═══════════════════════════════════════════════════════════════════════════
    /// 
    /// TextNode contains ONLY:
    /// - Glyph IDs (pre-shaped)
    /// - Absolute positions (pre-computed)
    /// - Resolved font handle
    /// - Resolved color
    /// 
    /// TextNode NEVER:
    /// - Shapes text
    /// - Chooses fallback fonts
    /// - Resolves writing modes
    /// - Measures text
    /// 
    /// ═══════════════════════════════════════════════════════════════════════════
    /// </summary>
    public sealed class SkiaRenderer
    {
        // ═══════════════════════════════════════════════════════════════════
        // NO FIELDS - COMPLETELY STATELESS
        // ═══════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Renders a paint tree to a canvas.
        /// This is the ONLY public method.
        /// </summary>
        public void Render(SKCanvas canvas, ImmutablePaintTree tree, SKRect viewport)
        {
            if (canvas == null || tree == null) return;
            
            // INVARIANT: FrameId is diagnostic only - never branch on it
            // tree.FrameId is NOT used for caching or conditional logic
            
            canvas.Clear(SKColors.White);
            
            foreach (var root in tree.Roots)
            {
                DrawNodeSafe(canvas, root, viewport);
            }
        }
        
        /// <summary>
        /// Renders a paint tree with a custom background color.
        /// </summary>
        public void Render(SKCanvas canvas, ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
            if (canvas == null || tree == null) return;
            
            canvas.Clear(backgroundColor);
            
            int rootCount = tree.Roots.Count;
            int totalNodes = tree.NodeCount;
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaRenderer] Starting render of {totalNodes} nodes ({rootCount} roots) into viewport {viewport}.\r\n"); } catch {}

            // DEBUG: Save screenshot (Only if content exists to avoid blank frames)
            try 
            {
                if (viewport.Width > 0 && viewport.Height > 0 && totalNodes > 20)
                {
                    using (var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height)))
                    {
                        if (surface != null)
                        {
                            var offCanvas = surface.Canvas;
                            offCanvas.Clear(backgroundColor);
                            foreach (var root in tree.Roots) DrawNodeSafe(offCanvas, root, viewport);
                            
                            using (var image = surface.Snapshot())
                            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                            using (var stream = System.IO.File.OpenWrite(@"C:\Users\udayk\Videos\FENBROWSER\debug_screenshot.png"))
                            {
                                data.SaveTo(stream);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
               try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaRenderer] Screenshot failed: {ex.Message}\r\n"); } catch {}
            }

            foreach (var root in tree.Roots)
            {
                DrawNodeSafe(canvas, root, viewport);
            }
            
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaRenderer] Render complete.\r\n"); } catch {}
        }
        
        /// <summary>
        /// Non-fatal wrapper for DrawNode. Catches any exceptions and skips bad nodes.
        /// INVARIANT: Renderer must never crash due to content.
        /// </summary>
        private void DrawNodeSafe(SKCanvas canvas, PaintNodeBase node, SKRect viewport)
        {
            try
            {
                DrawNode(canvas, node, viewport);
            }
            catch
            {
                // Skip node silently - renderer must never crash
            }
        }
        
        /// <summary>
        /// Core recursive drawing algorithm.
        /// INVARIANT: Uses recursive traversal ONLY - visitor pattern is for tooling.
        /// INVARIANT: Same input → identical output (deterministic).
        /// </summary>
        private void DrawNode(SKCanvas canvas, PaintNodeBase node, SKRect viewport)
        {
            if (node == null) return;
            
            // INVARIANT: Skip invalid geometry, don't crash
            if (!IsValidBounds(node.Bounds)) return;
            
            // Cull nodes outside viewport
            if (!node.IntersectsViewport(viewport) && node.Bounds.Width > 0 && node.Bounds.Height > 0)
            {
                return;
            }
            
            canvas.Save();
            
            // Apply transform
            if (node.Transform.HasValue)
            {
                var matrix = node.Transform.Value;
                canvas.Concat(ref matrix);
            }
            
            // INVARIANT: ClipRect is pre-resolved in layout space
            // Renderer does NOT compute clip intersections - just applies
            if (node.ClipRect.HasValue)
            {
                canvas.ClipRect(node.ClipRect.Value);
            }
            
            // INVARIANT: Opacity is group-based ONLY
            // Only OpacityGroupNode creates opacity layers
            // Renderer does NOT multiply opacity down the tree
            bool isOpacityGroup = node is OpacityGroupPaintNode && node.Opacity < 1.0f;
            if (isOpacityGroup)
            {
                byte alpha = (byte)System.Math.Clamp(node.Opacity * 255, 0, 255);
                using var layerPaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
                canvas.SaveLayer(layerPaint);
            }
            
            // Draw self based on node type
            DrawSelf(canvas, node);
            
            // Interaction feedback (Focus Ring & Hover Highlight)
            if (node.IsHovered) DrawHoverHighlight(canvas, node);
            if (node.IsFocused) DrawFocusRing(canvas, node);
            
            // Draw children in order (recursive traversal, not visitor)
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    DrawNodeSafe(canvas, child, viewport);
                }
            }
            
            // Restore opacity layer
            if (isOpacityGroup)
            {
                canvas.Restore();
            }
            
            canvas.Restore();
        }

        private void DrawHoverHighlight(SKCanvas canvas, PaintNodeBase node)
        {
            // Only draw hover for background nodes or top-level containers to avoid mess
            if (!(node is BackgroundPaintNode) && !(node is ImagePaintNode) && !(node is BorderPaintNode)) return;

            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 40), // Subtle white overlay
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            
            if (node is BackgroundPaintNode bg && bg.BorderRadius != null)
            {
                using var path = CreateRoundedRectPath(node.Bounds, bg.BorderRadius);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawRect(node.Bounds, paint);
            }
        }

        private void DrawFocusRing(SKCanvas canvas, PaintNodeBase node)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(0, 120, 215, 200), // Windows-like blue
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.0f,
                IsAntialias = true
            };
            
            var ringRect = node.Bounds;
            ringRect.Inflate(2, 2); // Slightly outside

            if (node is BackgroundPaintNode bg && bg.BorderRadius != null)
            {
                using var path = CreateRoundedRectPath(ringRect, bg.BorderRadius);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawRect(ringRect, paint);
            }
        }
        
        /// <summary>
        /// Validates bounds - returns false for NaN, Infinity, or extreme values.
        /// INVARIANT: Invalid geometry → skip, not crash.
        /// </summary>
        private static bool IsValidBounds(SKRect bounds)
        {
            return !float.IsNaN(bounds.Left) && !float.IsNaN(bounds.Top) &&
                   !float.IsNaN(bounds.Right) && !float.IsNaN(bounds.Bottom) &&
                   !float.IsInfinity(bounds.Left) && !float.IsInfinity(bounds.Top) &&
                   !float.IsInfinity(bounds.Right) && !float.IsInfinity(bounds.Bottom) &&
                   bounds.Width < 100000 && bounds.Height < 100000; // Sanity check
        }
        
        /// <summary>
        /// Draws the node's own content (not children).
        /// Uses visitor pattern internally.
        /// </summary>
        private void DrawSelf(SKCanvas canvas, PaintNodeBase node)
        {
            switch (node)
            {
                case BackgroundPaintNode bg:
                    DrawBackground(canvas, bg);
                    break;
                    
                case BorderPaintNode border:
                    DrawBorder(canvas, border);
                    break;
                    
                case TextPaintNode text:
                    DrawText(canvas, text);
                    break;
                    
                case ImagePaintNode image:
                    DrawImage(canvas, image);
                    break;
                    
                case ClipPaintNode clip:
                    ApplyClipPath(canvas, clip);
                    break;
                    
                case BoxShadowPaintNode shadow:
                    DrawBoxShadow(canvas, shadow);
                    break;

                case StackingContextPaintNode _:
                case OpacityGroupPaintNode _:
                    // These are grouping nodes - no self-drawing
                    break;
            }
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // DRAWING PRIMITIVES
        // ═══════════════════════════════════════════════════════════════════
        
        private void DrawBackground(SKCanvas canvas, BackgroundPaintNode node)
        {
            if (!node.Color.HasValue && node.Gradient == null) return;
            
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            
            if (node.Gradient != null)
            {
                paint.Shader = node.Gradient;
            }
            else if (node.Color.HasValue)
            {
                paint.Color = node.Color.Value;
            }
            
            if (node.BorderRadius != null && HasNonZeroRadius(node.BorderRadius))
            {
                // Rounded rectangle
                using var path = CreateRoundedRectPath(node.Bounds, node.BorderRadius);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawRect(node.Bounds, paint);
            }
        }
        
        private void DrawBoxShadow(SKCanvas canvas, BoxShadowPaintNode node)
        {
            if (node.Color.Alpha == 0) return;

            using var paint = new SKPaint
            {
                Color = node.Color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            // Apply blur
            if (node.Blur > 0)
            {
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, node.Blur / 1.5f); // 1.5 factor approximates CSS blur radius
            }

            var drawRect = node.Bounds;
            
            // Apply offset
            drawRect.Offset(node.Offset.X, node.Offset.Y);
            
            // Handling Spread: Negative spread shrinks, positive expands
            if (node.Spread != 0)
            {
                drawRect.Inflate(node.Spread, node.Spread);
            }

            if (HasNonZeroRadius(node.BorderRadius))
            {
                // Rounded shadow
                using var path = CreateRoundedRectPath(drawRect, node.BorderRadius);
                
                // If inset, we need to clip outer or do inner shadow logic
                // CSS Inset shadows are complex in Skia. 
                // Simplified INSET implementation: Clip to bounds, draw stroked rect outside?
                // For now, only supporting OUTSET (Drop) shadows robustly as per plan.
                if (!node.Inset)
                {
                    canvas.DrawPath(path, paint);
                }
            }
            else
            {
                if (!node.Inset)
                {
                    canvas.DrawRect(drawRect, paint);
                }
            }
        }
        
        private void DrawBorder(SKCanvas canvas, BorderPaintNode node)
        {
            if (node.Widths == null || node.Colors == null) return;
            
            if (HasNonZeroRadius(node.BorderRadius))
            {
                // Rounded border path
                // Simplify: assume uniform width/color for now (common case for buttons/inputs)
                float width = node.Widths[0];
                if (width > 0 && node.Colors[0].Alpha > 0)
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = width,
                        Color = node.Colors[0]
                    };
                    
                    // Inset bounds by half width to stroke inside
                    float halfWidth = width / 2;
                    var strokeBounds = new SKRect(
                        node.Bounds.Left + halfWidth, 
                        node.Bounds.Top + halfWidth, 
                        node.Bounds.Right - halfWidth, 
                        node.Bounds.Bottom - halfWidth);
                        
                    // Check if bounds valid
                    if (strokeBounds.Width > 0 && strokeBounds.Height > 0)
                    {
                        using var path = CreateRoundedRectPath(strokeBounds, node.BorderRadius); // Radii should technically be adjusted too, but skipped for now
                        canvas.DrawPath(path, paint);
                    }
                }
                return;
            }

            // Draw each border side
            for (int i = 0; i < 4; i++)
            {
                float width = node.Widths[i];
                if (width <= 0) continue;
                
                SKColor color = node.Colors[i];
                if (color.Alpha == 0) continue;
                
                using var paint = new SKPaint
                {
                    IsAntialias = width >= 1.5f,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = width,
                    Color = color
                };
                
                // Apply border style
                if (node.Styles != null && i < node.Styles.Length)
                {
                    ApplyBorderStyle(paint, node.Styles[i]);
                }
                
                // Draw border line
                DrawBorderSide(canvas, node.Bounds, i, width, paint);
            }
        }
        
        private void DrawBorderSide(SKCanvas canvas, SKRect bounds, int side, float width, SKPaint paint)
        {
            float halfWidth = width / 2;
            
            switch (side)
            {
                case 0: // Top
                    canvas.DrawLine(bounds.Left, bounds.Top + halfWidth, bounds.Right, bounds.Top + halfWidth, paint);
                    break;
                case 1: // Right
                    canvas.DrawLine(bounds.Right - halfWidth, bounds.Top, bounds.Right - halfWidth, bounds.Bottom, paint);
                    break;
                case 2: // Bottom
                    canvas.DrawLine(bounds.Left, bounds.Bottom - halfWidth, bounds.Right, bounds.Bottom - halfWidth, paint);
                    break;
                case 3: // Left
                    canvas.DrawLine(bounds.Left + halfWidth, bounds.Top, bounds.Left + halfWidth, bounds.Bottom, paint);
                    break;
            }
        }
        
        private void ApplyBorderStyle(SKPaint paint, string style)
        {
            switch (style?.ToLowerInvariant())
            {
                case "dashed":
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 6, 4 }, 0);
                    break;
                case "dotted":
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0);
                    break;
                case "double":
                    // Double border needs special handling - simplified here
                    break;
                case "none":
                case "hidden":
                    paint.StrokeWidth = 0;
                    break;
                // solid is default - no effect needed
            }
        }
        
        private void DrawText(SKCanvas canvas, TextPaintNode node)
        {
            float fontSize = node.FontSize > 0 ? node.FontSize : 16;
            var typeface = node.Typeface ?? SKTypeface.Default;
            
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = node.Color,
                TextSize = fontSize,
                Typeface = typeface
            };
            
            float textWidth = 0;
            float baselineY = node.TextOrigin.Y;
            
            // Use glyphs if available (preferred)
            if (node.Glyphs != null && node.Glyphs.Count > 0)
            {
                textWidth = DrawGlyphs(canvas, node.Glyphs, paint, typeface, fontSize);
            }
            // Fallback to raw text
            else if (!string.IsNullOrEmpty(node.FallbackText))
            {
                // Sanitize text to remove control characters that might render as boxes
                string cleanText = node.FallbackText.Replace("\r", "").Replace("\n", "").Replace("\t", " ");
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    canvas.DrawText(cleanText, node.TextOrigin.X, node.TextOrigin.Y, paint);
                    textWidth = paint.MeasureText(cleanText);
                }
            }
            
            // Draw text decorations
            if (textWidth > 0 && node.TextDecorations != null)
            {
                DrawTextDecorations(canvas, node, textWidth, paint);
            }
        }
        
        private float DrawGlyphs(SKCanvas canvas, System.Collections.Generic.IReadOnlyList<PositionedGlyph> glyphs, SKPaint paint, SKTypeface typeface, float fontSize)
        {
            if (glyphs.Count == 0) return 0;
            
            // Build glyph arrays for batch rendering
            var glyphIds = new ushort[glyphs.Count];
            var positions = new SKPoint[glyphs.Count];
            
            float minX = float.MaxValue, maxX = float.MinValue;
            
            for (int i = 0; i < glyphs.Count; i++)
            {
                glyphIds[i] = glyphs[i].GlyphId;
                positions[i] = new SKPoint(glyphs[i].X, glyphs[i].Y);
                minX = System.Math.Min(minX, glyphs[i].X);
                maxX = System.Math.Max(maxX, glyphs[i].X);
            }
            
            // Use SKTextBlob for efficient glyph rendering
            using var font = new SKFont(typeface, fontSize);
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, glyphs.Count);
            
            var glyphSpan = run.GetGlyphSpan();
            var posSpan = run.GetPositionSpan();
            
            for (int i = 0; i < glyphs.Count; i++)
            {
                glyphSpan[i] = glyphIds[i];
                posSpan[i] = positions[i];
            }
            
            using var blob = builder.Build();
            if (blob != null)
            {
                canvas.DrawText(blob, 0, 0, paint);
            }
            
            // Return approximate text width
            return maxX - minX + fontSize * 0.6f;
        }
        
        private void DrawTextDecorations(SKCanvas canvas, TextPaintNode node, float textWidth, SKPaint basePaint)
        {
            if (node.TextDecorations == null) return;
            
            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Color = basePaint.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = System.Math.Max(1, basePaint.TextSize / 16)
            };
            
            float x = node.TextOrigin.X;
            float y = node.TextOrigin.Y;
            
            foreach (var decoration in node.TextDecorations)
            {
                switch (decoration.ToLowerInvariant())
                {
                    case "underline":
                        float underlineY = y + basePaint.TextSize * 0.15f;
                        canvas.DrawLine(x, underlineY, x + textWidth, underlineY, linePaint);
                        break;
                        
                    case "line-through":
                        float strikeY = y - basePaint.TextSize * 0.3f;
                        canvas.DrawLine(x, strikeY, x + textWidth, strikeY, linePaint);
                        break;
                        
                    case "overline":
                        float overlineY = y - basePaint.TextSize * 0.85f;
                        canvas.DrawLine(x, overlineY, x + textWidth, overlineY, linePaint);
                        break;
                }
            }
        }
        
        private void DrawImage(SKCanvas canvas, ImagePaintNode node)
        {
            if (node.Bitmap == null)
            {
                // Do not draw diagnostic placeholders in production
                return;
            }
            
            using var paint = new SKPaint { 
                IsAntialias = true,
                FilterQuality = SKFilterQuality.Medium 
            };
            
            SKRect srcRect = node.SourceRect ?? new SKRect(0, 0, node.Bitmap.Width, node.Bitmap.Height);
            SKRect destRect = CalculateDestRect(node.Bounds, srcRect, node.ObjectFit);
            
            canvas.DrawBitmap(node.Bitmap, srcRect, destRect, paint);
        }

        // Removed DrawDiagnosticPlaceholder
        
        private static SKRect CalculateDestRect(SKRect bounds, SKRect srcRect, string objectFit)
        {
            if (string.IsNullOrEmpty(objectFit) || objectFit == "fill")
            {
                return bounds;
            }
            
            float srcAspect = srcRect.Width / srcRect.Height;
            float destAspect = bounds.Width / bounds.Height;
            
            float destWidth, destHeight;
            
            switch (objectFit.ToLowerInvariant())
            {
                case "contain":
                    if (srcAspect > destAspect)
                    {
                        destWidth = bounds.Width;
                        destHeight = bounds.Width / srcAspect;
                    }
                    else
                    {
                        destHeight = bounds.Height;
                        destWidth = bounds.Height * srcAspect;
                    }
                    break;
                    
                case "cover":
                    if (srcAspect > destAspect)
                    {
                        destHeight = bounds.Height;
                        destWidth = bounds.Height * srcAspect;
                    }
                    else
                    {
                        destWidth = bounds.Width;
                        destHeight = bounds.Width / srcAspect;
                    }
                    break;
                    
                case "none":
                    destWidth = srcRect.Width;
                    destHeight = srcRect.Height;
                    break;
                    
                case "scale-down":
                    if (srcRect.Width <= bounds.Width && srcRect.Height <= bounds.Height)
                    {
                        destWidth = srcRect.Width;
                        destHeight = srcRect.Height;
                    }
                    else
                    {
                        // Same as contain
                        if (srcAspect > destAspect)
                        {
                            destWidth = bounds.Width;
                            destHeight = bounds.Width / srcAspect;
                        }
                        else
                        {
                            destHeight = bounds.Height;
                            destWidth = bounds.Height * srcAspect;
                        }
                    }
                    break;
                    
                default:
                    return bounds;
            }
            
            // Center the image in bounds
            float x = bounds.Left + (bounds.Width - destWidth) / 2;
            float y = bounds.Top + (bounds.Height - destHeight) / 2;
            
            return new SKRect(x, y, x + destWidth, y + destHeight);
        }
        
        private void ApplyClipPath(SKCanvas canvas, ClipPaintNode node)
        {
            if (node.ClipPath != null)
            {
                canvas.ClipPath(node.ClipPath);
            }
            else if (node.ClipRect.HasValue)
            {
                canvas.ClipRect(node.ClipRect.Value);
            }
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════════
        
        private static bool HasNonZeroRadius(float[] radius)
        {
            if (radius == null || radius.Length < 4) return false;
            return radius[0] > 0 || radius[1] > 0 || radius[2] > 0 || radius[3] > 0;
        }
        
        private static SKPath CreateRoundedRectPath(SKRect bounds, float[] radius)
        {
            var path = new SKPath();
            
            float tl = radius[0], tr = radius[1], br = radius[2], bl = radius[3];
            
            // CSS Spec: The sum of radii should be capped at the dimension
            float scale = 1.0f;
            float topSum = tl + tr;
            float bottomSum = bl + br;
            float leftSum = tl + bl;
            float rightSum = tr + br;

            if (topSum > bounds.Width) scale = System.Math.Min(scale, bounds.Width / topSum);
            if (bottomSum > bounds.Width) scale = System.Math.Min(scale, bounds.Width / bottomSum);
            if (leftSum > bounds.Height) scale = System.Math.Min(scale, bounds.Height / leftSum);
            if (rightSum > bounds.Height) scale = System.Math.Min(scale, bounds.Height / rightSum);

            if (scale < 1.0f)
            {
                tl *= scale; tr *= scale; br *= scale; bl *= scale;
            }

            path.MoveTo(bounds.Left + tl, bounds.Top);
            path.LineTo(bounds.Right - tr, bounds.Top);
            if (tr > 0) path.ArcTo(new SKRect(bounds.Right - tr * 2, bounds.Top, bounds.Right, bounds.Top + tr * 2), -90, 90, false);
            
            path.LineTo(bounds.Right, bounds.Bottom - br);
            if (br > 0) path.ArcTo(new SKRect(bounds.Right - br * 2, bounds.Bottom - br * 2, bounds.Right, bounds.Bottom), 0, 90, false);
            
            path.LineTo(bounds.Left + bl, bounds.Bottom);
            if (bl > 0) path.ArcTo(new SKRect(bounds.Left, bounds.Bottom - bl * 2, bounds.Left + bl * 2, bounds.Bottom), 90, 90, false);
            
            path.LineTo(bounds.Left, bounds.Top + tl);
            if (tl > 0) path.ArcTo(new SKRect(bounds.Left, bounds.Top, bounds.Left + tl * 2, bounds.Top + tl * 2), 180, 90, false);
            
            path.Close();
            return path;
        }
    }
}
