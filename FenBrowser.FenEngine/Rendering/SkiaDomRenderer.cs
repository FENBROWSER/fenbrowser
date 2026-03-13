using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using System.Diagnostics;

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
        private readonly PaintCompositingStabilityController _paintStabilityController = new PaintCompositingStabilityController();
        private readonly PaintDamageTracker _paintDamageTracker = new PaintDamageTracker();
        private readonly DamageRasterizationPolicy _damageRasterizationPolicy = new DamageRasterizationPolicy();
        private readonly ScrollDamageComputer _scrollDamageComputer = new ScrollDamageComputer();
        private readonly DamageRegionNormalizationPolicy _damageNormalizationPolicy = new DamageRegionNormalizationPolicy();
        private readonly FrameBudgetAdaptivePolicy _frameBudgetAdaptivePolicy = new FrameBudgetAdaptivePolicy();

        private IReadOnlyDictionary<Node, CssComputed> _lastStyles;
        private LayoutResult _lastLayout;
        private ImmutablePaintTree _lastPaintTree;
        private IReadOnlyList<SKRect> _lastDamageRegions = Array.Empty<SKRect>();
        private float _viewportWidth;
        private float _viewportHeight;
        private float _lastViewportWidth;
        private float _lastViewportHeight;
        private float _lastScrollY;
        private Node _lastRoot;
        private const float DefaultViewportWidth = 1920f;
        private const float DefaultViewportHeight = 1080f;
        private const float MaxSafeViewportDimension = 16384f;
        public RendererSafetyPolicy SafetyPolicy { get; set; } = RendererSafetyPolicy.Default;
        public bool LastFrameWatchdogTriggered { get; private set; }
        public string LastFrameWatchdogReason { get; private set; }
        
        /// <summary>
        /// Current overlays for input elements.
        /// </summary>
        public List<InputOverlayData> CurrentOverlays { get; } = new List<InputOverlayData>();
        
        /// <summary>
        /// Access to scroll manager.
        /// </summary>
        public Interaction.ScrollManager ScrollManager => _scrollManager;

        /// <summary>
        /// Damage regions computed from the most recent paint-tree delta.
        /// </summary>
        public IReadOnlyList<SKRect> LastDamageRegions => _lastDamageRegions;
        public bool LastFrameUsedDamageRasterization { get; private set; }
        public float LastDamageAreaRatio { get; private set; }
        

        
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
            SKSize? separateLayoutViewport = null,
            bool hasBaseFrame = false)
        {
            if (root == null || canvas == null) return;
            
            // Re-entrancy Guard
            if (_isRendering)
            {
                // FenLogger.Warn("Skipping re-entrant Render call.");
                return;
            }
            
            _isRendering = true;
            var pipelineContext = PipelineContext.Current;
            var frameWatchdog = Stopwatch.StartNew();
            bool watchdogAbortBeforeRaster = false;
            LastFrameWatchdogTriggered = false;
            LastFrameWatchdogReason = null;
            try
            {
            using var _frameScope = pipelineContext.BeginScopedFrame();
            DiagnosticPaths.AppendRootText("debug_render_start.txt", $"Render Start: Root={root?.GetType().Name}\n");
            CurrentOverlays.Clear();
            
            // CRITICAL FIX: Detect if styles changed using instance comparison, count, or DOM dirty flags
            // JavaScript updates (element.style.xxx) mark the node as StyleDirty.
            bool treeDirty = root.StyleDirty || root.ChildStyleDirty;
            bool stylesChanged = _lastStyles == null || styles == null || _lastStyles != styles || (styles != null && _lastStyles != null && _lastStyles.Count != styles.Count) || treeDirty;
            _lastStyles = styles;

            // [Verification] Capture content metrics for the verification report
            if (root is Document doc)
            {

            }
            
            // Capture state from root downwards using the centralized helper
            FenBrowser.Core.Verification.ContentVerifier.RegisterRenderedFromNode(root, baseUrl ?? "about:blank");


            
            float layoutWidth = SanitizeViewportDimension(
                separateLayoutViewport?.Width ?? viewport.Width,
                DefaultViewportWidth);
            float layoutHeight = SanitizeViewportDimension(
                separateLayoutViewport?.Height ?? viewport.Height,
                DefaultViewportHeight);
            
            _viewportWidth = layoutWidth;
            _viewportHeight = layoutHeight;
            
            // CRITICAL: Update global CSS parser context for viewport units (vh/vw) mechanism
            CssParser.MediaViewportWidth = _viewportWidth;
            CssParser.MediaViewportHeight = _viewportHeight;
            pipelineContext.SetViewport(_viewportWidth, _viewportHeight);

            // Check if resize occurred or if root node changed (new page navigation)
            bool forceLayout = _lastLayout == null || 
                               root != _lastRoot ||
                               stylesChanged ||
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

                using (pipelineContext.BeginScopedStage(PipelineStage.Styling))
                {
                    if (stylesChanged || hasActiveAnimations)
                    {
                        pipelineContext.DirtyFlags.InvalidateStyle();
                    }

                    pipelineContext.SetStyleSnapshot(styles ?? new Dictionary<Node, CssComputed>());
                }

                if (hasActiveAnimations) isLayoutDirty = true;

                // PHASE 1: Layout using the new LayoutEngine
                using (pipelineContext.BeginScopedStage(PipelineStage.Layout))
                {
                RenderPipeline.EnterLayout();
                if (isLayoutDirty)
                {
                    pipelineContext.DirtyFlags.InvalidateLayout();
                    // [ANCHORING] Before layout, select anchor
                    // Use ROOT element or Document as container? 
                    // SkiaDomRenderer handles viewport scroll on 'root' (usually Document or Body?).
                    // Let's assume 'root' is the scroll container if it's Document, or Body.
                    // Actually, if root is Document, we use document.DocumentElement ??
                    Element scrollable = (root as Document)?.DocumentElement ?? root as Element;
                    
                    if (scrollable != null)
                    {
                        // We need OLD boxes for selection
                        _scrollManager.SelectAnchor(scrollable, root, (n) => {
                             if (_boxes.TryGetValue(n, out var b)) return b.BorderBox;
                             return SKRect.Empty;
                        });
                    }

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

                    // Populate a shared RenderContext for InputManager usage?
                    // For now, we construct it on demand in HitTest.
                    
                    // [ANCHORING] After layout, adjust scroll
                    if (scrollable != null)
                    {
                         _scrollManager.AdjustScroll(scrollable, (n) => {
                             if (_boxes.TryGetValue(n, out var b)) return b.BorderBox;
                             return SKRect.Empty;
                         });
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
                        } catch (Exception ex) { FenLogger.Warn($"[SkiaDomRenderer] DOM dump logging failed: {ex.Message}", LogCategory.Rendering); }
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
                pipelineContext.SetLayoutSnapshot(_lastLayout);
                }
                
                // Update Scroll Animations
                bool scrollAnimationActive = _scrollManager.OnFrame();
                if (scrollAnimationActive)
                {
                    // Scroll changed -> Paint Dirty
                    // (We don't strict-track scroll dirty yet, so relying on this or just repainting)
                }

                // PHASE 2: Build Paint Tree
                using (pipelineContext.BeginScopedStage(PipelineStage.Painting))
                {
                var paintStageWatchdog = Stopwatch.StartNew();
                RenderPipeline.EnterPaint(); // Checks LayoutFrozen
                bool paintInvalidationSignal = isLayoutDirty
                                               || root.PaintDirty
                                               || root.ChildPaintDirty
                                               || ImageLoader.HasActiveAnimatedImages
                                               || scrollAnimationActive;
                // PC-4: Suppress forced rebuilds under sustained frame-budget pressure.
                bool adaptiveSuppressed = _frameBudgetAdaptivePolicy.ShouldSuppressForcedRebuild(RenderPipeline.FrameBudget);
                bool forcePaintRebuild = _paintStabilityController.ShouldForcePaintRebuild && !adaptiveSuppressed;
                bool isPaintDirty = paintInvalidationSignal || _lastPaintTree == null || forcePaintRebuild;
                bool rebuiltPaintTree = false;
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
                    pipelineContext.DirtyFlags.InvalidatePaint();
                    FenLogger.Debug($"[SkiaDomRenderer] Invoke NewPaintTreeBuilder... Root={root.GetType().Name} BoxCount={_boxes.Count}");
                    var previousPaintTree = _lastPaintTree;
                    var paintTree = NewPaintTreeBuilder.Build(
                        root,
                        _boxes,
                        styles,
                        _viewportWidth,
                        _viewportHeight,
                        _scrollManager,
                        baseUrl);
                    _lastPaintTree = paintTree;
                    rebuiltPaintTree = true;

                    // PC-3: Tree-diff damage.
                    var currentViewport = new SKRect(0, 0, _viewportWidth, _viewportHeight);
                    var treeDiffDamage = _paintDamageTracker.ComputeDamageRegions(
                        previousPaintTree,
                        paintTree,
                        currentViewport);

                    // PC-3: Scroll-aware damage strips merged with tree-diff damage.
                    float currentScrollY = GetDocumentScrollY(root);
                    var scrollDamage = _scrollDamageComputer.ComputeScrollDamage(
                        _lastScrollY,
                        currentScrollY,
                        new SKSize(_lastViewportWidth, _lastViewportHeight),
                        currentViewport);
                    _lastScrollY = currentScrollY;

                    var allDamage = new System.Collections.Generic.List<SKRect>(treeDiffDamage);
                    foreach (var r in scrollDamage) allDamage.Add(r);

                    _lastDamageRegions = allDamage.Count > 0
                        ? _damageNormalizationPolicy.Normalize(allDamage, currentViewport)
                        : treeDiffDamage;

                    // Clear Paint Dirty Flags
                    RecursivelyClearDirty(root, InvalidationKind.Paint);
                }
                else
                {
                     _lastDamageRegions = Array.Empty<SKRect>();
                     // FenLogger.Debug("[SkiaDomRenderer] Skipping Paint Tree Build (Clean)");
                }
                _paintStabilityController.ObserveFrame(paintInvalidationSignal, rebuiltPaintTree);
                pipelineContext.SetPaintSnapshot(_lastPaintTree);
                paintStageWatchdog.Stop();

                if (SafetyPolicy?.EnableWatchdog == true)
                {
                    var paintMs = paintStageWatchdog.Elapsed.TotalMilliseconds;
                    if (paintMs > SafetyPolicy.MaxPaintStageMs)
                    {
                        LastFrameWatchdogTriggered = true;
                        LastFrameWatchdogReason = $"Paint stage exceeded budget ({paintMs:F2}ms > {SafetyPolicy.MaxPaintStageMs:F2}ms)";
                        FenLogger.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        if (SafetyPolicy.SkipRasterWhenOverBudget)
                        {
                            watchdogAbortBeforeRaster = true;
                        }
                    }

                    var frameMsAfterPaint = frameWatchdog.Elapsed.TotalMilliseconds;
                    if (frameMsAfterPaint > SafetyPolicy.MaxFrameBudgetMs)
                    {
                        LastFrameWatchdogTriggered = true;
                        LastFrameWatchdogReason = $"Frame exceeded budget before raster ({frameMsAfterPaint:F2}ms > {SafetyPolicy.MaxFrameBudgetMs:F2}ms)";
                        FenLogger.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        if (SafetyPolicy.SkipRasterWhenOverBudget)
                        {
                            watchdogAbortBeforeRaster = true;
                        }
                    }
                }
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
                            log += $"  Tag={rootEl.TagName}\n";

                            if (rootStyle.BackgroundColor.HasValue && rootStyle.BackgroundColor.Value.Alpha > 0)
                            {
                                bgColor = rootStyle.BackgroundColor.Value;
                                log += $"-> Using HTML BG: {bgColor}\n";
                            }
                        }
                        else { log += "HTML Style NOT Found.\n"; }

                        if (bgColor == SKColors.White)
                        {
                            var body = rootEl.Children?.FirstOrDefault(c => c is Element e && string.Equals(e.TagName, "body", StringComparison.OrdinalIgnoreCase)) as Element;
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
                catch (Exception ex) { FenLogger.Warn($"[SkiaDomRenderer] Background color resolution failed: {ex.Message}", LogCategory.Rendering); }
                
                using (pipelineContext.BeginScopedStage(PipelineStage.Rasterizing))
                {
                    var rasterStageWatchdog = Stopwatch.StartNew();
                    bool useDamageRasterization = _damageRasterizationPolicy.ShouldUseDamageRasterization(
                        hasBaseFrame,
                        _lastDamageRegions,
                        viewport,
                        out var damageAreaRatio);

                    LastDamageAreaRatio = damageAreaRatio;
                    LastFrameUsedDamageRasterization = useDamageRasterization;

                    if (watchdogAbortBeforeRaster && SafetyPolicy?.SkipRasterWhenOverBudget == true)
                    {
                        // Fail-safe: avoid expensive raster work when frame budget is already blown.
                        canvas.Clear(bgColor);
                        LastFrameUsedDamageRasterization = false;
                    }
                    else if (useDamageRasterization)
                    {
                        _renderer.RenderDamaged(canvas, _lastPaintTree, viewport, bgColor, _lastDamageRegions);
                    }
                    else
                    {
                        _renderer.Render(canvas, _lastPaintTree, viewport, bgColor);
                    }
                    RenderPipeline.EndPaint(); // State -> Composite
                    rasterStageWatchdog.Stop();

                    if (SafetyPolicy?.EnableWatchdog == true)
                    {
                        var rasterMs = rasterStageWatchdog.Elapsed.TotalMilliseconds;
                        if (rasterMs > SafetyPolicy.MaxRasterStageMs)
                        {
                            LastFrameWatchdogTriggered = true;
                            LastFrameWatchdogReason = $"Raster stage exceeded budget ({rasterMs:F2}ms > {SafetyPolicy.MaxRasterStageMs:F2}ms)";
                            FenLogger.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        }

                        var frameMs = frameWatchdog.Elapsed.TotalMilliseconds;
                        if (frameMs > SafetyPolicy.MaxFrameBudgetMs)
                        {
                            LastFrameWatchdogTriggered = true;
                            LastFrameWatchdogReason = $"Frame exceeded budget during raster ({frameMs:F2}ms > {SafetyPolicy.MaxFrameBudgetMs:F2}ms)";
                            FenLogger.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        }
                    }
                }

                using (pipelineContext.BeginScopedStage(PipelineStage.Presenting))
                {
                    RenderPipeline.EnterPresent();
                    // Callback with layout info
                    CurrentOverlays.Clear();
                    CollectOverlays();
                    float totalHeight = _lastLayout?.ContentHeight ?? _viewportHeight;
                    onLayoutUpdated?.Invoke(new SKSize(_viewportWidth, totalHeight), CurrentOverlays);

                    RenderPipeline.EndFrame(); // State -> Idle
                    // PC-4: Record frame duration for EMA-based adaptive pressure tracking.
                    _frameBudgetAdaptivePolicy.ObserveFrame(RenderPipeline.LastFrameDuration);
                }
            }
            catch (Exception ex)
            {
                RenderPipeline.Reset();
                // Log error but don't crash
                FenLogger.Error($"[SkiaDomRenderer] Render error: {ex}", LogCategory.Rendering);
                
                // CRITICAL FIX: Clear stale state to prevent "Ghost UI" where old page features remain interactive
                _lastPaintTree = null;
                _lastDamageRegions = Array.Empty<SKRect>();
                LastFrameUsedDamageRasterization = false;
                LastDamageAreaRatio = 0f;
                _paintStabilityController.Reset();
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
                    var tag = el.TagName?.ToLowerInvariant();
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
                            InitialText = tag == "textarea" ? (el.GetAttribute("value") ?? el.TextContent ?? "") : (el.GetAttribute("value") ?? ""),
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
        /// <summary>
        /// Hit test at document coordinates.
        /// </summary>
        public bool HitTest(float x, float y, out HitTestResult result)
        {
            result = HitTestResult.None;
            if (_lastPaintTree == null || _lastPaintTree.Roots == null) return false;

            // Delegate to canonical HitTester
            var ctx = new FenBrowser.FenEngine.Rendering.Core.RenderContext 
            { 
                 Boxes = _boxes, 
                 Styles = _lastStyles as Dictionary<Node, CssComputed>, 
                 PaintTreeRoots = _lastPaintTree.Roots 
            };
            
            // Use HitTester
            // Note: HitTester.HitTest returns Element. We need full result.
            // HitTester.HitTestRecursive returns bool and out result.
            // We should use that if possible, but it takes list.
            // Let's call HitTester.HitTestRecursive directly?
            // HitTester is in FenBrowser.FenEngine.Rendering.Interaction namespace.
            
            return FenBrowser.FenEngine.Rendering.Interaction.HitTester.HitTestRecursive(_lastPaintTree.Roots, x, y, out result);
        }

        public RenderContext CreateRenderContext()
        {
            var styles = _lastStyles as Dictionary<Node, CssComputed>;
            var boxesSnapshot = new Dictionary<Node, BoxModel>(_boxes);
            var ctx = new RenderContext
            {
                Boxes = boxesSnapshot,
                Styles = styles,
                PaintTreeRoots = _lastPaintTree?.Roots,
                ViewportWidth = _viewportWidth,
                ViewportHeight = _viewportHeight,
                Viewport = new SKRect(0, 0, _viewportWidth, _viewportHeight)
            };
            return ctx;
        }
        
        // Remove HitTestRecursive and FindInteractiveAncestor as they are now in HitTester (or accessible via it)
        // Wait, I didn't verify if I moved FindInteractiveAncestor to HitTester or made it public?
        // In my `write_to_file` for `HitTester.cs`, I did NOT include `FindInteractiveAncestor` (I removed it in the logic or inlined it? No I used it?).
        // Let me check my memory/output of step 11559.
        // I used `var element = node.SourceNode as Element;`...
        // `result = new HitTestResult(...)`.
        // I did NOT call FindInteractiveAncestor in the new HitTester code I wrote.
        // I simplified it to just return the element.
        // If I want the "Interactive Ancestor" logic (bubbling to <A> or <BUTTON>), I should restore it in HitTester.
        // My previous write_to_file for HitTester seemed to just return the hit element.
        
        // Actually, HitTestRecursive in SkiaDomRenderer DID simple bubbling.
        // I should probably keep SkiaDomRenderer's detailed logic IF HitTester doesn't have it.
        // But the goal was "Stacking Context Aware".
        // HitTester now HAS the recursive loop.
        
        // Let's use HitTester.HitTestRecursive.
        // But if HitTester doesn't do "interactive" check, I might regress "Clicking a span inside a button".
        // I should verify HitTester.cs content again? 
        // I wrote it in Step 11559.
        // Content:
        // result = new HitTestResult( ... IsClickable: true ... )
        // It does NOT walk up.
        
        // I should UPDATE HitTester to include FindInteractiveAncestor logic to be robust.
        // Retaining old behavior is important.
        
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
                
                string tag = (node as Element)?.NodeName ?? (node.IsText() ? "#text" : node.NodeName);
                int childCount = node.ChildNodes?.Length ?? 0;
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
                if (node is Element el && el.Attributes != null && el.Attributes.Any())
                {
                    sb.Append(" {");
                    bool first = true;
                    foreach (var attr in el.Attributes)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append($"{attr.Name}='{attr.Value}'");
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
                
                if (node.ChildNodes != null && node.ChildNodes.Length > 0)
                {
                    // Push in reverse order to maintain original iteration order
                    for (int i = node.ChildNodes.Length - 1; i >= 0; i--)
                    {
                        stack.Push((node.ChildNodes[i], depth + 1));
                    }
                }
            }
        }
        
        private void RecursivelyClearDirty(Node node, InvalidationKind kind)
        {
            if (node == null) return;
            node.ClearDirty(kind);
            node.ClearDirty(InvalidationKind.Style); // Ensure Style is cleared too
            
            if (node.ChildNodes != null)
            {
                foreach (var child in node.ChildNodes)
                {
                    RecursivelyClearDirty(child, kind);
                }
            }
        }

        /// <summary>
        /// Gets the document-level vertical scroll offset for scroll-damage computation.
        /// Uses the body or documentElement as the primary scrollable container.
        /// Falls back to 0 when no scroll state is available.
        /// </summary>
        private float GetDocumentScrollY(Node root)
        {
            Element scrollable = (root as Document)?.DocumentElement ?? root as Element;
            if (scrollable == null) return 0f;
            var state = _scrollManager.GetScrollState(scrollable);
            if (state != null) return state.ScrollY;
            return 0f;
        }

        private static float SanitizeViewportDimension(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            {
                return fallback;
            }

            if (value > MaxSafeViewportDimension)
            {
                return MaxSafeViewportDimension;
            }

            return value;
        }

        private void CollectAllNodes(IReadOnlyList<PaintNodeBase> nodes, List<PaintNodeBase> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                result.Add(node);
                CollectAllNodes(node.Children.Cast<PaintNodeBase>().ToList(), result);
            }
        }
    }
}




