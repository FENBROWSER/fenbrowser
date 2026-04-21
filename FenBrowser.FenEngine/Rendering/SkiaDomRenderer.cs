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
using System.Linq;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Production render-frame pipeline built around layout, paint-tree construction,
    /// damage tracking, compositing stability, and backend rasterization.
    /// </summary>
    public class SkiaDomRenderer : IRenderFramePipeline
    {
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
        private int _lastDomNodeCount;
        private const float DefaultViewportWidth = 1920f;
        private const float DefaultViewportHeight = 1080f;
        private const float MaxSafeViewportDimension = 16384f;
        public RendererSafetyPolicy SafetyPolicy { get; set; } = RendererSafetyPolicy.Default;
        public bool LastFrameWatchdogTriggered { get; private set; }
        public string LastFrameWatchdogReason { get; private set; }
        public RenderFrameTelemetry LastFrameTelemetry { get; private set; }
        private readonly Stopwatch _lastDomDumpWatch = new Stopwatch();
        
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

        public RenderFrameResult RenderFrame(RenderFrameRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            Render(
                request.Root,
                request.Canvas,
                request.Styles,
                request.Viewport,
                request.BaseUrl,
                request.OnLayoutUpdated,
                request.SeparateLayoutViewport,
                request.HasBaseFrame,
                request.InvalidationReason,
                request.RequestedBy,
                request.EmitVerificationReport);

            return new RenderFrameResult
            {
                Layout = _lastLayout,
                PaintTree = _lastPaintTree,
                DamageRegions = _lastDamageRegions ?? Array.Empty<SKRect>(),
                Overlays = CurrentOverlays.ToArray(),
                WatchdogTriggered = LastFrameWatchdogTriggered,
                WatchdogReason = LastFrameWatchdogReason,
                UsedDamageRasterization = LastFrameUsedDamageRasterization,
                DamageAreaRatio = LastDamageAreaRatio,
                InvalidationReason = request.InvalidationReason,
                RequestedBy = request.RequestedBy,
                RasterMode = LastFrameTelemetry?.RasterMode ?? RenderFrameRasterMode.None,
                Telemetry = LastFrameTelemetry
            };
        }

        public void Render(
            Node root, 
            SKCanvas canvas, 
            Dictionary<Node, CssComputed> styles, 
            SKRect viewport, 
            string baseUrl = null, 
            Action<SKSize, List<InputOverlayData>> onLayoutUpdated = null, 
            SKSize? separateLayoutViewport = null,
            bool hasBaseFrame = false,
            RenderFrameInvalidationReason invalidationReason = RenderFrameInvalidationReason.Unknown,
            string requestedBy = "direct-call",
            bool emitVerificationReport = true)
        {
            if (root == null || canvas == null) return;
            
            // Re-entrancy Guard
            if (_isRendering)
            {
                // EngineLogCompat.Warn("Skipping re-entrant Render call.");
                return;
            }
            
            _isRendering = true;
            var pipelineContext = PipelineContext.Current;
            var frameWatchdog = Stopwatch.StartNew();
            var layoutStageWatchdog = new Stopwatch();
            var paintStageWatchdog = new Stopwatch();
            var rasterStageWatchdog = new Stopwatch();
            bool watchdogAbortBeforeRaster = false;
            bool layoutUpdated = false;
            bool rebuiltPaintTree = false;
            var rasterMode = RenderFrameRasterMode.None;
            LastFrameWatchdogTriggered = false;
            LastFrameWatchdogReason = null;
            try
            {
            using var _frameScope = pipelineContext.BeginScopedFrame();
            DiagnosticPaths.AppendRootText("debug_render_start.txt", $"Render Start: Root={root?.GetType().Name}\n");
            CurrentOverlays.Clear();
            
            // Detect style changes via DOM dirty flags (primary) or dictionary identity (fallback).
            // node.ComputedStyle is the single source of truth set by CascadeIntoComputedStyles.
            bool treeDirty = root.StyleDirty || root.ChildStyleDirty;
            bool stylesChanged = treeDirty || _lastStyles == null || _lastStyles != styles || (styles != null && _lastStyles != null && _lastStyles.Count != styles.Count);
            bool styleInvalidation = treeDirty || stylesChanged || (invalidationReason & RenderFrameInvalidationReason.Style) != 0;
            _lastStyles = styles;

            if (_lastDomNodeCount <= 0 || root != _lastRoot || treeDirty || (invalidationReason & (RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom)) != 0)
            {
                _lastDomNodeCount = CountNodes(root);
            }
            
            if (emitVerificationReport)
            {
                FenBrowser.Core.Verification.ContentVerifier.RegisterRendered(baseUrl ?? "about:blank", _lastDomNodeCount, 0);
            }


            
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
                var animationInvalidation = InvalidationKind.None;
                bool paintInvalidationSignal = false;

                // PHASE 0: Update Animations
                // Integrate CSS Animation Engine
                if (root is Element && styles != null)
                {
                    // PERF: Only check for new transitions/animations if style is dirty OR on navigation.
                    // ALWAYS update existing active animations/transitions.
                    
                    // 1. Check for NEW animations/transitions starting only on elements with potentially changed styles
                    // To do this properly we'd need a list of elements whose styles were JUST changed.
                    // For now, we optimize by only checking elements that POSSESS animation/transition properties
                    // OR we just iterate over all elements but avoid the Keys.ToList() allocation.
                    
                    // 2. Update existing animations (O(N_active) instead of O(N_total))
                    var activeElements = CssAnimationEngine.Instance.GetAllActiveAnimationElements();
                    foreach (var elem in activeElements)
                    {
                        if (!styles.TryGetValue(elem, out var style)) continue;
                        
                        // Check for transitions/animations start (needed even for active ones to handle interruptions)
                        CssAnimationEngine.Instance.CheckTransitions(elem, style);
                        CssAnimationEngine.Instance.StartAnimation(elem, style);
                        
                        // Get current animated values
                        var animatedProps = CssAnimationEngine.Instance.GetAnimatedProperties(elem);
                        var transitionProps = CssAnimationEngine.Instance.GetTransitionedProperties(elem);
                        
                        if (animatedProps.Count > 0 || transitionProps.Count > 0)
                        {
                            hasActiveAnimations = true;
                            animationInvalidation |= CssAnimationEngine.DetermineInvalidationKind(
                                animatedProps.Keys.Concat(transitionProps.Keys));

                            // Create a clone for this frame to avoid persisting animated values into the base style
                            var frameStyle = style.Clone();
                            
                            foreach(var kvp in transitionProps)
                                FenBrowser.FenEngine.Rendering.Css.CssStyleApplicator.ApplyProperty(frameStyle, kvp.Key, kvp.Value);
                                
                            foreach(var kvp in animatedProps)
                                FenBrowser.FenEngine.Rendering.Css.CssStyleApplicator.ApplyProperty(frameStyle, kvp.Key, kvp.Value);
                                
                            styles[elem] = frameStyle;
                        }
                    }
                    
                    // 3. Check for NEW animations on other elements if style was invalidated globally
                    if (styleInvalidation)
                    {
                        foreach(var kvp in styles)
                        {
                            if (kvp.Key is not Element elem || activeElements.Contains(elem)) continue;
                            
                            // Check if this element should START an animation
                            var style = kvp.Value;
                            if (style.Map.ContainsKey("animation-name") || style.Map.ContainsKey("transition"))
                            {
                                CssAnimationEngine.Instance.CheckTransitions(elem, style);
                                CssAnimationEngine.Instance.StartAnimation(elem, style);
                                // If it started, it will be caught in the next frame
                            }
                        }
                    }
                }

                using (pipelineContext.BeginScopedStage(PipelineStage.Styling))
                {
                    if (styleInvalidation || hasActiveAnimations)
                    {
                        pipelineContext.DirtyFlags.InvalidateStyle();
                    }

                    pipelineContext.SetStyleSnapshot(styles ?? new Dictionary<Node, CssComputed>());
                }

                if ((animationInvalidation & InvalidationKind.Layout) != 0)
                {
                    isLayoutDirty = true;
                }

                // PHASE 1: Layout using the new LayoutEngine
                using (pipelineContext.BeginScopedStage(PipelineStage.Layout))
                {
                layoutStageWatchdog.Restart();
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
                    layoutUpdated = true;
                        

                        



                    
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
                        // EngineLogCompat.Debug($"[L-04 CHECK-LATE] RootHeight={rootBox.PaddingBox.Height} Viewport={_viewportHeight} Match={almostFullCheck}");
                    }

                    // PERF: Throttle DOM dumping to stay within frame budget during animations
                    bool shouldDump = FenBrowser.Core.Logging.DebugConfig.LogDomTree;
                    if (shouldDump && _lastDomDumpWatch.IsRunning && _lastDomDumpWatch.ElapsedMilliseconds < 1000)
                        shouldDump = false;

                    if (shouldDump)
                    {
                        try
                        {
                            var sb = new System.Text.StringBuilder();
                            DumpDom(root, 0, sb, styles, _boxes);
                            System.IO.File.WriteAllText(DiagnosticPaths.GetRootArtifactPath("dom_dump.txt"), sb.ToString());
                            _lastDomDumpWatch.Restart();

                            if (FenBrowser.Core.Logging.DebugConfig.LogDomTree)
                            {
                                EngineLogCompat.Debug($"[SkiaDomRenderer] DOM Dump: {sb}", LogCategory.Rendering);
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[SkiaDomRenderer] DOM dump write failed: {ex.Message}", LogCategory.Rendering);
                        }
                    }
                    
                    EngineLogCompat.Debug($"[SkiaDomRenderer] Copied {boxCount} boxes for rendering.", LogCategory.Rendering);
                    Console.WriteLine($"[DBG-RENDER] Layout boxes: {boxCount}, Root={root?.GetType().Name}/{(root as Element)?.TagName}");
                    
                    // Clear Layout Dirty Flags
                    RecursivelyClearDirty(root, InvalidationKind.Layout);
                }
                else
                {
                    // EngineLogCompat.Debug("[SkiaDomRenderer] Skipping Layout (Clean/Cached)");

                }
                RenderPipeline.EndLayout(); // State -> LayoutFrozen
                pipelineContext.SetLayoutSnapshot(_lastLayout);
                layoutStageWatchdog.Stop();
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
                paintStageWatchdog.Restart();
                RenderPipeline.EnterPaint(); // Checks LayoutFrozen
                paintInvalidationSignal = isLayoutDirty
                                          || styleInvalidation
                                          || root.PaintDirty
                                          || root.ChildPaintDirty
                                          || ImageLoader.HasActiveAnimatedImages
                                          || scrollAnimationActive
                                          || (animationInvalidation & InvalidationKind.Paint) != 0;
                // PC-4: Suppress forced rebuilds under sustained frame-budget pressure.
                bool adaptiveSuppressed = _frameBudgetAdaptivePolicy.ShouldSuppressForcedRebuild(RenderPipeline.FrameBudget);
                bool forcePaintRebuild = _paintStabilityController.ShouldForcePaintRebuild && !adaptiveSuppressed;
                bool isPaintDirty = paintInvalidationSignal || _lastPaintTree == null || forcePaintRebuild;
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
                    EngineLogCompat.Debug($"[SkiaDomRenderer] Invoke NewPaintTreeBuilder... Root={root.GetType().Name} BoxCount={_boxes.Count}");
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
                    Console.WriteLine($"[DBG-RENDER] PaintTree nodes: {paintTree?.NodeCount ?? 0}");

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

                    bool interactionFullRepaintRequested = ElementStateManager.Instance.ConsumeFullRepaintRequest();
                    _lastDamageRegions = allDamage.Count > 0
                        ? _damageNormalizationPolicy.Normalize(allDamage, currentViewport)
                        : treeDiffDamage;

                    if (interactionFullRepaintRequested)
                    {
                        _lastDamageRegions = new[] { currentViewport };
                        EngineLogCompat.Debug("[SkiaDomRenderer] Forcing full repaint for interaction-state visual change", LogCategory.Rendering);
                    }
                    else if (rebuiltPaintTree &&
                             paintInvalidationSignal &&
                             (_lastDamageRegions == null || _lastDamageRegions.Count == 0))
                    {
                        _lastDamageRegions = new[] { currentViewport };
                        EngineLogCompat.Warn(
                            "[SkiaDomRenderer] Paint tree rebuilt without localized damage; upgrading to full-viewport damage to avoid presenting a stale base frame.",
                            LogCategory.Rendering);
                    }

                    // Clear Paint Dirty Flags
                    RecursivelyClearDirty(root, InvalidationKind.Paint);
                }
                else
                {
                     _lastDamageRegions = Array.Empty<SKRect>();
                     // EngineLogCompat.Debug("[SkiaDomRenderer] Skipping Paint Tree Build (Clean)");
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
                        EngineLogCompat.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
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
                        EngineLogCompat.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        if (SafetyPolicy.SkipRasterWhenOverBudget)
                        {
                            watchdogAbortBeforeRaster = true;
                        }
                    }
                }
                }


                
                // PHASE 3: Render
                var bgColor = ResolveCanvasBackgroundColor(root, styles);
                
                using (pipelineContext.BeginScopedStage(PipelineStage.Rasterizing))
                {
                    rasterStageWatchdog.Restart();
                    bool useDamageRasterization = _damageRasterizationPolicy.ShouldUseDamageRasterization(
                        hasBaseFrame,
                        _lastDamageRegions,
                        viewport,
                        out var damageAreaRatio);

                    LastDamageAreaRatio = damageAreaRatio;
                    LastFrameUsedDamageRasterization = useDamageRasterization;
                    bool mustPresentFreshFrame = rebuiltPaintTree && paintInvalidationSignal;

                    if (watchdogAbortBeforeRaster && SafetyPolicy?.SkipRasterWhenOverBudget == true)
                    {
                        LastFrameUsedDamageRasterization = false;
                        bool preserveBaseFrame = hasBaseFrame && !mustPresentFreshFrame;
                        rasterMode = preserveBaseFrame
                            ? RenderFrameRasterMode.PreservedBaseFrame
                            : RenderFrameRasterMode.Full;

                        if (preserveBaseFrame)
                        {
                            // Preserve the caller-seeded base frame instead of presenting a blank fallback.
                            EngineLogCompat.Warn(
                                "[SkiaDomRenderer] Watchdog budget exceeded before raster; preserving the caller-supplied base frame.",
                                LogCategory.Performance);
                        }
                        else
                        {
                            // DOM/layout/paint changes must present a fresh frame even under budget pressure.
                            EngineLogCompat.Warn(
                                "[SkiaDomRenderer] Watchdog budget exceeded before raster on a fresh paint tree; forcing full raster to avoid presenting stale content.",
                                LogCategory.Performance);
                            _renderer.Render(canvas, _lastPaintTree, viewport, bgColor);
                        }
                    }
                    else if (hasBaseFrame && (_lastDamageRegions == null || _lastDamageRegions.Count == 0))
                    {
                        rasterMode = RenderFrameRasterMode.PreservedBaseFrame;
                    }
                    else if (useDamageRasterization)
                    {
                        rasterMode = RenderFrameRasterMode.Damage;
                        _renderer.RenderDamaged(canvas, _lastPaintTree, viewport, bgColor, _lastDamageRegions);
                    }
                    else
                    {
                        rasterMode = RenderFrameRasterMode.Full;
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
                            EngineLogCompat.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
                        }

                        var frameMs = frameWatchdog.Elapsed.TotalMilliseconds;
                        if (frameMs > SafetyPolicy.MaxFrameBudgetMs)
                        {
                            LastFrameWatchdogTriggered = true;
                            LastFrameWatchdogReason = $"Frame exceeded budget during raster ({frameMs:F2}ms > {SafetyPolicy.MaxFrameBudgetMs:F2}ms)";
                            EngineLogCompat.Warn($"[SkiaDomRenderer] Watchdog: {LastFrameWatchdogReason}", LogCategory.Performance);
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
                    LastFrameTelemetry = CreateTelemetry(
                        baseUrl,
                        requestedBy,
                        invalidationReason,
                        rasterMode,
                        layoutUpdated,
                        rebuiltPaintTree,
                        hasBaseFrame,
                        _lastDomNodeCount,
                        _boxes.Count,
                        _lastPaintTree?.NodeCount ?? 0,
                        CurrentOverlays.Count,
                        _lastDamageRegions?.Count ?? 0,
                        layoutStageWatchdog.Elapsed.TotalMilliseconds,
                        paintStageWatchdog.Elapsed.TotalMilliseconds,
                        rasterStageWatchdog.Elapsed.TotalMilliseconds,
                        RenderPipeline.LastFrameDuration.TotalMilliseconds > 0
                            ? RenderPipeline.LastFrameDuration.TotalMilliseconds
                            : frameWatchdog.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                RenderPipeline.Reset();
                // Log error but don't crash
                EngineLogCompat.Error($"[SkiaDomRenderer] Render error: {ex}", LogCategory.Rendering);
                
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
                LastFrameTelemetry = CreateTelemetry(
                    baseUrl,
                    requestedBy,
                    invalidationReason,
                    RenderFrameRasterMode.Full,
                    layoutUpdated,
                    rebuiltPaintTree,
                    hasBaseFrame,
                    _lastDomNodeCount,
                    _boxes.Count,
                    _lastPaintTree?.NodeCount ?? 0,
                    0,
                    0,
                    layoutStageWatchdog.Elapsed.TotalMilliseconds,
                    paintStageWatchdog.Elapsed.TotalMilliseconds,
                    rasterStageWatchdog.Elapsed.TotalMilliseconds,
                    frameWatchdog.Elapsed.TotalMilliseconds);
            }
            
            if (emitVerificationReport &&
                (layoutUpdated ||
                 rebuiltPaintTree ||
                 LastFrameWatchdogTriggered ||
                 (invalidationReason & (RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom | RenderFrameInvalidationReason.Style | RenderFrameInvalidationReason.Diagnostics)) != 0))
            {
                FenBrowser.Core.Verification.ContentVerifier.RegisterRendered(baseUrl ?? "about:blank", _lastDomNodeCount, CountRenderedTextLength(root));
                FenBrowser.Core.Verification.ContentVerifier.PerformVerification();
            }
            }
            finally
            {
                _isRendering = false;
            }
        }

        internal static SKColor ResolveCanvasBackgroundColor(
            Node root,
            IReadOnlyDictionary<Node, CssComputed> styles)
        {
            const string defaultReason = "default white canvas";

            try
            {
                if (styles == null)
                {
                    EngineLogCompat.Debug($"[SkiaDomRenderer] Canvas background fallback: {defaultReason} (styles unavailable)", LogCategory.Rendering);
                    return SKColors.White;
                }

                var (html, body) = ResolveDocumentElements(root);

                if (TryResolveOpaqueBackground(styles, html, out var htmlColor))
                {
                    EngineLogCompat.Debug($"[SkiaDomRenderer] Canvas background resolved from HTML: {htmlColor}", LogCategory.Rendering);
                    return htmlColor;
                }

                if (TryResolveOpaqueBackground(styles, body, out var bodyColor))
                {
                    EngineLogCompat.Debug($"[SkiaDomRenderer] Canvas background resolved from BODY: {bodyColor}", LogCategory.Rendering);
                    return bodyColor;
                }

                EngineLogCompat.Debug($"[SkiaDomRenderer] Canvas background fallback: {defaultReason}", LogCategory.Rendering);
                return SKColors.White;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[SkiaDomRenderer] Background color resolution failed: {ex.Message}", LogCategory.Rendering);
                return SKColors.White;
            }
        }

        private static (Element html, Element body) ResolveDocumentElements(Node root)
        {
            if (root is Document doc)
            {
                var html = doc.DocumentElement;
                var body = doc.Body;
                if (body == null && html != null)
                {
                    body = html
                        .Descendants()
                        .OfType<Element>()
                        .FirstOrDefault(e => string.Equals(e.TagName, "body", StringComparison.OrdinalIgnoreCase));
                }

                return (html, body);
            }

            if (root is Element element)
            {
                if (string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase))
                {
                    var body = element.ChildNodes?
                        .OfType<Element>()
                        .FirstOrDefault(child => string.Equals(child.TagName, "body", StringComparison.OrdinalIgnoreCase));
                    if (body == null)
                    {
                        body = element
                            .Descendants()
                            .OfType<Element>()
                            .FirstOrDefault(e => string.Equals(e.TagName, "body", StringComparison.OrdinalIgnoreCase));
                    }
                    return (element, body);
                }

                if (string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase))
                {
                    var html = element.ParentElement;
                    if (html != null && !string.Equals(html.TagName, "html", StringComparison.OrdinalIgnoreCase))
                    {
                        html = null;
                    }

                    return (html, element);
                }
            }

            return (null, null);
        }

        private static bool TryResolveOpaqueBackground(
            IReadOnlyDictionary<Node, CssComputed> styles,
            Element element,
            out SKColor color)
        {
            color = SKColors.Transparent;
            if (element == null || !styles.TryGetValue(element, out var style) || style == null)
            {
                return false;
            }

            if (style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0)
            {
                color = style.BackgroundColor.Value;
                return true;
            }

            return false;
        }

        private RenderFrameTelemetry CreateTelemetry(
            string baseUrl,
            string requestedBy,
            RenderFrameInvalidationReason invalidationReason,
            RenderFrameRasterMode rasterMode,
            bool layoutUpdated,
            bool rebuiltPaintTree,
            bool hasBaseFrame,
            int domNodeCount,
            int boxCount,
            int paintNodeCount,
            int overlayCount,
            int damageRegionCount,
            double layoutDurationMs,
            double paintDurationMs,
            double rasterDurationMs,
            double totalDurationMs)
        {
            return new RenderFrameTelemetry
            {
                FrameSequence = RenderPipeline.FrameSequence,
                Url = baseUrl ?? "about:blank",
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "unspecified" : requestedBy,
                InvalidationReason = invalidationReason,
                RasterMode = rasterMode,
                DomNodeCount = domNodeCount,
                BoxCount = boxCount,
                PaintNodeCount = paintNodeCount,
                OverlayCount = overlayCount,
                DamageRegionCount = damageRegionCount,
                LayoutUpdated = layoutUpdated,
                PaintTreeRebuilt = rebuiltPaintTree,
                BaseFrameSeeded = hasBaseFrame,
                WatchdogTriggered = LastFrameWatchdogTriggered,
                WatchdogReason = LastFrameWatchdogReason,
                LayoutDurationMs = layoutDurationMs,
                PaintDurationMs = paintDurationMs,
                RasterDurationMs = rasterDurationMs,
                TotalDurationMs = totalDurationMs,
                DamageAreaRatio = LastDamageAreaRatio
            };
        }

        private static int CountNodes(Node root)
        {
            if (root == null)
            {
                return 0;
            }

            var count = 0;
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                count++;
                var children = current.ChildNodes;
                if (children == null)
                {
                    continue;
                }

                for (var i = children.Length - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return count;
        }

        private static int CountRenderedTextLength(Node root)
        {
            if (root == null)
            {
                return 0;
            }

            var length = 0;
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                if (current.NodeType == NodeType.Text)
                {
                    var text = current.TextContent;
                    if (!string.IsNullOrEmpty(text))
                    {
                        length += text.Length;
                    }
                }

                var children = current.ChildNodes;
                if (children == null)
                {
                    continue;
                }

                for (var i = children.Length - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return length;
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
                            TextColor = ResolveOverlayTextColor(el, style),
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

        private SKColor ResolveOverlayTextColor(Element element, CssComputed style)
        {
            // Keep native host input overlays legible even when page CSS sets transparent text.
            if (style?.ForegroundColor is SKColor direct &&
                direct.Alpha > 0 &&
                !IsCurrentColorSentinel(direct))
            {
                return direct;
            }

            for (Element ancestor = element?.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (_lastStyles != null &&
                    _lastStyles.TryGetValue(ancestor, out var ancestorStyle) &&
                    ancestorStyle?.ForegroundColor is SKColor inherited &&
                    inherited.Alpha > 0 &&
                    !IsCurrentColorSentinel(inherited))
                {
                    return inherited;
                }
            }

            return SKColors.Black;
        }

        private static bool IsCurrentColorSentinel(SKColor color)
        {
            // CssParser.ParseColor("currentColor") sentinel (ARGB 1,255,0,255).
            return color.Red == 255 && color.Green == 0 && color.Blue == 255 && color.Alpha == 1;
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




