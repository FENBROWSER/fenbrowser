using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Adapter class that bridges the old SkiaDomRenderer API to the new clean-slate rendering pipeline.
    /// 
    /// This maintains backward compatibility with existing code in Host, UI, and Tests
    /// while internally delegating to LayoutEngine, NewPaintTreeBuilder, and SkiaRenderer.
    /// 
    /// NOTE: This is a transitional class - once all consumers are updated to use
    /// the new pipeline directly, this class should be removed.
    /// </summary>
    public class SkiaDomRenderer
    {
        /// <summary>
        /// Feature flag: set to false to bypass new pipeline (for debugging).
        /// Default: true (new pipeline is active).
        /// </summary>
        public static bool UseNewRenderPipeline { get; set; } = true;
        
        private readonly SkiaRenderer _renderer = new SkiaRenderer();
        private readonly Dictionary<Node, BoxModel> _boxes = new Dictionary<Node, BoxModel>();
        private readonly Interaction.ScrollManager _scrollManager = new Interaction.ScrollManager();

        private IReadOnlyDictionary<Node, CssComputed> _lastStyles;
        private LayoutResult _lastLayout;
        private ImmutablePaintTree _lastPaintTree;
        private float _viewportWidth;
        private float _viewportHeight;
        private float _lastViewportWidth;
        private float _lastViewportHeight;
        private Node _lastRoot;
        
        /// <summary>
        /// Current overlays for input elements.
        /// </summary>
        public List<InputOverlayData> CurrentOverlays { get; } = new List<InputOverlayData>();
        
        /// <summary>
        /// Access to scroll manager.
        /// </summary>
        public Interaction.ScrollManager ScrollManager => _scrollManager;
        

        
        /// <summary>
        /// Last layout result.
        /// </summary>
        public LayoutResult LastLayout => _lastLayout;
        
        /// <summary>
        /// Get the layout box for a specific element.
        /// </summary>
        public BoxModel GetElementBox(Node node)
        {
            if (node != null && _boxes.TryGetValue(node, out var box))
                return box;
            return null;
        }
        
        /// <summary>
        /// Main render entry point - performs layout and paint.
        /// </summary>
        private bool _isRendering = false;

        public void Render(
            Node root, 
            SKCanvas canvas, 
            Dictionary<Node, CssComputed> styles, 
            SKRect viewport, 
            string baseUrl = null, 
            Action<SKSize, List<InputOverlayData>> onLayoutUpdated = null, 
            SKSize? separateLayoutViewport = null)
        {
            if (root == null || canvas == null) return;
            
            // Re-entrancy Guard
            if (_isRendering)
            {
                // FenLogger.Warn("Skipping re-entrant Render call.");
                return;
            }
            
            _isRendering = true;
            try
            {
            CurrentOverlays.Clear();
            _lastStyles = styles;

            // [Verification] Capture content metrics for the verification report
            if (root is Document doc)
            {
                doc.DumpTree();
            }
            
            // Capture state from root downwards using the centralized helper
            FenBrowser.Core.Verification.ContentVerifier.RegisterRenderedFromNode(root, baseUrl ?? "about:blank");


            
            float layoutWidth = separateLayoutViewport?.Width ?? viewport.Width;
            float layoutHeight = separateLayoutViewport?.Height ?? viewport.Height;
            
            _viewportWidth = layoutWidth > 0 ? layoutWidth : 1920;
            _viewportHeight = layoutHeight > 0 ? layoutHeight : 1080;
            
            // CRITICAL: Update global CSS parser context for viewport units (vh/vw) mechanism
            CssParser.MediaViewportWidth = _viewportWidth;
            CssParser.MediaViewportHeight = _viewportHeight;

            // Check if resize occurred or if root node changed (new page navigation)
            bool forceLayout = _lastLayout == null || 
                               root != _lastRoot ||
                               Math.Abs(_viewportWidth - _lastViewportWidth) > 0.1f || 
                               Math.Abs(_viewportHeight - _lastViewportHeight) > 0.1f;
            
            _lastViewportWidth = _viewportWidth;
            _lastViewportHeight = _viewportHeight;
            _lastRoot = root;
            

            
            try
            {
                // Track dirty state
                bool isLayoutDirty = forceLayout || (root.LayoutDirty || root.ChildLayoutDirty);
                bool hasActiveAnimations = false;

                // PHASE 0: Update Animations
                // Integrate CSS Animation Engine
                if (root is Element && styles != null)
                {
                    // We need to iterate over a copy of keys because we might modify the dictionary
                    var elements = new List<Element>();
                    foreach(var k in styles.Keys) if(k is Element e) elements.Add(e);
                    
                    foreach(var elem in elements)
                    {
                        if(!styles.TryGetValue(elem, out var style)) continue;
                        
                        // Check for transitions/animations start
                        CssAnimationEngine.Instance.CheckTransitions(elem, style);
                        CssAnimationEngine.Instance.StartAnimation(elem, style);
                        
                        // Get current animated values
                        var animatedProps = CssAnimationEngine.Instance.GetAnimatedProperties(elem);
                        var transitionProps = CssAnimationEngine.Instance.GetTransitionedProperties(elem);
                        
                        if (animatedProps.Count > 0 || transitionProps.Count > 0)
                        {
                            hasActiveAnimations = true; // Animations active -> assume layout might change

                            // Create a clone for this frame to avoid persisting animated values into the base style
                            // (which would break future transitions as 'oldValue' would become the animated value)
                            var frameStyle = style.Clone();
                            
                            // Apply transitions
                            foreach(var kvp in transitionProps)
                                FenBrowser.FenEngine.Rendering.Css.CssStyleApplicator.ApplyProperty(frameStyle, kvp.Key, kvp.Value);
                                
                            // Apply animations (override transitions)
                            foreach(var kvp in animatedProps)
                                FenBrowser.FenEngine.Rendering.Css.CssStyleApplicator.ApplyProperty(frameStyle, kvp.Key, kvp.Value);
                                
                            // Replace style for this render pass
                            styles[elem] = frameStyle;
                        }
                    }
                }

                if (hasActiveAnimations) isLayoutDirty = true;

                // PHASE 1: Layout using the new LayoutEngine
                RenderPipeline.EnterLayout();
                if (isLayoutDirty)
                {
                    var layoutEngine = new LayoutEngine(
                        styles ?? new Dictionary<Node, CssComputed>(),
                        _viewportWidth,
                        _viewportHeight,
                        null,
                        baseUrl);
                    
                    _lastLayout = layoutEngine.ComputeLayout(root, _viewportWidth, 
                        _viewportHeight);
                        

                        



                    
                    // Copy boxes for hit testing
                    _boxes.Clear();
                    int boxCount = 0;
                    foreach (var box in layoutEngine.AllBoxes)
                    {
                        _boxes[box.Key] = box.Value;
                        boxCount++;
                    }
                    
                    // [L-04] VALIDATION GATE (Optimized)
                    if (FenBrowser.Core.Logging.DebugConfig.LogLayoutConstraints && _boxes.ContainsKey(root))
                    {
                        var rootBox = _boxes[root];
                        bool almostFullCheck = Math.Abs(rootBox.PaddingBox.Height - _viewportHeight) < 1.0f;
                        // FenLogger.Debug($"[L-04 CHECK-LATE] RootHeight={rootBox.PaddingBox.Height} Viewport={_viewportHeight} Match={almostFullCheck}");
                    }

                    // Debug: Dump DOM tree with Boxes
                    if (FenBrowser.Core.Logging.DebugConfig.LogDomTree)
                    {
                        try { 
                            var sb = new System.Text.StringBuilder();
                            DumpDom(root, 0, sb, styles, _boxes);
                            FenLogger.Debug($"[SkiaDomRenderer] DOM Dump: {sb}", LogCategory.Rendering);
                        } catch {}
                    }
                    
                    FenLogger.Debug($"[SkiaDomRenderer] Copied {boxCount} boxes for rendering.", LogCategory.Rendering);
                    
                    // Clear Layout Dirty Flags
                    RecursivelyClearDirty(root, InvalidationKind.Layout);
                }
                else
                {
                    // FenLogger.Debug("[SkiaDomRenderer] Skipping Layout (Clean/Cached)");

                }
                RenderPipeline.EndLayout(); // State -> LayoutFrozen
                
                // Update Scroll Animations
                if (_scrollManager.OnFrame())
                {
                    // Scroll changed -> Paint Dirty
                    // (We don't strict-track scroll dirty yet, so relying on this or just repainting)
                }

                // PHASE 2: Build Paint Tree
                RenderPipeline.EnterPaint(); // Checks LayoutFrozen
                bool isPaintDirty = isLayoutDirty || root.PaintDirty || root.ChildPaintDirty || _lastPaintTree == null;
                // If scroll changed, we usually repaint. But ScrollManager handles offsets in PaintTreeBuilder.
                // If we skip build, we use old PaintTree with old scroll offsets? 
                // Currently NewPaintTreeBuilder reads scroll offsets.
                // So strictly, if Scroll changed, we MUST rebuild paint tree.
                // Assuming ScrollManager.OnFrame returning true means something changed.
                // But checking flag availability? 
                // Let's assume always build paint tree if animations/scroll active for safety, or implement dirty tracking there.
                // For now: Always repaint if layout changed OR dirty flags OR scroll active?
                // Let's stick to dirty flags + layout changed.
                
                if (isPaintDirty)
                {
                    FenLogger.Debug($"[SkiaDomRenderer] Invoke NewPaintTreeBuilder... Root={root.GetType().Name} BoxCount={_boxes.Count}");
                    var paintTree = NewPaintTreeBuilder.Build(
                        root,
                        _boxes,
                        styles,
                        _viewportWidth,
                        _viewportHeight,
                        _scrollManager,
                        baseUrl);
                    _lastPaintTree = paintTree;


                    
                    // Clear Paint Dirty Flags
                    RecursivelyClearDirty(root, InvalidationKind.Paint);
                }
                else
                {
                     // FenLogger.Debug("[SkiaDomRenderer] Skipping Paint Tree Build (Clean)");
                }


                
                // PHASE 3: Render
                SKColor bgColor = SKColors.White;
                
                try 
                {
                    string log = $"[Render] Root={root?.GetType().Name}, StylesNull={styles==null}, Count={styles?.Count}\n";
                    if (root is Element rootEl && styles != null)
                    {
                        if (styles.TryGetValue(rootEl, out var rootStyle) && rootStyle != null)
                        {
                            log += $"HTML Style Found. BG={rootStyle.BackgroundColor}\n";
                            if (rootStyle.Map.ContainsKey("background")) log += $"  Map[background] = '{rootStyle.Map["background"]}'\n";
                            if (rootStyle.Map.ContainsKey("background-color")) log += $"  Map[background-color] = '{rootStyle.Map["background-color"]}'\n";
                            log += $"  Tag={rootEl.Tag}\n";

                            if (rootStyle.BackgroundColor.HasValue && rootStyle.BackgroundColor.Value.Alpha > 0)
                            {
                                bgColor = rootStyle.BackgroundColor.Value;
                                log += $"-> Using HTML BG: {bgColor}\n";
                            }
                        }
                        else { log += "HTML Style NOT Found.\n"; }

                        if (bgColor == SKColors.White)
                        {
                            var body = rootEl.Children?.FirstOrDefault(c => c is Element e && e.TagName?.ToLowerInvariant() == "body") as Element;
                            log += $"Body Element={body!=null}\n";
                            if (body != null)
                            {
                                if (styles.TryGetValue(body, out var bodyStyle) && bodyStyle != null)
                                {
                                    log += $"BODY Style Found. BG={bodyStyle.BackgroundColor}\n";
                                    if (bodyStyle.BackgroundColor.HasValue && bodyStyle.BackgroundColor.Value.Alpha > 0)
                                    {
                                        bgColor = bodyStyle.BackgroundColor.Value;
                                        log += $"-> Using BODY BG: {bgColor}\n";
                                    }
                                }
                                else { log += "BODY Style NOT Found.\n"; }
                            }
                        }
                    }

                } 
                catch {}
                
                _renderer.Render(canvas, _lastPaintTree, viewport, bgColor);
                
                RenderPipeline.EndPaint(); // State -> Composite


                
                // Callback with layout info
                CurrentOverlays.Clear();
                CollectOverlays();
                float totalHeight = _lastLayout?.ContentHeight ?? _viewportHeight;
                onLayoutUpdated?.Invoke(new SKSize(_viewportWidth, totalHeight), CurrentOverlays);
                
                RenderPipeline.EndFrame(); // State -> Idle
            }
            catch (Exception ex)
            {
                RenderPipeline.Reset();
                // Log error but don't crash
                FenLogger.Error($"[SkiaDomRenderer] Render error: {ex}", LogCategory.Rendering);
                
                // CRITICAL FIX: Clear stale state to prevent "Ghost UI" where old page features remain interactive
                _lastPaintTree = null;
                _boxes.Clear();
                CurrentOverlays.Clear();
                
                canvas.Clear(SKColors.White);

                // Optional: Draw error message on screen for debug visibility
                using var paint = new SKPaint { Color = SKColors.Red, TextSize = 20 };
                canvas.DrawText($"Render Error: {ex.GetType().Name}", 20, 40, paint);
            }
            
            // [Verification] Finalize and log report
            FenBrowser.Core.Verification.ContentVerifier.PerformVerification();
            }
            finally
            {
                _isRendering = false;
            }
        }
        
        private void CollectOverlays()
        {
            if (_lastPaintTree?.Roots == null) return;
            
            var buffer = new List<PaintNodeBase>();
            CollectAllNodes(_lastPaintTree.Roots, buffer);
            
            var processed = new HashSet<Element>();
            
            foreach (var ptNode in buffer)
            {
                if (ptNode.SourceNode is Element el)
                {
                    var tag = el.Tag?.ToLowerInvariant();
                    if (tag == "input" || tag == "textarea")
                    {
                        if (processed.Contains(el)) continue;
                        processed.Add(el);
                        
                        // Skip invisible or zero-size
                        // Use Layout Box for definitive bounds
                        if (!_boxes.TryGetValue(el, out var box) || box == null) continue;
                        if (box.BorderBox.Width <= 0 || box.BorderBox.Height <= 0) continue;

                        CssComputed style = null;
                        if (_lastStyles != null) _lastStyles.TryGetValue(el, out style);
                        
                        if (style != null && string.Equals(style.Visibility, "hidden", StringComparison.OrdinalIgnoreCase)) continue;

                        string align = "left";
                        if (style?.TextAlign == SKTextAlign.Center) align = "center";
                        else if (style?.TextAlign == SKTextAlign.Right) align = "right";

                        var overlay = new InputOverlayData
                        {
                            Node = el,
                            Bounds = box.BorderBox, 
                            Type = el.GetAttribute("type") ?? "text",
                            InitialText = el.GetAttribute("value") ?? "",
                            Placeholder = el.GetAttribute("placeholder") ?? "",
                            
                            FontFamily = style?.FontFamilyName ?? "Segoe UI",
                            FontSize = (float)(style?.FontSize ?? 16.0),
                            TextColor = style?.ForegroundColor ?? SKColors.Black,
                            BackgroundColor = SKColors.Transparent, // Transparent so Skia background shows
                            TextAlign = align,
                            BorderThickness = new Thickness(0), // Disable native border
                            BorderRadius = style?.BorderRadius ?? new CssCornerRadius(0)
                        };
                        CurrentOverlays.Add(overlay);
                    }
                }
            }
        }

        /// <summary>
        /// Hit test at document coordinates.
        /// </summary>
        /// <summary>
        /// Hit test at document coordinates.
        /// </summary>
        public bool HitTest(float x, float y, out HitTestResult result)
        {
            result = HitTestResult.None;
            if (_lastPaintTree == null || _lastPaintTree.Roots == null) return false;

            // Use recursive hit testing to respect hierarchy, clips, and z-order
            return HitTestRecursive(_lastPaintTree.Roots, x, y, out result);
        }

        private bool HitTestRecursive(IReadOnlyList<PaintNodeBase> nodes, float x, float y, out HitTestResult result)
        {
            result = HitTestResult.None;
            if (nodes == null) return false;

            // Traverse in REVERSE paint order (Front-to-Back)
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var node = nodes[i];
                if (node == null) continue; // Skip nulls

                // 1. Apply Inverse Transform to get Point in Node's Local Space
                float localX = x;
                float localY = y;
                
                if (node.Transform.HasValue)
                {
                    if (!node.Transform.Value.TryInvert(out var inv)) continue; // Degenerate transform (scale=0), unclickable
                    var pt = inv.MapPoint(x, y);
                    localX = pt.X;
                    localY = pt.Y;
                }

                // 2. Check Clipping (Happens in Local Space, before Scroll)
                // Handle generic ClipRect (base property)
                if (node.ClipRect.HasValue)
                {
                    if (!node.ClipRect.Value.Contains(localX, localY)) continue;
                }
                // Handle specialized ClipPaintNode (path clipping)
                if (node is ClipPaintNode clipNode)
                {
                    bool inside = false;
                    if (clipNode.ClipPath != null) inside = clipNode.ClipPath.Contains(localX, localY);
                    else if (clipNode.ClipRect.HasValue) inside = clipNode.ClipRect.Value.Contains(localX, localY); // Redundant if base ClipRect handled, but safe
                    else inside = clipNode.Bounds.Contains(localX, localY); // Fallback to bounds

                    if (!inside) continue;
                }

                // 3. Apply Inverse Scroll / Sticky (Transform Content Space)
                float contentX = localX;
                float contentY = localY;

                if (node is ScrollPaintNode scrollNode)
                {
                    // Scroll shifts content UP (-y), so to hit content at 'y', we must look at 'y + scrollY'
                    contentX += scrollNode.ScrollX;
                    contentY += scrollNode.ScrollY;
                }

                if (node is StickyPaintNode stickyNode)
                {
                    // Sticky shifts content down (+y). Inverse is -y.
                    // Wait, Render: Translate(X, Y). HitTest: Translate(-X, -Y).
                    contentX -= stickyNode.StickyOffset.X;
                    contentY -= stickyNode.StickyOffset.Y;
                }

                // 4. Check Children FIRST (Front-most) using Content Coordinates
                if (node.Children != null && node.Children.Count > 0)
                {
                    if (HitTestRecursive(node.Children, contentX, contentY, out result)) return true;
                }

                // 5. Check Self (Background/Border) using Content Coordinates
                // Note: DrawSelf is drawn AFTER scroll transform in SkiaRenderer.
                // So checking bounds against contentX/Y is correct.
                
                // Optimization: Don't hit wrappers
                if (node is StackingContextPaintNode || node is OpacityGroupPaintNode || node is StickyPaintNode || node is ScrollPaintNode)
                {
                    // These usually don't paint "Self" content, just manage children.
                    // Unless they have a background? 
                    // PaintNodeBase doesn't strict enforce "No Paint". 
                    // But typically they are just wrappers. 
                    // We'll rely on SourceNode check.
                }

                // Check intersection with node bounds
                // Note: Node.Bounds are typically in LOCAL space (post-transform, post-scroll?).
                // In Layout, bounds are relative to parent content.
                // In Paint, SkiaRenderer draws 'node.Bounds' at (0,0) offset? 
                // SkiaRenderer 'DrawSelf' uses `node.Bounds`.
                // If Scroll was applied, `DrawSelf` draws at `node.Bounds` in the scrolled space.
                // So `contentX/Y` vs `node.Bounds` is the correct check.
                
                if (node.Bounds.Contains(contentX, contentY))
                {
                     var domNode = node.SourceNode;
                    var element = domNode as Element;
                    if (element == null && domNode?.Parent is Element parentEl) element = parentEl;

                    if (element != null)
                    {
                        if (_lastStyles != null && _lastStyles.TryGetValue(element, out var style))
                        {
                            if (style.PointerEvents == "none") continue;
                        }

                        // Found a hit! Resolve interactive ancestor.
                        var interactive = FindInteractiveAncestor(element);
                        string tagName = element.Tag;
                        string elementId = element.GetAttribute("id");
                        string href = null;

                        if (interactive != null)
                        {
                            if (string.Equals(interactive.Tag, "a", StringComparison.OrdinalIgnoreCase))
                            {
                                href = interactive.GetAttribute("href");
                            }
                            else if (string.Equals(interactive.Tag, "button", StringComparison.OrdinalIgnoreCase))
                            {
                                tagName = "button";
                                element = interactive;
                            }
                        }

                        string tagLow = tagName?.ToLowerInvariant();
                        bool isClickable = !string.IsNullOrEmpty(href) || tagLow == "button" || tagLow == "input" || tagLow == "label";
                        bool isFocusable = isClickable || tagLow == "textarea" || tagLow == "select";
                        bool isEditable = tagLow == "input" || tagLow == "textarea";

                        result = new HitTestResult(
                            TagName: tagLow ?? "",
                            Href: href,
                            Cursor: !string.IsNullOrEmpty(href) ? CursorType.Pointer : (isEditable ? CursorType.Text : CursorType.Default),
                            IsClickable: isClickable,
                            IsFocusable: isFocusable,
                            IsEditable: isEditable,
                            ElementId: elementId,
                            NativeElement: element,
                            BoundingBox: node.Bounds
                        );
                        return true;
                    }
                }
            }
            return false;
        }
        
        private Element FindInteractiveAncestor(Element element)
        {
            var current = element;
            while (current != null)
            {
                if (string.Equals(current.Tag, "a", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.Tag, "button", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.Parent as Element;
            }
            return null;
        }
        
        /// <summary>
        /// Handle hover state changes.
        /// </summary>
        public void OnHover(Element current, Element previous)
        {
            ElementStateManager.Instance.SetHoveredElement(current);
        }
        


        private static void DumpDom(Node root, int startDepth, System.Text.StringBuilder sb, Dictionary<Node, CssComputed> styles, IReadOnlyDictionary<Node, Layout.BoxModel> boxes = null)
        {
            if (root == null) return;
            
            var stack = new Stack<(Node node, int depth)>();
            stack.Push((root, startDepth));
            
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (node == null) continue;
                
                string tag = (node as Element)?.TagName ?? (node.IsText ? "#text" : node.NodeName);
                int childCount = node.Children?.Count ?? 0;
                sb.Append(new string(' ', depth * 2))
                  .Append($"[DOM DUMP] Type: {node.NodeType}, Tag: {tag}, Inst: {node.GetHashCode()}, Children: {childCount}");
                
                // Add Styles info
                if (styles != null && styles.TryGetValue(node, out var style))
                {
                    sb.Append($" [Display: {style.Display ?? "null"}, Vis: {style.Visibility ?? "null"}]");
                }
                
                // Add Box info
                if (boxes != null && boxes.TryGetValue(node, out var box) && box != null)
                {
                    sb.Append($" [Box: {box.ContentBox.Width:F1}x{box.ContentBox.Height:F1} @ {box.ContentBox.Left:F1},{box.ContentBox.Top:F1}]");
                }

                // Add Attributes info for Elements
                if (node is Element el && el.Attributes != null && el.Attributes.Count > 0)
                {
                    sb.Append(" {");
                    bool first = true;
                    foreach (var attr in el.Attributes)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append($"{attr.Key}='{attr.Value}'");
                        first = false;
                    }
                    sb.Append("}");
                }
                
                if (node is Text t && !string.IsNullOrWhiteSpace(t.Data))
                {
                    string snippet = t.Data.Length > 20 ? t.Data.Substring(0, 20) + "..." : t.Data;
                    sb.Append(" [").Append(snippet.Replace("\r", "").Replace("\n", " ")).Append("]");
                }
                sb.AppendLine();
                
                if (node.Children != null && node.Children.Count > 0)
                {
                    // Push in reverse order to maintain original iteration order
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((node.Children[i], depth + 1));
                    }
                }
            }
        }
        
        private void RecursivelyClearDirty(Node node, InvalidationKind kind)
        {
            if (node == null) return;
            node.ClearDirty(kind, subtree: false);
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    RecursivelyClearDirty(child, kind);
                }
            }
        }

        private void CollectAllNodes(IReadOnlyList<PaintNodeBase> nodes, List<PaintNodeBase> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                result.Add(node);
                CollectAllNodes(node.Children, result);
            }
        }
    }
}
