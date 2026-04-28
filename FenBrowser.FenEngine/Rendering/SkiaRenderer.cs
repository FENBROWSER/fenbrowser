using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Backends;
using FenBrowser.FenEngine.Rendering.Css;
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;

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
        private static readonly TimeSpan DebugScreenshotMinimumInterval = TimeSpan.FromMilliseconds(500);
        
        private sealed class RenderPassStats
        {
            public int VisitedNodes;
            public int InvalidBoundsSkipped;
            public int ViewportCulled;
            public int DrawErrors;
        }

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
            var stats = new RenderPassStats();
            var hoverPaintedSources = new HashSet<Node>(ReferenceEqualityComparer.Instance);
             
            backend.Clear(SKColors.White);
             
            foreach (var root in tree.Roots)
            {
                DrawNodeSafe(backend, root, viewport, stats, hoverPaintedSources);
            }

            LogRenderSummary(viewport, tree.NodeCount, stats, "RenderBackend");
        }
        
        /// <summary>
        /// Renders a paint tree with a custom background color.
        /// </summary>
        public void Render(SKCanvas canvas, ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
            if (canvas == null || tree == null) return;
            var stats = new RenderPassStats();
            var hoverPaintedSources = new HashSet<Node>(ReferenceEqualityComparer.Instance);
             
            // Wrap canvas in adapter (Rule 4)
            var backend = new SkiaRenderBackend(canvas);
            
            // Perform screenshot if needed (legacy debug logic)
            // Ideally this should be moved out, but keeping for continuity
            CaptureDebugScreenshot(tree, viewport, backgroundColor);
            
            // Set background color manually since we bypassed the default Clear via delegation
            backend.Clear(backgroundColor);
            
            foreach (var root in tree.Roots)
            {
                EngineLogCompat.Debug($"[SkiaRenderer] Drawing Root Node {root.GetType().Name} Bounds={root.Bounds} Z={((root as OpacityGroupPaintNode)?.Opacity ?? 1)}");
                DrawNodeSafe(backend, root, viewport, stats, hoverPaintedSources);
            }

            LogRenderSummary(viewport, tree.NodeCount, stats, "RenderCanvas");
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
            var stats = new RenderPassStats();
            var hoverPaintedSources = new HashSet<Node>(ReferenceEqualityComparer.Instance);
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
                    DrawNodeSafe(backend, root, viewport, stats, hoverPaintedSources);
                }

                canvas.Restore();
            }

            LogRenderSummary(viewport, tree.NodeCount, stats, "RenderDamaged");
        }
        
        private void CaptureDebugScreenshot(ImmutablePaintTree tree, SKRect viewport, SKColor backgroundColor)
        {
             try 
            {
                if (viewport.Width <= 0 || viewport.Height <= 0)
                {
                    EngineLogCompat.Debug($"[SkiaRenderer] Skipping debug_screenshot.png capture due to invalid viewport {viewport.Width}x{viewport.Height}", LogCategory.Rendering);
                    return;
                }

                var screenshotPath = DiagnosticPaths.GetRootArtifactPath("debug_screenshot.png");
                if (!ShouldCaptureDebugScreenshot(screenshotPath, tree.NodeCount, viewport))
                {
                    return;
                }

                using (var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height)))
                {
                    if (surface == null)
                    {
                        EngineLogCompat.Warn("[SkiaRenderer] Failed to allocate debug screenshot surface", LogCategory.Rendering);
                        return;
                    }

                    var offCanvas = surface.Canvas;
                    // Capture the same viewport-relative pixels the host presents, not raw
                    // document-space coordinates. This keeps debug_screenshot.png aligned
                    // with scroll/fragment navigations such as Acid2 #top.
                    offCanvas.Clear(backgroundColor);
                    offCanvas.Save();
                    offCanvas.Translate(-viewport.Left, -viewport.Top);
                    var backend = new SkiaRenderBackend(offCanvas);
                    var stats = new RenderPassStats();
                    var hoverPaintedSources = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                    foreach (var root in tree.Roots)
                    {
                        DrawNodeSafe(backend, root, viewport, stats, hoverPaintedSources);
                    }
                    offCanvas.Restore();

                    using (var image = surface.Snapshot())
                    {
                        if (image == null)
                        {
                            EngineLogCompat.Warn($"[SkiaRenderer] Snapshot returned null while writing {screenshotPath}", LogCategory.Rendering);
                            return;
                        }

                        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                        {
                            if (data == null)
                            {
                                EngineLogCompat.Warn($"[SkiaRenderer] PNG encode returned null while writing {screenshotPath}", LogCategory.Rendering);
                                return;
                            }

                            using (var stream = File.Open(screenshotPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                            {
                                data.SaveTo(stream);
                                WriteDebugScreenshotMetadata(screenshotPath, tree.NodeCount, viewport);
                                EngineLogCompat.Debug($"[SkiaRenderer] Wrote debug_screenshot.png to {screenshotPath} ({viewport.Width}x{viewport.Height}, NodeCount={tree.NodeCount})", LogCategory.Rendering);
                                FenBrowser.Core.Verification.ContentVerifier.RegisterScreenshot(screenshotPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[SkiaRenderer] RenderToBitmap failed: {ex.Message}", LogCategory.Rendering); }
        }

        private static bool ShouldCaptureDebugScreenshot(string screenshotPath, int nodeCount, SKRect viewport)
        {
            var metadataPath = screenshotPath + ".meta";

            try
            {
                if (!File.Exists(screenshotPath))
                {
                    return true;
                }

                if (TryReadDebugScreenshotMetadata(metadataPath, out var lastCapturedUtc, out var lastNodeCount, out var lastViewport))
                {
                    if (lastNodeCount != nodeCount)
                    {
                        return true;
                    }

                    if (Math.Abs(lastViewport.Left - viewport.Left) > 0.5f ||
                        Math.Abs(lastViewport.Top - viewport.Top) > 0.5f ||
                        Math.Abs(lastViewport.Width - viewport.Width) > 0.5f ||
                        Math.Abs(lastViewport.Height - viewport.Height) > 0.5f)
                    {
                        return true;
                    }

                    return DateTime.UtcNow - lastCapturedUtc >= DebugScreenshotMinimumInterval;
                }

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(screenshotPath);
                return age >= DebugScreenshotMinimumInterval;
            }
            catch
            {
                return true;
            }
        }

        private static void WriteDebugScreenshotMetadata(string screenshotPath, int nodeCount, SKRect viewport)
        {
            try
            {
                File.WriteAllText(
                    screenshotPath + ".meta",
                    string.Join(
                        "|",
                        DateTime.UtcNow.Ticks,
                        nodeCount,
                        viewport.Left,
                        viewport.Top,
                        viewport.Width,
                        viewport.Height));
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static bool TryReadDebugScreenshotMetadata(string metadataPath, out DateTime capturedUtc, out int nodeCount, out SKRect viewport)
        {
            capturedUtc = default;
            nodeCount = 0;
            viewport = SKRect.Empty;

            try
            {
                if (!File.Exists(metadataPath))
                {
                    return false;
                }

                var parts = File.ReadAllText(metadataPath)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length == 2 &&
                    long.TryParse(parts[0], out var legacyTicks) &&
                    int.TryParse(parts[1], out nodeCount))
                {
                    capturedUtc = new DateTime(legacyTicks, DateTimeKind.Utc);
                    viewport = SKRect.Empty;
                    return true;
                }

                if (parts.Length != 6 ||
                    !long.TryParse(parts[0], out var ticks) ||
                    !int.TryParse(parts[1], out nodeCount) ||
                    !float.TryParse(parts[2], out var left) ||
                    !float.TryParse(parts[3], out var top) ||
                    !float.TryParse(parts[4], out var width) ||
                    !float.TryParse(parts[5], out var height))
                {
                    return false;
                }

                capturedUtc = new DateTime(ticks, DateTimeKind.Utc);
                viewport = new SKRect(left, top, left + width, top + height);
                return true;
            }
            catch
            {
                capturedUtc = default;
                nodeCount = 0;
                viewport = SKRect.Empty;
                return false;
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
        private void DrawNodeSafe(IRenderBackend backend, PaintNodeBase node, SKRect viewport, RenderPassStats stats, HashSet<Node> hoverPaintedSources)
        {
            int initialSaveDepth = backend.SaveDepth;

            try
            {
                DrawNode(backend, node, viewport, stats, hoverPaintedSources);
            }
            catch (Exception ex)
            {
                if (stats != null) stats.DrawErrors++;
                EngineLogCompat.Error($"[SkiaRenderer] DrawNode Failed for {node.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (backend.SaveDepth != initialSaveDepth)
                {
                    EngineLogCompat.Warn(
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
        private void DrawNode(IRenderBackend backend, PaintNodeBase node, SKRect viewport, RenderPassStats stats, HashSet<Node> hoverPaintedSources)
        {
            if (node == null) return;
            if (stats != null) stats.VisitedNodes++;
             
            // INVARIANT: Skip invalid geometry, don't crash
            if (!IsValidBounds(node.Bounds))
            {
                if (stats != null) stats.InvalidBoundsSkipped++;
                return;
            }
            
            // Cull leaf/visual nodes outside the viewport.
            // Grouping nodes can legitimately carry approximate bounds while their children
            // remain visible after transforms, sticky offsets, or stacking-context wrapping.
            if (ShouldCullByOwnBounds(node) &&
                node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
                !IntersectsViewportBounds(node.Bounds, viewport))
            {
                if (stats != null) stats.ViewportCulled++;
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
                // EngineLogCompat.Debug($"[SkiaRenderer] Pushing Path Clip for {node.GetType().Name}");
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
            if (node.IsHovered && ShouldDrawHoverHighlight(node, hoverPaintedSources)) DrawHoverHighlight(backend, node);
            if (node.IsFocused && ShouldDrawGenericFocusRing(node)) DrawFocusRing(backend, node);
            
            // Draw children in order (recursive traversal)
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    DrawNodeSafe(backend, child, viewport, stats, hoverPaintedSources);
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
                     EngineLogCompat.Debug($"[SkiaRenderer] Mask skipped: MaskBitmap was disposed");
                 }
                 catch (AccessViolationException)
                 {
                     EngineLogCompat.Warn($"[SkiaRenderer] Mask skipped: AccessViolationException (native memory corruption)");
                 }
                 catch (Exception ex)
                 {
                     EngineLogCompat.Error($"[SkiaRenderer] Mask failed: {ex.Message}");
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

            SKPoint[] radius = null;
            if (node is BackgroundPaintNode bg)
            {
                radius = bg.BorderRadius;
            }
            else if (node is BorderPaintNode border)
            {
                radius = border.BorderRadius;
            }

            if (radius != null && HasNonZeroRadius(radius))
            {
                using var path = CreateRoundedRectPath(node.Bounds, radius);
                backend.DrawPath(path, paintColor);
            }
            else
            {
                backend.DrawRect(node.Bounds, paintColor);
            }
        }

        private static bool ShouldDrawHoverHighlight(PaintNodeBase node, HashSet<Node> hoverPaintedSources)
        {
            if (!(node is BackgroundPaintNode) && !(node is ImagePaintNode) && !(node is BorderPaintNode) && !(node is MaskPaintNode))
            {
                return false;
            }

            if (node.SourceNode is not Node source)
            {
                return true;
            }

            if (hoverPaintedSources.Contains(source))
            {
                return false;
            }

            hoverPaintedSources.Add(source);
            return true;
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

        private static bool ShouldDrawGenericFocusRing(PaintNodeBase node)
        {
            if (node is TextPaintNode)
            {
                return false;
            }

            if (node.SourceNode is Element element)
            {
                var tag = element.TagName?.ToLowerInvariant();
                if (tag is "input" or "textarea" or "select")
                {
                    return false;
                }

                if (element.Attr?.TryGetValue("contenteditable", out var editable) == true &&
                    !string.Equals(editable, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
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

            if (node.SourceNode is Element bgElement && ShouldTraceAcid2FaceElement(bgElement))
            {
                DiagnosticPaths.AppendRootText(
                    "debug_paint_start.txt",
                    $"[DRAW-BG] <{bgElement.TagName}> id='{bgElement.Id}' class='{bgElement.GetAttribute("class")}' bounds={node.Bounds}\n");
            }
            
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

            if (node.SourceNode is Element borderElement && ShouldTraceAcid2FaceElement(borderElement))
            {
                DiagnosticPaths.AppendRootText(
                    "debug_paint_start.txt",
                    $"[DRAW-BORDER] <{borderElement.TagName}> id='{borderElement.Id}' class='{borderElement.GetAttribute("class")}' bounds={node.Bounds} widths={string.Join("/", node.Widths)}\n");
            }
            
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
                LeftColor = node.Colors.Length > 3 ? node.Colors[3] : (node.Colors.Length > 1 ? node.Colors[1] : node.Colors[0]),
                TopStyle = node.Styles != null && node.Styles.Length > 0 ? node.Styles[0] : "solid",
                RightStyle = node.Styles != null && node.Styles.Length > 1 ? node.Styles[1] : "solid",
                BottomStyle = node.Styles != null && node.Styles.Length > 2 ? node.Styles[2] : "solid",
                LeftStyle = node.Styles != null && node.Styles.Length > 3 ? node.Styles[3] : "solid"
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
                     FenBrowser.Core.EngineLogCompat.Debug($"[SKIA-RENDERER-DRAW] '{txt}' at {node.TextOrigin} Color={node.Color} Alpha={node.Color.Alpha} Glyphs={node.Glyphs?.Count ?? 0}");
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
            string cleanText = string.IsNullOrEmpty(node.FallbackText)
                ? string.Empty
                : node.FallbackText.Replace("\r", "").Replace("\n", "").Replace("\t", " ");
            
            float textWidth = 0;

            // Prefer direct text drawing when we still have the source string.
            // Our current glyph-run path is a lightweight glyph-id transport, not a full shaping/fallback engine,
            // so preserving the source text at the render backend is more reliable for browser content.
            if (!string.IsNullOrWhiteSpace(cleanText))
            {
                // DEBUG: Log text rendering
                if (cleanText.IndexOf("centered", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cleanText.Contains("This text"))
                {
                    FenBrowser.Core.EngineLogCompat.Info($"[DRAW-TEXT-DEBUG] Drawing '{cleanText}' at Origin=({node.TextOrigin.X:F2}, {node.TextOrigin.Y:F2}) Color={node.Color}", FenBrowser.Core.Logging.LogCategory.Layout);
                }

                backend.DrawText(cleanText, node.TextOrigin, node.Color, fontSize, typeface);
                textWidth = MeasureRenderedTextWidth(cleanText, fontSize, typeface);
            }
            else if (node.Glyphs != null && node.Glyphs.Count > 0)
            {
                var glyphArray = System.Linq.Enumerable.ToArray(node.Glyphs.Select(g => new FenBrowser.FenEngine.Typography.PositionedGlyph
                {
                    GlyphId = g.GlyphId,
                    X = g.X,
                    Y = g.Y,
                    AdvanceX = 0
                }));

                var run = new FenBrowser.FenEngine.Typography.GlyphRun
                {
                    Typeface = typeface,
                    FontSize = fontSize,
                    Glyphs = glyphArray
                };

                if (glyphArray.Length > 0)
                {
                    float minX = float.MaxValue;
                    float maxX = float.MinValue;
                    foreach (var g in glyphArray)
                    {
                        if (g.X < minX) minX = g.X;
                        if (g.X > maxX) maxX = g.X;
                    }

                    textWidth = maxX - minX + fontSize * 0.6f;
                }

                backend.DrawGlyphRun(SKPoint.Empty, run, node.Color);
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

        internal static float MeasureRenderedTextWidth(string text, float fontSize, SKTypeface typeface)
        {
            if (string.IsNullOrEmpty(text) || fontSize <= 0)
            {
                return 0;
            }

            using var paint = new SKPaint
            {
                Typeface = typeface ?? SKTypeface.Default,
                TextSize = fontSize,
                IsAntialias = true
            };

            return paint.MeasureText(text);
        }
        
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
                
                // Guard: Check bitmap validity before accessing
                if (node.Bitmap == null) return;
                
                // Additional null-safety: Check if bitmap has been disposed
                // SKBitmap.IsNull indicates if the native handle is valid
                if (node.Bitmap.IsNull)
                {
                    EngineLogCompat.Debug($"[SkiaRenderer] DrawImage skipped: Bitmap is disposed/null");
                    return;
                }

                EngineLogCompat.Debug($"[SkiaRenderer] DrawImage called. Bounds={node.Bounds}, Bitmap=present");
                
                // Validate bitmap dimensions to prevent divide-by-zero or invalid geometry
                if (node.Bitmap.Width <= 0 || node.Bitmap.Height <= 0)
                {
                    EngineLogCompat.Debug($"[SkiaRenderer] DrawImage skipped: Invalid bitmap dimensions");
                    return;
                }

                if (node.IsBackgroundImage)
                {
                    DrawBackgroundImageNode(backend, node);
                    return;
                }
                
                SKRect srcRect = node.SourceRect ?? new SKRect(0, 0, node.Bitmap.Width, node.Bitmap.Height);
                SKRect destRect = CalculateDestRect(node.Bounds, srcRect, node.ObjectFit);
                
                EngineLogCompat.Debug($"[SkiaRenderer] DrawImage destRect={destRect}");
                
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
                EngineLogCompat.Debug($"[SkiaRenderer] DrawImage skipped: Bitmap was disposed");
            }
            catch (AccessViolationException)
            {
                // Native memory was corrupted or freed - can't recover, just skip this image
                EngineLogCompat.Warn($"[SkiaRenderer] DrawImage skipped: AccessViolationException (native memory corruption)");
            }
            catch (Exception ex)
            {
                // Catch any other unexpected errors to prevent renderer crash
                EngineLogCompat.Error($"[SkiaRenderer] DrawImage failed: {ex.Message}");
            }
        }

        private void DrawBackgroundImageNode(IRenderBackend backend, ImagePaintNode node)
        {
            float anchorX = node.BackgroundAttachmentFixed ? node.FixedViewportOrigin.X : node.BackgroundOrigin.X;
            float anchorY = node.BackgroundAttachmentFixed ? node.FixedViewportOrigin.Y : node.BackgroundOrigin.Y;

            var matrix = SKMatrix.CreateTranslation(
                anchorX + node.BackgroundPosition.X,
                anchorY + node.BackgroundPosition.Y);

            using var shader = SKShader.CreateBitmap(node.Bitmap, node.TileModeX, node.TileModeY, matrix);
            backend.DrawRect(node.Bounds, shader);
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

        private static bool ShouldTraceAcid2FaceElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            string id = element.Id ?? string.Empty;
            if (id is "eyes-b" or "eyes-c" or "smile-outer" or "smile-inner" or "smile-span" or "parser" or "tail")
            {
                return true;
            }

            string className = element.GetAttribute("class") ?? string.Empty;
            return className.Contains("smile", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("chin", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("parser", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("nose", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("empty", StringComparison.OrdinalIgnoreCase);
        }
        
        private static SKPath CreateRoundedRectPath(SKRect bounds, SKPoint[] radius)
        {
            // Clamp radii to geometry bounds to avoid pathological capsules/overdraw artifacts.
            var clamped = NormalizeCornerRadii(bounds, radius);
            var path = new SKPath();
            var rrect = new SKRoundRect();
            rrect.SetRectRadii(bounds, clamped);
            path.AddRoundRect(rrect);
            return path;
        }

        private static SKPoint[] NormalizeCornerRadii(SKRect bounds, SKPoint[] radius)
        {
            if (radius == null || radius.Length < 4)
            {
                return new[] { SKPoint.Empty, SKPoint.Empty, SKPoint.Empty, SKPoint.Empty };
            }

            float width = Math.Max(0f, bounds.Width);
            float height = Math.Max(0f, bounds.Height);
            if (width <= 0f || height <= 0f)
            {
                return new[] { SKPoint.Empty, SKPoint.Empty, SKPoint.Empty, SKPoint.Empty };
            }

            var normalized = new SKPoint[4];
            for (int i = 0; i < 4; i++)
            {
                float rx = Math.Max(0f, radius[i].X);
                float ry = Math.Max(0f, radius[i].Y);
                normalized[i] = new SKPoint(Math.Min(rx, width * 0.5f), Math.Min(ry, height * 0.5f));
            }

            return normalized;
        }

        private static void LogRenderSummary(SKRect viewport, int nodeCount, RenderPassStats stats, string phase)
        {
            if (stats == null) return;

            EngineLog.Write(
                LogSubsystem.Paint,
                LogSeverity.Info,
                "Renderer pass complete",
                LogMarker.None,
                default,
                new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["nodeCount"] = nodeCount,
                    ["visitedNodes"] = stats.VisitedNodes,
                    ["invalidSkipped"] = stats.InvalidBoundsSkipped,
                    ["culledByViewport"] = stats.ViewportCulled,
                    ["drawErrors"] = stats.DrawErrors,
                    ["viewportWidth"] = viewport.Width,
                    ["viewportHeight"] = viewport.Height
                });
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

