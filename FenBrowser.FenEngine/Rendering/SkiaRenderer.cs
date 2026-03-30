using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Backends;
using FenBrowser.FenEngine.Rendering.Css;
using System;
using System.IO;
using System.Collections.Generic;

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
        private static readonly TimeSpan DebugScreenshotMinimumInterval = TimeSpan.FromSeconds(5);

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

        /// <summary>
        /// Partial raster path for damage-only redraw. Caller must pre-populate canvas with previous frame.
        /// </summary>
        public void RenderDamaged(SKCanvas canvas, ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor, IReadOnlyList<SKRect> damageRegions)
        {
            if (canvas == null || tree == null)
            {
                return;
            }

            CaptureDebugScreenshot(tree, viewport, backgroundColor);

            if (damageRegions == null || damageRegions.Count == 0)
            {
                Render(canvas, tree, viewport, backgroundColor);
                return;
            }

            var normalizedRegions = new DamageRegionNormalizationPolicy().Normalize(damageRegions, viewport);
            if (normalizedRegions.Count == 0)
            {
                Render(canvas, tree, viewport, backgroundColor);
                return;
            }

            var backend = new SkiaRenderBackend(canvas);
            using var bgPaint = new SKPaint
            {
                Color = backgroundColor,
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };

            for (var i = 0; i < normalizedRegions.Count; i++)
            {
                if (!TryIntersect(normalizedRegions[i], viewport, out var clipped))
                {
                    continue;
                }

                canvas.Save();
                canvas.ClipRect(clipped, SKClipOperation.Intersect, true);
                canvas.DrawRect(clipped, bgPaint);

                foreach (var root in tree.Roots)
                {
                    DrawNodeSafe(backend, root, viewport);
                }

                canvas.Restore();
            }
        }
        
        private void CaptureDebugScreenshot(ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
             try 
            {
                if (viewport.Width <= 0 || viewport.Height <= 0)
                {
                    FenLogger.Debug($"[SkiaRenderer] Skipping debug_screenshot.png capture due to invalid viewport {viewport.Width}x{viewport.Height}", LogCategory.Rendering);
                    return;
                }

                if (tree.NodeCount <= 2)
                {
                    FenLogger.Debug($"[SkiaRenderer] Skipping debug_screenshot.png capture due to tiny paint tree NodeCount={tree.NodeCount}", LogCategory.Rendering);
                    return;
                }

                var screenshotPath = DiagnosticPaths.GetRootArtifactPath("debug_screenshot.png");
                if (!ShouldCaptureDebugScreenshot(screenshotPath))
                {
                    return;
                }

                using (var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height)))
                {
                    if (surface == null)
                    {
                        FenLogger.Warn("[SkiaRenderer] Failed to allocate debug screenshot surface", LogCategory.Rendering);
                        return;
                    }

                    var offCanvas = surface.Canvas;
                    // Recursive delegation would be cleaner but for now just use local backend
                    var backend = new SkiaRenderBackend(offCanvas);
                    backend.Clear(backgroundColor);
                    foreach (var root in tree.Roots)
                    {
                        DrawNodeSafe(backend, root, viewport);
                    }

                    using (var image = surface.Snapshot())
                    {
                        if (image == null)
                        {
                            FenLogger.Warn($"[SkiaRenderer] Snapshot returned null while writing {screenshotPath}", LogCategory.Rendering);
                            return;
                        }

                        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                        {
                            if (data == null)
                            {
                                FenLogger.Warn($"[SkiaRenderer] PNG encode returned null while writing {screenshotPath}", LogCategory.Rendering);
                                return;
                            }

                            using (var stream = File.Open(screenshotPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                            {
                                data.SaveTo(stream);
                                FenLogger.Debug($"[SkiaRenderer] Wrote debug_screenshot.png to {screenshotPath} ({viewport.Width}x{viewport.Height}, NodeCount={tree.NodeCount})", LogCategory.Rendering);
                                FenBrowser.Core.Verification.ContentVerifier.RegisterScreenshot(screenshotPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { FenLogger.Warn($"[SkiaRenderer] RenderToBitmap failed: {ex.Message}", LogCategory.Rendering); }
        }

        private static bool ShouldCaptureDebugScreenshot(string screenshotPath)
        {
            try
            {
                if (!File.Exists(screenshotPath))
                {
                    return true;
                }

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(screenshotPath);
                return age >= DebugScreenshotMinimumInterval;
            }
            catch
            {
                return true;
            }
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
            int initialSaveDepth = backend.SaveDepth;

            try
            {
                DrawNode(backend, node, viewport);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[SkiaRenderer] DrawNode Failed for {node.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (backend.SaveDepth != initialSaveDepth)
                {
                    FenLogger.Warn(
                        $"[SkiaRenderer] Correcting backend save-depth imbalance for {node?.GetType().Name}: {backend.SaveDepth} -> {initialSaveDepth}",
                        LogCategory.Rendering);
                    backend.RestoreToSaveDepth(initialSaveDepth);
                }
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
            
            // Cull leaf/visual nodes outside the viewport.
            // Grouping nodes can legitimately carry approximate bounds while their children
            // remain visible after transforms, sticky offsets, or stacking-context wrapping.
            if (ShouldCullByOwnBounds(node) &&
                node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
                !IntersectsViewportBounds(node.Bounds, viewport))
            {
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
            // Handle explicit ClipRect (base property)
            if (node.ClipRect.HasValue)
            {
                backend.PushClip(node.ClipRect.Value);
                pushedClip = true;
            }
            // Handle ClipPaintNode specific path clip (e.g. border-radius)
            else if (node is ClipPaintNode clipNode && clipNode.ClipPath != null)
            {
                // FenLogger.Debug($"[SkiaRenderer] Pushing Path Clip for {node.GetType().Name}");
                backend.PushClip(clipNode.ClipPath);
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
            
            // Apply mask
            bool pushedMask = false;
            if (node is MaskPaintNode maskNode && maskNode.MaskBitmap != null)
            {
                 // Isolate content for masking
                 backend.PushLayer(1.0f);
                 pushedMask = true;
            }

            // Apply CSS filter (stacking-context level)
            bool pushedFilter = false;
            SKImageFilter filterLayer = null;
            if (node is StackingContextPaintNode scFilter &&
                !string.IsNullOrEmpty(scFilter.Filter))
            {
                filterLayer = CssFilterParser.Parse(scFilter.Filter);
                if (filterLayer != null)
                {
                    backend.PushFilter(filterLayer);
                    pushedFilter = true;
                }
            }

            // Apply backdrop-filter (affects backdrop behind this context)
            if (node is StackingContextPaintNode scBackdrop &&
                !string.IsNullOrEmpty(scBackdrop.BackdropFilter))
            {
                using var backdropFilter = CssFilterParser.Parse(scBackdrop.BackdropFilter);
                if (backdropFilter != null)
                {
                    backend.ApplyBackdropFilter(scBackdrop.Bounds, backdropFilter);
                }
            }

            // Apply scroll offset
            bool pushedScroll = false;
            if (node is ScrollPaintNode scrollNode && (scrollNode.ScrollX != 0 || scrollNode.ScrollY != 0))
            {
                backend.PushTransform(SKMatrix.CreateTranslation(-scrollNode.ScrollX, -scrollNode.ScrollY));
                pushedScroll = true;
            }

            // Apply sticky offset
            bool pushedSticky = false;
            if (node is StickyPaintNode stickyNode && (stickyNode.StickyOffset.X != 0 || stickyNode.StickyOffset.Y != 0))
            {
                backend.PushTransform(SKMatrix.CreateTranslation(stickyNode.StickyOffset.X, stickyNode.StickyOffset.Y));
                pushedSticky = true;
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
            if (pushedFilter)
            {
                backend.PopFilter();
            }
            filterLayer?.Dispose();

            if (pushedMask)
            {
                 var maskNodePop = (MaskPaintNode)node;
                 // SAFETY: Wrap mask bitmap access with null and disposed checks
                 try
                 {
                     if (maskNodePop.MaskBitmap != null && !maskNodePop.MaskBitmap.IsNull)
                     {
                         // Apply mask using DstIn
                         using var maskImage = SKImage.FromBitmap(maskNodePop.MaskBitmap);
                         if (maskImage != null)
                         {
                             backend.ApplyMask(maskImage, maskNodePop.Bounds);
                         }
                     }
                 }
                 catch (ObjectDisposedException)
                 {
                     FenLogger.Debug($"[SkiaRenderer] Mask skipped: MaskBitmap was disposed");
                 }
                 catch (AccessViolationException)
                 {
                     FenLogger.Warn($"[SkiaRenderer] Mask skipped: AccessViolationException (native memory corruption)");
                 }
                 catch (Exception ex)
                 {
                     FenLogger.Error($"[SkiaRenderer] Mask failed: {ex.Message}");
                 }
                 // Pop isolation layer (always pop, even if mask failed)
                 backend.PopLayer();
            }

            if (pushedSticky) backend.PopLayer(); // Pop sticky transform
            if (pushedScroll) backend.PopLayer(); // Pop scroll transform
            if (pushedOpacity) backend.PopLayer();
            if (pushedClip) backend.PopClip();
            if (pushedTransform) backend.PopLayer(); // PopLayer restores the Save from PushTransform
            
            backend.Restore();
        }

        private void DrawHoverHighlight(IRenderBackend backend, PaintNodeBase node)
        {
            // Only draw hover for background nodes or top-level containers to avoid mess
            if (!(node is BackgroundPaintNode) && !(node is ImagePaintNode) && !(node is BorderPaintNode) && !(node is MaskPaintNode)) return;
            
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
            // Prevent duplicate focus rings on content nodes
            if (node is TextPaintNode) return;

            var ringColor = new SKColor(0, 120, 215, 200); // Windows-like blue
            float strokeWidth = 2.0f;
            
            var ringRect = node.Bounds;
            ringRect.Inflate(2, 2); // Slightly outside

            var borderStyle = BorderStyle.Uniform(strokeWidth, ringColor);
            
            SKPoint[] radius = null;
            if (node is BackgroundPaintNode bg) radius = bg.BorderRadius;
            else if (node is BorderPaintNode border) radius = border.BorderRadius;

            if (radius != null && HasNonZeroRadius(radius))
            {
                borderStyle.TopLeftRadius = new SKPoint(radius[0].X + 2, radius[0].Y + 2);
                borderStyle.TopRightRadius = new SKPoint((radius.Length > 1 ? radius[1].X : radius[0].X) + 2, (radius.Length > 1 ? radius[1].Y : radius[0].Y) + 2);
                borderStyle.BottomRightRadius = new SKPoint((radius.Length > 2 ? radius[2].X : radius[0].X) + 2, (radius.Length > 2 ? radius[2].Y : radius[0].Y) + 2);
                borderStyle.BottomLeftRadius = new SKPoint((radius.Length > 3 ? radius[3].X : (radius.Length > 1 ? radius[1].X : radius[0].X)) + 2, (radius.Length > 3 ? radius[3].Y : (radius.Length > 1 ? radius[1].Y : radius[0].Y)) + 2);
            }
            
            backend.DrawBorder(ringRect, borderStyle);
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

        private static bool IntersectsViewportBounds(SKRect bounds, SKRect viewport)
        {
            return bounds.Right > viewport.Left &&
                   bounds.Left < viewport.Right &&
                   bounds.Bottom > viewport.Top &&
                   bounds.Top < viewport.Bottom;
        }

        private static bool ShouldCullByOwnBounds(PaintNodeBase node)
        {
            return node is not ClipPaintNode &&
                   node is not StackingContextPaintNode &&
                   node is not OpacityGroupPaintNode &&
                   node is not ScrollPaintNode &&
                   node is not StickyPaintNode &&
                   node is not MaskPaintNode;
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
                    // handled in DrawNode wrapper
                    break;
                    
                case BoxShadowPaintNode shadow:
                    DrawBoxShadow(backend, shadow);
                    break;
                    
                case CustomPaintNode custom:
                    DrawCustom(backend, custom);
                    break;

                case StackingContextPaintNode _:
                case OpacityGroupPaintNode _:
                case ScrollPaintNode _:
                case StickyPaintNode _:
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
            if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0) return;
            
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

            if (node.Inset)
            {
                backend.DrawInsetBoxShadow(node.Bounds, node.BorderRadius, node.Offset.X, node.Offset.Y, node.Blur, node.Spread, node.Color);
                return;
            }

            // Outset shadow (existing logic)
            var drawRect = node.Bounds;
            drawRect.Offset(node.Offset.X, node.Offset.Y);

            if (node.Spread != 0)
            {
                drawRect.Inflate(node.Spread, node.Spread);
            }

            if (HasNonZeroRadius(node.BorderRadius))
            {
                using var path = CreateRoundedRectPath(drawRect, node.BorderRadius);
                backend.DrawShadow(path, 0, 0, node.Blur, node.Color);
            }
            else
            {
                backend.DrawBoxShadow(drawRect, 0, 0, node.Blur, 0, node.Color);
            }
        }

        private void DrawCustom(IRenderBackend backend, CustomPaintNode node)
        {
            if (node.PaintAction == null) return;

            backend.ExecuteCustomPaint(node.PaintAction, node.Bounds);
        }
        
        private void DrawBorder(IRenderBackend backend, BorderPaintNode node)
        {
            if (node.Widths == null || node.Colors == null) return;
            if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0) return;
            
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

            // [DEBUG-LOGGING]
            if (FenBrowser.Core.Logging.DebugConfig.LogPaintCommands)
            {
                string txt = !string.IsNullOrEmpty(node.FallbackText) ? node.FallbackText : (node.Glyphs?.Count > 0 ? $"[Glyphs:{node.Glyphs.Count}]" : "[Empty]");
                if (txt.Contains("Google") || txt.Contains("Wiki") || txt.Contains("Reddit"))
                     FenBrowser.Core.FenLogger.Debug($"[SKIA-RENDERER-DRAW] '{txt}' at {node.TextOrigin} Color={node.Color} Alpha={node.Color.Alpha} Glyphs={node.Glyphs?.Count ?? 0}");
            }

            bool isVertical = node.WritingMode == "vertical-rl";
            bool pushedVerticalTransform = false;

            if (isVertical)
            {
                backend.PushTransform(SKMatrix.CreateRotationDegrees(90, node.TextOrigin.X, node.TextOrigin.Y));
                pushedVerticalTransform = true;
            }
            
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
                    // DEBUG: Log text rendering
                    // Log if text contains "centered" (case insensitive) or "This text"
                    if (cleanText.IndexOf("centered", StringComparison.OrdinalIgnoreCase) >= 0 || 
                        cleanText.Contains("This text"))
                    {
                        FenBrowser.Core.FenLogger.Info($"[DRAW-TEXT-DEBUG] Drawing '{cleanText}' at Origin=({node.TextOrigin.X:F2}, {node.TextOrigin.Y:F2}) Color={node.Color}", FenBrowser.Core.Logging.LogCategory.Layout);
                    }
                    
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

            if (pushedVerticalTransform)
            {
                backend.PopLayer();
            }
        }
        
        // Removed DrawGlyphs (logic moved to DrawText/Backend)
        
        private void DrawTextDecorations(IRenderBackend backend, TextPaintNode node, float textWidth, float fontSize, SKColor color)
        {
            if (node.TextDecorations == null) return;
            
            var typeface = node.Typeface ?? SKTypeface.Default;
            using var paint = new SKPaint { Typeface = typeface, TextSize = fontSize };
            paint.GetFontMetrics(out var metrics);

            float strokeWidth = metrics.UnderlineThickness ?? System.Math.Max(1, fontSize / 16);
            
            float x = node.TextOrigin.X;
            float y = node.TextOrigin.Y;
            
            foreach (var decoration in node.TextDecorations)
            {
                switch (decoration.ToLowerInvariant())
                {
                    case "underline":
                        // metrics.UnderlinePosition is often positive (below baseline) in Skia
                        float underlineY = y + (metrics.UnderlinePosition ?? fontSize * 0.15f);
                        backend.DrawRect(new SKRect(x, underlineY, x + textWidth, underlineY + strokeWidth), color);
                        break;
                        
                    case "line-through":
                        float strikeY = y + (metrics.StrikeoutPosition ?? -fontSize * 0.3f);
                        float sWidth = metrics.StrikeoutThickness ?? strokeWidth;
                        backend.DrawRect(new SKRect(x, strikeY, x + textWidth, strikeY + sWidth), color);
                        break;
                        
                    case "overline":
                        // Overline is usually near the ascent
                        float overlineY = y + metrics.Ascent; 
                        backend.DrawRect(new SKRect(x, overlineY, x + textWidth, overlineY + strokeWidth), color);
                        break;
                }
            }
        }
        
        private void DrawImage(IRenderBackend backend, ImagePaintNode node)
        {
            // SAFETY: Catch AccessViolationException and ObjectDisposedException from disposed/corrupted bitmaps
            // This can happen when:
            // 1. Image cache eviction removes a bitmap while rendering is in progress
            // 2. GC collects a bitmap that's still referenced by a paint node
            // 3. A bitmap was corrupted during async loading
            try
            {
                if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0)
                {
                    return;
                }

                FenLogger.Debug($"[SkiaRenderer] DrawImage called. Bounds={node.Bounds}, Bitmap={(node.Bitmap != null ? $"{node.Bitmap.Width}x{node.Bitmap.Height}" : "NULL")}");
                
                // Guard: Check bitmap validity before accessing
                if (node.Bitmap == null) return;
                
                // Additional null-safety: Check if bitmap has been disposed
                // SKBitmap.IsNull indicates if the native handle is valid
                if (node.Bitmap.IsNull)
                {
                    FenLogger.Debug($"[SkiaRenderer] DrawImage skipped: Bitmap is disposed/null");
                    return;
                }
                
                // Validate bitmap dimensions to prevent divide-by-zero or invalid geometry
                if (node.Bitmap.Width <= 0 || node.Bitmap.Height <= 0)
                {
                    FenLogger.Debug($"[SkiaRenderer] DrawImage skipped: Invalid bitmap dimensions");
                    return;
                }
                
                SKRect srcRect = node.SourceRect ?? new SKRect(0, 0, node.Bitmap.Width, node.Bitmap.Height);
                SKRect destRect = CalculateDestRect(node.Bounds, srcRect, node.ObjectFit);
                
                FenLogger.Debug($"[SkiaRenderer] DrawImage destRect={destRect}");
                
                // CRITICAL: Wrap SKImage.FromBitmap in try-catch as this is where AccessViolationException
                // typically occurs when the underlying native bitmap memory is corrupted
                SKImage image = null;
                try
                {
                    image = SKImage.FromBitmap(node.Bitmap);
                    if (image != null)
                    {
                        backend.DrawImage(image, destRect, srcRect);
                    }
                }
                finally
                {
                    image?.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
                // Bitmap was disposed between check and use - safe to skip
                FenLogger.Debug($"[SkiaRenderer] DrawImage skipped: Bitmap was disposed");
            }
            catch (AccessViolationException)
            {
                // Native memory was corrupted or freed - can't recover, just skip this image
                FenLogger.Warn($"[SkiaRenderer] DrawImage skipped: AccessViolationException (native memory corruption)");
            }
            catch (Exception ex)
            {
                // Catch any other unexpected errors to prevent renderer crash
                FenLogger.Error($"[SkiaRenderer] DrawImage failed: {ex.Message}");
            }
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
        
        private static bool HasNonZeroRadius(SKPoint[] radius)
        {
            if (radius == null || radius.Length < 4) return false;
            return radius[0].X > 0 || radius[0].Y > 0 || 
                   radius[1].X > 0 || radius[1].Y > 0 || 
                   radius[2].X > 0 || radius[2].Y > 0 || 
                   radius[3].X > 0 || radius[3].Y > 0;
        }
        
        private static SKPath CreateRoundedRectPath(SKRect bounds, SKPoint[] radius)
        {
            var path = new SKPath();
            var rrect = new SKRoundRect();
            rrect.SetRectRadii(bounds, radius);
            path.AddRoundRect(rrect);
            return path;
        }

        private static bool TryIntersect(SKRect a, SKRect b, out SKRect intersection)
        {
            var left = Math.Max(a.Left, b.Left);
            var top = Math.Max(a.Top, b.Top);
            var right = Math.Min(a.Right, b.Right);
            var bottom = Math.Min(a.Bottom, b.Bottom);

            if (right <= left || bottom <= top)
            {
                intersection = SKRect.Empty;
                return false;
            }

            intersection = new SKRect(left, top, right, bottom);
            return true;
        }
    }
}

