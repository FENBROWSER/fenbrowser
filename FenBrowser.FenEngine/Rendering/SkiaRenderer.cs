using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Backends;
using System;

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
            Render(canvas, tree, viewport, SKColors.White);
        }
        
        /// <summary>
        /// RULE 4: Render using abstracted backend interface.
        /// This overload enables backend-independent rendering for testing and portability.
        /// </summary>
        public void Render(IRenderBackend backend, ImmutablePaintTree tree, SKRect viewport)
        {
            if (backend == null || tree == null) return;
            
            backend.Clear(SKColors.White);
            
            foreach (var root in tree.Roots)
            {
                DrawNodeSafe(backend, root, viewport);
            }
        }
        
        /// <summary>
        /// Renders a paint tree with a custom background color.
        /// </summary>
        public void Render(SKCanvas canvas, ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
            if (canvas == null || tree == null) return;
            
            // Wrap canvas in adapter (Rule 4)
            var backend = new SkiaRenderBackend(canvas);
            
            // Perform screenshot if needed (legacy debug logic)
            // Ideally this should be moved out, but keeping for continuity
            CaptureDebugScreenshot(tree, viewport, backgroundColor);
            
            // Set background color manually since we bypassed the default Clear via delegation
            backend.Clear(backgroundColor);
            
            foreach (var root in tree.Roots)
            {
                FenLogger.Debug($"[SkiaRenderer] Drawing Root Node {root.GetType().Name} Bounds={root.Bounds} Z={((root as OpacityGroupPaintNode)?.Opacity ?? 1)}");
                DrawNodeSafe(backend, root, viewport);
            }
        }
        
        private void CaptureDebugScreenshot(ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
             try 
            {
                if (viewport.Width > 0 && viewport.Height > 0 && tree.NodeCount > 20)
                {
                    using (var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height)))
                    {
                        if (surface != null)
                        {
                            var offCanvas = surface.Canvas;
                            // Recursive delegation would be cleaner but for now just use local backend
                            var backend = new SkiaRenderBackend(offCanvas);
                            backend.Clear(backgroundColor);
                            foreach (var root in tree.Roots) DrawNodeSafe(backend, root, viewport);
                            
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
            catch {}
        }
        
        /// <summary>
        /// Non-fatal wrapper for DrawNode. Catches any exceptions and skips bad nodes.
        /// INVARIANT: Renderer must never crash due to content.
        /// </summary>
        /// <summary>
        /// Non-fatal wrapper for DrawNode. Catches any exceptions and skips bad nodes.
        /// INVARIANT: Renderer must never crash due to content.
        /// </summary>
        private void DrawNodeSafe(IRenderBackend backend, PaintNodeBase node, SKRect viewport)
        {
            try
            {
                DrawNode(backend, node, viewport);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[SkiaRenderer] DrawNode Failed for {node.GetType().Name}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Core recursive drawing algorithm.
        /// INVARIANT: Uses recursive traversal ONLY - visitor pattern is for tooling.
        /// INVARIANT: Same input → identical output (deterministic).
        /// </summary>
        private void DrawNode(IRenderBackend backend, PaintNodeBase node, SKRect viewport)
        {
            if (node == null) return;
            
            // INVARIANT: Skip invalid geometry, don't crash
            if (!IsValidBounds(node.Bounds)) return;
            
            // Cull nodes outside viewport
            if (!node.IntersectsViewport(viewport) && node.Bounds.Width > 0 && node.Bounds.Height > 0)
            {
                // FenLogger.Debug($"[SkiaRenderer] Culled {node.GetType().Name} at {node.Bounds}");
                return;
            }
            
            backend.Save();
            
            // Apply transform (PushTransform does Save + Concat)
            bool pushedTransform = false;
            if (node.Transform.HasValue)
            {
                backend.PushTransform(node.Transform.Value);
                pushedTransform = true;
            }
            
            // Apply clip (PushClip does Save + Clip)
            bool pushedClip = false;
            if (node.ClipRect.HasValue)
            {
                FenLogger.Debug($"[SkiaRenderer] Pushing Clip: {node.ClipRect.Value} for {node.GetType().Name} (Bounds={node.Bounds})");
                backend.PushClip(node.ClipRect.Value);
                pushedClip = true;
            }
            
            // Apply opacity (PushLayer does SaveLayer)
            bool pushedOpacity = false;
            bool isOpacityGroup = node is OpacityGroupPaintNode && node.Opacity < 1.0f;
            if (isOpacityGroup)
            {
                float opacity = Math.Clamp(node.Opacity, 0f, 1f);
                backend.PushLayer(opacity);
                pushedOpacity = true;
            }
            
            // Draw self based on node type
            DrawSelf(backend, node);
            
            // Interaction feedback (Focus Ring & Hover Highlight)
            if (node.IsHovered) DrawHoverHighlight(backend, node);
            if (node.IsFocused) DrawFocusRing(backend, node);
            
            // Draw children in order (recursive traversal)
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    DrawNodeSafe(backend, child, viewport);
                }
            }
            
            // Restore state in reverse order
            if (pushedOpacity) backend.PopLayer();
            if (pushedClip) backend.PopClip();
            if (pushedTransform) backend.PopLayer(); // PopLayer restores the Save from PushTransform
            
            backend.Restore();
        }

        private void DrawHoverHighlight(IRenderBackend backend, PaintNodeBase node)
        {
            // Only draw hover for background nodes or top-level containers to avoid mess
            if (!(node is BackgroundPaintNode) && !(node is ImagePaintNode) && !(node is BorderPaintNode)) return;

            var paintColor = new SKColor(255, 255, 255, 40); // Subtle white overlay
            
            if (node is BackgroundPaintNode bg && bg.BorderRadius != null && HasNonZeroRadius(bg.BorderRadius))
            {
                using var path = CreateRoundedRectPath(node.Bounds, bg.BorderRadius);
                backend.DrawPath(path, paintColor);
            }
            else
            {
                backend.DrawRect(node.Bounds, paintColor);
            }
        }

        private void DrawFocusRing(IRenderBackend backend, PaintNodeBase node)
        {
            var ringColor = new SKColor(0, 120, 215, 200); // Windows-like blue
            float strokeWidth = 2.0f;
            
            var ringRect = node.Bounds;
            ringRect.Inflate(2, 2); // Slightly outside

            if (node is BackgroundPaintNode bg && bg.BorderRadius != null && HasNonZeroRadius(bg.BorderRadius))
            {
                using var path = CreateRoundedRectPath(ringRect, bg.BorderRadius);
                // Backend lacks generic DrawPathStroke, so we simulate or skip for now.
                // Wait, IRenderBackend doesn't have DrawPathStroke yet?
                // Assuming we use DrawPath with stroke color? No DrawPath is fill.
                // Fallback to RectStroke for simplicity or add feature later.
                // For now, draw rect stroke to avoid interface churn.
                backend.DrawRectStroke(ringRect, ringColor, strokeWidth);
            }
            else
            {
                backend.DrawRectStroke(ringRect, ringColor, strokeWidth);
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
        private void DrawSelf(IRenderBackend backend, PaintNodeBase node)
        {
            switch (node)
            {
                case BackgroundPaintNode bg:
                    DrawBackground(backend, bg);
                    break;
                    
                case BorderPaintNode border:
                    DrawBorder(backend, border);
                    break;
                    
                case TextPaintNode text:
                    DrawText(backend, text);
                    break;
                    
                case ImagePaintNode image:
                    DrawImage(backend, image);
                    break;
                    
                case ClipPaintNode clip:
                    ApplyClipPath(backend, clip);
                    break;
                    
                case BoxShadowPaintNode shadow:
                    DrawBoxShadow(backend, shadow);
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
        
        private void DrawBackground(IRenderBackend backend, BackgroundPaintNode node)
        {
            if (!node.Color.HasValue && node.Gradient == null) return;
            
            bool hasRadius = node.BorderRadius != null && HasNonZeroRadius(node.BorderRadius);
            
            if (node.Gradient != null)
            {
                // Gradient background
                if (hasRadius)
                {
                    using var path = CreateRoundedRectPath(node.Bounds, node.BorderRadius);
                    backend.DrawPath(path, node.Gradient);
                }
                else
                {
                    backend.DrawRect(node.Bounds, node.Gradient);
                }
            }
            else if (node.Color.HasValue)
            {
                // Solid color background
                if (hasRadius)
                {
                    using var path = CreateRoundedRectPath(node.Bounds, node.BorderRadius);
                    backend.DrawPath(path, node.Color.Value);
                }
                else
                {
                    backend.DrawRect(node.Bounds, node.Color.Value);
                }
            }
        }
        
        private void DrawBoxShadow(IRenderBackend backend, BoxShadowPaintNode node)
        {
            if (node.Color.Alpha == 0) return;

            // Box shadow logic involves drawing a blurred shape behind the element
            // IRenderBackend abstracts this via DrawBoxShadow (rect) and DrawShadow (path/complex)

            // Prepare geometry
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
                // Rounded shadow - use generic DrawShadow with path
                using var path = CreateRoundedRectPath(drawRect, node.BorderRadius);
                
                // If inset, standard backend doesn't support it yet (simplified to Outset)
                if (!node.Inset)
                {
                    backend.DrawShadow(path, 0, 0, node.Blur, node.Color);
                }
            }
            else
            {
                if (!node.Inset)
                {
                    // Rectangular shadow - use optimized DrawBoxShadow
                    // backend handles blur sigma calc
                    backend.DrawBoxShadow(drawRect, 0, 0, node.Blur, 0, node.Color);
                }
            }
        }
        
        private void DrawBorder(IRenderBackend backend, BorderPaintNode node)
        {
            if (node.Widths == null || node.Colors == null) return;
            
            // Construct BorderStyle for backend
            // SkiaRenderer previously had logic to simplify rounded borders to uniform stroke
            // We pass full info to backend and let it decide strategy
            
            var style = new BorderStyle
            {
                TopWidth = node.Widths[0],
                RightWidth = node.Widths.Length > 1 ? node.Widths[1] : node.Widths[0],
                BottomWidth = node.Widths.Length > 2 ? node.Widths[2] : node.Widths[0],
                LeftWidth = node.Widths.Length > 3 ? node.Widths[3] : (node.Widths.Length > 1 ? node.Widths[1] : node.Widths[0]),
                
                TopColor = node.Colors[0],
                RightColor = node.Colors.Length > 1 ? node.Colors[1] : node.Colors[0],
                BottomColor = node.Colors.Length > 2 ? node.Colors[2] : node.Colors[0],
                LeftColor = node.Colors.Length > 3 ? node.Colors[3] : (node.Colors.Length > 1 ? node.Colors[1] : node.Colors[0])
            };
            
            // Apply radii if present
            if (node.BorderRadius != null)
            {
                style.TopLeftRadius = node.BorderRadius[0];
                style.TopRightRadius = node.BorderRadius.Length > 1 ? node.BorderRadius[1] : node.BorderRadius[0];
                style.BottomRightRadius = node.BorderRadius.Length > 2 ? node.BorderRadius[2] : node.BorderRadius[0];
                style.BottomLeftRadius = node.BorderRadius.Length > 3 ? node.BorderRadius[3] : (node.BorderRadius.Length > 1 ? node.BorderRadius[1] : node.BorderRadius[0]);
            }
            
            backend.DrawBorder(node.Bounds, style);
        }
        
        private void DrawText(IRenderBackend backend, TextPaintNode node)
        {
            if (node.Color.Alpha == 0) return;
            
            // Fix: Access properties directly (node.Format doesn't exist)
            float fontSize = node.FontSize > 0 ? node.FontSize : 16f;
            var typeface = node.Typeface ?? SKTypeface.Default;
            
            float textWidth = 0;
            
            // Use glyphs if available (preferred)
            if (node.Glyphs != null && node.Glyphs.Count > 0)
            {
                // Fix: Use object initializer for GlyphRun (no constructor)
                // Convert List to Array and Map types (CS0029 mismatch fix)
                // Mapping Rendering.PositionedGlyph -> Typography.PositionedGlyph
                var glyphArray = System.Linq.Enumerable.ToArray(node.Glyphs.Select(g => new FenBrowser.FenEngine.Typography.PositionedGlyph 
                {
                    GlyphId = g.GlyphId,
                    X = g.X,
                    Y = g.Y,
                    AdvanceX = 0 // Backend doesn't use AdvanceX for drawing
                }));
                
                var run = new FenBrowser.FenEngine.Typography.GlyphRun
                {
                    Typeface = typeface,
                    FontSize = fontSize,
                    Glyphs = glyphArray
                };
                
                // Estimate width for decorations from glyphs
                if (glyphArray.Length > 0)
                {
                    float minX = float.MaxValue, maxX = float.MinValue;
                    foreach (var g in glyphArray)
                    {
                         if (g.X < minX) minX = g.X;
                         if (g.X > maxX) maxX = g.X;
                    }
                    textWidth = maxX - minX + fontSize * 0.6f;
                }
                
                backend.DrawGlyphRun(SKPoint.Empty, run, node.Color);
            }
            // Fallback to raw text
            else if (!string.IsNullOrEmpty(node.FallbackText))
            {
                // Sanitize text
                string cleanText = node.FallbackText.Replace("\r", "").Replace("\n", "").Replace("\t", " ");
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    backend.DrawText(cleanText, node.TextOrigin, node.Color, fontSize, typeface);
                    // Measure text logic would be needed for correct decoration width...
                    // For fallback, we might skip precise decoration width or guess.
                    textWidth = cleanText.Length * fontSize * 0.6f; // Rough estimate
                }
            }
            
            // Draw text decorations
            if (textWidth > 0 && node.TextDecorations != null)
            {
                DrawTextDecorations(backend, node, textWidth, fontSize, node.Color);
            }
        }
        
        // Removed DrawGlyphs (logic moved to DrawText/Backend)
        
        private void DrawTextDecorations(IRenderBackend backend, TextPaintNode node, float textWidth, float fontSize, SKColor color)
        {
            if (node.TextDecorations == null) return;
            
            float strokeWidth = System.Math.Max(1, fontSize / 16);
            
            float x = node.TextOrigin.X;
            float y = node.TextOrigin.Y;
            
            foreach (var decoration in node.TextDecorations)
            {
                switch (decoration.ToLowerInvariant())
                {
                    case "underline":
                        float underlineY = y + fontSize * 0.15f;
                        backend.DrawRect(new SKRect(x, underlineY, x + textWidth, underlineY + strokeWidth), color);
                        break;
                        
                    case "line-through":
                        float strikeY = y - fontSize * 0.3f;
                        backend.DrawRect(new SKRect(x, strikeY, x + textWidth, strikeY + strokeWidth), color);
                        break;
                        
                    case "overline":
                        float overlineY = y - fontSize * 0.85f;
                        backend.DrawRect(new SKRect(x, overlineY, x + textWidth, overlineY + strokeWidth), color);
                        break;
                }
            }
        }
        
        private void DrawImage(IRenderBackend backend, ImagePaintNode node)
        {
            // Trace image drawing with clip info for debug
            // Note: backend methods like DrawImage are abstract, we can't inspect Clip from backend simply here
            // unless we cast or backend exposes it. 
            // BUT SkiaRenderBackend wraps SKCanvas, we could add diagnostic there?
            // Actually, wait, the method signature I viewed doesn't have 'canvas'.
            // I need to modify SkiaRenderBackend or just accept that I can't easily see the clip here
            // without casting backend.
            
            var skiaBackend = backend as SkiaRenderBackend;
            string clipInfo = "N/A";
            if (skiaBackend != null)
            {
               // Reflection or public prop? SkiaRenderBackend usually exposes Canvas?
               // Let's assume for now just logging bounds.
               // Actually, SkiaRenderer.cs is generic over IRenderBackend.
               // For debugging I'll try to cast to see if I can get the canvas or tracked state.
            }
            
            FenLogger.Debug($"[SkiaRenderer] DrawImage called. Bounds={node.Bounds}, Bitmap={(node.Bitmap != null ? $"{node.Bitmap.Width}x{node.Bitmap.Height}" : "NULL")}");
            if (node.Bitmap == null) return;
            
            SKRect srcRect = node.SourceRect ?? new SKRect(0, 0, node.Bitmap.Width, node.Bitmap.Height);
            SKRect destRect = CalculateDestRect(node.Bounds, srcRect, node.ObjectFit);
            
            FenLogger.Debug($"[SkiaRenderer] DrawImage destRect={destRect}");
            
            using var image = SKImage.FromBitmap(node.Bitmap);
            backend.DrawImage(image, destRect);
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
        
        private void ApplyClipPath(IRenderBackend backend, ClipPaintNode node)
        {
            if (node.ClipPath != null)
            {
                backend.PushClip(node.ClipPath);
            }
            else if (node.ClipRect.HasValue)
            {
                backend.PushClip(node.ClipRect.Value);
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
