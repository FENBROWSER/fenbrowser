using SkiaSharp;
using SkiaSharp.HarfBuzz;
using FenBrowser.Core;
// using FenBrowser.Core.Math; // Namespace moved to Core
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;
// using FenBrowser.FenEngine.Rendering.Layout; // Deleted
using FenBrowser.FenEngine.Rendering.Interaction;
using FenBrowser.FenEngine.Rendering.UserAgent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.Globalization;
// Removed Avalonia imports

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A new experimental renderer built from scratch using SkiaSharp.
    /// This bypasses Avalonia's high-level layout system to give us pixel-perfect control.
    /// </summary>
    public class InputOverlayData
    {
        public LiteElement Node { get; set; }
        public SKRect Bounds { get; set; }
        public string Type { get; set; }
        public string InitialText { get; set; }
        public string Placeholder { get; set; }  // HTML placeholder attribute
        public List<string> Options { get; set; } = new List<string>();
        public int SelectedIndex { get; set; } = -1;

        // Visual Styling
        public SKColor? BackgroundColor { get; set; }
        public SKColor? TextColor { get; set; }
        public string FontFamily { get; set; }
        public float FontSize { get; set; }
        public Thickness BorderThickness { get; set; }
        public SKColor? BorderColor { get; set; }
        public CornerRadius BorderRadius { get; set; }
        public string TextAlign { get; set; } // left, center, right

        // Pseudo-elements styling
        public SKColor? PlaceholderColor { get; set; }
        public string PlaceholderFontFamily { get; set; }
        public float PlaceholderFontSize { get; set; }
        
        public SKColor? SelectionColor { get; set; }
        public SKColor? SelectionBackgroundColor { get; set; }
    }

    /// <summary>
    /// Parsed box-shadow value
    /// </summary>
    public class BoxShadowParsed
    {
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float BlurRadius { get; set; }
        public float SpreadRadius { get; set; }
        public SKColor Color { get; set; } = SKColors.Black;
        public bool Inset { get; set; } = false;
    }

    /// <summary>
    /// Parsed transform value
    /// </summary>
    public class TransformParsed
    {
        public float TranslateX { get; set; }
        public float TranslateY { get; set; }
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float Rotate { get; set; } // degrees
        public float SkewX { get; set; }
        public float SkewY { get; set; }
    }

    /// <summary>
    /// Parsed text-decoration value
    /// </summary>
    public class TextDecorationParsed
    {
        public bool Underline { get; set; }
        public bool Overline { get; set; }
        public bool LineThrough { get; set; }
        public SKColor? Color { get; set; }
        public string Style { get; set; } = "solid"; // solid, dashed, dotted, wavy
    }

    public class SkiaDomRenderer : ILayoutEngine
    {
        private const float DefaultFontSize = 16f;
        private const float DefaultLineHeightMultiplier = 1.2f;
        
        public List<InputOverlayData> CurrentOverlays { get; private set; } = new List<InputOverlayData>();

        // ILayoutEngine.Context implementation - provides access to shared state
        private RenderContext _context;
        public RenderContext Context 
        { 
            get 
            {
                if (_context == null) _context = new RenderContext();
                // Sync state from renderer to context
                _context.Styles = _styles;
                _context.ViewportHeight = _viewportHeight;
                _context.ViewportWidth = _viewportWidth;
                _context.Viewport = _viewport;
                _context.BaseUrl = _baseUrl;
                return _context;
            }
        }

        // Box Model storage
        private class BoxModel
        {
            public SKRect MarginBox;
            public SKRect BorderBox;
            public SKRect PaddingBox;
            public SKRect ContentBox;
            public Thickness Margin;
            public Thickness Border;
            public Thickness Padding;
            public float LineHeight; // Computed line height for text
            public float Ascent;     // Distance from baseline to top of content
            public float Descent;    // Distance from baseline to bottom of content
            public TransformParsed Transform; // Transform for this element
        }

        // Use ConcurrentDictionary for thread safety between render and hit test
        private readonly System.Collections.Concurrent.ConcurrentDictionary<LiteElement, BoxModel> _boxes = new System.Collections.Concurrent.ConcurrentDictionary<LiteElement, BoxModel>();
        private readonly Dictionary<LiteElement, LiteElement> _parents = new Dictionary<LiteElement, LiteElement>(); // Parent map
        private readonly Dictionary<LiteElement, List<TextLine>> _textLines = new Dictionary<LiteElement, List<TextLine>>(); // Text wrapping
        private Dictionary<LiteElement, CssComputed> _styles;
        private string _baseUrl;
        private float _viewportHeight; // Store viewport height for height:100% resolution
        private float _viewportWidth;  // Store viewport width for fixed positioning
        private SKRect _viewport;      // Full viewport rect for position:fixed
        
        // CSS Counters
        private Dictionary<string, int> _counters = new Dictionary<string, int>();
        
        // Deferred absolute-positioned elements for two-pass layout
        private readonly List<LiteElement> _deferredAbsoluteElements = new List<LiteElement>();
        
        // Intrinsic size cache for images and text (reduces re-layout calls)
        private readonly Dictionary<string, (float width, float height)> _intrinsicSizeCache = new Dictionary<string, (float, float)>();
        private readonly Dictionary<LiteElement, float> _textMeasureCache = new Dictionary<LiteElement, float>();
        
        // Display List for deferred rendering (Phase 6)
        private List<RenderCommand> _displayList = new List<RenderCommand>();
        private bool _useDisplayList = false; // Disable display list mode to force direct rendering (fix white screen)
        
        // Stacking Context Tree for correct z-order (Phase 7)
        private StackingContext _rootStackingContext;
        private StackingContext _currentStackingContext;
        
        // Dirty Rect Optimization (Phase 9)
        private readonly List<SKRect> _dirtyRects = new List<SKRect>();
        private readonly Dictionary<LiteElement, SKRect> _previousBounds = new Dictionary<LiteElement, SKRect>();
        private bool _fullRepaintNeeded = true; // Start with full repaint
        private bool _enableDirtyRectOptimization = false; // Disabled until stable
        
        // Debug layout visualization - set to true to see box boundaries
        private const bool DEBUG_LAYOUT = false;
        
        // Debug file logging - DISABLE for production (sync file I/O causes severe lag)
        private const bool DEBUG_FILE_LOGGING = true;
        private static object _logLock = new object();
        
        // Scrollbar state persistence to prevent layout jitter and infinite loops (Google infinite scroll fix)
        private bool _verticalScrollbarVisible = false;
        
        // Scroll offset for hash fragment navigation (position:fixed elements need to counter this)
        private float _scrollOffsetY = 0;
        
        // Recursion depth for layout safety
        private int _layoutDepth = 0;
        // Text line for wrapping
        private class TextLine
        {
            public string Text;
            public float Width;
            public float Y;
        }

        public SkiaDomRenderer() { }

        public void Render(LiteElement root, SKCanvas canvas, Dictionary<LiteElement, CssComputed> styles, SKRect viewport, string baseUrl = null, Action<SKSize, List<InputOverlayData>> onLayoutUpdated = null, SKSize? separateLayoutViewport = null)
        {
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", $"[SkiaDomRenderer] Render called for root: {root?.Tag}, viewport {viewport}\r\n"); } catch {}
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaDomRenderer] Render called for root: {root?.Tag}, viewport {viewport}\r\n"); } catch {}
            _boxes.Clear();
            _deferredAbsoluteElements.Clear();
            _parents.Clear();
            _textLines.Clear();
            CurrentOverlays.Clear();
            _baseUrl = baseUrl; // Store for relative path resolution
            _styles = styles; // Assign styles for layout engine
            
            // Store viewport height for height:100% resolution
            // CRITICAL FIX: Use separate layout viewport if provided (window size)
            // preventing infinite feedback loops where content height becomes viewport height
            if (separateLayoutViewport.HasValue)
            {
                _viewportHeight = separateLayoutViewport.Value.Height;
                _viewportWidth = separateLayoutViewport.Value.Width;
                
                // If scrollbar is visible, reduce layout width to simulate scrollbar taking space
                // This logic is mostly handled by the caller (MainWindow) passing the Viewport width which already excludes scrollbar?
                // Actually ScrollViewer.Viewport *excludes* scrollbars usually.
                // But let's respect our internal logic too if needed.
            }
            else
            {
                _viewportHeight = viewport.Height;
                _viewportWidth = viewport.Width;
            }
            
            _viewport = viewport; // Paint bounds (what we can draw to)
            if (_viewportHeight <= 0) _viewportHeight = 1080; // Fallback
            if (_viewportWidth <= 0) _viewportWidth = 1920;   // Fallback
            
            // Clear CSS counters for new render
            _counters.Clear();
            
            Console.WriteLine($"[RENDER] viewport.Height={viewport.Height} viewport.Width={viewport.Width} _viewportHeight={_viewportHeight}");
            if (separateLayoutViewport.HasValue)
            {
                Console.WriteLine($"[RENDER] using separateLayoutViewport: {_viewportWidth}x{_viewportHeight} (Origin: window/scrollviewer)");
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] separateLayoutViewport: {_viewportWidth}x{_viewportHeight}\r\n"); } catch {} }
            }
            else
            {
                 if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] viewport fallback: {_viewportWidth}x{_viewportHeight}\r\n"); } catch {} }
            }
            
            // Draw background only in the strict viewport of the control
            using (var paint = new SKPaint { Color = SKColors.White })
            {
                canvas.DrawRect(viewport, paint);
            }

            if (root == null) return;
            
            // Parse hash fragment for scroll-to-element (e.g., #top from URL#top)
            string hashFragment = null;
            if (!string.IsNullOrEmpty(baseUrl) && baseUrl.Contains("#"))
            {
                int hashIndex = baseUrl.IndexOf('#');
                hashFragment = baseUrl.Substring(hashIndex + 1);
                ElementStateManager.Instance.SetTargetFragment(hashFragment);
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Render] Hash fragment detected: #{hashFragment}\r\n"); } catch {} }            }

            if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\render_debug.txt", $"[RENDER] PaintViewport: {viewport.Width}x{viewport.Height} LayoutViewport: {_viewportWidth}x{_viewportHeight}\r\n"); } catch {} }

            // 1. Layout Pass
            // Use viewport width for layout constraints
            if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\render_debug.txt", $"[RENDER] Starting ComputeLayout...\r\n"); } catch {} }
            float initialWidth = viewport.Width;
            if (initialWidth <= 0) initialWidth = 1920; // Fallback

            try
            {
                // Clear any previously deferred absolute elements
                _deferredAbsoluteElements.Clear();
                
                // First pass: Layout all non-absolute elements
                float scrollbarWidth = 16.0f; // Standard scrollbar width
                float layoutWidth = initialWidth;
                
                // Use persistent scrollbar state to determine initial layout width
                // This prevents frame-by-frame jitter which confuses JS layout logic (e.g. Google)
                if (_verticalScrollbarVisible)
                {
                    layoutWidth -= scrollbarWidth;
                }
                
                try
                {
                    ComputeLayout(root, 0, 0, layoutWidth, shrinkToContent: false, availableHeight: _viewportHeight);
                    FenLogger.Debug("[RENDER] ComputeLayout success.", LogCategory.Rendering);
                }
                catch (Exception layoutEx)
                {
                    Console.WriteLine($"[Layout] CRASH: {layoutEx}");
                    FenLogger.Error($"[Layout] CRASH: {layoutEx}", LogCategory.Rendering);
                }
                
                // Check for vertical overflow
                bool hasOverflow = false;
                if (_boxes.TryGetValue(root, out var rootBoxCheck))
                {
                    hasOverflow = rootBoxCheck.MarginBox.Bottom > _viewportHeight;
                }
                
                // Update scrollbar state if it changed
                // (Only update if meaningful change to avoid oscillation)
                if (hasOverflow != _verticalScrollbarVisible)
                {
                    // State changed!
                    bool prev = _verticalScrollbarVisible;
                    _verticalScrollbarVisible = hasOverflow;
                    
                    if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Layout] Scrollbar state changed: {prev}->{_verticalScrollbarVisible}. Re-layout.\r\n"); } catch {} }

                    // Perform re-layout if needed
                    // Case 1: Was NO scrollbar, now YES -> Shrink width, Re-layout
                    // Case 2: Was YES scrollbar, now NO -> Expand width, Re-layout ??
                    // Expanding width usually removes overflow, so we oscillate.
                    // Hysteresis: Only expand if SIGNIFICANTLY under overflow?
                    // For now: Always respect current frame overflow status to ensure correctness.
                    // To stop infinite loop, JS needs STABLE width. 
                    // If we flip-flop, JS sees width change every frame.
                    
                    // Force re-layout with new state
                    float newLayoutWidth = initialWidth;
                    if (_verticalScrollbarVisible) newLayoutWidth -= scrollbarWidth;
                    
                    // Clear Previous Layout
                    _boxes.Clear();
                    _deferredAbsoluteElements.Clear();
                    
                    ComputeLayout(root, 0, 0, newLayoutWidth, shrinkToContent: false, availableHeight: _viewportHeight);
                }
                
                // Second pass: Layout deferred absolute-positioned elements
                // Now all positioned ancestors should have their boxes computed
                while (_deferredAbsoluteElements.Count > 0)
                {
                    var batch = new List<LiteElement>(_deferredAbsoluteElements);
                    _deferredAbsoluteElements.Clear();
                    
                    foreach (var element in batch)
                    {
                        CssComputed s = null;
                        if (_styles != null) _styles.TryGetValue(element, out s);
                        string pos = s?.Position?.ToLowerInvariant();
                        
                        SKRect container;
                        if (pos == "fixed")
                            container = FindFixedContainer(element);
                        else
                            container = FindAbsoluteContainer(element);
                        ComputeAbsoluteLayout(element, container);
                        
                        // DEBUG: Log position:fixed elements to trace viewport positioning
                        if (pos == "fixed")
                        {
                            if (_boxes.TryGetValue(element, out var fixedBox))
                            {
                                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[FIXED] tag={element.Tag} Top={fixedBox.MarginBox.Top} Left={fixedBox.MarginBox.Left} viewport={_viewport}\r\n"); } catch {} }                            }
                        }
                    }
                }
                
                // Calculate Total Height - respect overflow:hidden on html/body elements
                float totalHeight = 0;
                if (_boxes.TryGetValue(root, out var rootBox))
                {
                    totalHeight = rootBox.MarginBox.Bottom;
                    
                    // BUGFIX: Exclude position:fixed elements from height calculation
                    // Fixed elements should not contribute to scrollable content height
                    // They are positioned relative to viewport, not document flow
                    float maxFlowHeight = 0;
                    LiteElement tallestElement = null;
                    
                    foreach (var kvp in _boxes)
                    {
                        var element = kvp.Key;
                        var box = kvp.Value;
                        
                        // Skip fixed elements - they don't contribute to scroll height
                        if (_styles != null && _styles.TryGetValue(element, out var elStyle))
                        {
                            string elPos = elStyle?.Position?.ToLowerInvariant();
                            if (elPos == "fixed")
                                continue;
                        }
                        
                        // Skip elements with unreasonable bottom values (likely layout bugs)
                        // FIX: Increased threshold from 10x to 100x. 10x (~10,000px) is too small for legitimate long pages.
                        if (box.MarginBox.Bottom > _viewportHeight * 100)
                        {
                            // Log the problematic element
                            if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[HEIGHT] Skipping element with huge bottom: {element.Tag} class={element.GetAttribute("class")} bottom={box.MarginBox.Bottom}\r\n"); } catch {} }
                            continue;
                        }
                        
                        // Track maximum bottom of in-flow elements
                        if (box.MarginBox.Bottom > maxFlowHeight)
                        {
                            maxFlowHeight = box.MarginBox.Bottom;
                            
                            // Ignore root containers when reporting the "tallest" element to find the culprit
                            string tag = element.Tag?.ToUpperInvariant();
                            if (tag != "#DOCUMENT" && tag != "HTML" && tag != "BODY")
                            {
                                tallestElement = element;
                            }
                        }
                    }
                    
                    // Ensure totalHeight is at least the viewport height (prevent white screen on empty/absolute-only pages)
                    if (maxFlowHeight < _viewportHeight)
                    {
                        if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[HEIGHT] maxFlowHeight {maxFlowHeight} < viewport {_viewportHeight}. Clamping to viewport.\r\n"); } catch {} }
                        maxFlowHeight = _viewportHeight;
                    }
                    
                    // Use the calculated flow height
                    totalHeight = maxFlowHeight;
                    
                    // Sanity check: Limit maximum height to 10x viewport
                    // This prevents infinite scrolling bugs from layout errors
                    float maxReasonableHeight = _viewportHeight * 10;
                    if (totalHeight > maxReasonableHeight)
                    {
                        if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Render] WARNING: totalHeight {totalHeight} exceeds max, clamping to {maxReasonableHeight}\r\n"); } catch {} }
                        totalHeight = maxReasonableHeight;
                    }
                    
                    // Find html element (may be root or child of #document)
                    LiteElement htmlElement = null;
                    if (root.Tag?.ToUpperInvariant() == "HTML")
                    {
                        htmlElement = root;
                    }
                    else if (root.Children != null)
                    {
                        // Look for html child in #document
                        htmlElement = root.Children.FirstOrDefault(c => c.Tag?.ToUpperInvariant() == "HTML");
                    }
                    
                    // Check if html has overflow:hidden
                    if (htmlElement != null)
                    {
                        CssComputed htmlStyle = null;
                        if (_styles != null) _styles.TryGetValue(htmlElement, out htmlStyle);
                        string htmlOverflow = htmlStyle?.Overflow?.ToLowerInvariant();
                        
                        if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Render] Found html element, overflow={htmlOverflow}\r\n"); } catch {} }                        
                        if (htmlOverflow == "hidden" || htmlOverflow == "clip")
                        {
                            // Limit total height to viewport height (no scroll beyond viewport)
                            totalHeight = Math.Min(totalHeight, _viewportHeight);
                            if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Render] HTML has overflow:{htmlOverflow}, constraining height from {rootBox.MarginBox.Bottom} to viewport={_viewportHeight}\r\n"); } catch {} }                        }
                    }
                }
                
                // 2. Hash Fragment Scroll - find element by ID and scroll to it
                float scrollOffsetY = 0;
                if (!string.IsNullOrEmpty(hashFragment))
                {
                    var targetElement = FindElementById(root, hashFragment);
                    if (targetElement != null && _boxes.TryGetValue(targetElement, out var targetBox))
                    {
                        // Scroll to put the target element's CONTENT at the top of viewport
                        // Use ContentBox.Top instead of MarginBox.Top to skip large margins
                        scrollOffsetY = targetBox.ContentBox.Top;
                        if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Render] Hash scroll to #{hashFragment}: scrollOffsetY={scrollOffsetY} (MarginBox.Top was {targetBox.MarginBox.Top})\r\n"); } catch {} }                    }
                }
                
                // 3. Paint Pass (this is where overlays are collected)
                // Store scroll offset for use in DrawLayout (position:fixed elements counter this)
                _scrollOffsetY = scrollOffsetY;
                
                // Clear display list for new frame
                ClearDisplayList();
                
                // Build stacking context tree for correct z-order (Phase 7)
                var stackingBuilder = new StackingContextBuilder(_styles);
                _rootStackingContext = stackingBuilder.BuildTree(root);
                _currentStackingContext = _rootStackingContext;
                
                canvas.Save();
                if (scrollOffsetY > 0)
                {
                    canvas.Translate(0, -scrollOffsetY);
                }
                
                // DrawLayout populates the display list with commands
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] Starting DrawLayout...\r\n"); } catch {} }
                DrawLayout(root, canvas);
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] DrawLayout done.\r\n"); } catch {} }
                
                // Execute display list using stacking context for correct paint order
                if (_useDisplayList && _displayList.Count > 0)
                {
                    // Calculate the visible viewport accounting for scroll
                    var visibleViewport = new SKRect(
                        _viewport.Left,
                        _viewport.Top + scrollOffsetY,
                        _viewport.Right,
                        _viewport.Bottom + scrollOffsetY
                    );
                    
                    // Apply dirty rect clipping if optimization is enabled (Phase 9)
                    SKRect effectiveViewport = visibleViewport;
                    bool useDirtyClip = _enableDirtyRectOptimization && !_fullRepaintNeeded && _dirtyRects.Count > 0;
                    
                    if (useDirtyClip)
                    {
                        // Get union of dirty rects and intersect with viewport
                        var dirtyUnion = GetDirtyUnion();
                        effectiveViewport = SKRect.Intersect(visibleViewport, dirtyUnion);
                        
                        // Clip canvas to dirty region for faster rendering
                        canvas.Save();
                        canvas.ClipRect(effectiveViewport);
                    }
                    
                    // Use stacking context tree to get correct paint order
                    // For now, execute commands directly (stacking context integration is foundation)
                    // Future: Replace with _rootStackingContext.Flatten() when DrawLayout fully converted
                    ExecuteDisplayList(canvas, effectiveViewport);
                    if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] ExecuteDisplayList done.\r\n"); } catch {} }
                    
                    if (useDirtyClip)
                    {
                        canvas.Restore();
                    }
                    
                    // Store bounds for next frame comparison and clear dirty rects
                    StorePreviousBounds();
                    ClearDirtyRects();
                }
                
                canvas.Restore();
                
                // Invoke callback with Size AND Overlays AFTER DrawLayout populates CurrentOverlays
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RENDER] totalHeight calculated: {totalHeight} Boxes: {_boxes.Count}. Callback invoked.\r\n"); } catch {} }
                var overlaysCopy = new List<InputOverlayData>(CurrentOverlays);
                onLayoutUpdated?.Invoke(new SKSize(initialWidth, totalHeight), overlaysCopy);
            }

            catch (Exception)
            {
                 // Ignore render errors to prevent crash
            }
        }

        /// <summary>
        /// Execute the display list with viewport culling.
        /// Commands outside the viewport are skipped for performance.
        /// </summary>
        private void ExecuteDisplayList(SKCanvas canvas, SKRect viewport)
        {
            if (_displayList == null || _displayList.Count == 0)
                return;
            
            int totalCommands = _displayList.Count;
            int culledCommands = 0;
            
            foreach (var command in _displayList)
            {
                // Cull commands completely outside the viewport
                // Note: Some commands (Save, Restore, Clip) have empty bounds and should always execute
                bool hasValidBounds = command.Bounds.Width > 0 || command.Bounds.Height > 0;
                
                if (hasValidBounds && !command.IntersectsWith(viewport))
                {
                    culledCommands++;
                    continue; // Skip off-screen commands
                }
                
                command.Execute(canvas);
            }
            
            // Log culling stats for debugging
            if (culledCommands > 0)
            {
                FenLogger.Debug($"[DisplayList] Executed {totalCommands - culledCommands}/{totalCommands} commands ({culledCommands} culled)", LogCategory.Rendering);
            }
        }
        
        /// <summary>
        /// Clear the display list for next frame
        /// </summary>
        private void ClearDisplayList()
        {
            _displayList.Clear();
        }
        
        /// <summary>
        /// Add a command to the display list
        /// </summary>
        private void AddCommand(RenderCommand command)
        {
            _displayList.Add(command);
        }

        #region Dirty Rect Optimization (Phase 9)
        
        /// <summary>
        /// Mark an element as dirty, requiring repaint of its bounding box.
        /// Also marks the area where the element was previously drawn.
        /// </summary>
        public void MarkDirty(LiteElement element)
        {
            if (element == null || !_enableDirtyRectOptimization) 
            {
                _fullRepaintNeeded = true;
                return;
            }
            
            // Get current bounds
            if (_boxes.TryGetValue(element, out var box))
            {
                _dirtyRects.Add(box.MarginBox);
            }
            
            // Also mark previous location as dirty (for moved elements)
            if (_previousBounds.TryGetValue(element, out var prevBounds))
            {
                _dirtyRects.Add(prevBounds);
            }
        }
        
        /// <summary>
        /// Mark a specific rectangle as dirty for repaint
        /// </summary>
        public void MarkDirtyRect(SKRect rect)
        {
            if (!_enableDirtyRectOptimization)
            {
                _fullRepaintNeeded = true;
                return;
            }
            _dirtyRects.Add(rect);
        }
        
        /// <summary>
        /// Request a full repaint of the entire viewport
        /// </summary>
        public void RequestFullRepaint()
        {
            _fullRepaintNeeded = true;
            _dirtyRects.Clear();
        }
        
        /// <summary>
        /// Get the union of all dirty rectangles for clipping
        /// </summary>
        private SKRect GetDirtyUnion()
        {
            if (_fullRepaintNeeded || _dirtyRects.Count == 0)
                return _viewport;
            
            float left = float.MaxValue, top = float.MaxValue;
            float right = float.MinValue, bottom = float.MinValue;
            
            foreach (var rect in _dirtyRects)
            {
                if (rect.Left < left) left = rect.Left;
                if (rect.Top < top) top = rect.Top;
                if (rect.Right > right) right = rect.Right;
                if (rect.Bottom > bottom) bottom = rect.Bottom;
            }
            
            // Expand slightly to avoid aliasing issues
            return new SKRect(left - 1, top - 1, right + 1, bottom + 1);
        }
        
        /// <summary>
        /// Store current element bounds for next frame comparison
        /// </summary>
        private void StorePreviousBounds()
        {
            _previousBounds.Clear();
            foreach (var kvp in _boxes)
            {
                _previousBounds[kvp.Key] = kvp.Value.MarginBox;
            }
        }
        
        /// <summary>
        /// Clear dirty rects after processing
        /// </summary>
        private void ClearDirtyRects()
        {
            _dirtyRects.Clear();
            _fullRepaintNeeded = false;
        }
        
        /// <summary>
        /// Check if any dirty rects are pending
        /// </summary>
        public bool HasDirtyRects => _fullRepaintNeeded || _dirtyRects.Count > 0;
        
        /// <summary>
        /// Enable or disable dirty rect optimization
        /// </summary>
        public bool EnableDirtyRectOptimization
        {
            get => _enableDirtyRectOptimization;
            set
            {
                _enableDirtyRectOptimization = value;
                if (!value) _fullRepaintNeeded = true;
            }
        }
        
        /// <summary>
        /// Called when mouse hovers over an element - updates ElementStateManager and marks for repaint
        /// </summary>
        public void OnHover(LiteElement element, LiteElement previousElement = null)
        {
            // Update ElementStateManager - this will track hover chain and notify about state changes
            ElementStateManager.Instance.SetHoveredElement(element);
            
            if (previousElement != null)
            {
                MarkDirty(previousElement); // Unhover old element
            }
            if (element != null)
            {
                MarkDirty(element); // Hover new element
            }
        }
        
        /// <summary>
        /// Called when an element is clicked (mouse down) - updates ElementStateManager for :active state
        /// </summary>
        public void OnClick(LiteElement element)
        {
            // Update ElementStateManager for :active pseudo-class
            ElementStateManager.Instance.SetActiveElement(element);
            
            if (element != null)
            {
                MarkDirty(element);
            }
        }
        
        /// <summary>
        /// Called when mouse is released - clears :active state
        /// </summary>
        public void OnMouseUp()
        {
            var active = ElementStateManager.Instance.ActiveElement;
            ElementStateManager.Instance.SetActiveElement(null);
            if (active != null)
            {
                MarkDirty(active);
            }
        }
        
        /// <summary>
        /// Called when an element receives/loses focus - updates ElementStateManager and marks for repaint
        /// </summary>
        public void OnFocus(LiteElement element, bool focused)
        {
            // Update ElementStateManager for :focus and :focus-within pseudo-classes
            if (focused)
            {
                ElementStateManager.Instance.SetFocusedElement(element);
            }
            else
            {
                // Only clear if this element was the focused one
                if (ElementStateManager.Instance.FocusedElement == element)
                {
                    ElementStateManager.Instance.SetFocusedElement(null);
                }
            }
            
            if (element != null)
            {
                MarkDirty(element);
            }
        }
        
        /// <summary>
        /// Called when an element's style changes - marks for repaint
        /// </summary>
        public void OnStyleChange(LiteElement element)
        {
            if (element != null)
            {
                MarkDirty(element);
                // Also mark parent for layout-affecting changes
                if (element.Parent != null)
                {
                    MarkDirty(element.Parent);
                }
            }
        }
        
        #endregion

        public void ComputeLayout(LiteElement node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0)
        {
            _layoutDepth++;
            try
            {
                if (_layoutDepth > 200) throw new Exception($"Layout recursion too deep on {node.Tag ?? "unknown"}");
                ComputeLayoutInternal(node, x, y, availableWidth, shrinkToContent, availableHeight);
            }
            finally
            {
                _layoutDepth--;
            }
        }

        // Added shrinkToContent and availableHeight parameters
        private void ComputeLayoutInternal(LiteElement node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0)
        {
            // Get styles
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);

            // GOOGLE COMPATIBILITY FIX: Force language bar container to layout horizontally
            // Using Flexbox is more robust than inline-block if the children are block-level wrappers
            if (node.Attr != null && node.Attr.TryGetValue("id", out var idVal) && idVal.IndexOf("SIvCob", StringComparison.OrdinalIgnoreCase) >= 0)
            { 
                 if (style == null) style = new CssComputed();
                 style.Display = "flex";
                 style.FlexDirection = "row";
                 style.FlexWrap = "wrap";
                 style.AlignItems = "baseline"; // align text baselines
                 style.JustifyContent = "center"; // FIX: Center the languages!
                 style.TextAlign = SKTextAlign.Center;      // Extra insurance for text nodes
                 style.Gap = 15.0;            // Increased space (was 12)
                 style.LineHeight = 24.0f;    // Ensure vertical separation
                 style.MinHeight = 40.0f;     // FIX: Prevent height collapse which hides content!
                 FenLogger.Debug("[Compat] Forced #SIvCob to flex-row center with spacing + MinHeight", LogCategory.Layout);
            }

            // GOOGLE COMPATIBILITY FIX: Force Header (#gb) to Layout Horizontally
            // Prevents links from stacking vertically
            if (node.Attr != null && node.Attr.TryGetValue("id", out var idGb) && idGb.Equals("gb", StringComparison.OrdinalIgnoreCase))
            {
                if (style == null) style = new CssComputed();
                style.Display = "flex";
                style.FlexDirection = "row";
                style.AlignItems = "center";
                style.JustifyContent = "space-between"; 
                style.WidthPercent = 100.0f; 
                FenLogger.Debug("[Compat] Forced #gb to flex-row", LogCategory.Layout);
            }

            // Process CSS counters
            ProcessCssCounters(style);
            
            // CSS Animations: Start animation if element has animation-name
            // and apply any currently animated properties to the computed style
            if (style?.Map != null)
            {
                // DEBUG: Inspect .main-columns styles
                if (node.Attr != null && node.Attr.TryGetValue("class", out var dbgMain) && (dbgMain.Contains("main-columns") || dbgMain.Contains("corset")))
                {
                     var sb = new System.Text.StringBuilder();
                     foreach(var kvp in style.Map) sb.Append($"{kvp.Key}:{kvp.Value}; ");
                     FenLogger.Debug($"[StyleProbe] {node.Tag} Class='{dbgMain}' Display='{style.Display}' FlexDir='{style.FlexDirection}' Float='{style.Float}' Map: {sb}", LogCategory.Layout);
                }

                string animName = null;
                style.Map.TryGetValue("animation-name", out animName);
                if (string.IsNullOrEmpty(animName)) style.Map.TryGetValue("animation", out animName);
                
                if (!string.IsNullOrWhiteSpace(animName) && animName != "none")
                {
                    // Start animation if not already running
                    if (!CssAnimationEngine.Instance.HasActiveAnimations(node))
                    {
                        CssAnimationEngine.Instance.StartAnimation(node, style);
                    }
                    
                    // Apply animated properties to computed style
                    var animatedProps = CssAnimationEngine.Instance.GetAnimatedProperties(node);
                    foreach (var kvp in animatedProps)
                    {
                        style.Map[kvp.Key] = kvp.Value;
                    }
                }
            }
            
            // VISIBILITY CHECK
            if (ShouldHide(node, style)) return;

            // DEBUG: Trace CENTER element through ComputeLayout
            string nodeTag = node.Tag?.ToUpperInvariant();
            if (nodeTag == "CENTER")
            {
                string nodeClass = node.Attr != null && node.Attr.TryGetValue("class", out var cls) ? cls : "";
                FenLogger.Debug($"[ComputeLayout] CENTER passed visibility check, class='{nodeClass}' children={node.Children?.Count}", LogCategory.Layout);
            }

            // DEBUG: Trace INPUT element to find Search Box Container
            if (nodeTag == "INPUT")
            {
                 var parent = _parents.ContainsKey(node) ? _parents[node] : null;
                 string pClass = parent?.Attr != null && parent.Attr.TryGetValue("class", out var pc) ? pc : "null";
                 FenLogger.Debug($"[InputProbe] Found INPUT. Parent Tag={parent?.Tag} Class='{pClass}'", LogCategory.Layout);
            }

            // Apply User Agent (UA) styles for inputs if missing
            ApplyUserAgentStyles(node, ref style);

            // Calculate Box Model
            var box = new BoxModel();
            
            // Extract CSS values (default to 0)
            box.Margin = style?.Margin ?? new Thickness(0);
            box.Border = style?.BorderThickness ?? new Thickness(0);
            box.Padding = style?.Padding ?? new Thickness(0);

            // Width calculation
            float marginLeft = (float)box.Margin.Left;
            float marginRight = (float)box.Margin.Right;
            float borderLeft = (float)box.Border.Left;


            // NOTE: Previous Google-specific patches removed - CSS parser handles these properly now
            float borderRight = (float)box.Border.Right;
            float paddingLeft = (float)box.Padding.Left;
            float paddingRight = (float)box.Padding.Right;

            // Check for margin: auto (horizontal centering)
            bool marginLeftAuto = false, marginRightAuto = false;
            if (style?.Map != null)
            {
                style.Map.TryGetValue("margin-left", out string ml);
                style.Map.TryGetValue("margin-right", out string mr);
                style.Map.TryGetValue("margin", out string m);
                
                marginLeftAuto = ml?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
                marginRightAuto = mr?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
                
                // Parse margin shorthand
                if (!marginLeftAuto || !marginRightAuto)
                {
                    var parts = (m ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        marginLeftAuto = marginRightAuto = true;
                    }
                    else if (parts.Length == 3 && parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        marginLeftAuto = marginRightAuto = true;
                    }
                    else if (parts.Length >= 4)
                    {
                        if (parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) marginRightAuto = true;
                        if (parts[3].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) marginLeftAuto = true;
                    }
                }
            }

            // Box Model Calculation (Content Width)
            
            // Check box-sizing
            string boxSizing = style?.Map != null && style.Map.TryGetValue("box-sizing", out var bs) ? bs.ToLowerInvariant() : "content-box";
            bool isBorderBox = boxSizing == "border-box";

            // Content width calculation
            float contentWidth = 0;
            
            // explicit width override (pixel value)
            bool hasExplicitWidth = false;
            
            // Check Expression First
            if (!string.IsNullOrEmpty(style?.WidthExpression))
            {
                 float evalW = EvaluateCssExpression(style.WidthExpression, availableWidth);
                 if (evalW >= 0)
                 {
                     hasExplicitWidth = true;
                     contentWidth = evalW;
                     // Is the result content-box or border-box? 
                     // Usually explicit width matches box-sizing.
                     if (isBorderBox)
                     {
                         contentWidth -= (paddingLeft + paddingRight + borderLeft + borderRight);
                     }
                 }
            }
            
            // Fallback to static values if expression not present/failed, 
            // OR if expression didn't set width (e.g. invalid)
            if (!hasExplicitWidth && style?.Width.HasValue == true)
            {
                hasExplicitWidth = true;
                float declaredWidth = (float)style.Width.Value;
                if (isBorderBox)
                {
                    // Width includes padding + border, so we subtract them to get content width
                    contentWidth = declaredWidth - (paddingLeft + paddingRight + borderLeft + borderRight);
                }
                else
                {
                    // content-box: Width IS the content width
                    contentWidth = declaredWidth;
                }
            }
            // Also handle percentage width (e.g., width: 100%)
            else if (!hasExplicitWidth && style?.WidthPercent.HasValue == true)
            {
                // FIX: If available width is infinite/unconstrained, percentage width is undefined/auto.
                // We fallback to auto width (shrink to content) to avoid Infinite width propagation which leads to invisible content
                // or 1.5f wrapping bugs in Flex Layout.
                if (float.IsInfinity(availableWidth) || availableWidth > 1e7) // 1e7 sentinel
                {
                    hasExplicitWidth = false; 
                    contentWidth = 0; // Trigger intrinsic/shrink-to-fit logic later
                    
                    // Log this specific fallback
                    // FenLogger.Debug($"[ComputeLayout] Percent width with Infinite available -> Fallback to Auto. Tag={nodeTag}", LogCategory.Layout);
                }
                else
                {
                    hasExplicitWidth = true; // Treat percentage as explicit for sizing decisions
                    float percentValue = (float)style.WidthPercent.Value;
                    
                    // Base for percentage: usually the parent's content width (availableWidth)
                    // But we must subtract margins from availableWidth to get the "containing block logic" correct?
                    // Actually availableWidth passed here = parent.ContentWidth (usually)
                    
                    // Standard block flow: availableWidth is the width of the containing block
                    // For a child in normal flow, width:100% means it fills the containing block.
                    // But we must account for margins of THIS element.
                    float availableForBox = availableWidth - (marginLeft + marginRight);
                    
                    float calculatedWidth = (percentValue / 100f) * availableForBox;
                    
                    if (isBorderBox || 
                        nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" ||
                        nodeTag == "DIV" || nodeTag == "FOOTER" || nodeTag == "HEADER" || 
                        nodeTag == "NAV" || nodeTag == "SECTION" || nodeTag == "MAIN" || 
                        nodeTag == "ARTICLE" || nodeTag == "ASIDE" || nodeTag == "BUTTON")
                    {
                        // FIX: Always apply border-box logic for containers/inputs with percentage width
                        // This prevents common layout issues where padding blows out the container (Google search bar & footer)
                        contentWidth = calculatedWidth - (paddingLeft + paddingRight + borderLeft + borderRight);
                    }
                    else
                    {
                        contentWidth = calculatedWidth;
                    }
                }
            }
            else
            {
                // Auto width
                // In normal flow, auto width = available - margins - borders - padding
                
                // FIX: Check for Infinite available width (e.g. during Flex measurement)
                if (float.IsInfinity(availableWidth) || availableWidth > 1e7)
                {
                    // Use Infinity so children don't wrap prematurely (Max-Content)
                    contentWidth = availableWidth; 
                    // Force shrinkToContent so we update to actual size after layout
                    shrinkToContent = true;
                }
                else
                {
                    contentWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
                }
            }
            
            if (contentWidth < 0) contentWidth = 0;
            
            if (contentWidth < 0) contentWidth = 0;
            
            // Apply Min/Max Width Expressions
            if (!string.IsNullOrEmpty(style?.MinWidthExpression))
            {
                float minW = EvaluateCssExpression(style.MinWidthExpression, availableWidth);
                if (isBorderBox) minW -= (paddingLeft + paddingRight + borderLeft + borderRight);
                if (contentWidth < minW) contentWidth = minW;
            }
            if (!string.IsNullOrEmpty(style?.MaxWidthExpression))
            {
                float maxW = EvaluateCssExpression(style.MaxWidthExpression, availableWidth);
                if (isBorderBox) maxW -= (paddingLeft + paddingRight + borderLeft + borderRight);
                if (contentWidth > maxW) contentWidth = maxW;
            }
            
            // Apply max-width constraint
            bool hasMaxWidth = style?.MaxWidth.HasValue == true;
            if (hasMaxWidth)
            {
                float maxW = (float)style.MaxWidth.Value;
                FenLogger.Debug($"[ComputeLayout] max-width enforcement: tag={nodeTag} maxWidth={maxW} contentWidth(before)={contentWidth}", LogCategory.Layout);
                if (contentWidth > maxW)
                {
                    contentWidth = maxW;
                    FenLogger.Debug($"[ComputeLayout] max-width applied: contentWidth now={contentWidth}", LogCategory.Layout);
                }
            }
            
            // Apply min-width constraint
            if (style?.MinWidth.HasValue == true && contentWidth < (float)style.MinWidth.Value)
            {
                contentWidth = (float)style.MinWidth.Value;
            }

            // Position (relative to parent content box, passed as x,y)
            float currentX = x + marginLeft;
            float currentY = y + (float)box.Margin.Top;
            
            // Apply margin: auto centering (if element has explicit width OR max-width)
            // This allows elements with max-width and margin:0 auto to be centered
            if ((hasExplicitWidth || hasMaxWidth) && marginLeftAuto && marginRightAuto)
            {
                float totalBoxWidth = borderLeft + contentWidth + borderRight + paddingLeft + paddingRight;
                float remainingSpace = availableWidth - totalBoxWidth;
                FenLogger.Debug($"[Layout] margin:auto centering: tag={nodeTag} totalBoxWidth={totalBoxWidth} availableWidth={availableWidth} remainingSpace={remainingSpace}", LogCategory.Layout);
                if (remainingSpace > 0)
                {
                    currentX = x + remainingSpace / 2;
                    FenLogger.Debug($"[Layout] Centered: currentX now={currentX}", LogCategory.Layout);
                }
            }
            else if (nodeTag == "FORM" || nodeTag == "MAIN" || nodeTag == "HEADER")
            {
                // Log why centering wasn't applied
                FenLogger.Debug($"[Layout] margin:auto NOT applied: tag={nodeTag} hasExplicitWidth={hasExplicitWidth} hasMaxWidth={hasMaxWidth} marginLeftAuto={marginLeftAuto} marginRightAuto={marginRightAuto}", LogCategory.Layout);
            }

            // Initialize Boxes (Heights 0 initially)
            // If shrinkToContent is true and no explicit width, we use a temporary infinite width? 
            // Or use available but reset later.
            box.MarginBox = new SKRect(x, y, x + availableWidth, y); 
            box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
            
            box.PaddingBox = new SKRect(
                box.BorderBox.Left + borderLeft, 
                box.BorderBox.Top + (float)box.Border.Top,
                box.BorderBox.Right - borderRight, 
                box.BorderBox.Top + (float)box.Border.Top);
            
            box.ContentBox = new SKRect(
                box.PaddingBox.Left + paddingLeft,
                box.PaddingBox.Top + (float)box.Padding.Top,
                box.PaddingBox.Right - paddingRight,
                box.PaddingBox.Top + (float)box.Padding.Top);
            
            // DEBUG: Log H2 box initialization to trace 2477px height bug
            if (nodeTag == "H2")
            {
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[H2-BOX-INIT] y={y} MarginTop={box.Margin.Top} currentY={currentY} BorderTop={box.Border.Top} PaddingTop={box.Padding.Top}\r\n[H2-BOX-INIT] MarginBox.Top={box.MarginBox.Top} BorderBox.Top={box.BorderBox.Top} PaddingBox.Top={box.PaddingBox.Top} ContentBox.Top={box.ContentBox.Top}\r\n"); } catch {} }            }


            // --- LAYOUT CHILDREN ---
            if (node.Children != null)
            {
                foreach(var c in node.Children) _parents[c] = node;
            }

            string display = style?.Display?.ToLowerInvariant();
            
            // DEBUG: Log display for CENTER
            if (nodeTag == "CENTER")
            {
                FenLogger.Debug($"[ComputeLayout] CENTER initial display from CSS: '{display}'", LogCategory.Layout);
            }
            
            // Default display logic (Refactored to Block Whitelist)
            if (string.IsNullOrEmpty(display))
            {
                string tag = node.Tag?.ToUpperInvariant();
                var blocks = new HashSet<string> { 
                    "DIV", "P", "H1", "H2", "H3", "H4", "H5", "H6", 
                    "UL", "OL", "LI", "TR", "TABLE", "BODY", "HTML", 
                    "HEADER", "FOOTER", "NAV", "SECTION", "ARTICLE", "MAIN", 
                    "HR", "PRE", "BLOCKQUOTE", "FORM", "BR", "DL", "DT", "DD", "FIGURE", "FIGCAPTION", "FIELDSET", "DETAILS", "SUMMARY",
                    "CENTER" // CENTER is block-level and centers its content
                };
                
                if (blocks.Contains(tag))
                    display = "block";
                else
                    display = "inline-block"; // Default for SPAN, A, IMG, INPUT, TD, B, I, CODE, etc.
            }
            
            // CENTER element: This is a deprecated HTML element that centers content horizontally
            // CRITICAL FIX: CENTER should NEVER use flex layout - it's a block element with text-align: center
            // Override any CSS that sets display: flex on CENTER
            if (nodeTag == "CENTER")
            {
                if (style == null)
                {
                    style = new CssComputed();
                    _styles[node] = style;
                }
                
                // Get parent and classes for debugging
                string parentTag = node.Parent?.Tag?.ToUpperInvariant() ?? "NONE";
                string nodeClasses = (node as LiteElement)?.GetAttribute("class") ?? "";
                FenLogger.Debug($"[ComputeLayout] CENTER parent={parentTag} classes='{nodeClasses}' original_display='{display}' flexDir='{style.FlexDirection}'", LogCategory.Layout);
                
                // FORCE CENTER to use block layout, NOT flex
                // CENTER is semantically a block element that centers inline content via text-align
                if (display == "flex" || display == "inline-flex")
                {
                    FenLogger.Debug($"[ComputeLayout] CENTER: OVERRIDING display from '{display}' to 'block' - CENTER should NOT be flex container", LogCategory.Layout);
                    display = "block";
                    style.Display = "block";
                    style.FlexDirection = null;  // Clear flex properties
                }
                
                // Set text-align: center for proper content centering
                if (style.TextAlign == null)
                {
                    style.TextAlign = SKTextAlign.Center;
                }
            }

            // CRITICAL FIX: Inline/inline-block elements should ALWAYS shrink to content
            // This is the CSS box model behavior - inline elements don't expand to fill available width
            if (display == "inline" || display == "inline-block" || node.IsText)
            {
                shrinkToContent = true;
            }

            // --- REPLACED ELEMENTS SIZE ---
            // nodeTag already declared above
            bool isReplaced = nodeTag == "IMG" || nodeTag == "INPUT" || nodeTag == "BUTTON" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "SVG" || nodeTag == "METER" || nodeTag == "PROGRESS";
            float intrinsicWidth = 0;
            float intrinsicHeight = 0;
            float aspectRatio = 0;

            if (isReplaced)
            {
                if (nodeTag == "INPUT" || nodeTag == "SELECT") {
                    // Check if hidden input - should have zero size
                    string inputType = null;
                    if (nodeTag == "INPUT" && node.Attr != null) 
                    {
                        node.Attr.TryGetValue("type", out inputType);
                        inputType = inputType?.ToLowerInvariant() ?? "text";
                    }
                    
                    if (inputType == "hidden")
                    {
                        intrinsicHeight = 0;
                        intrinsicWidth = 0;
                    }
                    else if (inputType == "submit" || inputType == "button" || inputType == "reset")
                    {
                        // Submit/Button/Reset inputs should size based on their value attribute
                        intrinsicHeight = 30;
                        string btnValue = node.Attr != null && node.Attr.TryGetValue("value", out var v) ? v : "";
                        if (!string.IsNullOrEmpty(btnValue))
                        {
                            using (var paint = new SKPaint { TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize })
                            {
                                var bounds = new SKRect();
                                paint.MeasureText(btnValue, ref bounds);
                                intrinsicWidth = bounds.Width + 24; // Add padding
                            }
                        }
                        else
                        {
                            intrinsicWidth = 80; // Default for empty button
                        }
                        // Minimum width
                        if (intrinsicWidth < 60) intrinsicWidth = 60;
                    }
                    else
                    {
                        intrinsicHeight = 30;
                        intrinsicWidth = 150;
                    }
                }
                if (nodeTag == "BUTTON") 
                { 
                    intrinsicHeight = 30; 
                    // Calculate button width based on text content
                    string btnText = GetTextContent(node);
                    if (!string.IsNullOrEmpty(btnText))
                    {
                        // FIX: Trim the button text and limit to reasonable length
                        // Some buttons contain lots of hidden content (like Google's search buttons)
                        btnText = btnText.Trim();
                        if (btnText.Length > 100) 
                        {
                            btnText = btnText.Substring(0, 100);
                        }
                        
                        using (var paint = new SKPaint { TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize })
                        {
                            var bounds = new SKRect();
                            paint.MeasureText(btnText, ref bounds);
                            intrinsicWidth = bounds.Width + 20; // Add padding
                        }
                        
                        // FIX: Cap button width to a reasonable maximum
                        if (intrinsicWidth > 300) 
                        {
                            intrinsicWidth = 300;
                        }
                    }
                    else
                    {
                        intrinsicWidth = 80;
                    }
                }
                if (nodeTag == "TEXTAREA") { intrinsicHeight = 40; intrinsicWidth = 150; }
                // METER element: displays a scalar value within a known range
                if (nodeTag == "METER")
                {
                    intrinsicHeight = 16;
                    intrinsicWidth = 150;
                }
                // PROGRESS element: displays indicator showing completion progress
                if (nodeTag == "PROGRESS")
                {
                    intrinsicHeight = 16;
                    intrinsicWidth = 150;
                }
                if (nodeTag == "SVG")
                {
                    // HTML5 spec: Default SVG size is 300x150
                    intrinsicWidth = 300;
                    intrinsicHeight = 150;
                    
                    if (node.Attr != null)
                    {
                        // First try explicit width/height attributes
                        if (node.Attr.TryGetValue("width", out var wAttr))
                        {
                            // Handle "100px", "100%", or "100"
                            string wStr = wAttr.Replace("px", "").Trim();
                            if (!wStr.Contains("%") && float.TryParse(wStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && !float.IsNaN(w))
                                intrinsicWidth = w;
                        }
                        if (node.Attr.TryGetValue("height", out var hAttr))
                        {
                            string hStr = hAttr.Replace("px", "").Trim();
                            if (!hStr.Contains("%") && float.TryParse(hStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && !float.IsNaN(h))
                                intrinsicHeight = h;
                        }
                        
                        // If no explicit size, try viewBox for intrinsic dimensions
                        if ((intrinsicWidth == 300 || intrinsicHeight == 150) && node.Attr.TryGetValue("viewBox", out var viewBox))
                        {
                            var vbParts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (vbParts.Length >= 4)
                            {
                                if (float.TryParse(vbParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbW) && !float.IsNaN(vbW) && vbW > 0)
                                    intrinsicWidth = vbW;
                                if (float.TryParse(vbParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbH) && !float.IsNaN(vbH) && vbH > 0)
                                    intrinsicHeight = vbH;
                            }
                        }
                    }
                    
                    // NaN protection
                    if (float.IsNaN(intrinsicWidth) || float.IsInfinity(intrinsicWidth)) intrinsicWidth = 300;
                    if (float.IsNaN(intrinsicHeight) || float.IsInfinity(intrinsicHeight)) intrinsicHeight = 150;
                }
                if (nodeTag == "IMG") 
                {
                    intrinsicHeight = 50; 
                    intrinsicWidth = 50; 
                    
                    // Attempt to fetch actual size from cache
                    string src = node.Attr?.ContainsKey("src") == true ? node.Attr["src"] : null;
                    string originalSrc = src; // Keep original for logging
                    if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(_baseUrl))
                    {
                         try 
                         {
                             // Simple resolution (mirrors DrawLayout logic)
                             if (!src.StartsWith("http") && !src.StartsWith("data:"))
                             {
                                 var baseUri = new Uri(_baseUrl);
                                 var resolved = new Uri(baseUri, src);
                                 src = resolved.AbsoluteUri;
                                 
                                 // Debug: Log relative URL resolution
                                 if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                     $"[SkiaRenderer] IMG URL resolved: '{originalSrc}' + base '{_baseUrl}' => '{src}'\r\n"); } catch {} }                             }
                             
                             // Check cache first
                             if (_intrinsicSizeCache.TryGetValue(src, out var cachedSize))
                             {
                                 intrinsicWidth = cachedSize.width;
                                 intrinsicHeight = cachedSize.height;
                             }
                             else
                             {
                                 var bmp = ImageLoader.GetImage(src);
                                 if (bmp != null)
                                 {
                                     intrinsicWidth = bmp.Width;
                                     intrinsicHeight = bmp.Height;
                                     // Store in cache for future use
                                     _intrinsicSizeCache[src] = (intrinsicWidth, intrinsicHeight);
                                 }
                             }
                         }
                         catch (Exception ex)
                         {
                             FenLogger.Error($"[ComputeLayout] Failed to load/decode image: {src} - {ex.Message}", LogCategory.Rendering);
                         }
                    }
                }
                
                if (intrinsicHeight > 0 && intrinsicWidth > 0)
                    aspectRatio = intrinsicWidth / intrinsicHeight;
            }

            // Determine Content Width
            // If explicit width, use it.
            // If replaced element and auto width, use intrinsic.
            // otherwise use available (block) or 0 (inline?) - wait block uses available.
            
            if (!hasExplicitWidth)
            {
                if (isReplaced)
                {
                    // FIX: Replaced elements default to intrinsic width.
                    // If height is explicit and we have aspect ratio, derive width from height.
                    if (style?.Height.HasValue == true && aspectRatio > 0)
                    {
                        contentWidth = (float)style.Height.Value * aspectRatio;
                    }
                    else
                    {
                        // Otherwise use intrinsic or 0
                        contentWidth = (intrinsicWidth > 0) ? intrinsicWidth : 0;
                    }
                    
                    if (contentWidth > availableWidth) contentWidth = availableWidth;
                    
                    FenLogger.Debug($"[ComputeLayout] Replaced element {nodeTag}: intrinsicWidth={intrinsicWidth} contentWidth={contentWidth} availableWidth={availableWidth}", LogCategory.Layout);
                    
                    // FIX: Rebuild boxes with the corrected intrinsic width
                    // Without this, MarginBox stays at availableWidth (full screen width)
                    float totalWidth = marginLeft + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight + marginRight;
                    box.MarginBox = new SKRect(x, y, x + totalWidth, y);
                    box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight, currentY);
                    box.PaddingBox = new SKRect(
                        box.BorderBox.Left + borderLeft,
                        box.BorderBox.Top + (float)box.Border.Top,
                        box.BorderBox.Right - borderRight,
                        box.BorderBox.Top + (float)box.Border.Top);
                    box.ContentBox = new SKRect(
                        box.PaddingBox.Left + paddingLeft,
                        box.PaddingBox.Top + (float)box.Padding.Top,
                        box.PaddingBox.Right - paddingRight,
                        box.PaddingBox.Top + (float)box.Padding.Top);
                }
                else if (display == "inline" || display == "inline-block")
                {
                     // Inline elements should shrink to content, not take full available width
                     // We start with available width but will shrink after measuring children
                     // The key is that contentWidth is a MAX constraint, not the actual width
                     // After layout, we use maxChildWidth to determine actual size
                }
            }
            
            float contentHeight = 0;
            float maxChildWidth = 0;

            // Calculate container height for flex-grow (only for explicit heights, not percentage)
            float flexContainerHeight = 0;
            if (!string.IsNullOrEmpty(style?.HeightExpression))
            {
                // For function expressions, we resolve using availableHeight or viewport fallback
                // This mimics the logic used in ComputeLayoutInternal
                float parentH = availableHeight > 0 ? availableHeight : _viewportHeight;
                flexContainerHeight = EvaluateCssExpression(style.HeightExpression, parentH);
            }
            else if (style?.Height.HasValue == true)
            {
                flexContainerHeight = (float)style.Height.Value;
            }
            // For height:%, use availableHeight if passed from parent, otherwise viewport height for root flex containers
            else if (style?.HeightPercent.HasValue == true)
            {
                float percentValue = (float)style.HeightPercent.Value;
                
                // Use availableHeight if provided (passed from parent/root)
                // This enables proper height:100% resolution throughout the tree
                if (availableHeight > 0)
                {
                    flexContainerHeight = availableHeight * (percentValue / 100f);
                }
                // Fallback to viewport height for root-level flex containers
                else if (nodeTag == "HTML" || nodeTag == "BODY" || 
                         (node.Attr != null && node.Attr.TryGetValue("class", out var classAttr) && classAttr.Contains("L3eUgb")))
                {
                    flexContainerHeight = _viewportHeight * (percentValue / 100f);
                }
            }

            // GOOGLE DEEP DIVE: Probe for Main Wrapper (L3eUgb)
            // Reverted compat patch. Watching natural state.
            if (node.Attr != null && node.Attr.TryGetValue("class", out var l3Class) && l3Class.Contains("L3eUgb"))
            {
                 var sb = new System.Text.StringBuilder();
                 if (style != null && style.Map != null)
                 {
                     foreach(var kvp in style.Map) sb.Append(kvp.Key + "=" + kvp.Value + "; ");
                 }
                 try {
                     System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                        $"[DeepDive] L3eUgb Natural: Height={style?.Height} MinHeight={style?.MinHeight} HeightPercent={style?.HeightPercent} Display={style?.Display} FlexDir={style?.FlexDirection} Map={sb}\r\n");
                 } catch {}
            }
            
            // DEBUG: Log styling for L3eUgb
            if (node.Attr != null && node.Attr.TryGetValue("class", out var dbgC) && dbgC.Contains("L3eUgb"))
            {
                 var sb = new System.Text.StringBuilder();
                 if (style != null && style.Map != null)
                 {
                     foreach(var kvp in style.Map) sb.Append(kvp.Key + "=" + kvp.Value + "; ");
                 }
                 try {
                     System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                        $"[L3eUgb_Style] Height={style?.Height} MinHeight={style?.MinHeight} HeightPercent={style?.HeightPercent} Map={sb.ToString()}\r\n");
                 } catch {}
            }
            
            // FIX: Apply MinHeight to available flex container height.
            // This is CRITICAL for 'min-height: 100vh' + 'justify-content: center' to work.
            // Without this, the container is treated as 'auto' height during layout (0 or content-height),
            // so there is no "extra space" to distribute for centering.
            if (style?.MinHeight.HasValue == true)
            {
                float minH = (float)style.MinHeight.Value;
                if (flexContainerHeight < minH) flexContainerHeight = minH;
            }

            // For inline/inline-block without explicit width, shrink to content
            // ALSO shrink when shrinkToContent is true (used by flex layout for flex items)
            bool shouldShrinkToContent = shrinkToContent || display == "inline" || display == "inline-block";

            // DEBUG: Log flex detection for key containers
            string debugClass = null;
            node.Attr?.TryGetValue("class", out debugClass);
            
            // TARGETED DEBUG: Probe Header Link "My browser" and Main Text "Chrome 143"
            // To diagnose Issue #2 (Invisible Text) and #3 (Wrong Color/Font)
            string traceTxt = node.IsText ? node.Text.Trim() : "";
            if (traceTxt.Contains("My browser") || traceTxt.Contains("Chrome 143"))
            {
                // Trace computed style properties using correct ForegroundColor property
                var c = style?.ForegroundColor;
                var ff = style?.FontFamilyName; // Use Name string
                var fs = style?.FontSize;
                lock(_logLock) { try {
                    System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                    $"[ContentProbe] Text='{traceTxt}' Tag={node.Tag} Parent={node.Parent?.Tag} Color={c} Font={ff} Size={fs} Display={style?.Display}\r\n");
                } catch {}}
            }

            if (display == "flex" || display == "inline-flex" || 
                nodeTag == "FORM" || nodeTag == "BUTTON" ||
                (debugClass != null && (debugClass.Contains("FPdoL") || debugClass.Contains("aajZC") || debugClass.Contains("lJ9F") || debugClass.Contains("button"))))
            {
                FenLogger.Debug($"[FlexDetect] {nodeTag} class='{debugClass}' display='{display}' flex-dir='{style?.FlexDirection}'", LogCategory.Layout);
            }

            if (display == "flex" || display == "inline-flex")
            {
                // FIX: Clamp ContentBox width for nested Flex containers.
                // If this element is shrinkToContent with auto width, box.ContentBox might have infinite width.
                // Use availableWidth (which comes from the parent's actual width) to constrain.
                SKRect flexContentBox = box.ContentBox;
                if (float.IsInfinity(flexContentBox.Width) || flexContentBox.Width <= 0)
                {
                    // Use availableWidth minus this element's padding/border as the effective width
                    float effectiveWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
                    if (effectiveWidth > 0 && !float.IsInfinity(effectiveWidth))
                    {
                        flexContentBox = new SKRect(flexContentBox.Left, flexContentBox.Top, flexContentBox.Left + effectiveWidth, flexContentBox.Bottom);
                    }
                }
                
                contentHeight = ComputeFlexLayout(node, flexContentBox, style, out maxChildWidth, shrinkToContent, flexContainerHeight);
            }
            else if (display == "grid" || display == "inline-grid")
            {
                contentHeight = ComputeGridLayout(node, box.ContentBox, style, out maxChildWidth);
            }
            else if (display == "table" || node.Tag?.ToUpperInvariant() == "TABLE")
            {
                contentHeight = ComputeTableLayout(node, box.ContentBox, style, out maxChildWidth);
            }
            else
            {
                // Pass the potentially constrained contentWidth (e.g. for IMG resizing)
                // If IMG, it has no children, so this returns 0 height usually.
                
                // DEBUG: Log when CENTER is about to enter ComputeBlockLayout
                if (nodeTag == "CENTER")
                {
                    FenLogger.Debug($"[ComputeLayout] CENTER about to call ComputeBlockLayout, display={display}", LogCategory.Layout);
                }
                
                contentHeight = ComputeBlockLayout(node, box.ContentBox, contentWidth, out maxChildWidth, shrinkToContent: shouldShrinkToContent);
            }
            
            // shouldShrinkToContent calculated above
            if (!hasExplicitWidth && !isReplaced && shouldShrinkToContent)
            {
                
                // FIX: Allow expansion if contentWidth started at 0 (from our infinite fallback)
                if (maxChildWidth > 0)
                {
                    contentWidth = maxChildWidth;
                    
                    // Rebuild boxes with new width
                    float totalWidth = marginLeft + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight + marginRight;
                    box.MarginBox = new SKRect(x, y, x + totalWidth, y);
                    box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight, currentY);
                    box.PaddingBox = new SKRect(
                        box.BorderBox.Left + borderLeft,
                        box.BorderBox.Top + (float)box.Border.Top,
                        box.BorderBox.Right - borderRight,
                        box.BorderBox.Top + (float)box.Border.Top);
                    box.ContentBox = new SKRect(
                        box.PaddingBox.Left + paddingLeft,
                        box.PaddingBox.Top + (float)box.Padding.Top,
                        box.PaddingBox.Right - paddingRight,
                        box.PaddingBox.Top + (float)box.Padding.Top);
                }
            }

            // Fix Height for Replaced Elements with Aspect Ratio preservation
            if (isReplaced)
            {
                // For form elements (INPUT, TEXTAREA, SELECT), use intrinsic height 
                // unless an explicit PIXEL height is set in CSS
                // Percentage heights should NOT stretch these elements to container height
                bool isFormElement = nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT";
                
                // If CSS didn't set an explicit pixel height
                if (!style?.Height.HasValue == true)
                {
                    if (isFormElement && intrinsicHeight > 0)
                    {
                        // Form elements use their intrinsic height regardless of percentage heights
                        contentHeight = intrinsicHeight;
                    }
                    // If we have an aspect ratio and a determinstic width (either explicit or intrinsic limited by max)
                    else if (aspectRatio > 0 && contentWidth > 0)
                    {
                        contentHeight = contentWidth / aspectRatio;
                    }
                    else if (intrinsicHeight > 0)
                    {
                        contentHeight = intrinsicHeight;
                    }
                }
                
                // Update Max Child Width reported
                 if (maxChildWidth < contentWidth) maxChildWidth = contentWidth;
                 
                 // Replaced elements default baseline is the bottom margin edge (or content bottom)
                 // For now, let's say Ascent = Height, Descent = 0 (sits on baseline)
                 box.Ascent = contentHeight + (float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom; 
                 box.Descent = 0;
            }

            // TEXT CONTENT
            if (node.IsText)
            {
                // Get white-space and word-wrap from parent
                var textParent = node;
                _parents.TryGetValue(node, out textParent);
                CssComputed parentStyle = null;
                if (textParent != null && _styles != null) _styles.TryGetValue(textParent, out parentStyle);
                
                string whiteSpace = parentStyle?.WhiteSpace?.ToLowerInvariant() ?? "normal";
                bool shouldWrap = whiteSpace != "nowrap" && whiteSpace != "pre";
                
                // Measure text with proper wrapping
                using (var paint = new SKPaint())
                {
                    float fontSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize;
                    paint.TextSize = fontSize;
                    
                    var text = node.Text ?? "";
                    float textHeight = 0;
                    float maxTextWidth = 0;
                    
                    // Get line-height
                    float lineHeight = fontSize * DefaultLineHeightMultiplier;
                    if (style?.LineHeight.HasValue == true)
                    {
                        var lh = style.LineHeight.Value;
                        if (lh > 3) lineHeight = (float)lh; // Pixel value
                        else lineHeight = fontSize * (float)lh; // Multiplier
                    }
                    else if (parentStyle?.LineHeight.HasValue == true)
                    {
                        var lh = parentStyle.LineHeight.Value;
                        if (lh > 3) lineHeight = (float)lh;
                        else lineHeight = fontSize * (float)lh;
                    }
                    
                    try
                    {
                        string ff = style?.FontFamily?.ToString() ?? parentStyle?.FontFamily?.ToString();
                        paint.Typeface = ResolveTypeface(ff, text);
                        
                        if (shouldWrap && contentWidth > 0)
                        {
                            // Word wrap the text with hyphens support
                            string hyphens = parentStyle?.Hyphens?.ToLowerInvariant() ?? "none";
                            var lines = WrapText(text, paint, contentWidth, whiteSpace, hyphens);
                            _textLines[node] = lines;
                            
                            foreach (var line in lines)
                            {
                                textHeight += lineHeight;
                                if (line.Width > maxTextWidth) maxTextWidth = line.Width;
                            }
                            
                            if (lines.Count == 0) textHeight = lineHeight;
                        }
                        else
                        {
                            // No wrapping - single line
                            using (var shaper = new SKShaper(paint.Typeface))
                            {
                                var result = shaper.Shape(text, paint);
                                maxTextWidth = result.Width;
                                var metrics = paint.FontMetrics;
                                textHeight = lineHeight;
                                
                                // Calculate baseline metrics with leading
                                float fontAscent = -metrics.Ascent; // Make positive
                                float fontDescent = metrics.Descent;
                                float fontHeight = fontAscent + fontDescent;
                                float halfLeading = (lineHeight - fontHeight) / 2;
                                
                                box.Ascent = fontAscent + halfLeading;
                                box.Descent = fontDescent + halfLeading;
                            }
                            
                            _textLines[node] = new List<TextLine> { new TextLine { Text = text, Width = maxTextWidth, Y = 0 } };
                        }
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error($"HarfBuzz Measure Error: {ex.Message}", LogCategory.Rendering);
                        
                        // Fallback measurement
                        var bounds = new SKRect();
                        paint.MeasureText(text, ref bounds);
                        maxTextWidth = bounds.Width;
                        textHeight = lineHeight;
                        
                        // Fallback metrics
                        var m = paint.FontMetrics;
                        float fa = -m.Ascent;
                        float fd = m.Descent;
                        float fl = (lineHeight - (fa + fd)) / 2;
                        box.Ascent = fa + fl;
                        box.Descent = fd + fl;
                        
                        _textLines[node] = new List<TextLine> { new TextLine { Text = text, Width = maxTextWidth, Y = 0 } };
                    }

                    textHeight += 5; // Add buffer 
                    contentHeight += textHeight;
                    
                    // Always track text width for inline shrinking
                    if (maxTextWidth > maxChildWidth)
                        maxChildWidth = maxTextWidth + 2;
                    
                    // If text width > contentWidth, expand
                    if (shrinkToContent && !hasExplicitWidth)
                    {
                        if (maxTextWidth > contentWidth) 
                        {
                            contentWidth = maxTextWidth + 2;
                            
                            box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
                            box.PaddingBox = new SKRect(
                                box.BorderBox.Left + borderLeft, 
                                box.BorderBox.Top + (float)box.Border.Top,
                                box.BorderBox.Right - borderRight, 
                                box.BorderBox.Top + (float)box.Border.Top);
                        
                            box.ContentBox = new SKRect(
                                box.PaddingBox.Left + paddingLeft,
                                box.PaddingBox.Top + (float)box.Padding.Top,
                                box.PaddingBox.Right - paddingRight,
                                box.PaddingBox.Top + (float)box.Padding.Top);
                        }
                    }
                    
                    box.LineHeight = lineHeight;
                    
                    // CRITICAL FIX: Shrink text node box to actual text width
                    // This is essential for inline element width calculation
                    if (shrinkToContent && maxTextWidth > 0)
                    {
                        
                        contentWidth = maxTextWidth + 2; // Small padding
                        
                        // Update boxes to match shrunk width
                        box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
                        box.PaddingBox = new SKRect(
                            box.BorderBox.Left + borderLeft, 
                            box.BorderBox.Top + (float)box.Border.Top,
                            box.BorderBox.Right - borderRight, 
                            box.BorderBox.Top + (float)box.Border.Top);
                        box.ContentBox = new SKRect(
                            box.PaddingBox.Left + paddingLeft,
                            box.PaddingBox.Top + (float)box.Padding.Top,
                            box.PaddingBox.Right - paddingRight,
                            box.PaddingBox.Top + (float)box.Padding.Top);
                        box.MarginBox = new SKRect(x, y, x + borderLeft + contentWidth + borderRight, y);
                    }
                }
            }

            // Finalize Heights
            if (!string.IsNullOrEmpty(style?.HeightExpression))
            {
                float evalH = EvaluateCssExpression(style.HeightExpression, availableHeight > 0 ? availableHeight : _viewportHeight);
                if (evalH >= 0)
                {
                    if (isBorderBox)
                         contentHeight = evalH - ((float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom);
                    else
                         contentHeight = evalH;
                    
                    if (contentHeight < 0) contentHeight = 0;
                }
            }
            else if (style?.Height.HasValue == true)
            {
                float declaredHeight = (float)style.Height.Value;
                if (isBorderBox)
                {
                    // Height includes padding + border, so we subtract them
                    contentHeight = declaredHeight - ((float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom);
                }
                else
                {
                    contentHeight = declaredHeight;
                }
                if (contentHeight < 0) contentHeight = 0;
            }
            // Handle height: 100% (or other percentages)
            // This is crucial for layout - percentage heights resolve against containing block's height
            else if (style?.HeightPercent.HasValue == true)
            {
                float percentValue = (float)style.HeightPercent.Value;
                float baseHeight = 0;
                
                // PRIORITY 1 FIX: Root elements (html, body) resolve against viewport height
                if (nodeTag == "HTML" || nodeTag == "BODY")
                {
                    baseHeight = _viewportHeight;
                    FenLogger.Debug($"[ComputeLayout] Root element {nodeTag} using viewport height: {baseHeight}", LogCategory.Layout);
                }
                else if (availableHeight > 0)
                {
                    // For other elements, use availableHeight passed from parent
                    baseHeight = availableHeight;
                }
                else
                {
                    // Fallback: Try to get parent's computed height for percentage resolution
                    LiteElement parent = _parents.ContainsKey(node) ? _parents[node] : null;
                    if (parent != null && _boxes.TryGetValue(parent, out var parentBox))
                    {
                        // Use parent's content box height if available
                        if (parentBox.ContentBox.Height > 0)
                        {
                            baseHeight = parentBox.ContentBox.Height;
                        }
                    }
                }
                
                if (baseHeight > 0)
                {
                    float resolvedHeight = baseHeight * (percentValue / 100f);
                    
                    if (isBorderBox)
                    {
                        // Border-box: resolved height includes padding and border
                        contentHeight = resolvedHeight - ((float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom);
                    }
                    else
                    {
                        // Content-box: resolved height IS the content height
                        contentHeight = resolvedHeight;
                    }
                    
                    if (contentHeight < 0) contentHeight = 0;
                    
                    FenLogger.Debug($"[ComputeLayout] Applied Height %: tag={nodeTag} percent={percentValue} base={baseHeight} result={contentHeight}", LogCategory.Layout);
                }
            }
            
            // Apply Min/Max Height Expressions
            if (!string.IsNullOrEmpty(style?.MinHeightExpression))
            {
                float parentH = availableHeight > 0 ? availableHeight : _viewportHeight;
                float minH = EvaluateCssExpression(style.MinHeightExpression, parentH);
                // Adjust for border-box not needed here as contentHeight logic above handles it? 
                // Wait, min-height CSS property IS usually border-box if box-sizing is set.
                // Our contentHeight variable is CONTENT box.
                if (isBorderBox) minH -= (float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom;
                
                if (contentHeight < minH) contentHeight = minH;
            }
             if (!string.IsNullOrEmpty(style?.MaxHeightExpression))
            {
                float parentH = availableHeight > 0 ? availableHeight : _viewportHeight;
                float maxH = EvaluateCssExpression(style.MaxHeightExpression, parentH);
                if (isBorderBox) maxH -= (float)box.Padding.Top + (float)box.Padding.Bottom + (float)box.Border.Top + (float)box.Border.Bottom;
                
                if (contentHeight > maxH) contentHeight = maxH;
            }

            // Apply min-height constraint
            if (style?.MinHeight.HasValue == true)
            {
                float minHeight = (float)style.MinHeight.Value;
                if (contentHeight < minHeight)
                {
                    contentHeight = minHeight;
                }
            }
            
            // Apply max-height constraint
            if (style?.MaxHeight.HasValue == true)
            {
                float maxHeight = (float)style.MaxHeight.Value;
                if (contentHeight > maxHeight)
                {
                    contentHeight = maxHeight;
                }
            }

            box.ContentBox.Bottom = box.ContentBox.Top + contentHeight;
            box.PaddingBox.Bottom = box.ContentBox.Bottom + (float)box.Padding.Bottom;
            box.BorderBox.Bottom = box.PaddingBox.Bottom + (float)box.Border.Bottom;
            box.MarginBox.Bottom = box.BorderBox.Bottom + (float)box.Margin.Bottom;
            
            // Ensure MarginBox.Right matches BorderBox for inline elements  
            if (display == "inline" || display == "inline-block")
            {
                box.MarginBox.Right = box.BorderBox.Right + marginRight;
            }

            // Debug log for replaced/overlay elements
            if (isReplaced && (nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "BUTTON"))
            {
                FenLogger.Debug($"[ComputeLayout] Final box for {nodeTag}: Left={box.MarginBox.Left} Top={box.MarginBox.Top} Width={box.MarginBox.Width} Height={box.MarginBox.Height} contentWidth={contentWidth} intrinsicWidth={intrinsicWidth}", LogCategory.Layout);
            }



            // Size Probe for "Blue Blocks"
            if (box.MarginBox.Height > 500 && nodeTag != "BODY" && nodeTag != "HTML")
            {
                 string sParams = "";
                 if (style?.Height.HasValue == true) sParams += $"Height={style.Height} ";
                 if (style?.MinHeight.HasValue == true) sParams += $"MinHeight={style.MinHeight} ";
                 if (style?.HeightPercent.HasValue == true) sParams += $"Height%={style.HeightPercent} ";
                 
                 string cL = node.Attr != null && node.Attr.TryGetValue("class", out var clss) ? clss : "";
                 FenLogger.Debug($"[SizeProbe] HUGE ELEMENT: {nodeTag} Class='{cL}' Height={box.MarginBox.Height} Width={box.MarginBox.Width} Params: {sParams}", LogCategory.Layout);
                 
                 if (node.Children != null)
                 {
                     int childCount = 0;
                     foreach(var c in node.Children)
                     {
                         if (childCount++ > 10) break; // Limit log spam
                         CssComputed cStyle = null;
                         if (_styles != null) _styles.TryGetValue(c, out cStyle);
                         string cDisp = cStyle?.Display ?? "null";
                         string cFloat = cStyle?.Float ?? "none";
                         string cTag = c.Tag;
                         string cClass = c.Attr != null && c.Attr.TryGetValue("class", out var cc) ? cc : "";
                         FenLogger.Debug($"   -> Child: {cTag} Class='{cClass}' Display='{cDisp}' Float='{cFloat}'", LogCategory.Layout);
                     }
                 }
            }

            _boxes[node] = box;
        }

        // Float tracking for block layout
        private class FloatRect
        {
            public float Left;
            public float Right;
            public float Top;
            public float Bottom;
            public bool IsLeft; // true = float:left, false = float:right
        }

        private float ComputeBlockLayout(LiteElement node, SKRect contentBox, float availableWidth, out float maxChildWidth, bool shrinkToContent = false)
        {
            float childY = contentBox.Top;
            float startY = childY;
            maxChildWidth = 0;
            float trackedMaxChildWidth = 0;

            // DEBUG: Trace block layout for suspect containers
             string nodeClassForLog = node.Attr != null && node.Attr.TryGetValue("class", out var clsLog) ? clsLog : "";
             if (nodeClassForLog.Contains("LS8OJ") || nodeClassForLog.Contains("rSk4se"))
             {
                lock(_logLock)
                {
                    try {
                        System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                            $"[BlockDebug] {node.Tag} Class='{nodeClassForLog}' AvailableWidth={availableWidth} Shrink={shrinkToContent}\r\n");
                        
                         foreach(var child in node.Children)
                         {
                             // Get child style for logging
                             CssComputed cStyle = null;
                             if (_styles != null) _styles.TryGetValue(child, out cStyle);
                             string cDisp = cStyle?.Display?.ToString() ?? "null";
                             string cWidth = cStyle?.Width?.ToString() ?? "null";
                             
                             string cClass = child.Attr != null && child.Attr.TryGetValue("class", out var cc) ? cc : "";
                             System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                $"   -> BlockChild: {child.Tag} Class='{cClass}' Display={cDisp} Width={cWidth}\r\n");
                         }
                    } catch {}
                }
             }

            // Float tracking
            var leftFloats = new List<FloatRect>();
            var rightFloats = new List<FloatRect>();

            // Get text-align
            CssComputed nodeStyle = null;
            if (_styles != null) _styles.TryGetValue(node, out nodeStyle);
            string textAlign = nodeStyle?.TextAlign?.ToString()?.ToLowerInvariant() ?? "left";
            
            // Helper to get available X range
            Func<float, (float left, float right)> getAvailableRangeAtY = (y) =>
            {
                float left = contentBox.Left;
                float right = contentBox.Right;
                foreach (var f in leftFloats) if (y >= f.Top && y < f.Bottom) left = Math.Max(left, f.Right);
                foreach (var f in rightFloats) if (y >= f.Top && y < f.Bottom) right = Math.Min(right, f.Left);
                return (left, right);
            };
            
            // Helper to clear floats
            Func<string, float> getClearY = (clearType) =>
            {
                float clearY = childY;
                if (clearType == "left" || clearType == "both") foreach (var f in leftFloats) clearY = Math.Max(clearY, f.Bottom);
                if (clearType == "right" || clearType == "both") foreach (var f in rightFloats) clearY = Math.Max(clearY, f.Bottom);
                return clearY;
            };

            if (node.Children != null)
            {
                List<LiteElement> pendingInlines = new List<LiteElement>();
                
                Action flushInlines = () => 
                {
                    if (pendingInlines.Count == 0) return;
                    
                    float usedHeight = ComputeInlineContext(pendingInlines, contentBox, availableWidth, childY, leftFloats, rightFloats, textAlign, out float inlineMaxW);
                    if (inlineMaxW > trackedMaxChildWidth) trackedMaxChildWidth = inlineMaxW;
                    
                    childY += usedHeight;
                    pendingInlines.Clear();
                };

                foreach (var child in node.Children)
                {
                    // Skip hidden inputs
                    if (child.Tag?.ToUpperInvariant() == "INPUT")
                    {
                         string inputType = child.Attr != null && child.Attr.TryGetValue("type", out var t) ? t : "";
                         if (inputType.ToLowerInvariant() == "hidden") continue;
                    }
                    
                    // Style & Display
                    CssComputed childStyle = null;
                    if (_styles != null) _styles.TryGetValue(child, out childStyle);

                    // Position handling (Abs/Fixed -> Remove from flow)
                    string pos = childStyle?.Position?.ToLowerInvariant();
                    if (pos == "absolute" || pos == "fixed")
                    {
                        _deferredAbsoluteElements.Add(child);
                        continue; 
                    }
                    // Sticky is treated as relative for layout purposes (real sticky behavior requires scroll tracking)
                    // This prevents sticky elements from breaking the layout
                    
                    // Clear handling
                    string clearVal = null;
                    if (childStyle?.Map?.TryGetValue("clear", out var cv) == true) clearVal = cv.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(clearVal) && clearVal != "none")
                    {
                        flushInlines();
                        childY = Math.Max(childY, getClearY(clearVal));
                    }
                    
                    // Float handling
                    string floatVal = childStyle?.Float?.ToLowerInvariant();
                    if (floatVal == "left" || floatVal == "right")
                    {
                        // Floats text nodes? Not really standard, but handle as block-float
                        flushInlines();
                        
                        // Measure float
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float floatAvail = rangeRight - rangeLeft;
                        ComputeLayout(child, rangeLeft, childY, floatAvail, shrinkToContent: true);
                        
                        if (_boxes.TryGetValue(child, out var fBox))
                        {
                             float fW = fBox.MarginBox.Width;
                             float fH = fBox.MarginBox.Height;
                             
                             float fX = (floatVal == "left") ? rangeLeft : (rangeRight - fW);
                             var fRect = new FloatRect { Left = fX, Right = fX + fW, Top = childY, Bottom = childY + fH, IsLeft = floatVal == "left" };
                             
                             if (floatVal == "left") leftFloats.Add(fRect); else rightFloats.Add(fRect);
                             
                             // Re-layout at final pos
                             ComputeLayout(child, fX, childY, floatAvail, shrinkToContent: true);
                        }
                        continue;
                    }

                    // Determine Display
                    string d = childStyle?.Display?.ToLowerInvariant();
                    // Text nodes are always inline
                    if (string.IsNullOrEmpty(child.Tag) || child.Tag == "#text") d = "inline";
                    
                    if (string.IsNullOrEmpty(d))
                    {
                        string t = child.Tag?.ToUpperInvariant();
                        // Form elements and inline elements default to inline/inline-block
                        if (t == "IMG" || t=="SPAN" || t == "A" || t=="B" || t=="STRONG" || t=="I" || t=="EM" || t=="SMALL" || t=="LABEL" || t=="CODE" || t=="BR" ||
                            t == "BUTTON" || t == "INPUT" || t == "SELECT" || t == "TEXTAREA") 
                            d = "inline-block";  // Form elements should be inline-block, not just inline
                        else
                            d = "block";
                    }

                    bool isInline = d == "inline" || d == "inline-block" || d == "inline-flex" || d == "inline-grid";

                    if (isInline)
                    {
                        pendingInlines.Add(child);
                    }
                    else // BLOCK
                    {
                        flushInlines();
                        
                        // Layout Block
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float blockW = rangeRight - rangeLeft;
                        
                        // If textAlign is center/right, block children that are inline-blocks need help? 
                        // Actually standard block layout doesn't inherit text-align for alignment OF the block, 
                        // but passes it down.
                        
                        ComputeLayout(child, rangeLeft, childY, blockW, shrinkToContent: shrinkToContent);
                        
                        if (_boxes.TryGetValue(child, out var bBox))
                        {
                            childY += bBox.MarginBox.Height;
                            childY = Math.Max(childY, getClearY("both")); // Clearance
                            if (bBox.MarginBox.Width > trackedMaxChildWidth) trackedMaxChildWidth = bBox.MarginBox.Width;
                        }
                    }
                }
                flushInlines();
                childY = Math.Max(childY, getClearY("both"));
            }

            maxChildWidth = trackedMaxChildWidth;
            return childY - startY;
        }

        private float ComputeInlineContext(
            List<LiteElement> items, 
            SKRect contentBox, 
            float availableWidth, 
            float startY, 
            List<FloatRect> leftFloats, 
            List<FloatRect> rightFloats, 
            string textAlign, 
            out float maxLineWidth)
        {
            maxLineWidth = 0;
            float trackedMaxLineWidth = 0;
            float currentY = startY;
            
            // Helper for Available Range
            Func<float, (float l, float r)> getRange = (y) => {
                float l = contentBox.Left; 
                float r = contentBox.Right;
                foreach (var f in leftFloats) if (y >= f.Top && y < f.Bottom) l = Math.Max(l, f.Right);
                foreach (var f in rightFloats) if (y >= f.Top && y < f.Bottom) r = Math.Min(r, f.Left);
                return (l, r);
            };

            // Current Line State
            var lineItems = new List<(LiteElement e, float w, float h)>();
            float currentLineX = -1; 
            float currentLineH = 0;
            float rangeLeft = 0, rangeRight = 0;

            Action startLine = () => {
                (rangeLeft, rangeRight) = getRange(currentY);
                currentLineX = rangeLeft;
                currentLineH = 0;
                lineItems.Clear();
            };
            
            Action flushLine = () => {
                if (lineItems.Count == 0) return;
                
                // 1. Calculate Line Metrics (Max Ascent, Max Descent)
                float lineAscent = 0;
                float lineDescent = 0;

                // Calculate strut height from inherited line-height (minimum line-box height)
                // The strut is an invisible inline box at the start of each line
                // with zero width but with the same ascent, descent, and line height
                // as the containing block's font properties
                float strutHeight = 0;
                float strutAscent = 0;
                float strutDescent = 0;
                
                if (lineItems.Count > 0)
                {
                    // Get parent's line-height to compute strut
                    var firstItem = lineItems[0].e;
                    if (_styles != null && firstItem.Parent != null && 
                        _styles.TryGetValue(firstItem.Parent, out var parentStyle))
                    {
                        float inheritedFontSize = (float)(parentStyle.FontSize ?? 16.0);
                        float inheritedLineHeight = (float)(parentStyle.LineHeight ?? (inheritedFontSize * 1.2));
                        
                        // Strut splits line-height evenly between ascent and descent
                        // Based on em-box with half-leading distribution
                        strutAscent = inheritedFontSize * 0.8f; // Approximate ascent ratio
                        strutDescent = inheritedFontSize * 0.2f; // Approximate descent ratio
                        strutHeight = (float)Math.Max(inheritedLineHeight, strutAscent + strutDescent);
                        
                        // Initialize line metrics with strut
                        lineAscent = strutAscent;
                        lineDescent = (float)Math.Max(strutDescent, strutHeight - strutAscent);
                    }
                }

                foreach(var item in lineItems)
                {
                    if (_boxes.TryGetValue(item.e, out var box))
                    {
                        if (box.Ascent > lineAscent) lineAscent = box.Ascent;
                        if (box.Descent > lineDescent) lineDescent = box.Descent;
                    } 
                    else 
                    {
                        // Fallback
                        if (item.h > lineAscent) lineAscent = item.h;
                    }
                }

                // Line height is sum of max ascent and max descent
                float lineHeight = lineAscent + lineDescent;
                
                // Enforce strut: line height cannot be less than strut height
                if (lineHeight < strutHeight) lineHeight = strutHeight;
                
                // Fallback: if calculated height is less than tracked max height (e.g. erratic metrics), take max height
                if (lineHeight < currentLineH) lineHeight = currentLineH;

                // Horizontal Alignment
                float lineWidth = currentLineX - rangeLeft;
                if (lineWidth > trackedMaxLineWidth) trackedMaxLineWidth = lineWidth;
                
                float offsetX = 0;
                if (textAlign == "center") offsetX = (rangeRight - rangeLeft - lineWidth) / 2;
                else if (textAlign == "right") offsetX = (rangeRight - rangeLeft - lineWidth);
                
                // Vertical Alignment & Shift
                foreach(var item in lineItems)
                {
                    // Get styles for vertical-align
                    string vAlign = "baseline";
                    if (_styles != null && _styles.TryGetValue(item.e, out var s) && s?.VerticalAlign != null) 
                        vAlign = s.VerticalAlign.ToLowerInvariant();
                        
                    float vShift = 0;
                    
                    if (_boxes.TryGetValue(item.e, out var box))
                    {
                        if (vAlign == "top")
                        {
                            vShift = 0; 
                        }
                        else if (vAlign == "bottom")
                        {
                            vShift = lineHeight - box.MarginBox.Height;
                        }
                        else if (vAlign == "middle")
                        {
                            vShift = (lineHeight - box.MarginBox.Height) / 2;
                        }
                        else // baseline (default)
                        {
                            // Align item baseline to line baseline
                            // Line baseline is at y = lineAscent (relative to line top)
                            // Item baseline is at y = box.Ascent (relative to item top)
                            // Shift needed: lineAscent - box.Ascent
                            vShift = lineAscent - box.Ascent;
                        }
                    }
                    else
                    {
                        // Fallback for missing box
                        vShift = lineHeight - item.h;
                    }
                    
                    ShiftTree(item.e, offsetX, vShift); 
                }
                
                currentY += lineHeight;
                startLine(); // Prep next line
            };

            startLine();

            foreach (var item in items)
            {
                // Verify if it fits
                // Measure item first
                // Use infinite/large width first to measure intrinsic size, OR use available line width?
                // Inline elements want to wrap if they contain text. But here `item` is a LiteElement.
                // If `item` is a text node, it has been wrapped by ComputeLayout? No, ComputeLayout processes text nodes.
                // If `item` is a container (SPAN), ComputeLayout lays it out.
                
                // Current approach: Ask ComputeLayout to layout item at current position.
                // Does ComputeLayout handle wrapping of text nodes internally? Yes (lines 800+).
                // Does it handle wrapping of nested inline children? Yes via recursion.
                
                float avail = rangeRight - currentLineX;
                ComputeLayout(item, currentLineX, currentY, avail > 0 ? avail : 0, shrinkToContent: true); 
                
                if (_boxes.TryGetValue(item, out var box))
                {
                    float w = box.MarginBox.Width;
                    float h = box.MarginBox.Height;
                    
                    // Check wrap
                    // A crude wrap check: if it overflowed the line available width SIGNIFICANTLY
                    // But ComputeLayout limited itself to `avail`. 
                    // If we gave it tight constraint, it might have grown vertically (text wrap).
                    // If it's atomic (img, inline-block) and didn't fit, it might have overflowed?
                    // We need to re-measure on new line if it doesn't fit and we aren't at start.
                    
                    bool doesFit = (currentLineX + w <= rangeRight + 1); // tolerance
                    bool isAtomic = item.Tag != null && item.Tag != "#text" && item.Tag != "SPAN" && item.Tag != "A" && item.Tag != "B" && item.Tag != "I";
                    
                    // If it's a BR tag, force break
                    if (item.Tag?.ToUpperInvariant() == "BR")
                    {
                        flushLine();
                        continue;
                    }
                    
                    if (!doesFit && lineItems.Count > 0)
                    {
                        // Needs wrap
                        flushLine();
                        
                        // Re-measure at new line
                        (rangeLeft, rangeRight) = getRange(currentY);
                        currentLineX = rangeLeft;
                        avail = rangeRight - rangeLeft;
                        
                        ComputeLayout(item, currentLineX, currentY, avail, shrinkToContent: true);
                        if (_boxes.TryGetValue(item, out box))
                        {
                            w = box.MarginBox.Width;
                            h = box.MarginBox.Height;
                        }
                    }
                    
                    lineItems.Add((item, w, h));
                    currentLineX += w;
                    if (h > currentLineH) currentLineH = h;
                }
            }
            flushLine();
            
            maxLineWidth = trackedMaxLineWidth;
            return currentY - startY;
        }


        private float ComputeFlexLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth, bool shrinkToContent = false, float containerHeight = 0)
        {
            string dir = style?.FlexDirection?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(dir)) dir = "row";
            
            bool isRow = dir.Contains("row");
            bool isReverse = dir.Contains("reverse");
            string justifyContent = style?.JustifyContent?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(justifyContent)) justifyContent = "flex-start";
            
            string alignItems = style?.AlignItems?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(alignItems)) alignItems = "stretch";
            
            string alignContent = style?.AlignContent?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(alignContent)) alignContent = "stretch";
            
            string flexWrap = style?.FlexWrap?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(flexWrap)) flexWrap = "nowrap";

            bool shouldWrap = flexWrap == "wrap" || flexWrap == "wrap-reverse";
            bool wrapReverse = flexWrap == "wrap-reverse";
            
            // DEBUG: Log flex properties for key container elements
            string nodeTag = node.Tag?.ToUpperInvariant();
            string nodeClass = node.Attr != null && node.Attr.TryGetValue("class", out var nc) ? nc : "";
            if (nodeTag == "FORM" || nodeTag == "MAIN" || nodeTag == "HEADER" || nodeTag == "NAV" || 
                nodeClass.Contains("container") || nodeClass.Contains("wrapper") || nodeClass.Contains("layout"))
            {
                FenLogger.Debug($"[FlexLayout] Processing {nodeTag} class='{nodeClass}' " +
                    $"dir={dir} justify={justifyContent} align={alignItems} wrap={flexWrap} " +
                    $"width={contentBox.Width} height={contentBox.Height}", LogCategory.Layout);
            }
            if (nodeTag == "CENTER")
            {
                FenLogger.Debug($"[ComputeFlexLayout] CENTER: justifyContent={justifyContent} alignItems={alignItems} dir={dir} wrap={flexWrap}", LogCategory.Layout);
            }
            
            // DEBUG: Trace specific Google containers (Class OR ID)
            string nodeClassForLog = node.Attr != null && node.Attr.TryGetValue("class", out var clsLog) ? clsLog : "";
            string nodeIdForLog = node.Attr != null && node.Attr.TryGetValue("id", out var idLog) ? idLog : "";
            
            // Thread-safe logging
            lock (_logLock) 
            {
                bool isTarget = nodeClassForLog.Contains("L3eUgb") || nodeClassForLog.Contains("SIvCob") || nodeClassForLog.Contains("KxwPGc") || nodeIdForLog.Contains("SIvCob") || nodeClassForLog.Contains("LS8OJ") || nodeClassForLog.Contains("rSk4se") || nodeClassForLog.Contains("o3j99") || nodeClassForLog.Contains("n1xJcf") || nodeClassForLog.Contains("SSwjIe") || nodeClassForLog.Contains("iTjxkf");
                
                if (isTarget)
                {
                   try {
                       System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                           $"[FlexDebug] {nodeTag} Class='{nodeClassForLog}' ID='{nodeIdForLog}' Width={contentBox.Width} Height={contentBox.Height} FlexDir={dir} Wrap={flexWrap} Align={alignItems} Justify={justifyContent}\r\n");

                       // Log children hierarchy
                       if (nodeClassForLog.Contains("L3eUgb") || nodeClassForLog.Contains("LS8OJ") || nodeClassForLog.Contains("rSk4se") || nodeClassForLog.Contains("o3j99") || nodeClassForLog.Contains("n1xJcf") || nodeClassForLog.Contains("SSwjIe") || nodeClassForLog.Contains("iTjxkf"))
                       {
                            foreach(var child in node.Children)
                            {
                                 string cClass = child.Attr != null && child.Attr.TryGetValue("class", out var cc) ? cc : "";
                                 string cId = child.Attr != null && child.Attr.TryGetValue("id", out var ci) ? ci : "";
                                 
                                 // For SSwjIe, also log measured child width
                                 float cW = 0;
                                 if (nodeClassForLog.Contains("SSwjIe") && _boxes.TryGetValue(child, out var cBox))
                                 {
                                     cW = cBox.MarginBox.Width;
                                 }
                                 System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                    $"   -> Child: {child.Tag} Class='{cClass}' ID='{cId}' MeasuredWidth={cW}\r\n");
                            }
                       }

                   } catch {}
                }
            }

            float gapValue = 0;
            float rowGap = 0;
            if (style?.Gap.HasValue == true) 
            {
                gapValue = (float)style.Gap.Value;
                rowGap = gapValue;
            }
            if (style?.ColumnGap.HasValue == true) gapValue = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;
            else if (style?.Map != null)
            {
                string gapStr = null;
                if (style.Map.TryGetValue("gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    if (float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
                    {
                        gapValue = g;
                        rowGap = g;
                    }
                }
                if (isRow && style.Map.TryGetValue("column-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out gapValue);
                }
                if (style.Map.TryGetValue("row-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rowGap);
                }
            }
            
            maxChildWidth = 0;
            
            float cursorX = contentBox.Left;
            float cursorY = contentBox.Top;
            
            if (node.Children == null) return 0;
            
            // Collect items and measure
            List<LiteElement> flexItems = new List<LiteElement>();
            foreach(var c in node.Children)
            {
                 // SKIP WHITESPACE TEXT NODES
                 if (c.IsText && string.IsNullOrWhiteSpace(c.Text)) continue;

                 CssComputed cStyle = null;
                 if (_styles != null) _styles.TryGetValue(c, out cStyle);
                 string cPos = cStyle?.Position?.ToLowerInvariant();
                 if (cPos == "absolute" || cPos == "fixed")
                 {
                     // Defer absolute/fixed elements to second pass for proper positioned ancestor lookup
                     _deferredAbsoluteElements.Add(c);
                 }
                 else
                 {
                     flexItems.Add(c);
                 }
            }

            if (flexItems.Count == 0) return 0;

            // Sort flex items by CSS 'order' property (default is 0)
            flexItems = flexItems.OrderBy(c =>
            {
                CssComputed cStyle = null;
                if (_styles != null) _styles.TryGetValue(c, out cStyle);
                return cStyle?.Order ?? 0;
            }).ToList();

            if (isRow)
            {
                // Flex row layout with wrapping support + flex-grow/flex-shrink
                var lines = new List<List<(LiteElement element, float width, float height, float grow, float shrink, float basis, float measuredWidth)>>();
                var currentLine = new List<(LiteElement element, float width, float height, float grow, float shrink, float basis, float measuredWidth)>();
                float currentLineWidth = 0;
                
                // First pass: measure all items and organize into lines
                foreach (var child in flexItems)
                {
                    // Get flex properties
                    CssComputed childStyle = null;
                    if (_styles != null) _styles.TryGetValue(child, out childStyle);
                    float grow = (float)(childStyle?.FlexGrow ?? 0);
                    float shrink = (float)(childStyle?.FlexShrink ?? 1); // Default is 1
                    float basis = (float)(childStyle?.FlexBasis ?? -1); // -1 means auto
                    
                    // Measure child
                    // FIX: Use Infinite width for measurement to allow shrink-to-fit behavior for 100% width items / auto width items.
                    // This ensures they don't force a wrap prematurely. They will be sized up by flex-grow later.
                    // HOWEVER: If the child is ALSO a Flex container, it needs a finite available width to constrain ITS children.
                    // But we still use shrinkToContent=true so the Flex child shrinks to its content, not expands to fill.
                    string childDisplay = childStyle?.Display?.ToLowerInvariant() ?? "";
                    bool childIsFlex = childDisplay == "flex" || childDisplay == "inline-flex";
                    // REVERT: Use Infinity for Max-Content measurement for all items in Row layout.
                    // The "Exploding Block" (width: auto) bug is now handled by the safety check in ComputeLayout.
                    // This allows items to report their true Max-Content width without being artificially constrained.
                    float measureWidth = (isRow) ? float.PositiveInfinity : (contentBox.Width > 0 ? contentBox.Width : 1e6f);
                    
                    // DEBUG: Log childIsFlex detection for KxwPGc children
                    string childClass = child.Attr != null && child.Attr.TryGetValue("class", out var ccDbg) ? ccDbg : "";
                    if (childClass.Contains("KxwPGc") || childClass.Contains("AghGtd") || childClass.Contains("iTjxkf"))
                    {
                        lock(_logLock) { try {
                            System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                $"[ChildFlexDetect] Child={child.Tag} Class='{childClass}' Display='{childDisplay}' childIsFlex={childIsFlex} measureWidth={measureWidth}\r\n");
                        } catch {} }
                    }
                    // DEBUG: Log pHiOh links (language links)
                    if (childClass.Contains("pHiOh"))
                    {
                        lock(_logLock) { try {
                            float actualWidth = 0;
                            if (_boxes.TryGetValue(child, out var childBoxDbg)) actualWidth = childBoxDbg.MarginBox.Width;
                            System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                $"[LinkDebug] Link={child.Tag} Class='{childClass}' Display='{childDisplay}' Width={actualWidth} containerWidth={contentBox.Width}\r\n");
                        } catch {} }
                    }
                    
                    // ALWAYS use shrinkToContent=true for Row Flex measurement so children shrink to content
                    // This allows Row flex-wrap to work correctly (items must shrink to fit, not expand to fill)
                    ComputeLayout(child, 0, 0, measureWidth, shrinkToContent: true);
                    
                    float childWidth = 0, childHeight = 0;
                    if (_boxes.TryGetValue(child, out var childBox))
                    {
                        childWidth = childBox.MarginBox.Width;
                        childHeight = childBox.MarginBox.Height;
                    }
                    
                    // Use flex-basis if set, otherwise use measured width
                    if (basis > 0) childWidth = basis;
                    
                    // Check if we need to wrap
                    // FIX: Also force wrap when items would exceed container width, even with nowrap
                    // This prevents items from being positioned way off-screen
                    if (currentLine.Count > 0)
                    {
                        float testWidth = currentLineWidth + gapValue + childWidth;
                        
                        // DEBUG: Trace wrap decision for SIvCob
                        string containerClass = node.Attr != null && node.Attr.TryGetValue("class", out var cc) ? cc : "";
                        string containerId = node.Attr != null && node.Attr.TryGetValue("id", out var ci) ? ci : "";
                        if (containerId.Contains("SIvCob") || containerClass.Contains("SIvCob") || containerClass.Contains("iTjxkf") || containerClass.Contains("SSwjIe") || containerClass.Contains("AghGtd"))
                        {
                            lock(_logLock) { try{
                                System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                  $"[WrapTrace] Container={containerClass} TestWidth={testWidth} ContentBoxWidth={contentBox.Width} ShouldWrap={shouldWrap} ChildWidth={childWidth} Gap={gapValue} WillWrap={shouldWrap && testWidth > contentBox.Width}\r\n");
                            } catch {} }
                        }
                        
                        if (shouldWrap && testWidth > contentBox.Width)
                        {
                            // Standard CSS wrap
                            lines.Add(currentLine);
                            currentLine = new List<(LiteElement element, float width, float height, float grow, float shrink, float basis, float measuredWidth)>();
                            currentLineWidth = 0;
                        }
                        // FIX: Removed 1.5x overflow wrap check. 'nowrap' must be respected.
                        // Overflowing content should just overflow or be shrunk later.
                    }
                    
                    
                    // Capture original measured width as the implicit minimum content width for min-width: auto
                    float measuredWidth = (childWidth == basis && basis > 0) ? childBox?.MarginBox.Width ?? 0 : childWidth;
                    // If we just overwrote childWidth with basis, we need to make sure measuredWidth reflects the INTIRNSIC size,
                    // which was in childWidth BEFORE the basis check.
                    // Correct logic:
                    // 1. childWidth = childBox.MarginBox.Width (Intrinsic)
                    // 2. measuredWidth = childWidth
                    // 3. if (basis > 0) childWidth = basis
                    // So we must act before line 1591 or restore it.
                    // Let's rely on re-reading childBox since we have it.
                    float intrinsicWidth = 0;
                    if (childBox != null) intrinsicWidth = childBox.MarginBox.Width;
                    
                    currentLine.Add((child, childWidth, childHeight, grow, shrink, basis, intrinsicWidth));
                    currentLineWidth += (currentLine.Count > 1 ? gapValue : 0) + childWidth;
                }
                
                if (currentLine.Count > 0)
                    lines.Add(currentLine);
                
                // DEBUG: Log lines structure for iTjxkf
                {
                    string containerClass = node.Attr != null && node.Attr.TryGetValue("class", out var cc) ? cc : "";
                    if (containerClass.Contains("iTjxkf") || containerClass.Contains("SSwjIe") || containerClass.Contains("AghGtd"))
                    {
                        lock(_logLock) { try {
                            System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                $"[LinesDebug] Container={containerClass} TotalLines={lines.Count} ItemsPerLine=[{string.Join(",", lines.Select(l => l.Count))}]\r\n");
                        } catch {} }
                    }
                }
                
                // Reverse lines if wrap-reverse
                if (wrapReverse)
                    lines.Reverse();
                
                // Calculate total lines height for align-content
                float totalLinesHeight = 0;
                var lineHeights = new List<float>();
                foreach (var line in lines)
                {
                    float lh = line.Max(item => item.height);
                    lineHeights.Add(lh);
                    totalLinesHeight += lh;
                }
                totalLinesHeight += rowGap * (lines.Count - 1); // Add gaps
                
                // Apply align-content for multi-line flex containers
                float effectiveContainerH = containerHeight > 0 ? containerHeight : contentBox.Height;
                float crossAxisFreeSpace = effectiveContainerH - totalLinesHeight;
                float alignContentStartOffset = 0;
                float alignContentGap = 0;
                
                if (shouldWrap && lines.Count > 1 && crossAxisFreeSpace > 0)
                {
                    switch (alignContent)
                    {
                        case "center":
                            alignContentStartOffset = crossAxisFreeSpace / 2;
                            break;
                        case "flex-end":
                            alignContentStartOffset = crossAxisFreeSpace;
                            break;
                        case "space-between":
                            if (lines.Count > 1)
                                alignContentGap = crossAxisFreeSpace / (lines.Count - 1);
                            break;
                        case "space-around":
                            alignContentGap = crossAxisFreeSpace / lines.Count;
                            alignContentStartOffset = alignContentGap / 2;
                            break;
                        case "space-evenly":
                            alignContentGap = crossAxisFreeSpace / (lines.Count + 1);
                            alignContentStartOffset = alignContentGap;
                            break;
                        case "stretch":
                            // Distribute extra space evenly among lines
                            if (lines.Count > 0)
                            {
                                float extraPerLine = crossAxisFreeSpace / lines.Count;
                                for (int i = 0; i < lineHeights.Count; i++)
                                    lineHeights[i] += extraPerLine;
                            }
                            break;
                        // "flex-start" is default - no offset
                    }
                }
                
                // Second pass: position items with flex-grow/flex-shrink
                
                string uTag = node.Tag?.ToUpperInvariant() ?? "";
                // DEBUG: Trace flex line construction for main-columns OR Header (UL/NAV)
                if (nodeClass.Contains("main-columns") || uTag == "UL" || uTag == "NAV")
                {
                     lock(_logLock) { try {
                         System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                             $"[FlexTrace] Container='{nodeClass}' Tag={node.Tag} isRow={isRow} Lines={lines.Count} ContentWidth={contentBox.Width}\r\n");
                         int lIdx = 0;
                         foreach(var ln in lines) {
                             System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                 $"  -> Line {lIdx++}: Items={ln.Count} Width={ln.Sum(x=>x.width)}\r\n");
                         }
                     } catch {} }
                }

                float totalHeight = 0;
                float lineY = cursorY + alignContentStartOffset;
                
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var line = lines[lineIndex];
                    if (isReverse) line.Reverse();
                    
                    // Use pre-calculated line height (may be stretched by align-content)
                    float lineHeight = lineHeights[lineIndex];
                    float lineWidth = line.Sum(item => item.width) + gapValue * (line.Count - 1);
                    
                    // Calculate free space (can be negative if overflow)
                    float freeSpace = contentBox.Width - lineWidth;
                    
                    // Apply flex-grow if there's extra space
                    float totalGrow = line.Sum(item => item.grow);
                    var adjustedWidths = new List<float>();
                    
                    if (freeSpace > 0 && totalGrow > 0)
                    {
                        // Distribute extra space according to flex-grow
                        foreach (var (child, childWidth, childHeight, grow, shrink, basis, measuredWidth) in line)
                        {
                            float extraWidth = grow > 0 ? freeSpace * (grow / totalGrow) : 0;
                            adjustedWidths.Add(childWidth + extraWidth);
                        }
                    }
                    else if (freeSpace < 0)
                    {
                        // Apply flex-shrink if there's overflow
                        float totalShrinkWeighted = line.Sum(item => item.shrink * item.width);
                        if (totalShrinkWeighted > 0)
                        {
                            foreach (var (child, childWidth, childHeight, grow, shrink, basis, measuredWidth) in line)
                            {
                                float shrinkRatio = (shrink * childWidth) / totalShrinkWeighted;
                                float shrinkAmount = Math.Abs(freeSpace) * shrinkRatio;
                                float targetWidth = childWidth - shrinkAmount;
                                
                                // Apply min-width: auto logic
                                // Spec: min-width: auto means min-content size if overflow is visible
                                CssComputed cStyle = null;
                                if (_styles != null) _styles.TryGetValue(child, out cStyle);
                                
                                float minSize = 0;
                                if (cStyle?.MinWidth.HasValue == true)
                                {
                                    minSize = (float)cStyle.MinWidth.Value;
                                }
                                else
                                {
                                    // min-width: auto
                                    bool overflowVisible = cStyle?.Overflow == null || cStyle.Overflow == "visible";
                                    // FIX: usage of 'measuredWidth' as min-size prevents shrinking for items with explicit width/percent
                                    // Only usage measuredWidth as min-content proxy if width is auto
                                    bool hasExplicitWidth = cStyle?.Width != null || cStyle?.WidthPercent != null;
                                    
                                    if (overflowVisible && !hasExplicitWidth)
                                    {
                                        minSize = measuredWidth;
                                    }
                                    // If explicit width exists, min-width: auto should technically be min-content.
                                    // For now, defaulting to 0 allows shrinking (browser behavior for inputs often defaults to small min-width).
                                    // Optimization: For inputs, we might want a small hardcoded min-width.
                                    if (hasExplicitWidth && (child.Tag == "INPUT" || child.Tag == "BUTTON"))
                                    {
                                        minSize = 0; // Allow full shrinking
                                    }
                                }
                                
                                adjustedWidths.Add(Math.Max(minSize, targetWidth));
                            }
                        }
                        else
                        {
                            // No shrinking allowed, use original widths
                            foreach (var item in line)
                                adjustedWidths.Add(item.width);
                        }
                    }
                    else
                    {
                        // No adjustment needed
                        foreach (var item in line)
                            adjustedWidths.Add(item.width);
                    }
                    
                    // Recalculate line width after flex adjustments
                    float adjustedLineWidth = adjustedWidths.Sum() + gapValue * (line.Count - 1);
                    
                    // Calculate justify-content offset based on adjusted widths
                    float remainingSpace = contentBox.Width - adjustedLineWidth;
                    // If shrinking to content and width is auto, we shouldn't distribute space based on available width
                    bool isAutoWidth = style?.Width == null && style?.WidthPercent == null;
                    if (shrinkToContent && isAutoWidth) remainingSpace = 0;
                    float startOffset = 0;
                    float extraGap = 0;
                    
                    switch (justifyContent)
                    {
                        case "center":
                            startOffset = remainingSpace / 2;
                            break;
                        case "flex-end":
                            startOffset = remainingSpace;
                            break;
                        case "space-between":
                            if (line.Count > 1) extraGap = remainingSpace / (line.Count - 1);
                            break;
                        case "space-around":
                            extraGap = remainingSpace / line.Count;
                            startOffset = extraGap / 2;
                            break;
                        case "space-evenly":
                            extraGap = remainingSpace / (line.Count + 1);
                            startOffset = extraGap;
                            break;
                    }
                    
                    // Position items in this line
                    float itemX = cursorX + startOffset;
                    
                    for (int i = 0; i < line.Count; i++)
                    {
                        var (child, childWidth, childHeight, grow, shrink, basis, _) = line[i];
                        float finalWidth = adjustedWidths[i];
                        
                        // DEBUG: Trace child placement
                        // (Moved logging down)
                        
                        // Get align-self for this specific item
                        CssComputed itemStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out itemStyle);
                        string itemAlign = !string.IsNullOrEmpty(itemStyle?.AlignSelf) ? itemStyle.AlignSelf : alignItems;
                        
                        // Calculate align-items/align-self offset
                        float itemY = lineY;
                        switch (itemAlign)
                        {
                            case "center":
                                itemY = lineY + (lineHeight - childHeight) / 2;
                                break;
                            case "flex-end":
                                itemY = lineY + lineHeight - childHeight;
                                break;
                                break;
                            case "baseline":
                                // Simplified: same as flex-start
                                break;
                            case "stretch":
                                // Stretch Logic: Force child height to line height (minus vertical margins)
                                // Stretch Logic: Force child height to line height (minus vertical margins)
                                // Only stretch if height is auto (neither fixed px nor percent is set)
                                if (itemStyle?.Height == null && itemStyle?.HeightPercent == null)
                                {
                                    float newHeight = lineHeight;
                                     // Account for margins if they are part of the box measurement logic in ComputeLayout
                                     // Here ComputeLayout takes 'width', and calculates height.
                                     // We need to force height.
                                     // In CSS, stretch imposes definite cross-size.
                                     // We verify if we can re-measure with fixed height.
                                     
                                     // Re-measure with fixed height
                                     // Note: ComputeLayout signature isn't designed for forcing height easily without style hacks, 
                                     // but we can pass it down or modifying style temporarily? No, style is computed.
                                     // Better: Update the box dimensions directly after layout if ComputeLayout doesn't support input height easily.
                                     // Actually, standard approach: Re-call ComputeLayout with definite height constraint if possible.
                                     // Simplest for now: Let it layout, then override height of the box.
                                     // But re-layout is needed for children that depend on height (column flex children etc).
                                     
                                     // For minimal risk change:
                                     // 1. Calculate target height
                                     float targetH = lineHeight;
                                     // 2. Subtract margins
                                     if (_boxes.TryGetValue(child, out var oldBox))
                                     {
                                         targetH -= (oldBox.MarginBox.Height - oldBox.BorderBox.Height); 
                                     }
                                     
                                     // 3. Force re-layout or just set bounds?
                                     // Re-layout is safer for content.
                                     // We don't have a direct "ForceHeight" param. 
                                     // Let's rely on setting the box height after layout for now to avoid massive refactor,
                                     // OR use the box to set content height if it's a block.
                                     
                                     // Let's do the "Post-Layout Fixup" which is common in simple engines.
                                     // We will perform the layout, then forcefully expand the box height.
                                     
                                     // Actually, we are about to call ComputeLayout below.
                                     // If we could pass "stretchHeight", that would be best.
                                     // BUT we can't change signature easily.
                                     
                                     // So... we let it layout naturally (auto height), then we stretch it.
                                     // itemY = lineY is correct (stretch starts at top).
                                }
                                break;
                        }
                        
                        // [LayoutProbe] - Cleaned up

                        // Re-layout at final position with adjusted width
                        string childTagDbg = child.Tag?.ToUpperInvariant() ?? "TEXT";
                        
                        // DEBUG: Log item positions for iTjxkf
                        {
                            string containerClass = node.Attr != null && node.Attr.TryGetValue("class", out var cc) ? cc : "";
                            if (containerClass.Contains("iTjxkf") || containerClass.Contains("AghGtd"))
                            {
                                lock(_logLock) { try {
                                    System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                        $"[PositionDebug] Container={containerClass} Item={childTagDbg} itemX={itemX} itemY={itemY} finalWidth={finalWidth} lineY={lineY} cursorX={cursorX}\r\n");
                                } catch {} }
                            }
                        }
                        
                        if (childTagDbg == "TEXTAREA" || childTagDbg == "INPUT")
                        {
                            FenLogger.Debug($"[ComputeFlexLayout] Row reposition: childTag={childTagDbg} itemX={itemX} itemY={itemY} finalWidth={finalWidth}", LogCategory.Layout);
                        }
                        ComputeLayout(child, itemX, itemY, finalWidth, shrinkToContent: true);

                        // Apply Stretch (Post-Layout)
                        if (itemAlign == "stretch" || (string.IsNullOrEmpty(itemAlign) && alignItems == "stretch"))
                        {
                             // Check if height is auto (fixed heights shouldn't stretch)
                             // Check if height is auto (fixed heights shouldn't stretch)
                             bool isAutoHeight = itemStyle?.Height == null && itemStyle?.HeightPercent == null;
                             if (isAutoHeight)
                             {
                                 if (_boxes.TryGetValue(child, out var finalChildBox))
                                 {
                                     float marginY = finalChildBox.MarginBox.Height - finalChildBox.BorderBox.Height;
                                     float targetHeight = lineHeight - marginY;
                                     if (targetHeight > finalChildBox.BorderBox.Height)
                                     {
                                         float diff = targetHeight - finalChildBox.BorderBox.Height;
                                         finalChildBox.ContentBox.Bottom += diff;
                                         finalChildBox.PaddingBox.Bottom += diff;
                                         finalChildBox.BorderBox.Bottom += diff;
                                         finalChildBox.MarginBox.Bottom += diff;
                                         // Log stretch
                                         FenLogger.Debug($"[FlexStretch] Row: Stretched {child.Tag} from {finalChildBox.BorderBox.Height} to {targetHeight}", LogCategory.Layout);
                                     }
                                 }
                             }
                        }
                        
                        itemX += finalWidth + gapValue + extraGap;
                    }
                    
                    if (adjustedLineWidth > maxChildWidth) maxChildWidth = adjustedLineWidth;
                    // Add align-content gap between lines
                    lineY += lineHeight + rowGap + alignContentGap;
                    totalHeight += lineHeight + rowGap + alignContentGap;
                }
                
                return totalHeight;
            }
            else
            {
                // Column Layout with wrapping
                if (shouldWrap)
                {
                    var columns = new List<List<(LiteElement element, float width, float height)>>();
                    var currentColumn = new List<(LiteElement, float, float)>();
                    float currentColumnHeight = 0;
                    
                    foreach (var child in flexItems)
                    {
                        ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                        
                        float childWidth = 0, childHeight = 0;
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            childWidth = childBox.MarginBox.Width;
                            childHeight = childBox.MarginBox.Height;
                        }
                        
                        float testHeight = currentColumnHeight + rowGap + childHeight;
                        if (currentColumn.Count > 0 && testHeight > contentBox.Height)
                        {
                            columns.Add(currentColumn);
                            currentColumn = new List<(LiteElement, float, float)>();
                            currentColumnHeight = 0;
                        }
                        
                        currentColumn.Add((child, childWidth, childHeight));
                        currentColumnHeight += (currentColumn.Count > 1 ? rowGap : 0) + childHeight;
                    }
                    
                    if (currentColumn.Count > 0)
                        columns.Add(currentColumn);
                    
                    // Position columns
                    float columnX = cursorX;
                    float maxHeight = 0;
                    
                    foreach (var column in columns)
                    {
                        if (isReverse) column.Reverse();
                        
                        float columnWidth = column.Max(item => item.width);
                        float itemY = cursorY;
                        
                        foreach (var (child, childWidth, childHeight) in column)
                        {
                            CssComputed childStyle = null;
                            if (_styles != null) _styles.TryGetValue(child, out childStyle);

                            // For column layout, align-items controls horizontal positioning
                            float itemX = columnX;
                            switch (alignItems)
                            {
                                case "center":
                                    // Center item horizontally within container
                                    itemX = contentBox.Left + (contentBox.Width - childWidth) / 2;
                                    break;
                                case "flex-end":
                                    // Align to right edge
                                    itemX = contentBox.Right - childWidth;
                                    break;
                                case "stretch":
                                     // Stretch (Column): Start at left
                                     itemX = contentBox.Left;
                                     break;
                                // flex-start is default - itemX stays at columnX
                            }
                            
                            // For Column Stretch: Force width to container width (minus margins) if width is auto
                            bool forceStretchWidth = alignItems == "stretch" && (childStyle?.Width == null && childStyle?.WidthPercent == null);
                            float childLayoutWidth = childWidth;
                            
                            if (forceStretchWidth)
                            {
                                // We want the child margin-box to be contentBox.Width
                                // But we pass 'availableWidth' to ComputeLayout.
                                // ComputeLayout normally respects it if shrinkToContent is false?
                                // If we pass shrinkToContent=false, block layout expands to width.
                                // So for stretch items, we should turn OFF shrinkToContent.
                                
                                // HOWEVER, we already measured it once (line 1670) with shrinkToContent=true to get intrinsic size.
                                // Now we re-layout for final position.
                                
                                childLayoutWidth = contentBox.Width; // Force full width
                                ComputeLayout(child, itemX, itemY, childLayoutWidth, shrinkToContent: false); 
                            }
                            else
                            {
                                ComputeLayout(child, itemX, itemY, childLayoutWidth, shrinkToContent: true);
                            }
                            itemY += childHeight + rowGap;
                        }
                        
                        float colHeight = itemY - cursorY - rowGap;
                        if (colHeight > maxHeight) maxHeight = colHeight;
                        columnX += columnWidth + gapValue;
                    }
                    
                    maxChildWidth = columnX - cursorX - gapValue;
                    return maxHeight;
                }
                else
                {
                    // Simple column layout (no wrap) with FLEX-GROW support
                    float maxWidth = 0;
                    
                    // FIRST PASS: Measure all items and calculate flex-grow totals (Step 3)
                    float totalChildrenHeight = 0;
                    float totalGrow = 0;
                    var childMeasurements = new List<(LiteElement child, float width, float height, float grow)>();
                    
                    foreach (var child in flexItems)
                    {
                        // Get child's flex-grow value
                        CssComputed childStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out childStyle);
                        float grow = (float)(childStyle?.FlexGrow ?? 0);
                        
                        // Measure child with shrink-to-content
                        // FIX: If child is also a Flex container, pass parent's width but STILL use shrinkToContent=true
                        // This allows nested Flex containers to shrink to their content while having a finite width constraint
                        string childDisplay = childStyle?.Display?.ToLowerInvariant() ?? "";
                        bool childIsFlex = childDisplay == "flex" || childDisplay == "inline-flex";
                        // ALWAYS use shrinkToContent=true for Column Flex measurement (same as Row Flex fix)
                        ComputeLayout(child, cursorX, 0, contentBox.Width, shrinkToContent: true);
                        
                        float childWidth = 0, childHeight = 0;
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            childWidth = childBox.MarginBox.Width;
                            childHeight = childBox.MarginBox.Height;
                        }
                        
                        childMeasurements.Add((child, childWidth, childHeight, grow));
                        totalChildrenHeight += childHeight;
                        totalGrow += grow;
                        
                        if (childWidth > maxWidth) maxWidth = childWidth;
                    }
                    
                    // Step 2 & 4: Compute Free Space (Main Axis = Vertical)
                    // Use passed containerHeight if available, otherwise contentBox.Height
                    float effectiveContainerHeight = containerHeight > 0 ? containerHeight : contentBox.Height;
                    // Fallback to explicit style height if measurement didn't set it yet
                    if ((effectiveContainerHeight <= 0 || float.IsInfinity(effectiveContainerHeight)) && style != null && style.Height.HasValue)
                    {
                        effectiveContainerHeight = (float)style.Height.Value;
                    }
                    if (effectiveContainerHeight <= 0 || float.IsInfinity(effectiveContainerHeight))
                    {
                         // If no height, free space is 0 (layout just stacks)
                         effectiveContainerHeight = totalChildrenHeight; 
                    }

                    float freeMain = effectiveContainerHeight - totalChildrenHeight - (rowGap * (flexItems.Count - 1));

                    // Step 5: Apply justify-content (Main Axis -> Vertical)
                    float mainOffsetStart = 0;
                    float extraGap = 0; // For spacing distribution

                    // Only apply alignment if no grow is active
                    if (totalGrow == 0) 
                    {
                        switch (justifyContent)
                        {
                            case "center":
                                mainOffsetStart = freeMain / 2;
                                break;
                            case "flex-end":
                                mainOffsetStart = freeMain;
                                break;
                            case "space-between":
                                if (flexItems.Count > 1) extraGap = freeMain / (flexItems.Count - 1);
                                break;
                            case "space-around":
                                extraGap = freeMain / flexItems.Count;
                                mainOffsetStart = extraGap / 2;
                                break;
                            case "space-evenly":
                                extraGap = freeMain / (flexItems.Count + 1);
                                mainOffsetStart = extraGap;
                                break;
                        }
                    }

                    // Step 7: Final Child Positioning
                    float currentY = cursorY + mainOffsetStart; // Step 7: y = container.y + paddingTop + mainOffsetStart
                    // note: cursorY is contentBox.Top
                    
                    float totalHeight = 0;

                    foreach (var (child, childWidth, childHeight, grow) in childMeasurements)
                    {
                        float finalHeight = childHeight;
                        
                        // Apply flex-grow (Standard Flex Logic, preserved as 'correct')
                        if (freeMain > 0 && totalGrow > 0 && grow > 0)
                        {
                            float extraHeight = freeMain * (grow / totalGrow);
                            finalHeight = childHeight + extraHeight;
                        }
                        
                        // Get align-self/align-items
                        CssComputed childStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out childStyle);
                        string itemAlign = !string.IsNullOrEmpty(childStyle?.AlignSelf) ? childStyle.AlignSelf : alignItems;

                        // Step 6: Apply align-items (Cross Axis -> Horizontal)
                        float crossOffset = 0;
                        if (_boxes.TryGetValue(child, out var finalChildBox))
                        {
                            // freeCross for this child = contentBox.Width - child.MarginBox.Width
                            switch (itemAlign)
                            {
                                case "center":
                                    crossOffset = (contentBox.Width - finalChildBox.MarginBox.Width) / 2;
                                    FenLogger.Debug($"[ColumnAlign] child={child.Tag} align={itemAlign} contentW={contentBox.Width} childW={finalChildBox.MarginBox.Width} offset={crossOffset}", FenBrowser.Core.Logging.LogCategory.Layout);
                                    break;
                                case "flex-end":
                                    crossOffset = contentBox.Width - finalChildBox.MarginBox.Width;
                                    break;
                                case "stretch":
                                    // Stretch in column direction: expand child width to container width
                                    // Only if width is auto (not explicitly set)
                                    if (childStyle?.Width == null && childStyle?.WidthPercent == null)
                                    {
                                        float marginHorizontal = finalChildBox.MarginBox.Width - finalChildBox.BorderBox.Width;
                                        float targetWidth = contentBox.Width - marginHorizontal;
                                        float currentWidth = finalChildBox.BorderBox.Width;
                                        
                                        if (targetWidth > currentWidth)
                                        {
                                            float diff = targetWidth - currentWidth;
                                            // Expand content, padding, border, and margin boxes
                                            finalChildBox.ContentBox = new SKRect(
                                                finalChildBox.ContentBox.Left,
                                                finalChildBox.ContentBox.Top,
                                                finalChildBox.ContentBox.Right + diff,
                                                finalChildBox.ContentBox.Bottom);
                                            finalChildBox.PaddingBox = new SKRect(
                                                finalChildBox.PaddingBox.Left,
                                                finalChildBox.PaddingBox.Top,
                                                finalChildBox.PaddingBox.Right + diff,
                                                finalChildBox.PaddingBox.Bottom);
                                            finalChildBox.BorderBox = new SKRect(
                                                finalChildBox.BorderBox.Left,
                                                finalChildBox.BorderBox.Top,
                                                finalChildBox.BorderBox.Right + diff,
                                                finalChildBox.BorderBox.Bottom);
                                            finalChildBox.MarginBox = new SKRect(
                                                finalChildBox.MarginBox.Left,
                                                finalChildBox.MarginBox.Top,
                                                finalChildBox.MarginBox.Right + diff,
                                                finalChildBox.MarginBox.Bottom);
                                            FenLogger.Debug($"[FlexStretch] Column: Stretched {child.Tag} width from {currentWidth} to {targetWidth}", LogCategory.Layout);
                                        }
                                    }
                                    break;
                            }
                            
                            // PLACEMENT: Move child to (currentX + crossOffset, currentY)
                            // We use ShiftTree because child was placed at (cursorX, 0) relative during measure?
                            // No, ComputeLayout(..., cursorX, 0...) actually places at cursorX, 0 (absolute Y=0).
                            // So finalChildBox.MarginBox.Top is 0. finalChildBox.MarginBox.Left is cursorX.
                            
                            float targetX = cursorX + crossOffset;
                            float targetY = currentY;
                            
                            float deltaX = targetX - finalChildBox.MarginBox.Left; // = crossOffset
                            float deltaY = targetY - finalChildBox.MarginBox.Top;  // = currentY - 0 = currentY
                            
                            if (Math.Abs(deltaX) > 0.01f || Math.Abs(deltaY) > 0.01f)
                            {
                                ShiftTree(child, deltaX, deltaY);
                            }
                            
                            // Extend box height if grown
                            if (finalHeight > childHeight + 0.1f)
                            {
                                float heightDelta = finalHeight - childHeight;
                                // Need to update the box properties directly
                                finalChildBox.ContentBox.Bottom += heightDelta;
                                finalChildBox.PaddingBox.Bottom += heightDelta;
                                finalChildBox.BorderBox.Bottom += heightDelta;
                                finalChildBox.MarginBox.Bottom += heightDelta;
                            }
                        }
                        
                        // Advance Main Axis
                        currentY += finalHeight + rowGap + extraGap;
                        totalHeight += finalHeight + rowGap + extraGap;
                    }
                    
                    maxChildWidth = maxWidth;
                    return totalHeight > 0 ? totalHeight - rowGap : 0;

            }
        }
    }

        /// <summary>
        /// CSS Grid layout implementation
        /// </summary>
        private float ComputeGridLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth)
        {
            maxChildWidth = 0;
            if (node.Children == null || node.Children.Count == 0) return 0;
            
            // --- 1. Parse Grid Setup ---
            
            // Explicit Tracks (Template)
            var columnWidths = ParseGridTemplate(style?.GridTemplateColumns, contentBox.Width);
            var rowHeightsTemplate = ParseGridTemplate(style?.GridTemplateRows, 0); // contentBox.Height is usually unconstrained
            
            // Parse grid-template-areas
            var gridAreas = ParseGridTemplateAreas(style?.GridTemplateAreas);
            
            // Resolve Auto Flow & Density
            string autoFlowRaw = style?.GridAutoFlow ?? "row";
            bool isRowFlow = !autoFlowRaw.Contains("column");
            bool isDense = autoFlowRaw.Contains("dense");
            
            // Gaps
            float columnGap = 0, rowGap = 0;
            if (style?.Gap.HasValue == true) { columnGap = (float)style.Gap.Value; rowGap = (float)style.Gap.Value; }
            if (style?.ColumnGap.HasValue == true) columnGap = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;

            // Implicit Track Sizes
            // Simplified: Use first value or default to auto (which we simulate as Content size or leftover)
            float defaultAutoCol = 0; // 0 means auto/fit-content in our simplified model
            float defaultAutoRow = 0;
            // TODO: Parse GridAutoColumns/Rows properly. For now assume auto.
            
            // --- 2. Collect Items ---
            var gridItems = new List<LiteElement>();
            foreach (var c in node.Children)
            {
                CssComputed cStyle = null;
                if (_styles != null) _styles.TryGetValue(c, out cStyle);
                string cPos = cStyle?.Position?.ToLowerInvariant();
                if (cPos == "absolute" || cPos == "fixed")
                {
                    _deferredAbsoluteElements.Add(c);
                }
                else
                {
                    gridItems.Add(c);
                }
            }
            if (gridItems.Count == 0) return 0;

            // --- 3. Placement Algorithm Strategy ---
            
            // Grid State
            var occupied = new Dictionary<(int row, int col), LiteElement>();
            var itemPlacements = new Dictionary<LiteElement, (int row, int col, int rowSpan, int colSpan)>();
            
            // Initial Grid Bounds (from template)
            int explicitColCount = columnWidths.Count;
            int explicitRowCount = rowHeightsTemplate.Count;
            
            // If explicit columns are empty but we have items, defaut to 1 column
            if (explicitColCount == 0) 
            {
                explicitColCount = 1;
                columnWidths.Add(contentBox.Width); // Treat as 1 auto column taking full width
            }
            // Logic to support Areas creating implicit lines
            if (gridAreas.Count > 0)
            {
                explicitRowCount = Math.Max(explicitRowCount, gridAreas.Count);
                if (gridAreas.Count > 0) explicitColCount = Math.Max(explicitColCount, gridAreas[0].Count);
            }

            int minRow = 0, maxRow = explicitRowCount - 1;
            int minCol = 0, maxCol = explicitColCount - 1;

            // List separation
            var fixedItems = new List<LiteElement>();
            var autoItems = new List<LiteElement>();
            
            foreach (var item in gridItems)
            {
                if (HasExplicitGridPosition(item, _styles, gridAreas))
                    fixedItems.Add(item);
                else
                    autoItems.Add(item);
            }
            
            // --- Step 3a: Place Fixed Items ---
            foreach (var item in fixedItems)
            {
                var placement = ResolveGridPlacement(item, _styles, gridAreas);
                itemPlacements[item] = placement;
                
                // Mark cells
                for (int r = placement.row; r < placement.row + placement.rowSpan; r++)
                {
                    for (int c = placement.col; c < placement.col + placement.colSpan; c++)
                    {
                        occupied[(r, c)] = item;
                    }
                }
                
                // Expand bounds
                maxRow = Math.Max(maxRow, placement.row + placement.rowSpan - 1);
                maxCol = Math.Max(maxCol, placement.col + placement.colSpan - 1);
            }
            
            // --- Step 3b: Place Auto Items ---
            
            // Cursors
            int autoRow = 0, autoCol = 0;
            
            foreach (var item in autoItems)
            {
                var span = ResolveGridSpan(item, _styles);
                int rowSpan = span.rowSpan;
                int colSpan = span.colSpan;
                
                // Search for empty spot
                bool placed = false;
                
                // If dense, reset cursors for each item to find first hole
                if (isDense)
                {
                    autoRow = 0;
                    autoCol = 0;
                }
                
                while (!placed)
                {
                    // Check if area [autoRow, autoCol] to [autoRow+rowSpan, autoCol+colSpan] is empty
                    bool fits = true;
                    for (int r = autoRow; r < autoRow + rowSpan; r++)
                    {
                        for (int c = autoCol; c < autoCol + colSpan; c++)
                        {
                            if (occupied.ContainsKey((r, c)))
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (!fits) break;
                    }
                    
                    if (fits)
                    {
                        // Place it
                        itemPlacements[item] = (autoRow, autoCol, rowSpan, colSpan);
                        for (int r = autoRow; r < autoRow + rowSpan; r++)
                        {
                            for (int c = autoCol; c < autoCol + colSpan; c++)
                            {
                                occupied[(r, c)] = item;
                            }
                        }
                        
                        maxRow = Math.Max(maxRow, autoRow + rowSpan - 1);
                        maxCol = Math.Max(maxCol, autoCol + colSpan - 1);
                        placed = true;
                    }
                    else
                    {
                        // Advance cursor
                        if (isRowFlow)
                        {
                            autoCol++;
                            if (autoCol >= explicitColCount && explicitColCount > 0) 
                            { 
                                // Reset to next row if we hit Explicit Column limit?
                                // Actually, CSS Grid allows adding Implicit Columns if we go past.
                                // But typically for row-flow, we wrap to new row unless we are pinning specific columns.
                                // Logic: If we are in row-flow, we fill columns then wrap to next row.
                                // Implicit columns only created if item spans or is placed specifically there?
                                // Simplified: Wrap to next row maxCol
                                autoCol = 0;
                                autoRow++;
                            }
                        }
                        else // Column flow
                        {
                            autoRow++;
                            if (autoRow >= explicitRowCount && explicitRowCount > 0)
                            {
                                autoRow = 0;
                                autoCol++;
                            }
                        }
                        
                        // Failing safeguard
                        if (autoRow > 10000 || autoCol > 10000) 
                        {
                            // prevent infinite loops
                            break; 
                        }
                    }
                }
            }
            
            // --- 4. Sizing & Layout ---
            
            // Finalize Track Sizes
            int finalRowCount = maxRow + 1;
            int finalColCount = maxCol + 1;
            
            // Expand track arrays to final sizes
            var finalColWidths = new float[finalColCount];
            var finalRowHeights = new float[finalRowCount];
            
            // 4a. Copy explicit/template sizes
            for (int i = 0; i < finalColCount; i++)
            {
                if (i < columnWidths.Count) finalColWidths[i] = columnWidths[i];
                else finalColWidths[i] = defaultAutoCol; // Implicit col
            }
            
            for (int i = 0; i < finalRowCount; i++)
            {
                if (i < rowHeightsTemplate.Count) finalRowHeights[i] = rowHeightsTemplate[i];
                else finalRowHeights[i] = defaultAutoRow; // Implicit row
            }
            
            // 4b. Measure Items & Adjust Auto Tracks
            // This is a naive sizing: explicitly sized tracks are fixed, 'auto' tracks grow to fit content
            foreach (var kvp in itemPlacements)
            {
                var item = kvp.Key;
                var p = kvp.Value;
                
                // Determine available width for initial measurement if possible
                float availW = 0;
                // If spanning fixed tracks, sum them
                bool fixedWidth = true;
                for (int c = p.col; c < p.col + p.colSpan; c++)
                {
                   // If any track is auto (0), then we can't constrain width yet
                   // For now, treat 0 as "auto"
                   if (finalColWidths[c] <= 0) fixedWidth = false;
                   else availW += finalColWidths[c];
                }
                if (p.colSpan > 1) availW += (p.colSpan - 1) * columnGap;
                
                // Measure
                // If width is constrained, pass it. If auto, pass 0/unconstrained.
                float measureW = fixedWidth ? availW : 0; 
                ComputeLayout(item, 0, 0, measureW, shrinkToContent: !fixedWidth);
                
                if (_boxes.TryGetValue(item, out var box))
                {
                    // Propagate height to Row Tracks
                     // If item spans 1 row, maximize that row
                    if (p.rowSpan == 1)
                    {
                        if (finalRowHeights[p.row] < box.MarginBox.Height)
                            finalRowHeights[p.row] = box.MarginBox.Height;
                    }
                    
                    // Propagate width to Col Tracks (if auto)
                    if (p.colSpan == 1 && finalColWidths[p.col] <= 0)
                    {
                         if (finalColWidths[p.col] < box.MarginBox.Width)
                            finalColWidths[p.col] = box.MarginBox.Width;
                    }
                }
            }
            
            // 4c. Resolve Auto Tracks that are still 0?
            // If explicit grid had 'auto' columns/rows, they should have been expanded by content.
            // If they are still 0 (empty), give them 0? Or split available space?
            // Simplified: leave as is.
            
            // --- 5. Final Positioning ---
            
            // Build Start Positions
            var colStarts = new float[finalColCount + 1];
            colStarts[0] = contentBox.Left;
            for (int i = 0; i < finalColCount; i++)
            {
                float trackW = finalColWidths[i] > 0 ? finalColWidths[i] : 0; // Collapse empty auto tracks
                colStarts[i+1] = colStarts[i] + trackW + (i < finalColCount - 1 ? columnGap : 0);
            }
            
            var rowStarts = new float[finalRowCount + 1];
            rowStarts[0] = contentBox.Top;
            for (int i = 0; i < finalRowCount; i++)
            {
                float trackH = finalRowHeights[i] > 0 ? finalRowHeights[i] : 0;
                rowStarts[i+1] = rowStarts[i] + trackH + (i < finalRowCount - 1 ? rowGap : 0);
            }

            foreach (var kvp in itemPlacements)
            {
                var item = kvp.Key;
                var p = kvp.Value;
                
                float x = colStarts[p.col];
                float y = rowStarts[p.row];
                
                // Calculate total spanned width/height
                float w = 0;
                for (int c = p.col; c < p.col + p.colSpan; c++) w += (finalColWidths[c] > 0 ? finalColWidths[c] : 0);
                w += (p.colSpan - 1) * columnGap;
                
                float h = 0;
                for (int r = p.row; r < p.row + p.rowSpan; r++) h += (finalRowHeights[r] > 0 ? finalRowHeights[r] : 0);
                h += (p.rowSpan - 1) * rowGap;
                
                // Final layout pass at correct position & size
                ComputeLayout(item, x, y, w, shrinkToContent: false); // Item should stretch to cell unless aligned differently
                
                // TODO: Handle alignments (center/end etc)
            }
            
            maxChildWidth = colStarts[finalColCount] - contentBox.Left;
            return rowStarts[finalRowCount] - contentBox.Top;
        }
        
        /// <summary>
        /// Parse grid-template-areas string into row/column area names
        /// </summary>
        private List<List<string>> ParseGridTemplateAreas(string areasStr)
        {
            var result = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(areasStr)) return result;
            
            // Format: "header header header" "nav main sidebar" "footer footer footer"
            var matches = System.Text.RegularExpressions.Regex.Matches(areasStr, "\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var row = match.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                result.Add(row);
            }
            return result;
        }
        
        /// <summary>
        /// Find the bounds of a named grid area
        /// </summary>
        private (int row, int col, int rowSpan, int colSpan)? FindGridAreaBounds(List<List<string>> areas, string areaName)
        {
            if (areas.Count == 0) return null;
            
            int startRow = -1, endRow = -1, startCol = -1, endCol = -1;
            
            for (int r = 0; r < areas.Count; r++)
            {
                for (int c = 0; c < areas[r].Count; c++)
                {
                    if (areas[r][c] == areaName)
                    {
                        if (startRow == -1) startRow = r;
                        if (startCol == -1 || c < startCol) startCol = c;
                        endRow = r;
                        if (c > endCol) endCol = c;
                    }
                }
            }
            
            if (startRow == -1) return null;
            
            return (startRow, startCol, endRow - startRow + 1, endCol - startCol + 1);
        }
        
        // --- Grid Helpers ---
        
        private bool HasExplicitGridPosition(LiteElement item, Dictionary<LiteElement, CssComputed> styles, List<List<string>> areas)
        {
            CssComputed style = null;
            if (styles != null) styles.TryGetValue(item, out style);
            if (style == null) return false;
            
            // If GridArea matches a named area found in template, it is explicit
            if (!string.IsNullOrEmpty(style.GridArea) && areas != null && areas.Count > 0)
            {
                var bounds = FindGridAreaBounds(areas, style.GridArea);
                if (bounds.HasValue) return true;
            }
            
            // If explicit row or col start is provided (and not auto/span)
            bool hasRow = !string.IsNullOrEmpty(style.GridRowStart) && !style.GridRowStart.StartsWith("span") && style.GridRowStart != "auto";
            bool hasCol = !string.IsNullOrEmpty(style.GridColumnStart) && !style.GridColumnStart.StartsWith("span") && style.GridColumnStart != "auto";
            
            return hasRow || hasCol;
        }
        
        private (int row, int col, int rowSpan, int colSpan) ResolveGridPlacement(LiteElement item, Dictionary<LiteElement, CssComputed> styles, List<List<string>> areas)
        {
            CssComputed style = null;
            if (styles != null) styles.TryGetValue(item, out style);
            
            int row = 0, col = 0, rowSpan = 1, colSpan = 1;
            
            // 1. Try Area
             if (!string.IsNullOrEmpty(style?.GridArea) && areas != null && areas.Count > 0)
            {
                var bounds = FindGridAreaBounds(areas, style.GridArea);
                if (bounds.HasValue) return bounds.Value;
            }
            
            // 2. Explicit Lines
            if (style != null)
            {
                var rowStart = ParseGridLineValue(style.GridRowStart);
                var rowEnd = ParseGridLineValue(style.GridRowEnd);
                var colStart = ParseGridLineValue(style.GridColumnStart);
                var colEnd = ParseGridLineValue(style.GridColumnEnd);
                
                // Row
                if (rowStart.line > 0)
                {
                    row = rowStart.line - 1;
                    if (rowEnd.line > 0) rowSpan = Math.Max(1, rowEnd.line - 1 - row);
                    else if (rowEnd.span > 0) rowSpan = rowEnd.span;
                }
                
                // Col
                if (colStart.line > 0)
                {
                    col = colStart.line - 1;
                    if (colEnd.line > 0) colSpan = Math.Max(1, colEnd.line - 1 - col);
                    else if (colEnd.span > 0) colSpan = colEnd.span;
                }
            }
            
            return (row, col, rowSpan, colSpan);
        }
        
        private (int rowSpan, int colSpan) ResolveGridSpan(LiteElement item, Dictionary<LiteElement, CssComputed> styles)
        {
            CssComputed style = null;
            if (styles != null) styles.TryGetValue(item, out style);
            
            int rowSpan = 1, colSpan = 1;
            
            if (style != null)
            {
                // Check starts for spans
                var rowStart = ParseGridLineValue(style.GridRowStart);
                if (rowStart.span > 0) rowSpan = rowStart.span;
                
                var colStart = ParseGridLineValue(style.GridColumnStart);
                if (colStart.span > 0) colSpan = colStart.span;
                
                // Check ends for spans
                var rowEnd = ParseGridLineValue(style.GridRowEnd);
                if (rowEnd.span > 0) rowSpan = Math.Max(rowSpan, rowEnd.span);
                
                var colEnd = ParseGridLineValue(style.GridColumnEnd);
                if (colEnd.span > 0) colSpan = Math.Max(colSpan, colEnd.span);
            }
            
            return (rowSpan, colSpan);
        }
            
        private (int line, int span) ParseGridLineValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return (0, 0);
                
            value = value.Trim().ToLowerInvariant();
            
            // Handle "span N" syntax
            if (value.StartsWith("span "))
            {
                var spanPart = value.Substring(5).Trim();
                if (int.TryParse(spanPart, out int spanValue))
                {
                    return (0, spanValue);
                }
                return (0, 1);
            }
            
            // Handle "span" alone (defaults to 1)
            if (value == "span")
            {
                return (0, 1);
            }
            
            // Handle auto
            if (value == "auto")
            {
                return (0, 0);
            }
            
            // Handle line number (positive or negative)
            if (int.TryParse(value, out int lineNumber))
            {
                return (lineNumber, 0);
            }
            
            return (0, 0);
        }

        /// <summary>
        /// Parse grid-template-columns value
        /// </summary>
        private List<float> ParseGridTemplate(string template, float containerWidth)
        {
            var widths = new List<float>();
            if (string.IsNullOrWhiteSpace(template)) return widths;
            
            // Handle repeat()
            template = ExpandRepeat(template, containerWidth);
            
            // Split by whitespace
            var parts = Regex.Split(template.Trim(), @"\s+");
            
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                
                string p = part.Trim().ToLowerInvariant();
                
                // Handle fr units
                if (p.EndsWith("fr"))
                {
                    if (float.TryParse(p.TrimEnd('f', 'r'), NumberStyles.Float, CultureInfo.InvariantCulture, out float fr))
                    {
                        // For now, treat 1fr as equal portion of remaining space
                        widths.Add(containerWidth / 4); // Simplified
                    }
                }
                // Handle percentage
                else if (p.EndsWith("%"))
                {
                    if (float.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                    {
                        widths.Add(containerWidth * pct / 100);
                    }
                }
                // Handle px/em/rem
                else if (p.EndsWith("px") || p.EndsWith("em") || p.EndsWith("rem"))
                {
                    widths.Add(ParseCssLength(p));
                }
                // Handle auto
                else if (p == "auto")
                {
                    widths.Add(containerWidth / 4); // Simplified
                }
                // Handle minmax()
                else if (p.StartsWith("minmax("))
                {
                    var match = Regex.Match(p, @"minmax\s*\(\s*([^,]+)\s*,\s*([^)]+)\s*\)");
                    if (match.Success)
                    {
                        string minVal = match.Groups[1].Value.Trim();
                        string maxVal = match.Groups[2].Value.Trim();
                        
                        // Parse min value
                        float minWidth = 0;
                        if (minVal.EndsWith("px") || minVal.EndsWith("em") || minVal.EndsWith("rem"))
                            minWidth = ParseCssLength(minVal);
                        else if (minVal == "auto" || minVal == "min-content")
                            minWidth = 0; // Will be determined by content
                        else if (minVal.EndsWith("%"))
                        {
                            if (float.TryParse(minVal.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                                minWidth = containerWidth * pct / 100;
                        }
                        
                        // Parse max value
                        float maxWidth = containerWidth;
                        if (maxVal == "1fr" || maxVal == "auto")
                            maxWidth = containerWidth / 4; // Placeholder for fr distribution
                        else if (maxVal.EndsWith("px") || maxVal.EndsWith("em") || maxVal.EndsWith("rem"))
                            maxWidth = ParseCssLength(maxVal);
                        else if (maxVal.EndsWith("%"))
                        {
                            if (float.TryParse(maxVal.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                                maxWidth = containerWidth * pct / 100;
                        }
                        
                        // Use max as initial, clamp by min later when distributing
                        // For now, use max but ensure it's at least min
                        widths.Add(Math.Max(minWidth, maxWidth));
                    }
                }
                // Plain number
                else if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                {
                    widths.Add(val);
                }
            }
            
            // Distribute fr units properly
            if (widths.Count > 0)
            {
                float totalFixed = widths.Sum();
                float remaining = containerWidth - totalFixed;
                int frCount = widths.Count(w => w == containerWidth / 4);
                
                if (frCount > 0 && remaining > 0)
                {
                    float perFr = remaining / frCount;
                    for (int i = 0; i < widths.Count; i++)
                    {
                        if (widths[i] == containerWidth / 4)
                            widths[i] = perFr;
                    }
                }
            }
            
            return widths;
        }

        /// <summary>
        /// Expand repeat() in grid-template
        /// </summary>
        private string ExpandRepeat(string template, float containerWidth)
        {
            var match = Regex.Match(template, @"repeat\s*\(\s*(\d+|auto-fill|auto-fit)\s*,\s*([^)]+)\s*\)");
            if (!match.Success) return template;
            
            string countStr = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();
            
            int count = 4; // Default
            if (int.TryParse(countStr, out int explicitCount))
            {
                count = explicitCount;
            }
            else if (countStr == "auto-fill" || countStr == "auto-fit")
            {
                // Calculate how many columns fit
                // Handle minmax(min, max) - use the minimum value for counting
                float colWidth = 0;
                
                var minmaxMatch = Regex.Match(value, @"minmax\s*\(\s*([^,]+)\s*,\s*([^)]+)\s*\)");
                if (minmaxMatch.Success)
                {
                    string minVal = minmaxMatch.Groups[1].Value.Trim();
                    // Use minimum value for calculating how many fit
                    if (minVal.EndsWith("px") || minVal.EndsWith("em") || minVal.EndsWith("rem"))
                        colWidth = ParseCssLength(minVal);
                    else if (minVal.EndsWith("%"))
                    {
                        if (float.TryParse(minVal.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                            colWidth = containerWidth * pct / 100;
                    }
                    else
                        colWidth = ParseCssLength(value); // Fallback
                }
                else
                {
                    colWidth = ParseCssLength(value);
                }
                
                if (colWidth > 0)
                {
                    // Account for potential gaps (estimate 10px gap if not specified)
                    float estimatedGap = 10;
                    count = Math.Max(1, (int)((containerWidth + estimatedGap) / (colWidth + estimatedGap)));
                }
            }
            
            var expanded = string.Join(" ", Enumerable.Repeat(value, count));
            return template.Replace(match.Value, expanded);
        }
        
        private float ComputeTableLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth)
        {
            maxChildWidth = 0;
            float startY = contentBox.Top;
            float currentY = startY;

            // 1. Identify Rows and Cells
            var rows = new List<List<LiteElement>>();
            
            void CollectRows(LiteElement parent) 
            {
                 if (parent.Children == null) return;
                 foreach(var c in parent.Children)
                 {
                     string t = c.Tag?.ToUpperInvariant();
                     if (t == "TR") {
                         var cells = new List<LiteElement>();
                         if (c.Children != null) {
                            foreach(var cell in c.Children) {
                                string ct = cell.Tag?.ToUpperInvariant();
                                if(ct=="TD"||ct=="TH") cells.Add(cell);
                            }
                         }
                         rows.Add(cells);
                     } else if (t == "THEAD" || t == "TBODY" || t == "TFOOT") {
                         CollectRows(c);
                     }
                 }
            }
            CollectRows(node);

            if (rows.Count == 0) return 0;

            // 2. Build Grid Map (Coordinate System)
            var occupied = new HashSet<(int, int)>();
            var cellData = new List<TableGridCell>(); 
            
            int maxCols = 0;
            int currentRowIndex = 0;
            
            foreach(var row in rows)
            {
                int currentColIndex = 0;
                foreach(var cell in row)
                {
                    // Find next available slot
                    while (occupied.Contains((currentRowIndex, currentColIndex)))
                    {
                        currentColIndex++;
                    }

                    // Parse Span
                    int rowspan = 1;
                    int colspan = 1;
                    if (cell.Attr != null)
                    {
                        if (cell.Attr.TryGetValue("rowspan", out var rs)) int.TryParse(rs, out rowspan);
                        if (cell.Attr.TryGetValue("colspan", out var cs)) int.TryParse(cs, out colspan);
                    }
                    if (rowspan < 1) rowspan = 1;
                    if (colspan < 1) colspan = 1;

                    // Mark Occupied
                    for (int r = 0; r < rowspan; r++)
                    {
                        for (int c = 0; c < colspan; c++)
                        {
                            occupied.Add((currentRowIndex + r, currentColIndex + c));
                        }
                    }

                    cellData.Add(new TableGridCell 
                    { 
                        Element = cell, 
                        Row = currentRowIndex, 
                        Col = currentColIndex, 
                        RowSpan = rowspan, 
                        ColSpan = colspan 
                    });

                    if (currentColIndex + colspan > maxCols) maxCols = currentColIndex + colspan;

                    currentColIndex += colspan;
                }
                currentRowIndex++;
            }
            
            // 3. Measure Columns (Intrinsic Widths)
            float[] colWidths = new float[maxCols];
            
            // First pass: Measure 1x1 cells
            foreach(var cd in cellData)
            {
                if (cd.ColSpan == 1)
                {
                    ComputeLayout(cd.Element, 0, 0, 10000, shrinkToContent: true);
                    if (_boxes.TryGetValue(cd.Element, out var box))
                    {
                        if (box.MarginBox.Width > colWidths[cd.Col]) colWidths[cd.Col] = box.MarginBox.Width;
                    }
                }
            }
            
            // Ensure min width
            for(int i=0; i<maxCols; i++) if (colWidths[i] < 10) colWidths[i] = 10;

            // 4. Calculate Column X Positions
            float[] colX = new float[maxCols + 1];
            float cx = contentBox.Left;
            for(int i=0; i<maxCols; i++)
            {
                colX[i] = cx;
                cx += colWidths[i];
            }
            colX[maxCols] = cx; // End position
            
            if (cx - contentBox.Left > maxChildWidth) maxChildWidth = cx - contentBox.Left;

            // 5. Layout Rows & Heights
            float[] rowHeights = new float[currentRowIndex];
            float[] rowY = new float[currentRowIndex + 1];
            
            foreach(var cd in cellData)
            {
                float w = colX[cd.Col + cd.ColSpan] - colX[cd.Col];
                ComputeLayout(cd.Element, 0, 0, w, shrinkToContent: false);
                
                if (_boxes.TryGetValue(cd.Element, out var box))
                {
                     if (cd.RowSpan == 1)
                     {
                         if (box.MarginBox.Height > rowHeights[cd.Row]) rowHeights[cd.Row] = box.MarginBox.Height;
                     }
                }
            }

            // Calculate Y positions
            float cy = startY;
            for(int i=0; i<currentRowIndex; i++)
            {
                rowY[i] = cy;
                if (rowHeights[i] < 20) rowHeights[i] = 20; // Min height
                cy += rowHeights[i];
            }
            rowY[currentRowIndex] = cy;

            // 6. Final Positioning
            foreach(var cd in cellData)
            {
                float x = colX[cd.Col];
                float y = rowY[cd.Row];
                float w = colX[cd.Col + cd.ColSpan] - x;
                float h = rowY[cd.Row + cd.RowSpan] - y; 
                
                ComputeLayout(cd.Element, x, y, w, shrinkToContent: false);
                
                if (_boxes.TryGetValue(cd.Element, out var box))
                {
                    float delta = h - box.MarginBox.Height;
                    if (delta > 0)
                    {
                         box.MarginBox.Bottom += delta;
                         box.BorderBox.Bottom += delta;
                         box.PaddingBox.Bottom += delta;
                         box.ContentBox.Bottom += delta;
                         _boxes[cd.Element] = box;
                    }
                }
            }

            return rowY[currentRowIndex] - startY;
        }

        public void ShiftTree(LiteElement node, float dx, float dy)
        {
             // First, shift the node itself
             if (_boxes.TryGetValue(node, out var b))
             {
                 b.MarginBox.Offset(dx, dy);
                 b.BorderBox.Offset(dx, dy);
                 b.PaddingBox.Offset(dx, dy);
                 b.ContentBox.Offset(dx, dy);
                 _boxes[node] = b;
             }
             
             // Then shift all children recursively
             if (node.Children != null)
             {
                 foreach(var c in node.Children)
                 {
                     ShiftTree(c, dx, dy);
                 }
             }
        }
        
        private class TableGridCell
        {
            public LiteElement Element;
            public int Row;
            public int Col;
            public int RowSpan;
            public int ColSpan;
        }

        private void ComputeAbsoluteLayout(LiteElement node, SKRect containerBox)
        {
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);
            
            // Original style preservation
            double? originalWidth = style?.Width;
            double? originalHeight = style?.Height;
            
            // Check constraints
            bool hasLeft = style?.Left.HasValue == true;
            bool hasRight = style?.Right.HasValue == true;
            bool hasTop = style?.Top.HasValue == true;
            bool hasBottom = style?.Bottom.HasValue == true;
            
            var margin = style?.Margin ?? new Thickness(0);
            var padding = style?.Padding ?? new Thickness(0);
            var border = style?.BorderThickness ?? new Thickness(0);

            // 1. Horizontal Resolution
            float x = containerBox.Left;
            bool widthConstrained = false;
            
            if (hasLeft && hasRight)
            {
                // Left + Right + Width + Margins = ContainerWidth
                // Width = ContainerWidth - Left - Right - Margins - Borders - Padding
                float leftVal = (float)style.Left.Value;
                float rightVal = (float)style.Right.Value;
                float totalMargin = (float)margin.Left + (float)margin.Right;
                float totalBorder = (float)border.Left + (float)border.Right;
                float totalPadding = (float)padding.Left + (float)padding.Right;
                
                float contentW = containerBox.Width - leftVal - rightVal - totalMargin - totalBorder - totalPadding;
                if (contentW < 0) contentW = 0;
                
                if (style != null) style.Width = contentW;
                widthConstrained = true;
                x = containerBox.Left + leftVal;
            }
            else if (hasLeft)
            {
                x = containerBox.Left + (float)style.Left.Value;
            }
            // Right-only handled after layout
            
            // 2. Vertical Resolution
            float y = containerBox.Top;
            bool heightConstrained = false;
            
            if (hasTop && hasBottom)
            {
                float topVal = (float)style.Top.Value;
                float bottomVal = (float)style.Bottom.Value;
                float totalMargin = (float)margin.Top + (float)margin.Bottom;
                float totalBorder = (float)border.Top + (float)border.Bottom;
                float totalPadding = (float)padding.Top + (float)padding.Bottom;
                
                float contentH = containerBox.Height - topVal - bottomVal - totalMargin - totalBorder - totalPadding;
                if (contentH < 0) contentH = 0;
                
                if (style != null) style.Height = contentH;
                heightConstrained = true;
                y = containerBox.Top + topVal;
            }
            else if (hasTop)
            {
                y = containerBox.Top + (float)style.Top.Value;
            }
            // Bottom-only handled after layout
            
            // 3. Layout / Measurement
            // If width is constrained, use container width (ComputeLayout will pick up style.Width)
            // If not constrained, use container width but allow shrinking
            float availableWidth = containerBox.Width;
            bool shrink = !widthConstrained;
            
            ComputeLayout(node, x, y, availableWidth, shrinkToContent: shrink);
            
            // 4. Post-Layout Adjustments (Right/Bottom only)
            if (_boxes.TryGetValue(node, out var box))
            {
                // Handle Right alignment (if not Left+Right)
                if (hasRight && !hasLeft)
                {
                    float rightVal = (float)style.Right.Value;
                    // Right edge of margin box should be at containerRight - rightVal
                    float targetRight = containerBox.Right - rightVal;
                    float dx = targetRight - box.MarginBox.Right;
                    
                    if (Math.Abs(dx) > 0.1f)
                    {
                        ShiftTree(node, dx, 0);
                        // Update ref to moved box
                        _boxes.TryGetValue(node, out box);
                    }
                }
                
                // Handle Bottom alignment (if not Top+Bottom)
                if (hasBottom && !hasTop)
                {
                    float bottomVal = (float)style.Bottom.Value;
                    // Bottom edge of margin box should be at containerBottom - bottomVal
                    float targetBottom = containerBox.Bottom - bottomVal;
                    float dy = targetBottom - box.MarginBox.Bottom;
                    
                    if (Math.Abs(dy) > 0.1f)
                    {
                        ShiftTree(node, 0, dy);
                         _boxes.TryGetValue(node, out box);
                    }
                }
                
                // DEBUG LOGGING SPECIFICALLY FOR ACID2 CHIN - ADD FILE LOGGING
                string dClass = node.Attr?.GetValueOrDefault("class", "") ?? "";
                if (dClass.Contains("chin") || dClass.Contains("smile"))
                {
                    string logMsg = $"[ACID2-FIX] Computed Absolute: Tag={node.Tag} Class={dClass} " +
                        $"Top={(hasTop ? style.Top : "null")} Bottom={(hasBottom ? style.Bottom : "null")} Height={box.MarginBox.Height} " +
                        $"Constraint: {(heightConstrained ? "DUAL" : "SINGLE/NONE")}";
                    FenLogger.Debug(logMsg, LogCategory.Layout);
                        }
            }
            
            // 5. Restore Styles
            if (style != null)
            {
                style.Width = originalWidth;
                style.Height = originalHeight;
            }
        
            // DEBUG: Detailed log for Acid2 debugging
            if (_boxes.TryGetValue(node, out var debugBox))
            {
                string dTag = node.Tag?.ToUpperInvariant();
                string dClass = node.Attr?.GetValueOrDefault("class", "") ?? "";
                string dId = node.Attr?.GetValueOrDefault("id", "") ?? "";
                
                // Log key Acid2 elements
                if (dClass.Contains("face") || dClass.Contains("eyes") || dClass.Contains("smile") || 
                    dClass.Contains("scalp") || dClass.Contains("chin") || dClass.Contains("beard") || 
                    dClass.Contains("nose") || dId == "eyes" || dId == "smile")
                {
                     FenLogger.Debug($"[ACID2] {dTag} .{dClass} #{dId}: " +
                         $"POS={style?.Position} " +
                         $"TOP={style?.Top} LEFT={style?.Left} " +
                         $"MARGIN={debugBox.Margin.Top},{debugBox.Margin.Right},{debugBox.Margin.Bottom},{debugBox.Margin.Left} " +
                         $"BOX={debugBox.MarginBox}", LogCategory.Layout);
                         
                     if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                         $"[ACID2] {dTag} .{dClass} #{dId} POS={style?.Position} BOX={debugBox.MarginBox} OFFSET={x},{y}\r\n"); } catch {} }                }
            } 
        }

        private bool IsAbsolute(LiteElement node)
        {
            if (_styles != null && _styles.TryGetValue(node, out var s))
            {
                return s?.Position == "absolute" || s?.Position == "fixed";
            }
            return false;
        }

        private void DrawLayout(LiteElement node, SKCanvas canvas)
        {
            if (node == null) return;
            
            // Fetch style at the beginning so it's available for overlays too
            CssComputed layoutStyle = null;
            if (_styles != null) _styles.TryGetValue(node, out layoutStyle);

            if (!_boxes.TryGetValue(node, out var box)) return;
            
            // 1. Initial Checks

            // DEBUG LAYOUT VISUALIZATION
            if (DEBUG_LAYOUT)
            {
                // Draw debug outlines for box model visualization
                // Magenta = Margin box, Red = Border box, Green = Padding box, Blue = Content box
                using (var debugPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
                {
                    // Margin box (magenta, dashed effect via alpha)
                    debugPaint.Color = new SKColor(255, 0, 255, 80);
                    canvas.DrawRect(box.MarginBox, debugPaint);
                    
                    // Border box (red)
                    debugPaint.Color = new SKColor(255, 0, 0, 100);
                    canvas.DrawRect(box.BorderBox, debugPaint);
                    
                    // Padding box (green)
                    debugPaint.Color = new SKColor(0, 255, 0, 100);
                    canvas.DrawRect(box.PaddingBox, debugPaint);
                    
                    // Content box (blue)
                    debugPaint.Color = new SKColor(0, 0, 255, 100);
                    canvas.DrawRect(box.ContentBox, debugPaint);
                }
                
                // Log key element positions
                string tagName = node.Tag?.ToUpperInvariant();
                string className = node.Attr?.GetValueOrDefault("class", "") ?? "";
                string nodeId = node.Attr?.GetValueOrDefault("id", "") ?? "";
                // Log all body children and key elements
                if (tagName == "HTML" || tagName == "BODY" || tagName == "H2" || tagName == "DIV" || 
                    className.Contains("intro") || className.Contains("picture") ||
                    className.Contains("face") || className.Contains("eyes") || className.Contains("top") ||
                    className.Contains("bottom"))
                {
                    if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[BOX] {tagName} id={nodeId} class={className} Top={box.MarginBox.Top} Bottom={box.MarginBox.Bottom} Height={box.MarginBox.Height} POS={(_styles.ContainsKey(node) ? _styles[node].Position : "N/A")} H%={(_styles.ContainsKey(node) ? _styles[node].HeightPercent : -1)}\r\n"); } catch {} }                }
            }

            // Capture Inputs for Overlay
            string overlayTag = node.Tag?.ToUpperInvariant();
            bool isOverlay = false;
            string overlayType = "text";
            string overlayValue = "";

            // DEBUG: Log submit button positions at DrawLayout time
            // DEBUG: Log ALL input button positions at DrawLayout time (Standard Logger)
            if (overlayTag == "INPUT" || (node.Tag != null && node.Tag.Equals("input", StringComparison.OrdinalIgnoreCase)))
            {
                node.Attr.TryGetValue("type", out var inputType);
                node.Attr.TryGetValue("value", out var inputVal);
                string cssClass = node.Attr?.GetValueOrDefault("class", "") ?? "";
                
                string bg = layoutStyle?.BackgroundColor?.ToString() ?? "null";
                string bgRaw = layoutStyle?.Map?.ContainsKey("background-color") == true ? layoutStyle.Map["background-color"] : "missing";
                
                FenLogger.Debug($"[DrawLayout] INPUT DETECTED: Type='{inputType}' Val='{inputVal}' Class='{cssClass}' BG={bg} RAW_BG={bgRaw} Box={box.PaddingBox}", LogCategory.Layout);
            }
            
            if (overlayTag == "TEXTAREA")
            {
                isOverlay = true;
                string textareaId = node.Attr?.TryGetValue("id", out var tid) == true ? tid : "no-id";
                // FenLogger.Debug($"[DrawLayout] TEXTAREA detected id={textareaId}: box.BorderBox={box.BorderBox} Width={box.BorderBox.Width} Height={box.BorderBox.Height}", LogCategory.Layout);
            }
            else if (overlayTag == "BUTTON")
            {
                // HTML BUTTON element - use Avalonia Button overlay
                overlayValue = GetTextContentExcludingStyle(node)?.Trim() ?? ""; // Button label from text content, excluding STYLE elements
                // Skip buttons that contain CSS-like content (likely have STYLE children)
                if (overlayValue.Contains("{") && overlayValue.Contains(":") && overlayValue.Contains("}"))
                {
                    // This looks like CSS content, skip this button overlay
                    isOverlay = false;
                }
                // Skip "AI Mode" button from Google - it's a Google-specific feature that requires complex JS
                else if (overlayValue.ToLowerInvariant().Contains("ai mode"))
                {
                    isOverlay = false;
                }
                else
                {
                    isOverlay = false;
                    overlayType = "button";
                    // Truncate if too long
                    if (overlayValue.Length > 50) overlayValue = overlayValue.Substring(0, 50);
                }
            }
            else if (overlayTag == "INPUT")
            {
                 overlayValue = node.Attr != null && node.Attr.TryGetValue("value", out var v) ? v : "";
                 if (node.Attr != null && node.Attr.TryGetValue("type", out var t)) overlayType = t.ToLowerInvariant();
                 // Checkbox, Radio, Hidden are NOT overlays
                 // Button, Submit, Reset use NATIVE rendering to respect CSS styles (background, border, font)
                 // We do NOT use Overlay for them anymore, to prevent "Incorrect Button Styles" regression.
                 // Color, Range, File, Date need custom drawing fallback if overlay not supported
                 if (overlayType != "checkbox" && overlayType != "radio" && overlayType != "hidden" &&
                     overlayType != "color" && overlayType != "range" && overlayType != "file" && overlayType != "date" &&
                     overlayType != "submit" && overlayType != "button" && overlayType != "reset")
                 {
                     isOverlay = true;
                 }
            }
            else if (overlayTag == "SELECT")
            {
                isOverlay = true;
                overlayType = "select";
            }
            
            if (isOverlay)
            {
                 // Only if visible and within reasonable viewport bounds
                 // Skip elements positioned way off-screen (e.g., Left > 1920 on typical displays)
                 bool isWithinViewport = box.BorderBox.Left < 1920 && box.BorderBox.Left > -100;
                 if (box.BorderBox.Width > 0 && box.BorderBox.Height > 0 && isWithinViewport)
                 {
                     // FIX: Cap INPUT and TEXTAREA element overlay heights to prevent them from stretching
                     // to viewport height when parent containers have height: 100%
                     // Text inputs should never exceed ~50px in height unless explicitly styled
                     // TEXTAREA used as search boxes (single-line appearance) should also be capped
                     SKRect overlayBounds = box.PaddingBox;
                     if (overlayTag == "INPUT")
                     {
                         // Cap text input height at maximum reasonable value
                         float maxInputHeight = 50f;
                         if (overlayType == "submit" || overlayType == "button" || overlayType == "reset")
                         {
                             maxInputHeight = 60f; // Buttons can be slightly taller
                         }
                         
                         if (overlayBounds.Height > maxInputHeight)
                         {
                             FenLogger.Debug($"[DrawLayout] INPUT height capped from {overlayBounds.Height} to {maxInputHeight}", LogCategory.Layout);
                             overlayBounds = new SKRect(
                                 overlayBounds.Left,
                                 overlayBounds.Top,
                                 overlayBounds.Right,
                                 overlayBounds.Top + maxInputHeight
                             );
                         }
                     }
                     else if (overlayTag == "TEXTAREA")
                     {
                         // TEXTAREA used as search boxes (like Brave Search) can have incorrect heights
                         // Cap at reasonable maximum - real multi-line textareas should have explicit CSS height
                         float maxTextareaHeight = 100f;
                         
                         // If the TEXTAREA is extremely tall (>200px), it's likely a layout bug
                         if (overlayBounds.Height > maxTextareaHeight)
                         {
                             FenLogger.Debug($"[DrawLayout] TEXTAREA height capped from {overlayBounds.Height} to {maxTextareaHeight}", LogCategory.Layout);
                             overlayBounds = new SKRect(
                                 overlayBounds.Left,
                                 overlayBounds.Top,
                                 overlayBounds.Right,
                                 overlayBounds.Top + maxTextareaHeight
                             );
                         }
                     }
                     
                     // Extract placeholder from HTML attribute
                     string overlayPlaceholder = null;
                     if (node.Attr != null)
                     {
                         node.Attr.TryGetValue("placeholder", out overlayPlaceholder);
                         if (string.IsNullOrEmpty(overlayPlaceholder))
                             node.Attr.TryGetValue("aria-label", out overlayPlaceholder);
                     }
                     
                     var overlayData = new InputOverlayData
                     {
                         Node = node,
                         Bounds = overlayBounds,
                         Type = overlayTag == "TEXTAREA" ? "textarea" : overlayType,
                         InitialText = overlayValue,
                         Placeholder = overlayPlaceholder,
                         
                         // Populate Styles
                         BackgroundColor = layoutStyle?.BackgroundColor.HasValue == true ? new SKColor(layoutStyle.BackgroundColor.Value.Red, layoutStyle.BackgroundColor.Value.Green, layoutStyle.BackgroundColor.Value.Blue, layoutStyle.BackgroundColor.Value.Alpha) : (SKColor?)null,
                         TextColor = layoutStyle?.ForegroundColor.HasValue == true ? new SKColor(layoutStyle.ForegroundColor.Value.Red, layoutStyle.ForegroundColor.Value.Green, layoutStyle.ForegroundColor.Value.Blue, layoutStyle.ForegroundColor.Value.Alpha) : (SKColor?)null,
                         FontFamily = layoutStyle?.FontFamily?.FamilyName,
                         FontSize = (float)(layoutStyle?.FontSize ?? 16.0),
                         BorderThickness = layoutStyle?.BorderThickness ?? new Thickness(1),
                         BorderColor = layoutStyle?.BorderBrushColor.HasValue == true ? new SKColor(layoutStyle.BorderBrushColor.Value.Red, layoutStyle.BorderBrushColor.Value.Green, layoutStyle.BorderBrushColor.Value.Blue, layoutStyle.BorderBrushColor.Value.Alpha) : (SKColor?)null,
                         BorderRadius = layoutStyle?.BorderRadius ?? new CornerRadius(0),
                         TextAlign = layoutStyle?.TextAlign?.ToString().ToLowerInvariant(),
                         
                         // Pseudo-element styles (::placeholder, ::selection)
                         PlaceholderColor = layoutStyle?.Placeholder?.ForegroundColor.HasValue == true ? new SKColor(layoutStyle.Placeholder.ForegroundColor.Value.Red, layoutStyle.Placeholder.ForegroundColor.Value.Green, layoutStyle.Placeholder.ForegroundColor.Value.Blue, layoutStyle.Placeholder.ForegroundColor.Value.Alpha) : (SKColor?)null,
                         PlaceholderFontFamily = layoutStyle?.Placeholder?.FontFamily?.FamilyName,
                         PlaceholderFontSize = (float)(layoutStyle?.Placeholder?.FontSize ?? 0),
                         
                         SelectionColor = layoutStyle?.Selection?.ForegroundColor.HasValue == true ? new SKColor(layoutStyle.Selection.ForegroundColor.Value.Red, layoutStyle.Selection.ForegroundColor.Value.Green, layoutStyle.Selection.ForegroundColor.Value.Blue, layoutStyle.Selection.ForegroundColor.Value.Alpha) : (SKColor?)null,
                         SelectionBackgroundColor = layoutStyle?.Selection?.BackgroundColor.HasValue == true ? new SKColor(layoutStyle.Selection.BackgroundColor.Value.Red, layoutStyle.Selection.BackgroundColor.Value.Green, layoutStyle.Selection.BackgroundColor.Value.Blue, layoutStyle.Selection.BackgroundColor.Value.Alpha) : (SKColor?)null
                     };

                     // Extract Options for Select
                     if (overlayTag == "SELECT" && node.Children != null)
                     {
                         int idx = 0;
                         foreach(var child in node.Children)
                         {
                             if (child.Tag?.ToUpperInvariant() == "OPTION")
                             {
                                 string txt = child.Text ?? "";
                                 overlayData.Options.Add(txt);
                                 if (child.Attr != null && child.Attr.ContainsKey("selected"))
                                 {
                                     overlayData.SelectedIndex = idx;
                                 }
                                 idx++;
                             }
                         }
                         if (overlayData.SelectedIndex == -1 && overlayData.Options.Count > 0) overlayData.SelectedIndex = 0;
                     }

                     // Enhanced logging for debugging overlay containers
                     LiteElement parentNode = null;
                     _parents.TryGetValue(node, out parentNode);
                     string parentTag = parentNode?.Tag ?? "NONE";
                     string parentClass = parentNode?.Attr?.TryGetValue("class", out var pc) == true ? pc : "";
                     
                     // Check if parent has a background color
                     CssComputed parentStyle = null;
                     if (parentNode != null && _styles != null) _styles.TryGetValue(parentNode, out parentStyle);
                     string parentBg = parentStyle?.BackgroundColor.HasValue == true ? parentStyle.BackgroundColor.Value.ToString() : "none";
                     
                     FenLogger.Debug($"[DrawLayout] Adding overlay: type={overlayData.Type} bounds={overlayData.Bounds} parentTag={parentTag} parentClass={parentClass} parentBg={parentBg}", LogCategory.Layout);
                     CurrentOverlays.Add(overlayData);
                 }
                 return; // Skip drawing Skia representation
            }
            
            // (layoutStyle already fetched at top of method)

            // FIX: Position:fixed elements need to counter the scroll offset so they stay at viewport position
            // We do this AFTER early debug returns but BEFORE visibility checks (so children get context)
            bool isFixed = layoutStyle?.Position?.ToLowerInvariant() == "fixed";
            if (isFixed && _scrollOffsetY > 0)
            {
                canvas.Save();
                canvas.Translate(0, _scrollOffsetY);
            }

            // Check visibility
            string visibility = layoutStyle?.Map?.ContainsKey("visibility") == true ? layoutStyle.Map["visibility"]?.ToLowerInvariant() : null;
            
            // DIALOG ELEMENT: hidden by default unless 'open' attribute is present
            // WORKAROUND: Some sites (like Google) use dialog to wrap search forms that need to show
            if (node.Tag?.ToUpperInvariant() == "DIALOG")
            {
                bool hasOpen = node.Attr?.ContainsKey("open") == true;
                
                // Check if dialog contains a form - if so, likely should be visible (e.g., Google search)
                bool containsForm = node.Descendants().Any(d => d.Tag?.ToLowerInvariant() == "form");
                
                if (!hasOpen && !containsForm)
                {
                    // Dialog is closed and doesn't contain a form - don't render
                    if (isFixed && _scrollOffsetY > 0) canvas.Restore();
                    return;
                }
                
                // If dialog has a form but no 'open', treat it as inline content (don't center it)
                // This allows the dialog's children to render in normal flow
                
                // Dialog is open (has 'open' attribute) - center it in the viewport if position not absolute/fixed already
                // Only center if explicitly opened, not if just rendering because it contains a form
                if (hasOpen)
                {
                    string dialogPos = layoutStyle?.Position?.ToLowerInvariant();
                    if (dialogPos != "absolute" && dialogPos != "fixed")
                    {
                        // Position dialog at center of viewport
                        float dialogWidth = box.BorderBox.Width;
                        float dialogHeight = box.BorderBox.Height;
                        float centerX = (_viewport.Width - dialogWidth) / 2;
                        float centerY = (_viewport.Height - dialogHeight) / 2;
                        
                        // Adjust box position
                        float offsetX = centerX - box.BorderBox.Left;
                        float offsetY = centerY - box.BorderBox.Top;
                        
                        box = new BoxModel
                        {
                            MarginBox = new SKRect(box.MarginBox.Left + offsetX, box.MarginBox.Top + offsetY, box.MarginBox.Right + offsetX, box.MarginBox.Bottom + offsetY),
                            BorderBox = new SKRect(box.BorderBox.Left + offsetX, box.BorderBox.Top + offsetY, box.BorderBox.Right + offsetX, box.BorderBox.Bottom + offsetY),
                            PaddingBox = new SKRect(box.PaddingBox.Left + offsetX, box.PaddingBox.Top + offsetY, box.PaddingBox.Right + offsetX, box.PaddingBox.Bottom + offsetY),
                            ContentBox = new SKRect(box.ContentBox.Left + offsetX, box.ContentBox.Top + offsetY, box.ContentBox.Right + offsetX, box.ContentBox.Bottom + offsetY),
                            Border = box.Border
                        };
                        _boxes[node] = box;
                    }
                }
            }
            
            if (visibility == "hidden" || visibility == "collapse")
            {
                // Still take up space but don't render - skip to children
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        DrawLayout(child, canvas);
                    }
                }
                
                // RESTORE if we saved for fixed position
                if (isFixed && _scrollOffsetY > 0) canvas.Restore();
                
                return;
            }

            // Handle position:relative offset
            // For relative positioning, we offset the visual rendering without affecting layout
            bool isRelativePositioned = false;
            float relativeOffsetX = 0f, relativeOffsetY = 0f;
            string positionValue = layoutStyle?.Position?.ToLowerInvariant();
            if (positionValue == "relative" || positionValue == "sticky")
            {
                isRelativePositioned = true;
                
                // Get containing block dimensions (parent content box)
                float parentW = 0, parentH = 0;
                var parentNode = node.Parent;
                if (parentNode != null && _boxes.TryGetValue(parentNode, out var parentBox))
                {
                    parentW = parentBox.ContentBox.Width;
                    parentH = parentBox.ContentBox.Height;
                }
                
                // left overrides right, top overrides bottom
                if (layoutStyle.Left.HasValue)
                    relativeOffsetX = (float)layoutStyle.Left.Value;
                else if (layoutStyle.LeftPercent.HasValue)
                    relativeOffsetX = (float)layoutStyle.LeftPercent.Value / 100f * parentW;
                else if (layoutStyle.Right.HasValue)
                    relativeOffsetX = -(float)layoutStyle.Right.Value;
                else if (layoutStyle.RightPercent.HasValue)
                    relativeOffsetX = -((float)layoutStyle.RightPercent.Value / 100f * parentW);
                
                if (layoutStyle.Top.HasValue)
                    relativeOffsetY = (float)layoutStyle.Top.Value;
                else if (layoutStyle.TopPercent.HasValue)
                    relativeOffsetY = (float)layoutStyle.TopPercent.Value / 100f * parentH;
                else if (layoutStyle.Bottom.HasValue)
                    relativeOffsetY = -(float)layoutStyle.Bottom.Value;
                else if (layoutStyle.BottomPercent.HasValue)
                    relativeOffsetY = -((float)layoutStyle.BottomPercent.Value / 100f * parentH);
            }

            // Get opacity (default 1.0 = fully opaque)
            float opacity = 1f;
            if (layoutStyle?.Opacity.HasValue == true)
                opacity = (float)layoutStyle.Opacity.Value;

            // Get border-radius (CornerRadius is not nullable, check TopLeft > 0)
            float borderRadius = 0f;
            if (layoutStyle?.BorderRadius.TopLeft > 0)
                borderRadius = (float)layoutStyle.BorderRadius.TopLeft;

            // Parse transform using full 3D transform support
            SKMatrix? transformMatrix = null;
            if (!string.IsNullOrEmpty(layoutStyle?.Transform))
            {
                var transform3D = CssTransform3D.Parse(layoutStyle.Transform);
                if (transform3D.HasTransform)
                {
                    transformMatrix = transform3D.ToSKMatrix();
                }
            }

            // Parse box-shadow
            List<BoxShadowParsed> shadows = null;
            if (!string.IsNullOrEmpty(layoutStyle?.BoxShadow))
            {
                shadows = ParseBoxShadow(layoutStyle.BoxShadow);
            }

            // Parse text-decoration for non-text nodes (for links etc)
            TextDecorationParsed textDeco = null;
            if (!string.IsNullOrEmpty(layoutStyle?.TextDecoration))
            {
                textDeco = ParseTextDecoration(layoutStyle.TextDecoration);
            }

            // Get filter
            string filter = layoutStyle?.Filter;

            // Apply position:relative offset (before transforms)
            if (isRelativePositioned && (relativeOffsetX != 0 || relativeOffsetY != 0))
            {
                canvas.Save();
                canvas.Translate(relativeOffsetX, relativeOffsetY);
            }

            // Apply transform if present (using combined 3D matrix)
            bool hasTransform = transformMatrix.HasValue && !transformMatrix.Value.IsIdentity;
            
            if (hasTransform)
            {
                canvas.Save();
                
                // Apply transforms around the element center
                float cx = box.BorderBox.MidX;
                float cy = box.BorderBox.MidY;
                
                // Build centered transform: translate to center, apply matrix, translate back
                var centeredMatrix = SKMatrix.CreateTranslation(cx, cy);
                centeredMatrix = centeredMatrix.PreConcat(transformMatrix.Value);
                centeredMatrix = centeredMatrix.PreConcat(SKMatrix.CreateTranslation(-cx, -cy));
                
                canvas.Concat(ref centeredMatrix);
            }

            // Apply clip-path if present
            bool hasClipPath = !string.IsNullOrEmpty(layoutStyle?.ClipPath) && layoutStyle.ClipPath != "none";
            SKPath clipPathSkia = null;
            if (hasClipPath)
            {
                clipPathSkia = ParseClipPath(layoutStyle.ClipPath, box.BorderBox);
                if (clipPathSkia != null)
                {
                    canvas.Save();
                    canvas.ClipPath(clipPathSkia);
                }
            }

            // -0.5: Apply backdrop-filter (blur/effects behind the element, e.g. frosted glass)
            string backdropFilter = layoutStyle?.Map?.TryGetValue("backdrop-filter", out var bdf) == true ? bdf : null;
            if (string.IsNullOrEmpty(backdropFilter))
                layoutStyle?.Map?.TryGetValue("-webkit-backdrop-filter", out backdropFilter);
            
            if (!string.IsNullOrEmpty(backdropFilter) && backdropFilter != "none")
            {
                float blurRadius = ParseBackdropBlur(backdropFilter);
                if (blurRadius > 0)
                {
                    // Create blur effect behind the element
                    using (var blurFilter = SKImageFilter.CreateBlur(blurRadius, blurRadius))
                    using (var paint = new SKPaint())
                    {
                        paint.ImageFilter = blurFilter;
                        
                        // Save current state and apply blur to area behind element
                        canvas.SaveLayer(box.BorderBox, paint);
                        canvas.Restore();
                    }
                }
            }
            
            // -0.4: Apply mix-blend-mode (controls how element blends with background)
            string blendModeStr = layoutStyle?.Map?.TryGetValue("mix-blend-mode", out var mbm) == true ? mbm : null;
            SKBlendMode blendMode = ParseBlendMode(blendModeStr);
            bool hasBlendMode = blendMode != SKBlendMode.SrcOver;
            
            if (hasBlendMode)
            {
                canvas.SaveLayer(new SKPaint { BlendMode = blendMode });
            }
            
            // -0.3: Apply isolation (creates new stacking context)
            string isolation = layoutStyle?.Map?.TryGetValue("isolation", out var iso) == true ? iso : null;
            bool hasIsolation = isolation == "isolate";
            if (hasIsolation)
            {
                canvas.SaveLayer();
            }

            // 0. Draw Box Shadows (before background)
            if (shadows != null && shadows.Count > 0)
            {
                DrawBoxShadow(canvas, box.BorderBox, borderRadius, shadows, opacity);
            }

            // 1. Draw Background (with opacity, rounded corners, and filter)
            // First try gradient brush, then fall back to solid color
            bool backgroundDrawn = false;
            
            if (!string.IsNullOrEmpty(layoutStyle?.BackgroundImage))
            {
                var shader = CreateShaderFromCss(layoutStyle.BackgroundImage, box.BorderBox, opacity);
                if (shader != null)
                {
                    if (_useDisplayList)
                    {
                        AddCommand(new DrawShaderRectCommand
                        {
                            Rect = box.BorderBox,
                            Shader = shader,
                            BorderRadius = borderRadius,
                            Bounds = box.BorderBox,
                            Opacity = opacity
                        });
                        backgroundDrawn = true;
                    }
                    else
                    {
                        using (var paint = new SKPaint())
                        {
                            paint.Shader = shader;
                            paint.IsAntialias = true;
                            
                            if (!string.IsNullOrEmpty(filter))
                                ApplyFilter(paint, filter);
                            
                            if (borderRadius > 0)
                                canvas.DrawRoundRect(box.BorderBox, borderRadius, borderRadius, paint);
                            else
                                canvas.DrawRect(box.BorderBox, paint);
                        }
                        backgroundDrawn = true;
                    }
                    shader.Dispose();
                }
            }
            
            if (!backgroundDrawn && layoutStyle?.BackgroundColor.HasValue == true)
            {
                var c = layoutStyle.BackgroundColor.Value;
                byte alpha = (byte)(c.Alpha * opacity);
                
                // Debug: Log white/light backgrounds that are large (potential issue indicators)
                bool isLightBackground = c.Red > 240 && c.Green > 240 && c.Blue > 240;
                bool isLargeElement = box.BorderBox.Width > 300 && box.BorderBox.Height > 80;
                if (isLightBackground && isLargeElement)
                {
                    string nodeDbgTag = node.Tag ?? "TEXT";
                    string nodeDbgClass = node.Attr?.TryGetValue("class", out var nc) == true ? nc : "";
                    FenLogger.Debug($"[DrawLayout] Large white background: tag={nodeDbgTag} class={nodeDbgClass} color=RGB({c.Red},{c.Green},{c.Blue}) box={box.BorderBox}", LogCategory.Layout);
                }
                
                // Use display list if enabled, otherwise draw directly
                if (_useDisplayList)
                {
                    if (borderRadius > 0)
                    {
                        AddCommand(new DrawRoundRectCommand
                        {
                            Rect = box.BorderBox,
                            RadiusX = borderRadius,
                            RadiusY = borderRadius,
                            Color = new SKColor(c.Red, c.Green, c.Blue, alpha),
                            Bounds = box.BorderBox,
                            Opacity = 1f // Already applied to alpha
                        });
                    }
                    else
                    {
                        AddCommand(new DrawRectCommand
                        {
                            Rect = box.BorderBox,
                            Color = new SKColor(c.Red, c.Green, c.Blue, alpha),
                            Bounds = box.BorderBox,
                            Opacity = 1f
                        });
                    }
                }
                else
                {
                    using (var paint = new SKPaint { Color = new SKColor(c.Red, c.Green, c.Blue, alpha) })
                    {
                        // Apply filter if present
                        if (!string.IsNullOrEmpty(filter))
                            ApplyFilter(paint, filter);
                        
                        if (borderRadius > 0)
                            canvas.DrawRoundRect(box.BorderBox, borderRadius, borderRadius, paint);
                        else
                            canvas.DrawRect(box.BorderBox, paint); 
                    }
                }
            }

            // 1.5 Draw inset shadows (after background, inside element)
            if (shadows != null)
            {
                DrawInsetShadow(canvas, box.BorderBox, borderRadius, shadows, opacity);
            }

            // 2. Draw Borders (with opacity, rounded corners, and multiple styles)
            if (DEBUG_FILE_LOGGING && _boxes.Count < 500) // Log draws for debug
            {
                 try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\render_debug.txt", $"[DRAW] Tag={node.Tag} Box={box.BorderBox} Opacity={opacity} Visibility={visibility}\r\n"); } catch {} 
            }

            if (box.Border.Left > 0 || box.Border.Top > 0 || box.Border.Right > 0 || box.Border.Bottom > 0)
            {
                DrawStyledBorders(canvas, box, layoutStyle, borderRadius, opacity);
            }

            // 2.2 Draw Input Controls (Color, Range, Checkbox, Radio if not overlays)
            if (node.Tag?.ToUpperInvariant() == "INPUT")
            {
                DrawInputControl(node, box, layoutStyle, canvas, opacity);
            }
            
            // 2.3 Draw METER element (value indicator within a range)
            if (node.Tag?.ToUpperInvariant() == "METER")
            {
                DrawMeterElement(node, box, canvas, opacity);
            }
            
            // 2.4 Draw PROGRESS element (completion indicator)
            if (node.Tag?.ToUpperInvariant() == "PROGRESS")
            {
                DrawProgressElement(node, box, canvas, opacity);
            }
            
            // 2.45 Apply border-radius content clipping (clips children/content to rounded corners)
            // This is separate from overflow clipping - it ensures images/children are rounded
            bool hasBorderRadiusClip = false;
            SKPath borderRadiusClipPath = null;
            
            if (borderRadius > 0)
            {
                hasBorderRadiusClip = true;
                canvas.Save();
                borderRadiusClipPath = new SKPath();
                borderRadiusClipPath.AddRoundRect(box.PaddingBox, borderRadius, borderRadius);
                canvas.ClipPath(borderRadiusClipPath);
            }

            // 2.5 Apply overflow clipping (before drawing content like text, images, children)
            // This ensures "red leaks" (content overflowing padding box) are hidden
            bool hasOverflowClip = false;
            SKPath clipPathSaved = null; // Track if we need to dispose a path
            
            // Use typed OverflowX and OverflowY properties (already resolved from CSS)
            string overflowX = layoutStyle?.OverflowX?.ToLowerInvariant() ?? "visible";
            string overflowY = layoutStyle?.OverflowY?.ToLowerInvariant() ?? "visible";
            
            // Determine if we need to clip on each axis
            bool clipX = overflowX == "hidden" || overflowX == "clip" || overflowX == "scroll" || overflowX == "auto";
            bool clipY = overflowY == "hidden" || overflowY == "clip" || overflowY == "scroll" || overflowY == "auto";
            
            // DEBUG: Log overflow values for debugging
            if (clipX || clipY)
            {
                if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[DrawLayout] tag={node.Tag} class={node.Attr?.GetValueOrDefault("class", "")} overflowX='{overflowX}' overflowY='{overflowY}'\r\n"); } catch {} }
            }
            
            if (clipX || clipY)
            {
                hasOverflowClip = true;
                canvas.Save();
                
                // Calculate clip rect based on which axes need clipping
                SKRect clipRect = box.PaddingBox;
                
                if (!clipX)
                {
                    // Don't clip horizontally - extend to viewport bounds
                    clipRect = new SKRect(-10000, clipRect.Top, 10000, clipRect.Bottom);
                }
                if (!clipY)
                {
                    // Don't clip vertically - extend to viewport bounds
                    clipRect = new SKRect(clipRect.Left, -10000, clipRect.Right, 10000);
                }
                
                // Use rounded rect clipping if border-radius is present
                if (borderRadius > 0 && clipX && clipY)
                {
                    clipPathSaved = new SKPath();
                    clipPathSaved.AddRoundRect(clipRect, borderRadius, borderRadius);
                    canvas.ClipPath(clipPathSaved);
                }
                else
                {
                    canvas.ClipRect(clipRect);
                }
                
                FenLogger.Debug($"[DrawLayout] Applied overflow clipping for tag={node.Tag} clipX={clipX} clipY={clipY} box={clipRect}", LogCategory.Rendering);
            }

            // 3. Draw Text (with wrapping support)
            if (node.IsText && !string.IsNullOrWhiteSpace(node.Text))
            {
                using (var paint = new SKPaint())
                {
                    float fontSize = layoutStyle?.FontSize != null ? (float)layoutStyle.FontSize.Value : DefaultFontSize;
                    paint.TextSize = fontSize;
                    paint.Color = SKColors.Black; 
                    paint.IsAntialias = true;

                    // Get line height
                    float lineHeight = box.LineHeight > 0 ? box.LineHeight : fontSize * DefaultLineHeightMultiplier;

                    // SAFE ACCESS: Use ForegroundColor struct
                    SKColor textColor = SKColors.Black;
                    if (layoutStyle?.ForegroundColor.HasValue == true)
                    {
                        var c = layoutStyle.ForegroundColor.Value;
                        textColor = new SKColor(c.Red, c.Green, c.Blue, c.Alpha);
                        paint.Color = textColor;
                    }
                    else
                    {
                        // Check if parent is link (Default UA style)
                        var parent = GetParent(node);
                        if (parent != null && parent.Tag?.ToUpperInvariant() == "A")
                        {
                            textColor = SKColors.Blue;
                            paint.Color = textColor;
                        }
                    }
                    
                    // Apply opacity
                    textColor = textColor.WithAlpha((byte)(textColor.Alpha * opacity));
                    paint.Color = textColor;
                    
                    // Text Alignment
                    string textAlign = "left";
                    float containerWidth = box.ContentBox.Width;
                    var textParent = GetParent(node);
                    CssComputed parentStyle = null;
                    if (textParent != null && _styles != null && _styles.TryGetValue(textParent, out parentStyle))
                    {
                        if (parentStyle?.TextAlign != null)
                            textAlign = parentStyle.TextAlign.ToString().ToLowerInvariant();
                        containerWidth = _boxes.TryGetValue(textParent, out var parentBox) ? parentBox.ContentBox.Width : containerWidth;
                    }
                    
                    try
                    {
                        string ff = layoutStyle?.FontFamily?.ToString();
                        paint.Typeface = ResolveTypeface(ff, node.Text);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Debug($"[DrawLayout] Font resolution failed for '{layoutStyle?.FontFamily}': {ex.Message}", LogCategory.Rendering);
                    }
                    
                    // Get text decoration from parent
                    TextDecorationParsed deco = null;
                    if (textParent != null && _styles != null && _styles.TryGetValue(textParent, out var parentStyleDeco))
                    {
                        if (!string.IsNullOrEmpty(parentStyleDeco?.TextDecoration))
                            deco = ParseTextDecoration(parentStyleDeco.TextDecoration);
                        
                        // Auto-underline links
                        if (textParent.Tag?.ToUpperInvariant() == "A" && (deco == null || (!deco.Underline && !deco.LineThrough && !deco.Overline)))
                        {
                            deco = new TextDecorationParsed { Underline = true };
                        }
                    }
                    
                    // Get word/letter spacing from style
                    double? wordSpacing = layoutStyle?.WordSpacing;
                    double? letterSpacing = layoutStyle?.LetterSpacing;
                    
                    // Get pseudo-element styles from parent (where they are attached)
                    CssComputed firstLineStyle = parentStyle?.FirstLine;
                    CssComputed firstLetterStyle = parentStyle?.FirstLetter;
                    
                    // Draw text lines (wrapped or single)
                    if (_textLines.TryGetValue(node, out var lines) && lines.Count > 0)
                    {
                        float drawY = box.ContentBox.Top + lineHeight - 5;
                        
                        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                        {
                            var line = lines[lineIndex];
                            float drawX = box.ContentBox.Left;
                            
                            // Determine if this is the first line
                            bool isFirstLine = lineIndex == 0;
                            
                            // Apply pseudo-element styles to paint
                            using (var linePaint = paint.Clone())
                            {
                                if (isFirstLine && firstLineStyle != null)
                                {
                                    ApplyPseudoStyle(linePaint, firstLineStyle, opacity);
                                }
                                
                                // Calculate Alignment X
                                float lineWidthWithSpacing = MeasureTextWithSpacing(linePaint, line.Text, wordSpacing, letterSpacing);
                                if (textAlign == "center")
                                    drawX = box.ContentBox.Left + (containerWidth - lineWidthWithSpacing) / 2;
                                else if (textAlign == "right")
                                    drawX = box.ContentBox.Left + containerWidth - lineWidthWithSpacing;

                                // Handle ::first-letter (only on first line)
                                if (isFirstLine && firstLetterStyle != null && !string.IsNullOrEmpty(line.Text))
                                {
                                    // Split first letter (grapheme cluster aware ideally, but char for now)
                                    string firstLetter = line.Text.Substring(0, 1);
                                    if (char.IsSurrogate(line.Text[0]) && line.Text.Length > 1)
                                        firstLetter = line.Text.Substring(0, 2);
                                    
                                    string restOfLine = line.Text.Substring(firstLetter.Length);
                                    
                                    // 1. Draw First Letter with specialized style
                                    using (var letterPaint = linePaint.Clone())
                                    {
                                        // First apply first-line (already applied to linePaint), then override with first-letter
                                        ApplyPseudoStyle(letterPaint, firstLetterStyle, opacity);
                                        
                                        // Draw letter
                                        DrawTextWithSpacing(canvas, firstLetter, drawX, drawY, letterPaint, wordSpacing, letterSpacing);
                                        
                                        // Advance X
                                        float letterWidth = MeasureTextWithSpacing(letterPaint, firstLetter, wordSpacing, letterSpacing);
                                        drawX += letterWidth;
                                    }
                                    
                                    // 2. Draw rest of line with linePaint (normal or first-line)
                                    if (!string.IsNullOrEmpty(restOfLine))
                                    {
                                        DrawTextWithSpacing(canvas, restOfLine, drawX, drawY, linePaint, wordSpacing, letterSpacing);
                                    }
                                }
                                else
                                {
                                    // Standard Line Draw
                                    DrawTextWithSpacing(canvas, line.Text, drawX, drawY, linePaint, wordSpacing, letterSpacing);
                                }
                            }
                            
                            // Draw text decoration for this line
                            // Note: Text decoration should also ideally support pseudo-element colors but keeping simple for now
                            if (deco != null && (deco.Underline || deco.Overline || deco.LineThrough))
                            {
                                var lineBox = new SKRect(drawX, drawY - lineHeight + 5, drawX + line.Width, drawY + 5);
                                DrawTextDecoration(canvas, deco, lineBox, fontSize, textColor);
                            }
                            
                            drawY += lineHeight;
                        }
                    }
                    else
                    {
                        // Fallback: single line draw (treat as first line)
                        float drawX = box.ContentBox.Left;
                        
                        using (var linePaint = paint.Clone())
                        {
                            if (firstLineStyle != null)
                            {
                                ApplyPseudoStyle(linePaint, firstLineStyle, opacity);
                            }

                            float actualTextWidth = MeasureTextWithSpacing(linePaint, node.Text, wordSpacing, letterSpacing);
                            
                            if (textAlign == "center")
                                drawX = box.ContentBox.Left + (containerWidth - actualTextWidth) / 2;
                            else if (textAlign == "right")
                                drawX = box.ContentBox.Left + containerWidth - actualTextWidth;
                            
                            // Handle ::first-letter
                            if (firstLetterStyle != null && !string.IsNullOrEmpty(node.Text))
                            {
                                string firstLetter = node.Text.Substring(0, 1);
                                if (char.IsSurrogate(node.Text[0]) && node.Text.Length > 1)
                                    firstLetter = node.Text.Substring(0, 2);
                                string restOfLine = node.Text.Substring(firstLetter.Length);

                                using (var letterPaint = linePaint.Clone())
                                {
                                     ApplyPseudoStyle(letterPaint, firstLetterStyle, opacity);
                                     DrawTextWithSpacing(canvas, firstLetter, drawX, box.ContentBox.Bottom - 5, letterPaint, wordSpacing, letterSpacing);
                                     drawX += MeasureTextWithSpacing(letterPaint, firstLetter, wordSpacing, letterSpacing);
                                }
                                
                                if (!string.IsNullOrEmpty(restOfLine))
                                {
                                    DrawTextWithSpacing(canvas, restOfLine, drawX, box.ContentBox.Bottom - 5, linePaint, wordSpacing, letterSpacing);
                                }
                            }
                            else
                            {
                                DrawTextWithSpacing(canvas, node.Text, drawX, box.ContentBox.Bottom - 5, linePaint, wordSpacing, letterSpacing);
                            }
                        }
                        
                        // Draw text decoration
                        if (deco != null && (deco.Underline || deco.Overline || deco.LineThrough))
                        {
                            DrawTextDecoration(canvas, deco, box.ContentBox, fontSize, textColor);
                        }
                    }
                }
            }
            
            // 3.5 Draw Replaced Elements Helpers
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "INPUT" || tag == "TEXTAREA")
            {
                // Skip hidden inputs entirely
                if (tag == "INPUT" && node.Attr != null && node.Attr.TryGetValue("type", out var hiddenCheck) && hiddenCheck.ToLowerInvariant() == "hidden")
                {
                    // Don't draw anything for hidden inputs
                    return;
                }
                
                // Draw a debug border/background if the element is otherwise invisible
                // Real browsers rely on UA stylesheet. We simulate it here.
                if (box.Border.Top == 0)
                {
                    using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Gray, StrokeWidth = 1 })
                    {
                        canvas.DrawRect(box.BorderBox, paint);
                    }
                }
                
                // Draw Value (Text)
                if (node.Attr != null && node.Attr.ContainsKey("value"))
                {
                    string val = node.Attr["value"];
                    string type = node.Attr.ContainsKey("type") ? node.Attr["type"].ToLowerInvariant() : "text";
                    
                    if (type == "checkbox")
                    {
                         using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 })
                         {
                             // Draw Box
                             float size = Math.Min(box.ContentBox.Width, box.ContentBox.Height) - 4;
                             if (size < 10) size = 10;
                             float x = box.ContentBox.Left + 2;
                             float y = box.ContentBox.MidY - size/2;
                             var rect = new SKRect(x, y, x+size, y+size);
                             canvas.DrawRect(rect, paint);
                             
                             // Draw Checkmark if checked
                             if (node.Attr.ContainsKey("checked"))
                             {
                                 paint.Style = SKPaintStyle.Fill;
                                 rect.Inflate(-2, -2);
                                 canvas.DrawRect(rect, paint);
                             }
                         }
                    }
                    else if (type == "radio")
                    {
                         using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true })
                         {
                             // Draw Circle
                             float size = Math.Min(box.ContentBox.Width, box.ContentBox.Height) - 4;
                             if (size < 10) size = 10;
                             float x = box.ContentBox.Left + size/2 + 2;
                             float y = box.ContentBox.MidY;
                             
                             // DEBUG: Red Circle Probe
                             if (paint.Color.Red >= 200 && paint.Color.Green < 50 && paint.Color.Blue < 50)
                             {
                                  string probeId = node?.GetAttribute("id") ?? "";
                                  string probeClass = node?.GetAttribute("class") ?? "";
                                  FenLogger.Debug($"[RedCircleProbe] RADIO detected RED! Id='{probeId}' Class='{probeClass}'", LogCategory.Rendering);
                             }

                             canvas.DrawCircle(x, y, size/2, paint);
                             
                             // Draw Dot if checked
                             if (node.Attr.ContainsKey("checked"))
                             {
                                 paint.Style = SKPaintStyle.Fill;
                                 canvas.DrawCircle(x, y, size/2 - 3, paint);
                             }
                         }
                    }
                    else if (!string.IsNullOrEmpty(val))
                    {
                         bool isBtn = type == "submit" || type == "button" || type == "reset" || tag == "BUTTON";
                         
                         using (var paint = new SKPaint())
                         {
                             paint.TextSize = layoutStyle?.FontSize != null ? (float)layoutStyle.FontSize.Value : DefaultFontSize;
                             paint.Color = SKColors.Black;
                             paint.IsAntialias = true;
                             paint.Typeface = ResolveTypeface(layoutStyle?.FontFamily?.ToString(), val); 
                             
                             // Center text for buttons
                             if (isBtn)
                             {
                                 paint.TextAlign = SKTextAlign.Center;
                                 float centerX = box.ContentBox.MidX;
                                 float centerY = box.ContentBox.MidY + (paint.TextSize / 2) - 2; 
                                 canvas.DrawText(val, centerX, centerY, paint);
                             }
                             else
                             {
                                 // Left align for text inputs
                                 float drawX = box.ContentBox.Left + 2; 
                                 float drawY = box.ContentBox.MidY + (paint.TextSize / 2) - 2;
                                 
                                 canvas.Save();
                                 canvas.ClipRect(box.ContentBox);
                                 canvas.DrawText(val, drawX, drawY, paint);
                                 canvas.Restore();
                             }
                         }
                    }
                }
            }

            if (tag == "IMG")
            {
                 // Fetch from ImageLoader
                 string src = node.Attr?.ContainsKey("src") == true ? node.Attr["src"] : null;
                 
                  // Resolve Relative URL
                 if (!string.IsNullOrEmpty(src) && !src.StartsWith("http") && !src.StartsWith("data:") && !string.IsNullOrEmpty(_baseUrl))
                 {
                     try 
                     {
                         var baseUri = new Uri(_baseUrl);
                         var resolved = new Uri(baseUri, src);
                         src = resolved.AbsoluteUri;
                     }
                     catch (Exception ex)
                     {
                         FenLogger.Debug($"[DrawLayout] Image URL resolution failed: base='{_baseUrl}' src='{src}' - {ex.Message}", LogCategory.Rendering);
                     }
                 }

                 var bitmap = ImageLoader.GetImage(src);
                 
                 if (bitmap != null)
                 {
                     // Draw Bitmap with object-fit support
                     string objectFit = layoutStyle?.Map?.TryGetValue("object-fit", out var of) == true ? of : "fill";
                     
                     using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                     {
                         if (objectFit == "fill")
                         {
                             if (_useDisplayList)
                             {
                                 AddCommand(new DrawImageCommand
                                 {
                                     Bitmap = bitmap,
                                     DestRect = box.ContentBox,
                                     Bounds = box.ContentBox,
                                     Opacity = opacity
                                 });
                             }
                             else
                             {
                                 canvas.DrawBitmap(bitmap, box.ContentBox, paint);
                             }
                         }
                         else
                         {
                             // Setup for customized rendering
                             float imgW = bitmap.Width;
                             float imgH = bitmap.Height;
                             float destW = box.ContentBox.Width;
                             float destH = box.ContentBox.Height;
                             
                             if (imgW > 0 && imgH > 0 && destW > 0 && destH > 0)
                             {
                                 float scaleX = destW / imgW;
                                 float scaleY = destH / imgH;
                                 float scale = 1.0f;
                                 
                                 SKRect srcRect = new SKRect(0, 0, imgW, imgH);
                                 SKRect dstRect = box.ContentBox;
                                 
                                 canvas.Save();
                                 canvas.ClipRect(box.ContentBox); // Clip to content box
                                 
                                 if (objectFit == "contain")
                                 {
                                     scale = Math.Min(scaleX, scaleY);
                                     float renderW = imgW * scale;
                                     float renderH = imgH * scale;
                                     float offsetX = (destW - renderW) / 2;
                                     float offsetY = (destH - renderH) / 2;
                                     
                                     dstRect = new SKRect(
                                         box.ContentBox.Left + offsetX,
                                         box.ContentBox.Top + offsetY,
                                         box.ContentBox.Left + offsetX + renderW,
                                         box.ContentBox.Top + offsetY + renderH);
                                 }
                                 else if (objectFit == "cover")
                                 {
                                     scale = Math.Max(scaleX, scaleY);
                                     // We fill the destination, but crop the source
                                     float rendersrcW = destW / scale;
                                     float rendersrcH = destH / scale;
                                     float cropX = (imgW - rendersrcW) / 2;
                                     float cropY = (imgH - rendersrcH) / 2;
                                     
                                     srcRect = new SKRect(cropX, cropY, cropX + rendersrcW, cropY + rendersrcH);
                                     dstRect = box.ContentBox;
                                 }
                                 else if (objectFit == "none")
                                 {
                                     // Draw original size centered
                                     float offsetX = (destW - imgW) / 2;
                                     float offsetY = (destH - imgH) / 2;
                                     dstRect = new SKRect(
                                         box.ContentBox.Left + offsetX,
                                         box.ContentBox.Top + offsetY,
                                         box.ContentBox.Left + offsetX + imgW,
                                         box.ContentBox.Top + offsetY + imgH);
                                 }
                                 else if (objectFit == "scale-down")
                                 {
                                     // Smallest of none or contain
                                     float containScale = Math.Min(scaleX, scaleY);
                                     scale = Math.Min(1.0f, containScale); // Don't upscale
                                     
                                     float renderW = imgW * scale;
                                     float renderH = imgH * scale;
                                     float offsetX = (destW - renderW) / 2;
                                     float offsetY = (destH - renderH) / 2;
                                     
                                     dstRect = new SKRect(
                                         box.ContentBox.Left + offsetX,
                                         box.ContentBox.Top + offsetY,
                                         box.ContentBox.Left + offsetX + renderW,
                                         box.ContentBox.Top + offsetY + renderH);
                                 }
                                 
                                 if (_useDisplayList)
                                 {
                                     AddCommand(new DrawImageCommand
                                     {
                                         Bitmap = bitmap,
                                         SourceRect = srcRect,
                                         DestRect = dstRect,
                                         Bounds = box.ContentBox,
                                         Opacity = opacity
                                     });
                                 }
                                 else
                                 {
                                     canvas.DrawBitmap(bitmap, srcRect, dstRect, paint);
                                 }
                                 canvas.Restore();
                             }
                         }
                     } 
                 }
                 else
                 {
                     // Draw placeholder X
                     if (_useDisplayList)
                     {
                         AddCommand(new DrawRectCommand
                         {
                             Rect = box.ContentBox,
                             Color = SKColors.LightGray,
                             Style = SKPaintStyle.Stroke,
                             Bounds = box.ContentBox,
                             Opacity = 1f
                         });
                         AddCommand(new DrawLineCommand
                         {
                             X1 = box.ContentBox.Left,
                             Y1 = box.ContentBox.Top,
                             X2 = box.ContentBox.Right,
                             Y2 = box.ContentBox.Bottom,
                             Color = SKColors.LightGray,
                             Bounds = box.ContentBox
                         });
                         AddCommand(new DrawLineCommand
                         {
                             X1 = box.ContentBox.Right,
                             Y1 = box.ContentBox.Top,
                             X2 = box.ContentBox.Left,
                             Y2 = box.ContentBox.Bottom,
                             Color = SKColors.LightGray,
                             Bounds = box.ContentBox
                         });
                     }
                     else
                     {
                         using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray })
                         {
                            canvas.DrawRect(box.ContentBox, paint);
                            canvas.DrawLine(box.ContentBox.Left, box.ContentBox.Top, box.ContentBox.Right, box.ContentBox.Bottom, paint);
                            canvas.DrawLine(box.ContentBox.Right, box.ContentBox.Top, box.ContentBox.Left, box.ContentBox.Bottom, paint);
                         }
                     }
                 }
            }
            if (tag == "SVG")
            {
                 string svgXml = GetOuterXml(node);
                 if (!string.IsNullOrEmpty(svgXml))
                 {
                     try
                     {
                         using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgXml)))
                         {
                             var svg = new Svg.Skia.SKSvg();
                             svg.Load(ms);
                             if (svg.Picture != null)
                             {
                                 canvas.Save();
                                 
                                 // Get SVG dimensions from viewBox or attributes
                                 var cull = svg.Picture.CullRect;
                                 float svgW = cull.Width;
                                 float svgH = cull.Height;
                                 
                                 // Check for viewBox attribute for accurate sizing
                                 if (node.Attr != null && node.Attr.TryGetValue("viewBox", out var viewBox))
                                 {
                                     var vbParts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                     if (vbParts.Length >= 4)
                                     {
                                         float.TryParse(vbParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out svgW);
                                         float.TryParse(vbParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out svgH);
                                     }
                                 }
                                 
                                 // Also get width/height from SVG attributes as fallback
                                 if ((svgW <= 0 || svgH <= 0) && node.Attr != null)
                                 {
                                     if (node.Attr.TryGetValue("width", out var wStr))
                                         float.TryParse(wStr.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out svgW);
                                     if (node.Attr.TryGetValue("height", out var hStr))
                                         float.TryParse(hStr.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out svgH);
                                 }
                                 
                                 // DEBUG: Log SVG dimensions
                                 string svgClass = node.GetAttribute("class") ?? "";
                                 if (DEBUG_FILE_LOGGING) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SVG] Class='{svgClass}' svgW={svgW} svgH={svgH} boxW={box.ContentBox.Width} boxH={box.ContentBox.Height} cullRect={cull}\r\n"); } catch {} }
                                 
                                 // If box has no size but SVG has intrinsic size, use SVG's native dimensions
                                 float targetW = box.ContentBox.Width;
                                 float targetH = box.ContentBox.Height;
                                 
                                 if (targetW <= 1 && svgW > 0) targetW = svgW;
                                 if (targetH <= 1 && svgH > 0) targetH = svgH;
                                 
                                 if (svgW > 0 && svgH > 0 && targetW > 0 && targetH > 0)
                                 {
                                     // Calculate scale to fit while preserving aspect ratio
                                     float scaleX = targetW / svgW;
                                     float scaleY = targetH / svgH;
                                     float scale = Math.Min(scaleX, scaleY); // Use uniform scale for aspect ratio
                                     
                                     // Center the SVG within the content box
                                     float scaledW = svgW * scale;
                                     float scaledH = svgH * scale;
                                     float offsetX = box.ContentBox.Left + (box.ContentBox.Width - scaledW) / 2;
                                     float offsetY = box.ContentBox.Top + (box.ContentBox.Height - scaledH) / 2;
                                     
                                     // If box has no size, position at box origin
                                     if (box.ContentBox.Width <= 1) offsetX = box.ContentBox.Left;
                                     if (box.ContentBox.Height <= 1) offsetY = box.ContentBox.Top;
                                     
                                     canvas.Translate(offsetX, offsetY);
                                     canvas.Scale(scale, scale);
                                     
                                     // Offset for viewBox origin
                                     if (cull.Left != 0 || cull.Top != 0)
                                     {
                                         canvas.Translate(-cull.Left, -cull.Top);
                                     }
                                 }
                                 else
                                 {
                                     canvas.Translate(box.ContentBox.Left, box.ContentBox.Top);
                                 }
                                 
                                 canvas.DrawPicture(svg.Picture);
                                 canvas.Restore();
                             }
                         }
                     }
                     catch (Exception ex)
                     {
                         FenLogger.Debug($"[DrawLayout] SVG rendering failed: {ex.Message}", LogCategory.Rendering);
                     }
                 }
            }

            // 3.8 Media Placeholders
            if (tag == "VIDEO" || tag == "AUDIO" || tag == "CANVAS" || tag == "IFRAME" || tag == "OBJECT" || tag == "EMBED")
            {
                 using (var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.DarkGray })
                 {
                      canvas.DrawRect(box.BorderBox, paint);
                 }
                 using (var paint = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true, TextAlign = SKTextAlign.Center })
                 {
                      float midX = box.BorderBox.MidX;
                      float midY = box.BorderBox.MidY + 4;
                      canvas.DrawText($"<{tag}>", midX, midY, paint);
                 }
            }
            
            // 3.6 Draw HR
            if (tag == "HR")
            {
                using (var paint = new SKPaint { Color = SKColors.LightGray, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
                {
                     float midY = box.MarginBox.MidY;
                     canvas.DrawLine(box.MarginBox.Left, midY, box.MarginBox.Right, midY, paint);
                }
            }

            // 3.7 Draw List Markers (Bullets/Numbers)
            if (tag == "LI")
            {
                 var parent = GetParent(node);
                 if (parent != null)
                 {
                     string pTag = parent.Tag?.ToUpperInvariant();
                     float markerX = box.MarginBox.Left + 10; // Indent
                     float markerY = box.ContentBox.Top + 14; // Approx baseline (16px font -> 14px down)
                     
                     // Get list-style-type from either LI or parent UL/OL
                     string listStyleType = layoutStyle?.ListStyleType?.ToLowerInvariant();
                     if (string.IsNullOrEmpty(listStyleType) || listStyleType == "inherit" || listStyleType == "initial")
                     {
                         // Check parent for list-style-type
                         CssComputed parentStyle = null;
                         if (_styles != null) _styles.TryGetValue(parent, out parentStyle);
                         listStyleType = parentStyle?.ListStyleType?.ToLowerInvariant();
                     }
                     
                     // Default based on parent tag
                     if (string.IsNullOrEmpty(listStyleType))
                     {
                         listStyleType = pTag == "OL" ? "decimal" : "disc";
                     }
                     
                     // Get ::marker styles if available
                     CssComputed markerStyle = layoutStyle?.Marker;
                     float markerFontSize = 12f;
                     SKColor markerColor = SKColors.Black;
                     string markerContent = null;
                     
                     if (markerStyle != null)
                     {
                         // Apply ::marker color
                         if (markerStyle.ForegroundColor.HasValue)
                         {
                             var c = markerStyle.ForegroundColor.Value;
                             markerColor = new SKColor(c.Red, c.Green, c.Blue, c.Alpha);
                         }
                         
                         // Apply ::marker font-size
                         if (markerStyle.FontSize.HasValue)
                         {
                             markerFontSize = (float)markerStyle.FontSize.Value;
                         }
                         
                         // Apply ::marker content (custom marker text)
                         markerContent = markerStyle.Content;
                         if (!string.IsNullOrEmpty(markerContent))
                         {
                             // Remove quotes from content value
                             markerContent = markerContent.Trim('"', '\'');
                         }
                     }

                     using (var paint = new SKPaint { Color = markerColor, IsAntialias = true, TextSize = markerFontSize })
                     {
                         // Apply foreground color if set (fallback if no ::marker style)
                         if (markerStyle == null && layoutStyle?.ForegroundColor.HasValue == true)
                         {
                             var c = layoutStyle.ForegroundColor.Value;
                             paint.Color = new SKColor(c.Red, c.Green, c.Blue, c.Alpha);
                         }
                         
                         // If custom content is set via ::marker, use it
                         if (!string.IsNullOrEmpty(markerContent))
                         {
                             canvas.DrawText(markerContent, markerX, markerY, paint);
                         }
                         else
                         {
                             // Use list-style-type based rendering
                             switch (listStyleType)
                             {
                             case "none":
                                 // No marker
                                 break;
                                 
                             case "disc":
                                 // Filled circle (default for UL)
                                 paint.Style = SKPaintStyle.Fill;
                                 
                                 // DEBUG: Red Circle Probe
                                 if (paint.Color.Red >= 200 && paint.Color.Green < 50 && paint.Color.Blue < 50)
                                 {
                                      string probeId = node?.GetAttribute("id") ?? "";
                                      string probeClass = node?.GetAttribute("class") ?? "";
                                      FenLogger.Debug($"[RedCircleProbe] LI Disc detected RED! Id='{probeId}' Class='{probeClass}'", LogCategory.Rendering);
                                 }

                                 canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 break;
                                 
                             case "circle":
                                 // Hollow circle
                                 paint.Style = SKPaintStyle.Stroke;
                                 paint.StrokeWidth = 1.5f;
                                 canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 break;
                                 
                             case "square":
                                 // Filled square
                                 paint.Style = SKPaintStyle.Fill;
                                 canvas.DrawRect(markerX - 3, markerY - 7, 6, 6, paint);
                                 break;
                                 
                             case "decimal":
                                 // Numbers (1, 2, 3...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "decimal-leading-zero":
                                 // Numbers with leading zeros (01, 02, 03...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index:D2}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-roman":
                                 // Lowercase Roman numerals (i, ii, iii...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{ToRomanNumeral(index).ToLower()}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "upper-roman":
                                 // Uppercase Roman numerals (I, II, III...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{ToRomanNumeral(index)}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-alpha":
                             case "lower-latin":
                                 // Lowercase letters (a, b, c...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     string letter = index < 26 ? ((char)('a' + index)).ToString() : $"a{index - 25}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "upper-alpha":
                             case "upper-latin":
                                 // Uppercase letters (A, B, C...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     string letter = index < 26 ? ((char)('A' + index)).ToString() : $"A{index - 25}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-greek":
                                 // Greek letters (α, β, γ...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     char[] greek = { 'α', 'β', 'γ', 'δ', 'ε', 'ζ', 'η', 'θ', 'ι', 'κ', 'λ', 'μ', 'ν', 'ξ', 'ο', 'π', 'ρ', 'σ', 'τ', 'υ', 'φ', 'χ', 'ψ', 'ω' };
                                     string letter = index < greek.Length ? greek[index].ToString() : $"{greek[0]}{index - greek.Length + 1}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             default:
                                 // Default to disc for unrecognized types
                                 if (pTag == "OL")
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index}.", markerX, markerY, paint);
                                 }
                                 else
                                 {
                                     paint.Style = SKPaintStyle.Fill;
                                     canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 }
                                 break;
                             }
                         }
                     }
                 }
            }

            // 4. Apply overflow clipping logic - MOVED TO BEFORE CONTENT DRAWING
            // (See step 2.5 above)


            // 5. Recurse (with z-index sorting)
            // 5. Recurse (Painting Order per Stacking Context)
            if (node.Children != null && node.Children.Count > 0)
            {
                // Group children by painting layer
                var negZ = new List<(LiteElement node, int z)>();
                var blocks = new List<LiteElement>();
                var floats = new List<LiteElement>();
                var inlines = new List<LiteElement>();
                var posZ = new List<(LiteElement node, int z)>(); // Includes z-index: auto (0) and > 0

                foreach (var child in node.Children)
                {
                    CssComputed childStyle = null;
                    if (_styles != null) _styles.TryGetValue(child, out childStyle);
                    
                    bool isPositioned = childStyle?.Position != null && childStyle.Position != "static";
                    int zIndex = childStyle?.ZIndex ?? 0;
                    bool isFloat = !string.IsNullOrEmpty(childStyle?.Float) && childStyle.Float != "none";
                    
                    if (isPositioned)
                    {
                        if (zIndex < 0) negZ.Add((child, zIndex));
                        else posZ.Add((child, zIndex));
                    }
                    else if (isFloat)
                    {
                        floats.Add(child);
                    }
                    else
                    {
                        // Determine if block or inline level
                        bool isText = child.IsText;
                        string display = childStyle?.Display?.ToLowerInvariant();
                        
                        if (isText || display == "inline" || display == "inline-block" || display == "inline-flex" || display == "inline-grid" || display == "inline-table")
                        {
                            inlines.Add(child);
                        }
                        else
                        {
                            blocks.Add(child);
                        }
                    }
                }

                // 1. Negative Z-Index (Backgrounds of Stacking Context descendants)
                foreach (var item in negZ.OrderBy(x => x.z)) DrawLayout(item.node, canvas);

                // 2. Block Level (Non-positioned) - Backgrounds/Borders painted first (simplification: paint whole element)
                foreach (var item in blocks) DrawLayout(item, canvas);

                // 3. Floats (Non-positioned)
                foreach (var item in floats) DrawLayout(item, canvas);

                // 4. Inline Level (Non-positioned) - Text/Images
                foreach (var item in inlines) DrawLayout(item, canvas);

                // 5. Positive/Auto Z-Index (Positioned)
                foreach (var item in posZ.OrderBy(x => x.z)) DrawLayout(item.node, canvas);
            }
            
            // 6. Restore canvas after overflow clipping
            if (hasOverflowClip)
            {
                canvas.Restore();
                if (clipPathSaved != null) clipPathSaved.Dispose();
            }
            
            // 6.5 Restore canvas after border-radius content clipping
            if (hasBorderRadiusClip)
            {
                canvas.Restore();
                if (borderRadiusClipPath != null) borderRadiusClipPath.Dispose();
            }

            
            // 5. Restore canvas if we applied transform
            if (hasTransform)
            {
                canvas.Restore();
            }
            
            // 6. Restore canvas if we applied position:relative offset
            if (isRelativePositioned && (relativeOffsetX != 0 || relativeOffsetY != 0))
            {
                canvas.Restore();
            }
            
            // 7. Restore canvas if we applied clip-path
            if (hasClipPath && clipPathSkia != null)
            {
                canvas.Restore();
                clipPathSkia.Dispose();
            }
            
            // 8. Restore canvas if we applied position:fixed counter-translate
            if (isFixed && _scrollOffsetY > 0)
            {
                canvas.Restore();
            }
            
            // 9. Restore isolation layer (if applied)
            if (hasIsolation)
            {
                canvas.Restore();
            }
            
            // 10. Restore blend mode layer (if applied)
            if (hasBlendMode)
            {
                canvas.Restore();
            }
        }
        
        /// <summary>
        /// Recursively checks if an element or any of its descendants contains button/form elements.
        /// Used to detect button wrapper containers for inline layout heuristic.
        /// </summary>
        private bool ContainsButtonOrFormDescendant(LiteElement element)
        {
            if (element == null) return false;
            
            string tag = element.Tag?.ToUpperInvariant();
            if (tag == "BUTTON" || tag == "INPUT" || tag == "SELECT")
            {
                return true;
            }
            
            // For INPUT, also check type to ensure it's not hidden
            if (tag == "INPUT" && element.Attr != null)
            {
                element.Attr.TryGetValue("type", out var inputType);
                if (inputType?.ToLowerInvariant() == "hidden")
                    return false;
            }
            
            // Check children recursively
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (ContainsButtonOrFormDescendant(child))
                        return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an element contains significant block content (paragraphs, headings, lists).
        /// If true, the container should NOT be treated as inline even if it contains buttons.
        /// </summary>
        private bool ContainsSignificantBlockContent(LiteElement element)
        {
            if (element == null) return false;
            
            string tag = element.Tag?.ToUpperInvariant();
            // These are tags that indicate substantive block content
            var significantBlockTags = new HashSet<string> { "P", "H1", "H2", "H3", "H4", "H5", "H6", "UL", "OL", "LI", "TABLE", "PRE", "ARTICLE", "SECTION" };
            
            if (significantBlockTags.Contains(tag))
                return true;
            
            // Check children recursively (but only first level to avoid false positives)
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    string childTag = child.Tag?.ToUpperInvariant();
                    if (significantBlockTags.Contains(childTag))
                        return true;
                }
            }
            
            return false;
        }
        
        private void ApplyUserAgentStyles(LiteElement node, ref CssComputed style)
        {
            if (node == null) return;
            
            // Delegate to decoupled UAStyleProvider module
            UAStyleProvider.Apply(node, ref style);
            
            string tag = node.Tag?.ToUpperInvariant();

            if (tag == "HTML" || tag == "BODY")
            {
                if (style == null) style = new CssComputed();
                
                // Ensure Root elements fill the viewport (Critical for modern layouts using 100% or vh)
                if (!style.Height.HasValue && !style.HeightPercent.HasValue)
                {
                    style.HeightPercent = 100;
                }
                if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
            }

            if (tag == "BODY")
            {
                // Default browser margin is usually 8px
                if (style.Margin.Left == 0 && style.Margin.Top == 0 && style.Margin.Right == 0 && style.Margin.Bottom == 0)
                {
                    style.Margin = new Thickness(8);
                }
            }

            if (tag.Length == 2 && tag[0] == 'H' && char.IsDigit(tag[1]))
            {
                if (style == null) style = new CssComputed();
                if (!style.FontSize.HasValue)
                {
                    double baseSize = 16.0; // Default
                    switch (tag)
                    {
                        case "H1": baseSize = 32.0; break; // 2em
                        case "H2": baseSize = 24.0; break; // 1.5em
                        case "H3": baseSize = 18.72; break; // 1.17em
                        case "H4": baseSize = 16.0; break; // 1em
                        case "H5": baseSize = 13.28; break; // 0.83em
                        case "H6": baseSize = 10.72; break; // 0.67em
                    }
                    style.FontSize = baseSize;
                }
                if (!style.FontWeight.HasValue) style.FontWeight = 700; // Bold
                
                // Add default margins if not present (simple em approximation)
                if (style.Margin.Left == 0 && style.Margin.Top == 0 && style.Margin.Right == 0 && style.Margin.Bottom == 0)
                {
                    double marginEm = 1.0;
                    if (tag == "H1" || tag == "H2") marginEm = 0.67; 
                    float m = (float)(style.FontSize.Value * marginEm);
                    style.Margin = new Thickness(0, m, 0, m);
                }
            }

            if (tag == "INPUT" || tag == "TEXTAREA" || tag == "BUTTON" || tag == "SELECT" || tag == "FIELDSET")
            {
                if (style == null) style = new CssComputed();
                
                // Check if this is a button-type input
                string inputType = node.Attr?.ContainsKey("type") == true ? node.Attr["type"]?.ToLowerInvariant() : "";
                bool isButtonType = tag == "BUTTON" || inputType == "submit" || inputType == "button" || inputType == "reset";

                // 1. Force Background if missing or transparent
                // Note: SkiaColor transparent is 0 (alpha 0)
                bool hasBackground = style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0;
                
                if (!hasBackground && tag != "FIELDSET") // Fieldset transparent by default
                {
                    if (isButtonType)
                    {
                        // Google-style button: light gray background (#f8f9fa)
                        style.BackgroundColor = new SKColor(0xf8, 0xf9, 0xfa);
                    }
                    else
                    {
                        // Text inputs: white background
                        style.BackgroundColor = SKColors.White;
                    }
                }

                // 2. Force Border if missing
                if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
                {
                    if (tag == "FIELDSET")
                    {
                         style.BorderThickness = new Thickness(1); // Usually groove/etched
                         style.BorderBrushColor = SKColors.DarkGray;
                         style.Margin = new Thickness(2, 2, 2, 2); // Default margin
                    }
                    else if (isButtonType)
                    {
                        // Google-style button: border matches background (#f8f9fa)
                        style.BorderThickness = new Thickness(1);
                        style.BorderBrushColor = new SKColor(0xf8, 0xf9, 0xfa);
                    }
                    else
                    {
                        // Text inputs: gray border
                        style.BorderThickness = new Thickness(1);
                        style.BorderBrushColor = SKColors.Gray;
                    }
                }
                
                // 3. Force Padding if missing
                if (style.Padding.Top == 0 && style.Padding.Left == 0)
                {
                    if (tag == "FIELDSET") 
                         style.Padding = new Thickness(10, 10, 10, 10); // Give space for Legend
                    else if (isButtonType)
                         style.Padding = new Thickness(16, 8, 16, 8); // Google buttons have more horizontal padding
                    else
                         style.Padding = new Thickness(5, 2, 5, 2);
                }
                
                // 4. Add default border-radius for modern button appearance
                if (isButtonType && style.BorderRadius.TopLeft == 0)
                {
                    style.BorderRadius = new CornerRadius(8); // Google uses 8px radius
                }
                
                // 5. Add border-radius for search inputs (rounded pill-style)
                if (!isButtonType && (tag == "INPUT" || tag == "TEXTAREA") && style.BorderRadius.TopLeft == 0)
                {
                    style.BorderRadius = new CornerRadius(24); // Google search box uses 24px radius
                }
                
                // 6. Add height constraints for input elements to prevent oversized controls
                // This fixes Google search box being too tall
                if (tag == "INPUT" && !isButtonType)
                {
                    // Single-line inputs should have reasonable height
                    if (!style.Height.HasValue && !style.MaxHeight.HasValue)
                    {
                        // Don't override explicit height, but set max to prevent stretch
                        style.MaxHeight = 44; // Google search box is ~44px
                    }
                }
                
                if (tag == "TEXTAREA")
                {
                    // Textareas can be taller but shouldn't be unbounded
                    if (!style.Height.HasValue && !style.MinHeight.HasValue)
                    {
                        style.MinHeight = 60; // Reasonable minimum
                    }
                }
                
                // 7. Set align-self to prevent flexbox stretch
                if ((tag == "INPUT" || tag == "BUTTON" || tag == "SELECT") && string.IsNullOrEmpty(style.AlignSelf))
                {
                    style.AlignSelf = "center"; // Prevent stretching in flex containers
                }
                
                // 8. Set display to inline-block for buttons to keep them in a row
                if (isButtonType && string.IsNullOrEmpty(style.Display))
                {
                    style.Display = "inline-block"; // Buttons should be inline, not block
                }
            }
            
            // Fix for "Not Clickable" / "Mashed Together" links
            // Add default padding to A tags to increase hit target and spacing
            if (tag == "A")
            {
                 if (style == null) style = new CssComputed();
                 if (style.Padding.Top == 0 && style.Padding.Right == 0) 
                 {
                     // Add horizontal padding (space between links)
                     style.Padding = new Thickness(4, 0, 4, 0);
                 }
            }

            // Table and Cell Styles
            if (tag == "TABLE" || tag == "TD" || tag == "TH")
            {
                if (style == null) style = new CssComputed();
                
                // Force Border if missing (emulates border="1")
                if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
                {
                    style.BorderThickness = new Thickness(1);
                    style.BorderBrushColor = SKColors.Gray;
                }
                
                // Add padding for cells
                if ((tag == "TD" || tag == "TH") && style.Padding.Top == 0 && style.Padding.Left == 0)
                {
                    style.Padding = new Thickness(5, 5, 5, 5);
                }
            }
        }

        public LiteElement HitTest(float x, float y)
        {
             // We need to find the specific element.
             // Because _boxes is flat, we can iterate all, but we want the 'highest Z-order',
             // which usually means the last rendered or the deepest in the tree.
             
             LiteElement bestMatch = null;
             // Start with root? We don't have root stored. 
             // We can iterate _boxes. The dictionary order is not guaranteed z-order.
             // However, smaller boxes usually are children of larger boxes.
             // So if we find all boxes containing point, the one with SMALLEST area is likely the leaf.
             
             float minArea = float.MaxValue;
             
             // ConcurrentDictionary.ToArray() is thread-safe
             var boxSnapshot = _boxes.ToArray();
             
             foreach (var kvp in boxSnapshot)
             {
                 var element = kvp.Key;
                 var box = kvp.Value;
                 
                 // Visibility check
                 if (box.BorderBox.Width <= 0 || box.BorderBox.Height <= 0) continue;
                 
                 if (box.BorderBox.Contains(x,y))
                 {
                     float area = box.BorderBox.Width * box.BorderBox.Height;
                     // Prefer smaller area (child over parent)
                     // If equal area (e.g. block wrapping inline), prefer content? 
                     // Or prefer element that is NOT the body/html if poss.
                     
                     if (area <= minArea) // Use <= to let later items override? Random.
                     {
                         // If area is same, prefer the one that is 'deeper' in DOM? 
                         // We don't know depth here.
                         // But usually we want links (A tags) over spans over divs.
                         
                         minArea = area;
                         bestMatch = element;
                     }
                 }
             }
             
             return bestMatch;
        }

        private bool ShouldHide(LiteElement node, CssComputed style)
        {
            if (node == null) return true;
            
            // 1. Tag Filtering
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "DATALIST" || tag == "TEMPLATE")
                return true;

            // 2. Hidden inputs should not be rendered
            if (tag == "INPUT" && node.Attr != null && node.Attr.TryGetValue("type", out var inputType) && inputType.ToLowerInvariant() == "hidden")
                return true;

            // 3. CSS Display: None
            if (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
        
        /// <summary>
        /// Get all text content from an element and its children
        /// </summary>
        private string GetTextContent(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text ?? "";
            
            var sb = new System.Text.StringBuilder();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    sb.Append(GetTextContent(child));
                }
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Get text content from an element, excluding STYLE and SCRIPT elements
        /// </summary>
        private string GetTextContentExcludingStyle(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text ?? "";
            
            // Skip STYLE and SCRIPT elements entirely
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "STYLE" || tag == "SCRIPT") return "";
            
            var sb = new System.Text.StringBuilder();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    sb.Append(GetTextContentExcludingStyle(child));
                }
            }
            return sb.ToString();
        }
        
        public LiteElement GetParent(LiteElement node)
        {
            if (node == null) return null;
            _parents.TryGetValue(node, out var parent);
            return parent;
        }
        
        /// <summary>
        /// Find an element by its 'id' attribute, searching recursively through the DOM tree.
        /// </summary>
        private LiteElement FindElementById(LiteElement root, string id)
        {
            if (root == null || string.IsNullOrEmpty(id)) return null;
            
            // Check if this element has the matching ID
            if (root.Attr != null && root.Attr.TryGetValue("id", out var elemId))
            {
                if (string.Equals(elemId, id, StringComparison.OrdinalIgnoreCase))
                    return root;
            }
            
            // Recursively search children
            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    var found = FindElementById(child, id);
                    if (found != null) return found;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Find the nearest positioned ancestor (position: relative/absolute/fixed) for position:absolute elements.
        /// Returns the content box of that ancestor, or the viewport if no positioned ancestor is found.
        /// </summary>
        private SKRect FindPositionedAncestorBox(LiteElement element)
        {
            var current = GetParent(element);
            while (current != null)
            {
                var style = GetStyle(current);
                if (style != null)
                {
                    var pos = style.Position?.ToLowerInvariant();
                    if (pos == "relative" || pos == "absolute" || pos == "fixed" || pos == "sticky")
                    {
                        // Found a positioned ancestor - return its content box
                        if (_boxes.TryGetValue(current, out var ancestorBox))
                        {
                            FenLogger.Debug($"[FindPositionedAncestor] element={element.Tag} found ancestor={current.Tag} class={current.Attr?.GetValueOrDefault("class", "")} pos={pos} box={ancestorBox.ContentBox}", LogCategory.Layout);                            return ancestorBox.ContentBox;
                        }
                        else
                        {
                            // Positioned ancestor found but not yet laid out - log this and use viewport as fallback
                            FenLogger.Debug($"[FindPositionedAncestor] element={element.Tag} FOUND ANCESTOR={current.Tag} class={current.Attr?.GetValueOrDefault("class", "")} pos={pos} BUT BOX NOT YET COMPUTED, using viewport", LogCategory.Layout);                            return _viewport;
                        }
                    }
                }
                current = GetParent(current);
            }
            
            // No positioned ancestor found - use viewport (initial containing block)
            FenLogger.Debug($"[FindPositionedAncestor] element={element.Tag} no positioned ancestor, using viewport={_viewport}", LogCategory.Layout);            return _viewport;
        }


        
        public CssComputed GetStyle(LiteElement node)
        {
            if (node == null || _styles == null) return null;
            _styles.TryGetValue(node, out var style);
            return style;
        }

        private SKTypeface ResolveTypeface(string fontFamily, string text)
        {
            // 1. Try CSS Font Families
            if (!string.IsNullOrEmpty(fontFamily))
            {
                var families = fontFamily.Split(',');
                foreach (var f in families)
                {
                    var clean = f.Trim().Trim('\'', '"');
                    var tf = SKTypeface.FromFamilyName(clean);
                    if (tf != null && tf.FamilyName != "Arial") // SKTypeface.FromFamily often returns Default (Arial) if not found, checking if it actually differs or checking coverage is hard. 
                    {
                         // Basic check: if we asked for "Times" and got "Arial", it failed.
                         // But SKTypeface behavior depends on OS.
                         // Let's assume it returns something valid.
                         return tf;
                    }
                }
            }
            
            // 2. Fallback based on content (Character Matching)
            if (!string.IsNullOrEmpty(text))
            {
                 // Check first non-whitespace char
                 foreach (var c in text)
                 {
                     if (!char.IsWhiteSpace(c))
                     {
                         var matched = SKFontManager.Default.MatchCharacter(c);
                         if (matched != null) return matched;
                         break;
                     }
                 }
            }

            // 3. Ultimate Fallback
            // On Windows, Segoe UI is good.
             var fallback = SKTypeface.FromFamilyName("Segoe UI");
             if (fallback != null) return fallback;
             
            return SKTypeface.FromFamilyName("Arial");
        }

        private string GetOuterXml(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text;

            var sb = new System.Text.StringBuilder();
            string tag = node.Tag?.ToLowerInvariant();
            sb.Append($"<{tag}");
            
            if (node.Attr != null)
            {
                foreach(var kvp in node.Attr)
                {
                    sb.Append($" {kvp.Key}=\"{kvp.Value}\"");
                }
            }
            sb.Append(">");

            if (node.Children != null)
            {
                foreach(var child in node.Children)
                {
                    sb.Append(GetOuterXml(child));
                }
            }
            
            sb.Append($"</{tag}>");
            return sb.ToString();
        }

        /// <summary>
        /// Wrap text into multiple lines based on available width
        /// Supports word-break: break-all (break anywhere), keep-all, break-word
        /// </summary>
        private List<TextLine> WrapText(string text, SKPaint paint, float maxWidth, string whiteSpace, string hyphens = "none", string wordBreak = "normal")
        {
            var lines = new List<TextLine>();
            if (string.IsNullOrEmpty(text)) return lines;
            
            // Determine if hyphens should be added when breaking words
            bool useHyphens = hyphens == "auto" || hyphens == "manual";
            
            // word-break: break-all means we can break anywhere, not just at word boundaries
            bool breakAll = wordBreak == "break-all";
            
            // Handle pre/pre-wrap/pre-line whitespace modes
            bool preserveNewlines = whiteSpace == "pre" || whiteSpace == "pre-wrap" || whiteSpace == "pre-line";
            bool collapseSpaces = whiteSpace != "pre" && whiteSpace != "pre-wrap";
            
            // Normalize whitespace if needed
            if (collapseSpaces)
            {
                text = Regex.Replace(text, @"\s+", " ");
            }
            
            // Split by explicit newlines first
            var paragraphs = preserveNewlines ? text.Split('\n') : new[] { text };
            
            foreach (var paragraph in paragraphs)
            {
                var words = paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    if (preserveNewlines) lines.Add(new TextLine { Text = "", Width = 0, Y = lines.Count });
                    continue;
                }
                
                string currentLine = "";
                float currentWidth = 0;
                
                foreach (var word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    float testWidth = paint.MeasureText(testLine);
                    
                    if (testWidth <= maxWidth || string.IsNullOrEmpty(currentLine))
                    {
                        currentLine = testLine;
                        currentWidth = testWidth;
                    }
                    else
                    {
                        // Add current line and start new one
                        lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                        currentLine = word;
                        currentWidth = paint.MeasureText(word);
                        
                        // Handle very long words (break them)
                        if (currentWidth > maxWidth)
                        {
                            var brokenLines = BreakLongWord(word, paint, maxWidth, useHyphens);
                            for (int i = 0; i < brokenLines.Count - 1; i++)
                            {
                                lines.Add(new TextLine { Text = brokenLines[i].Text, Width = brokenLines[i].Width, Y = lines.Count });
                            }
                            if (brokenLines.Count > 0)
                            {
                                var last = brokenLines[brokenLines.Count - 1];
                                currentLine = last.Text;
                                currentWidth = last.Width;
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                }
            }
            
            return lines;
        }

        /// <summary>
        /// Break a long word that exceeds maxWidth into multiple lines
        /// </summary>
        private List<TextLine> BreakLongWord(string word, SKPaint paint, float maxWidth, bool useHyphens = false)
        {
            var lines = new List<TextLine>();
            string remaining = word;
            float hyphenWidth = useHyphens ? paint.MeasureText("-") : 0;
            
            while (!string.IsNullOrEmpty(remaining))
            {
                int breakPoint = remaining.Length;
                float width = paint.MeasureText(remaining);
                
                if (width <= maxWidth)
                {
                    lines.Add(new TextLine { Text = remaining, Width = width, Y = 0 });
                    break;
                }
                
                // Binary search for break point (accounting for hyphen width if using hyphens)
                float effectiveMaxWidth = useHyphens ? maxWidth - hyphenWidth : maxWidth;
                int low = 1, high = remaining.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    width = paint.MeasureText(remaining.Substring(0, mid));
                    if (width <= effectiveMaxWidth) low = mid;
                    else high = mid - 1;
                }
                
                if (low == 0) low = 1; // At least one character
                
                var part = remaining.Substring(0, low);
                // Add hyphen if breaking mid-word and more text remains
                if (useHyphens && remaining.Length > low)
                {
                    part = part + "-";
                }
                lines.Add(new TextLine { Text = part, Width = paint.MeasureText(part), Y = 0 });
                remaining = remaining.Substring(low);
            }
            
            return lines;
        }

        /// <summary>
        /// Parse CSS box-shadow value
        /// </summary>
        public static List<BoxShadowParsed> ParseBoxShadow(string value)
        {
            var shadows = new List<BoxShadowParsed>();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return shadows;
            
            // Split multiple shadows by comma (outside of parentheses)
            var parts = SplitShadows(value);
            
            foreach (var part in parts)
            {
                var shadow = ParseSingleShadow(part.Trim());
                if (shadow != null) shadows.Add(shadow);
            }
            
            return shadows;
        }

        private static List<string> SplitShadows(string value)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            
            if (start < value.Length)
                result.Add(value.Substring(start));
            
            return result;
        }

        private static BoxShadowParsed ParseSingleShadow(string value)
        {
            var shadow = new BoxShadowParsed();
            
            // Check for inset
            if (value.ToLowerInvariant().Contains("inset"))
            {
                shadow.Inset = true;
                value = Regex.Replace(value, @"\binset\b", "", RegexOptions.IgnoreCase).Trim();
            }
            
            // Extract color (rgba, rgb, hex, named)
            var colorMatch = Regex.Match(value, @"(rgba?\s*\([^)]+\)|#[0-9a-fA-F]{3,8}|\b(?:transparent|black|white|red|green|blue|gray|grey)\b)", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                shadow.Color = ParseColorToSK(colorMatch.Value);
                value = value.Replace(colorMatch.Value, "").Trim();
            }
            
            // Extract numeric values (offset-x, offset-y, blur, spread)
            var numbers = Regex.Matches(value, @"-?[\d.]+(?:px|em|rem)?");
            var values = new List<float>();
            
            foreach (Match m in numbers)
            {
                float v = ParseCssLength(m.Value);
                values.Add(v);
            }
            
            if (values.Count >= 2)
            {
                shadow.OffsetX = values[0];
                shadow.OffsetY = values[1];
                if (values.Count >= 3) shadow.BlurRadius = Math.Max(0, values[2]);
                if (values.Count >= 4) shadow.SpreadRadius = values[3];
            }
            
            return shadow;
        }

        /// <summary>
        /// Parse CSS transform value
        /// </summary>
        public static TransformParsed ParseTransform(string value)
        {
            var transform = new TransformParsed();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return transform;
            
            // Parse individual transform functions
            var funcMatches = Regex.Matches(value, @"(\w+)\s*\(([^)]+)\)");
            
            foreach (Match m in funcMatches)
            {
                string func = m.Groups[1].Value.ToLowerInvariant();
                string args = m.Groups[2].Value;
                var argValues = Regex.Matches(args, @"-?[\d.]+");
                var nums = new List<float>();
                foreach (Match a in argValues)
                {
                    if (float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        nums.Add(v);
                }
                
                switch (func)
                {
                    case "translate":
                        if (nums.Count >= 1) transform.TranslateX = nums[0];
                        if (nums.Count >= 2) transform.TranslateY = nums[1];
                        break;
                    case "translatex":
                        if (nums.Count >= 1) transform.TranslateX = nums[0];
                        break;
                    case "translatey":
                        if (nums.Count >= 1) transform.TranslateY = nums[0];
                        break;
                    case "scale":
                        if (nums.Count >= 1) transform.ScaleX = nums[0];
                        if (nums.Count >= 2) transform.ScaleY = nums[1];
                        else transform.ScaleY = transform.ScaleX;
                        break;
                    case "scalex":
                        if (nums.Count >= 1) transform.ScaleX = nums[0];
                        break;
                    case "scaley":
                        if (nums.Count >= 1) transform.ScaleY = nums[0];
                        break;
                    case "rotate":
                        if (nums.Count >= 1) transform.Rotate = nums[0];
                        break;
                    case "skew":
                        if (nums.Count >= 1) transform.SkewX = nums[0];
                        if (nums.Count >= 2) transform.SkewY = nums[1];
                        break;
                    case "skewx":
                        if (nums.Count >= 1) transform.SkewX = nums[0];
                        break;
                    case "skewy":
                        if (nums.Count >= 1) transform.SkewY = nums[0];
                        break;
                }
            }
            
            return transform;
        }

        /// <summary>
        /// Parse CSS text-decoration value
        /// </summary>
        public static TextDecorationParsed ParseTextDecoration(string value)
        {
            var deco = new TextDecorationParsed();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return deco;
            
            string lower = value.ToLowerInvariant();
            
            deco.Underline = lower.Contains("underline");
            deco.Overline = lower.Contains("overline");
            deco.LineThrough = lower.Contains("line-through");
            
            if (lower.Contains("dashed")) deco.Style = "dashed";
            else if (lower.Contains("dotted")) deco.Style = "dotted";
            else if (lower.Contains("wavy")) deco.Style = "wavy";
            
            // Try to extract color
            var colorMatch = Regex.Match(value, @"(rgba?\s*\([^)]+\)|#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                deco.Color = ParseColorToSK(colorMatch.Value);
            }
            
            return deco;
        }

        /// <summary>
        /// Parse a CSS color string to SKColor
        /// </summary>
        public static SKColor ParseColorToSK(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new SKColor(0, 0, 0, 80);
            value = value.Trim().ToLowerInvariant();
            
            // Named colors
            var namedColors = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase)
            {
                { "transparent", SKColors.Transparent },
                { "black", SKColors.Black },
                { "white", SKColors.White },
                { "red", SKColors.Red },
                { "green", SKColors.Green },
                { "blue", SKColors.Blue },
                { "gray", SKColors.Gray },
                { "grey", SKColors.Gray },
                { "yellow", SKColors.Yellow },
                { "orange", SKColors.Orange },
                { "purple", SKColors.Purple },
                { "pink", SKColors.Pink },
                { "cyan", SKColors.Cyan },
                { "magenta", SKColors.Magenta },
            };
            
            if (namedColors.TryGetValue(value, out var named))
                return named;
            
            // Hex color
            if (value.StartsWith("#"))
            {
                try
                {
                    return SKColor.Parse(value);
                }
                catch { }
            }
            
            // rgba(r, g, b, a)
            var rgbaMatch = Regex.Match(value, @"rgba?\s*\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)");
            if (rgbaMatch.Success)
            {
                byte r = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[1].Value, CultureInfo.InvariantCulture)));
                byte g = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[2].Value, CultureInfo.InvariantCulture)));
                byte b = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[3].Value, CultureInfo.InvariantCulture)));
                byte a = 255;
                
                if (rgbaMatch.Groups[4].Success)
                {
                    float alpha = float.Parse(rgbaMatch.Groups[4].Value, CultureInfo.InvariantCulture);
                    a = (byte)(alpha <= 1 ? alpha * 255 : Math.Min(255, alpha));
                }
                
                return new SKColor(r, g, b, a);
            }
            
            return new SKColor(0, 0, 0, 80); // Default shadow color
        }

        /// <summary>
        /// Parse CSS length value (px, em, rem) to float
        /// </summary>
        private static float ParseCssLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim().ToLowerInvariant();
            
            float multiplier = 1;
            if (value.EndsWith("px")) value = value.Substring(0, value.Length - 2);
            else if (value.EndsWith("em")) { value = value.Substring(0, value.Length - 2); multiplier = 16; }
            else if (value.EndsWith("rem")) { value = value.Substring(0, value.Length - 3); multiplier = 16; }
            
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result * multiplier;
            
            return 0;
        }

        /// <summary>
        /// Process CSS counter-reset and counter-increment properties
        /// </summary>
        private void ProcessCssCounters(CssComputed style)
        {
            if (style == null) return;
            
            // Process counter-reset
            if (!string.IsNullOrEmpty(style.CounterReset) && style.CounterReset != "none")
            {
                ParseAndApplyCounters(style.CounterReset, isReset: true);
            }
            
            // Process counter-increment
            if (!string.IsNullOrEmpty(style.CounterIncrement) && style.CounterIncrement != "none")
            {
                ParseAndApplyCounters(style.CounterIncrement, isReset: false);
            }
        }
        
        private void ParseAndApplyCounters(string counterStr, bool isReset)
        {
            // Format: "name [value] [name2 [value2]]..."
            // Default value for reset is 0, for increment is 1
            var parts = counterStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < parts.Length)
            {
                string name = parts[i].Trim();
                int value = isReset ? 0 : 1; // Default values
                
                // Check if next part is a number
                if (i + 1 < parts.Length && int.TryParse(parts[i + 1], out int num))
                {
                    value = num;
                    i += 2;
                }
                else
                {
                    i += 1;
                }
                
                if (isReset)
                {
                    _counters[name] = value;
                }
                else
                {
                    // Increment
                    if (!_counters.ContainsKey(name))
                        _counters[name] = 0;
                    _counters[name] += value;
                }
            }
        }
        
        /// <summary>
        /// Get current value of a CSS counter
        /// </summary>
        public int GetCounterValue(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            _counters.TryGetValue(name, out int value);
            return value;
        }
        
        /// <summary>
        /// Resolve counter() and counters() functions in content property
        /// </summary>
        public string ResolveContentCounters(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            
            // Resolve counter(name) or counter(name, style)
            var counterMatch = Regex.Match(content, @"counter\s*\(\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            while (counterMatch.Success)
            {
                string name = counterMatch.Groups[1].Value.Trim();
                string style = counterMatch.Groups[2].Value.Trim();
                int value = GetCounterValue(name);
                
                string formatted = FormatCounterValue(value, style);
                content = content.Substring(0, counterMatch.Index) + formatted + 
                          content.Substring(counterMatch.Index + counterMatch.Length);
                          
                counterMatch = Regex.Match(content, @"counter\s*\(\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            }
            
            // Resolve counters(name, string) or counters(name, string, style)
            var countersMatch = Regex.Match(content, @"counters\s*\(\s*([^,]+)\s*,\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            while (countersMatch.Success)
            {
                string name = countersMatch.Groups[1].Value.Trim().Trim('"', '\'');
                string separator = countersMatch.Groups[2].Value.Trim().Trim('"', '\'');
                string style = countersMatch.Groups[3].Value.Trim();
                
                int value = GetCounterValue(name);
                string formatted = FormatCounterValue(value, style);
                
                content = content.Substring(0, countersMatch.Index) + formatted + 
                          content.Substring(countersMatch.Index + countersMatch.Length);
                          
                countersMatch = Regex.Match(content, @"counters\s*\(\s*([^,]+)\s*,\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            }
            
            return content;
        }
        
        private string FormatCounterValue(int value, string style)
        {
            style = style?.ToLowerInvariant()?.Trim() ?? "";
            
            switch (style)
            {
                case "lower-alpha":
                case "lower-latin":
                    return value > 0 && value <= 26 ? ((char)('a' + value - 1)).ToString() : value.ToString();
                case "upper-alpha":
                case "upper-latin":
                    return value > 0 && value <= 26 ? ((char)('A' + value - 1)).ToString() : value.ToString();
                case "lower-roman":
                    return ToRomanNumeral(value).ToLowerInvariant();
                case "upper-roman":
                    return ToRomanNumeral(value);
                default: // decimal
                    return value.ToString();
            }
        }

        /// <summary>
        /// Parse CSS clip-path to SKPath
        /// </summary>
        private SKPath ParseClipPath(string clipPath, SKRect bounds)
        {
            if (string.IsNullOrWhiteSpace(clipPath)) return null;
            
            try
            {
                var lower = clipPath.ToLowerInvariant().Trim();
                
                // circle(radius at cx cy) or circle(radius)
                if (lower.StartsWith("circle("))
                {
                    var content = clipPath.Substring(7, clipPath.Length - 8).Trim();
                    float radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY;
                    
                    var parts = content.Split(new[] { " at " }, StringSplitOptions.None);
                    
                    // Parse radius
                    var radiusPart = parts[0].Trim();
                    if (radiusPart.EndsWith("%"))
                    {
                        if (float.TryParse(radiusPart.TrimEnd('%'), out float pct))
                            radius = Math.Min(bounds.Width, bounds.Height) * (pct / 100f) / 2;
                    }
                    else if (radiusPart.EndsWith("px"))
                    {
                        if (float.TryParse(radiusPart.Replace("px", ""), out float px))
                            radius = px;
                    }
                    else if (radiusPart == "closest-side")
                        radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    else if (radiusPart == "farthest-side")
                        radius = Math.Max(bounds.Width, bounds.Height) / 2;
                    
                    // Parse position
                    if (parts.Length > 1)
                    {
                        var posParts = parts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (posParts.Length >= 1) cx = ParseClipPosition(posParts[0], bounds.Left, bounds.Width);
                        if (posParts.Length >= 2) cy = ParseClipPosition(posParts[1], bounds.Top, bounds.Height);
                    }
                    
                    var path = new SKPath();
                    path.AddCircle(cx, cy, radius);
                    return path;
                }
                // ellipse(rx ry at cx cy) or ellipse()
                else if (lower.StartsWith("ellipse("))
                {
                    var content = clipPath.Substring(8, clipPath.Length - 9).Trim();
                    float rx = bounds.Width / 2;
                    float ry = bounds.Height / 2;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY;
                    
                    var parts = content.Split(new[] { " at " }, StringSplitOptions.None);
                    
                    // Parse radii
                    var radiiPart = parts[0].Trim();
                    if (!string.IsNullOrEmpty(radiiPart))
                    {
                        var radii = radiiPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (radii.Length >= 1) rx = ParseClipLength(radii[0], bounds.Width);
                        if (radii.Length >= 2) ry = ParseClipLength(radii[1], bounds.Height);
                        else if (radii.Length == 1) ry = rx; // If only one, use same for both
                    }
                    
                    // Parse position
                    if (parts.Length > 1)
                    {
                        var posParts = parts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (posParts.Length >= 1) cx = ParseClipPosition(posParts[0], bounds.Left, bounds.Width);
                        if (posParts.Length >= 2) cy = ParseClipPosition(posParts[1], bounds.Top, bounds.Height);
                    }
                    
                    var path = new SKPath();
                    path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                    return path;
                }
                // inset(top right bottom left round radius)
                else if (lower.StartsWith("inset("))
                {
                    var content = clipPath.Substring(6, clipPath.Length - 7).Trim();
                    float top = 0, right = 0, bottom = 0, left = 0;
                    float borderRadius = 0;
                    
                    // Split by "round" to get inset values and radius
                    var roundParts = content.Split(new[] { " round " }, StringSplitOptions.None);
                    var insetStr = roundParts[0].Trim();
                    
                    var insetParts = insetStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (insetParts.Length >= 1) top = ParseClipLength(insetParts[0], bounds.Height);
                    if (insetParts.Length >= 2) right = ParseClipLength(insetParts[1], bounds.Width);
                    else right = top;
                    if (insetParts.Length >= 3) bottom = ParseClipLength(insetParts[2], bounds.Height);
                    else bottom = top;
                    if (insetParts.Length >= 4) left = ParseClipLength(insetParts[3], bounds.Width);
                    else left = right;
                    
                    if (roundParts.Length > 1)
                    {
                        borderRadius = ParseClipLength(roundParts[1].Trim(), Math.Min(bounds.Width, bounds.Height));
                    }
                    
                    var rect = new SKRect(
                        bounds.Left + left,
                        bounds.Top + top,
                        bounds.Right - right,
                        bounds.Bottom - bottom
                    );
                    
                    var path = new SKPath();
                    if (borderRadius > 0)
                        path.AddRoundRect(rect, borderRadius, borderRadius);
                    else
                        path.AddRect(rect);
                    return path;
                }
                // polygon(x1 y1, x2 y2, ...)
                else if (lower.StartsWith("polygon("))
                {
                    var content = clipPath.Substring(8, clipPath.Length - 9).Trim();
                    var points = content.Split(',');
                    
                    var path = new SKPath();
                    bool first = true;
                    
                    foreach (var point in points)
                    {
                        var coords = point.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (coords.Length >= 2)
                        {
                            float x = ParseClipPosition(coords[0], bounds.Left, bounds.Width);
                            float y = ParseClipPosition(coords[1], bounds.Top, bounds.Height);
                            
                            if (first)
                            {
                                path.MoveTo(x, y);
                                first = false;
                            }
                            else
                            {
                                path.LineTo(x, y);
                            }
                        }
                    }
                    
                    path.Close();
                    return path;
                }
            }
            catch { }
            
            return null;
        }
        
        private float ParseClipPosition(string value, float start, float size)
        {
            value = value.Trim().ToLowerInvariant();
            if (value == "center") return start + size / 2;
            if (value == "left" || value == "top") return start;
            if (value == "right" || value == "bottom") return start + size;
            if (value.EndsWith("%") && float.TryParse(value.TrimEnd('%'), out float pct))
                return start + size * (pct / 100f);
            if (value.EndsWith("px") && float.TryParse(value.Replace("px", ""), out float px))
                return start + px;
            if (float.TryParse(value, out float num))
                return start + num;
            return start + size / 2;
        }
        
        private float ParseClipLength(string value, float refSize)
        {
            value = value.Trim().ToLowerInvariant();
            if (value.EndsWith("%") && float.TryParse(value.TrimEnd('%'), out float pct))
                return refSize * (pct / 100f);
            if (value.EndsWith("px") && float.TryParse(value.Replace("px", ""), out float px))
                return px;
            if (float.TryParse(value, out float num))
                return num;
            return 0;
        }

        /// <summary>
        /// Create Skia shader from CSS background string (gradients, images)
        /// </summary>
        private SKShader CreateShaderFromCss(string cssBackground, SKRect bounds, float opacity)
        {
            if (string.IsNullOrWhiteSpace(cssBackground)) return null;
            
            try
            {
                // Basic Linear Gradient Parsing
                if (cssBackground.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove wrapping "linear-gradient(" and ")"
                    var content = cssBackground.Substring(16).TrimEnd(')');
                    var parts = SplitShadows(content); // Use existing splitter or simple split
                    
                    if (parts.Count < 2) return null;
                    
                    // Parse direction
                    float startX = bounds.Left, startY = bounds.Top;
                    float endX = bounds.Left, endY = bounds.Bottom; // Default to-bottom
                    
                    int colorStartIndex = 0;
                    string firstPart = parts[0].Trim();
                    
                    if (firstPart.StartsWith("to "))
                    {
                        colorStartIndex = 1;
                        if (firstPart == "to right") { endX = bounds.Right; endY = bounds.Top; }
                        else if (firstPart == "to bottom") { endX = bounds.Left; endY = bounds.Bottom; }
                        else if (firstPart == "to left") { startX = bounds.Right; endX = bounds.Left; endY = bounds.Top; }
                        else if (firstPart == "to top") { startY = bounds.Bottom; endY = bounds.Top; }
                        // TODO: Add corner support (to bottom right etc.)
                    }
                    
                    var colors = new List<SKColor>();
                    var positions = new List<float>();
                    
                    for (int i = colorStartIndex; i < parts.Count; i++)
                    {
                        // Stop can be "color" or "color offset"
                        var stopStr = parts[i].Trim();
                        var stopParts = stopStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        
                        SKColor color = SKColors.Transparent;
                        if (stopParts.Length > 0)
                        {
                            color = ParseColorToSK(stopParts[0]); // Use existing helper
                        }
                        
                        float pos = -1;
                        if (stopParts.Length > 1)
                        {
                             pos = ParseCssLength(stopParts[1]); // Helper to parse px/%
                             // Normalize % to 0-1
                             if (stopParts[1].Contains("%")) pos /= 100f;
                             // If px, normalize by bounds? Gradient stops usually % or length along gradient line.
                             // For simplicity assuming % or 0-1 range for now or failing gracefully.
                        }
                        
                        colors.Add(color.WithAlpha((byte)(color.Alpha * opacity)));
                        if (pos >= 0) positions.Add(pos);
                    }
                    
                    // Fill missing positions
                    if (positions.Count < colors.Count)
                    {
                        // Simplified distribution
                        if (positions.Count == 0)
                        {
                            positions.Add(0);
                            if (colors.Count > 1)
                            {
                                float step = 1.0f / (colors.Count - 1);
                                for (int j = 1; j < colors.Count; j++) positions.Add(j * step);
                            }
                            else positions.Add(1);
                        }
                        else
                        {
                            // Pad remaining
                            while (positions.Count < colors.Count) positions.Add(1.0f);
                        }
                    }
                    
                    return SKShader.CreateLinearGradient(
                        new SKPoint(startX, startY),
                        new SKPoint(endX, endY),
                        colors.ToArray(),
                        positions.ToArray(),
                        SKShaderTileMode.Clamp);
                }
                
                // TODO: Radial Gradient
                
                // TODO: Url (Image)
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateShaderFromCss error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Apply pseudo-element styling to an existing SKPaint
        /// Matches standard CSS precedence where pseudo-styles override element styles
        /// </summary>
        private void ApplyPseudoStyle(SKPaint paint, CssComputed style, float parentOpacity)
        {
            if (style == null) return;
            
            // 1. Color
            if (style.ForegroundColor.HasValue)
            {
                var c = style.ForegroundColor.Value;
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, (byte)(c.Alpha * parentOpacity));
            }
            
            // 2. Font Size
            if (style.FontSize.HasValue)
            {
                paint.TextSize = (float)style.FontSize.Value;
            }
            
            // 3. Font Family (simplified resolution)
            if (style.FontFamily != null)
            {
                try
                {
                    // If typeface changes, we should ideally re-resolve
                    // But for simple overrides like font-weight/style change it's enough
                    // For completely different family, we need original text matching logic which is complex
                    // For now, if specified, try to resolve basic
                    
                    // Check if font-weight or font-style changed
                    SKFontStyleWeight weight = SKFontStyleWeight.Normal;
                    SKFontStyleSlant slant = SKFontStyleSlant.Upright;
                    
                    if (style.FontWeight.HasValue)
                        weight = (SKFontStyleWeight)style.FontWeight.Value;
                        
                    if (style.FontStyle.HasValue && style.FontStyle.Value == SKFontStyleSlant.Italic)
                        slant = SKFontStyleSlant.Italic;
                        
                    string familyName = style.FontFamily.FamilyName;
                    
                    // Use existing typeface family if name matches, just update style
                    if (paint.Typeface != null && paint.Typeface.FamilyName == familyName)
                    {
                        var newTf = SKTypeface.FromFamilyName(familyName, weight, SKFontStyleWidth.Normal, slant);
                        if (newTf != null) paint.Typeface = newTf;
                    }
                    else
                    {
                         // New family
                         var newTf = SKTypeface.FromFamilyName(familyName, weight, SKFontStyleWidth.Normal, slant);
                         if (newTf != null) paint.Typeface = newTf;
                    }
                }
                catch {}
            }
        }

        /// <summary>
        /// Apply CSS filter effects to SKPaint by composing them
        /// </summary>
        private void ApplyFilter(SKPaint paint, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter) || filter.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;
            
            // Parse filter functions
            var funcMatches = Regex.Matches(filter, @"(\w+)\s*\(([^)]+)\)");
            
            SKImageFilter currentFilter = null;
            
            foreach (Match m in funcMatches)
            {
                string func = m.Groups[1].Value.ToLowerInvariant();
                string arg = m.Groups[2].Value.Trim();
                
                SKImageFilter nextFilter = null;
                
                switch (func)
                {
                    case "blur":
                        float blurAmount = ParseCssLength(arg);
                        if (blurAmount > 0)
                        {
                            nextFilter = SKImageFilter.CreateBlur(blurAmount, blurAmount, currentFilter);
                        }
                        break;
                    
                    case "grayscale":
                        float grayAmount = ParsePercentOrDecimal(arg);
                        if (grayAmount > 0)
                        {
                            var cm = new float[]
                            {
                                0.2126f + 0.7874f * (1 - grayAmount), 0.7152f - 0.7152f * (1 - grayAmount), 0.0722f - 0.0722f * (1 - grayAmount), 0, 0,
                                0.2126f - 0.2126f * (1 - grayAmount), 0.7152f + 0.2848f * (1 - grayAmount), 0.0722f - 0.0722f * (1 - grayAmount), 0, 0,
                                0.2126f - 0.2126f * (1 - grayAmount), 0.7152f - 0.7152f * (1 - grayAmount), 0.0722f + 0.9278f * (1 - grayAmount), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                    
                    case "brightness":
                        float bright = ParsePercentOrDecimal(arg);
                        if (Math.Abs(bright - 1) > 0.01f)
                        {
                            // Brightness is scaling RGB
                            var cm = new float[]
                            {
                                bright, 0, 0, 0, 0,
                                0, bright, 0, 0, 0,
                                0, 0, bright, 0, 0,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                    
                    case "contrast":
                        float contrast = ParsePercentOrDecimal(arg);
                        if (Math.Abs(contrast - 1) > 0.01f)
                        {
                            float t = (1 - contrast) / 2 * 255;
                            var cm = new float[]
                            {
                                contrast, 0, 0, 0, t,
                                0, contrast, 0, 0, t,
                                0, 0, contrast, 0, t,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                    
                    case "sepia":
                        float sepiaAmount = ParsePercentOrDecimal(arg);
                        if (sepiaAmount > 0)
                        {
                            var cm = new float[]
                            {
                                0.393f + 0.607f * (1 - sepiaAmount), 0.769f - 0.769f * (1 - sepiaAmount), 0.189f - 0.189f * (1 - sepiaAmount), 0, 0,
                                0.349f - 0.349f * (1 - sepiaAmount), 0.686f + 0.314f * (1 - sepiaAmount), 0.168f - 0.168f * (1 - sepiaAmount), 0, 0,
                                0.272f - 0.272f * (1 - sepiaAmount), 0.534f - 0.534f * (1 - sepiaAmount), 0.131f + 0.869f * (1 - sepiaAmount), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                    
                    case "opacity":
                        float opacityVal = ParsePercentOrDecimal(arg);
                        if (Math.Abs(opacityVal - 1) > 0.01f)
                        {
                            // Opacity filter affects alpha
                            var cm = new float[]
                            {
                                1, 0, 0, 0, 0,
                                0, 1, 0, 0, 0,
                                0, 0, 1, 0, 0,
                                0, 0, 0, opacityVal, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                    
                    case "invert":
                        float invertAmount = ParsePercentOrDecimal(arg);
                        if (invertAmount > 0)
                        {
                            // Invert: 1 - c
                            // Matrix: -1 0 0 0 255 (if 100%)
                            // Mixed: (1-2*amt) 0 0 0 amt*255
                            var cm = new float[]
                            {
                                1 - 2 * invertAmount, 0, 0, 0, invertAmount * 255,
                                0, 1 - 2 * invertAmount, 0, 0, invertAmount * 255,
                                0, 0, 1 - 2 * invertAmount, 0, invertAmount * 255,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                        
                    case "saturate":
                        float satAmount = ParsePercentOrDecimal(arg);
                        {
                            float s = satAmount;
                            float lr = 0.2126f;
                            float lg = 0.7152f;
                            float lb = 0.0722f;
                            var cm = new float[]
                            {
                                lr + s * (1 - lr), lg - s * lg, lb - s * lb, 0, 0,
                                lr - s * lr, lg + s * (1 - lg), lb - s * lb, 0, 0,
                                lr - s * lr, lg - s * lg, lb + s * (1 - lb), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            var cf = SKColorFilter.CreateColorMatrix(cm);
                            nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                        }
                        break;
                        
                    case "hue-rotate":
                        {
                            float angle = 0;
                            arg = arg.Trim();
                            if (arg.EndsWith("deg")) float.TryParse(arg.Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                            else if (arg.EndsWith("rad")) { float.TryParse(arg.Replace("rad", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle); angle *= (180f / (float)Math.PI); }
                            else if (arg.EndsWith("turn")) { float.TryParse(arg.Replace("turn", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle); angle *= 360f; }
                            else float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                            
                            if (Math.Abs(angle) > 0.1f)
                            {
                                float rad = angle * (float)Math.PI / 180f;
                                float cos = (float)Math.Cos(rad);
                                float sin = (float)Math.Sin(rad);
                                
                                var cm = new float[]
                                {
                                    0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
                                    0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
                                    0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
                                    0, 0, 0, 1, 0
                                };
                                var cf = SKColorFilter.CreateColorMatrix(cm);
                                nextFilter = SKImageFilter.CreateColorFilter(cf, currentFilter);
                            }
                        }
                        break;
                        
                    case "drop-shadow":
                        {
                            var shadowParts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            float dx = 0, dy = 0, blur = 0;
                            SKColor shadowColor = SKColors.Black;
                            
                            int numericIndex = 0;
                            foreach (var part in shadowParts)
                            {
                                if (numericIndex < 3 && (char.IsDigit(part[0]) || part[0] == '-' || part[0] == '.'))
                                {
                                    float val = ParseCssLength(part);
                                    if (numericIndex == 0) dx = val;
                                    else if (numericIndex == 1) dy = val;
                                    else if (numericIndex == 2) blur = val;
                                    numericIndex++;
                                }
                                else
                                {
                                    var color = ParseColorToSK(part);
                                    shadowColor = new SKColor(color.Red, color.Green, color.Blue, color.Alpha);
                                }
                            }
                            
                            nextFilter = SKImageFilter.CreateDropShadow(dx, dy, blur / 2, blur / 2, shadowColor, currentFilter);
                        }
                        break;
                }
                
                if (nextFilter != null)
                {
                    currentFilter = nextFilter;
                }
            }
            
            if (currentFilter != null)
            {
                paint.ImageFilter = currentFilter;
            }
        }
        
        /// <summary>
        /// Draw custom inputs (Color, Date, Range) that don't have overlays
        /// </summary>
        private void DrawInputControl(LiteElement node, BoxModel box, CssComputed style, SKCanvas canvas, float opacity)
        {
            if (node.Attr == null) return;
            node.Attr.TryGetValue("type", out string type);
            type = type?.ToLowerInvariant() ?? "text";
            node.Attr.TryGetValue("value", out string val);
            
            SKRect r = box.ContentBox;
            
            using (var paint = new SKPaint { IsAntialias = true })
            {
                if (type == "color")
                {
                    // Draw color swatch
                    SKColor c = ParseColorToSK(val);
                    paint.Color = c;
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawRect(r, paint);
                    
                    // Border
                    paint.Color = SKColors.Gray;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1;
                    canvas.DrawRect(r, paint);
                }
                else if (type == "range")
                {
                    // Draw slider track
                    paint.Color = new SKColor(200, 200, 200, 255);
                    paint.Style = SKPaintStyle.Fill;
                    float trackH = 4;
                    float trackY = r.MidY - trackH / 2;
                    canvas.DrawRect(r.Left, trackY, r.Width, trackH, paint);
                    
                    // Draw thumb
                    // Simple logic: 0 to 100 default range
                    float min = 0, max = 100;
                    float v = 50;
                    if (node.Attr.TryGetValue("min", out string minStr)) float.TryParse(minStr, out min);
                    if (node.Attr.TryGetValue("max", out string maxStr)) float.TryParse(maxStr, out max);
                    if (float.TryParse(val, out float vParse)) v = vParse;
                    
                    float pct = (v - min) / (max - min);
                    if (pct < 0) pct = 0; if (pct > 1) pct = 1;
                    
                    float thumbX = r.Left + pct * r.Width;
                    paint.Color = new SKColor(0, 120, 215, 255); // Blue thumb
                    canvas.DrawCircle(thumbX, r.MidY, 8, paint);
                }
                else if (type == "radio" || type == "checkbox")
                {
                    // Fallback if not overlays (though we set them as overlays usually? No, I excluded them in Overlay logic too? No, I only excluded color/range/file/date)
                    // But wait, DrawLayout has logic:
                    // if (overlayType != "checkbox" && overlayType != "radio" && overlayType != "hidden") isOverlay = true;
                    // So Check/Radio are NOT overlays. They fall through.
                    // But standard drawing might ignore them if they have no text content.
                    // We should draw them here!
                    
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 2;
                    paint.Color = SKColors.DarkGray;
                    
                    float size = Math.Min(r.Width, r.Height);
                    if (size == 0) size = 13; // default
                    float cx = r.Left + size/2;
                    float cy = r.Top + size/2; // Align top or center? Usually baseline.
                    
                    bool isChecked = node.Attr.ContainsKey("checked");
                    
                    if (type == "radio")
                    {
                        canvas.DrawCircle(cx, cy, size/2 - 1, paint);
                        if (isChecked)
                        {
                            paint.Style = SKPaintStyle.Fill;
                            paint.Color = SKColors.Black;
                            canvas.DrawCircle(cx, cy, size/2 - 4, paint);
                        }
                    }
                    else // checkbox
                    {
                        var checkRect = new SKRect(r.Left, r.Top + (r.Height-size)/2, r.Left + size, r.Top + (r.Height-size)/2 + size);
                        canvas.DrawRect(checkRect, paint);
                    if (isChecked)
                    {
                        // Draw checkmark
                        paint.Color = SKColors.Black;
                        paint.StrokeWidth = 2;
                        var path = new SKPath();
                        path.MoveTo(checkRect.Left + 3, checkRect.Top + size/2);
                        path.LineTo(checkRect.Left + size/2 - 1, checkRect.Bottom - 3);
                        path.LineTo(checkRect.Right - 3, checkRect.Top + 3);
                        canvas.DrawPath(path, paint);
                    }
                }
            }
            else if (type == "submit" || type == "reset" || type == "button")
            {
                // Draw text for input buttons
                if (!string.IsNullOrEmpty(val))
                {
                     using (var textPaint = new SKPaint { IsAntialias = true })
                     {
                         // Fix Color: Convert Avalonia Color to SKColor
                         if (style.ForegroundColor.HasValue)
                         {
                             var ac = style.ForegroundColor.Value;
                             textPaint.Color = new SKColor(ac.Red, ac.Green, ac.Blue, ac.Alpha);
                         }
                         else
                         {
                             textPaint.Color = SKColors.Black;
                         }

                         // Fix FontSize: Cast double to float
                         textPaint.TextSize = (float)(style.FontSize ?? DefaultFontSize);

                         // Fix FontWeight: Compare Avalonia FontWeight
                         if (style.FontWeight.HasValue && style.FontWeight.Value == 700)
                         {
                             textPaint.FakeBoldText = true;
                         }

                         textPaint.TextAlign = SKTextAlign.Center;
                         
                         var bounds = new SKRect();
                         textPaint.MeasureText(val, ref bounds);
                         float y = r.MidY - bounds.MidY;
                         
                         canvas.DrawText(val, r.MidX, y, textPaint);
                     }
                }
            }

            }
        }
        
        /// <summary>
        /// Draw the METER element showing a value within a known range
        /// Colors: green (optimal), yellow (suboptimal), red (critical)
        /// </summary>
        private void DrawMeterElement(LiteElement node, BoxModel box, SKCanvas canvas, float opacity)
        {
            SKRect r = box.ContentBox;
            
            // Parse meter attributes
            float min = 0, max = 1, value = 0;
            float low = 0, high = 1, optimum = 0.5f;
            
            if (node.Attr != null)
            {
                if (node.Attr.TryGetValue("min", out var minStr)) float.TryParse(minStr, NumberStyles.Float, CultureInfo.InvariantCulture, out min);
                if (node.Attr.TryGetValue("max", out var maxStr)) float.TryParse(maxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out max);
                if (node.Attr.TryGetValue("value", out var valStr)) float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                if (node.Attr.TryGetValue("low", out var lowStr)) float.TryParse(lowStr, NumberStyles.Float, CultureInfo.InvariantCulture, out low);
                else low = min;
                if (node.Attr.TryGetValue("high", out var highStr)) float.TryParse(highStr, NumberStyles.Float, CultureInfo.InvariantCulture, out high);
                else high = max;
                if (node.Attr.TryGetValue("optimum", out var optStr)) float.TryParse(optStr, NumberStyles.Float, CultureInfo.InvariantCulture, out optimum);
                else optimum = (low + high) / 2;
            }
            
            // Calculate fill percentage
            float range = max - min;
            float pct = range > 0 ? (value - min) / range : 0;
            if (pct < 0) pct = 0;
            if (pct > 1) pct = 1;
            
            // Determine color based on value position relative to low/high/optimum
            SKColor barColor;
            if (value < low)
                barColor = new SKColor(255, 50, 50, (byte)(opacity * 255)); // Red - below low
            else if (value > high)
                barColor = new SKColor(255, 165, 0, (byte)(opacity * 255)); // Orange - above high
            else
                barColor = new SKColor(50, 205, 50, (byte)(opacity * 255)); // Green - in optimal range
            
            using (var paint = new SKPaint { IsAntialias = true })
            {
                // Draw background track
                paint.Color = new SKColor(220, 220, 220, (byte)(opacity * 255));
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRoundRect(r, 3, 3, paint);
                
                // Draw filled portion
                float fillWidth = r.Width * pct;
                if (fillWidth > 0)
                {
                    paint.Color = barColor;
                    var fillRect = new SKRect(r.Left, r.Top, r.Left + fillWidth, r.Bottom);
                    canvas.DrawRoundRect(fillRect, 3, 3, paint);
                }
                
                // Draw border
                paint.Color = new SKColor(169, 169, 169, (byte)(opacity * 255));
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                canvas.DrawRoundRect(r, 3, 3, paint);
            }
        }
        
        /// <summary>
        /// Draw the PROGRESS element showing completion progress
        /// </summary>
        private void DrawProgressElement(LiteElement node, BoxModel box, SKCanvas canvas, float opacity)
        {
            SKRect r = box.ContentBox;
            
            // Parse progress attributes
            float value = -1; // -1 means indeterminate
            float max = 1;
            
            if (node.Attr != null)
            {
                if (node.Attr.TryGetValue("max", out var maxStr)) float.TryParse(maxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out max);
                if (node.Attr.TryGetValue("value", out var valStr))
                {
                    if (float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedVal))
                        value = parsedVal;
                }
            }
            
            using (var paint = new SKPaint { IsAntialias = true })
            {
                // Draw background track
                paint.Color = new SKColor(220, 220, 220, (byte)(opacity * 255));
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRoundRect(r, 3, 3, paint);
                
                // Draw filled portion
                if (value >= 0)
                {
                    // Determinate progress
                    float pct = max > 0 ? value / max : 0;
                    if (pct < 0) pct = 0;
                    if (pct > 1) pct = 1;
                    
                    float fillWidth = r.Width * pct;
                    if (fillWidth > 0)
                    {
                        paint.Color = new SKColor(0, 120, 215, (byte)(opacity * 255)); // Blue progress
                        var fillRect = new SKRect(r.Left, r.Top, r.Left + fillWidth, r.Bottom);
                        canvas.DrawRoundRect(fillRect, 3, 3, paint);
                    }
                }
                else
                {
                    // Indeterminate progress - draw a moving gradient indicator
                    // For simplicity, draw a static indicator at center
                    float indicatorWidth = r.Width * 0.3f;
                    float indicatorX = r.Left + r.Width * 0.35f;
                    paint.Color = new SKColor(0, 120, 215, (byte)(opacity * 255));
                    var indicatorRect = new SKRect(indicatorX, r.Top, indicatorX + indicatorWidth, r.Bottom);
                    canvas.DrawRoundRect(indicatorRect, 3, 3, paint);
                }
                
                // Draw border
                paint.Color = new SKColor(169, 169, 169, (byte)(opacity * 255));
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                canvas.DrawRoundRect(r, 3, 3, paint);
            }
        }

        private float ParsePercentOrDecimal(string value)
        {
            value = value.Trim();
            if (value.EndsWith("%"))
            {
                if (float.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                    return pct / 100f;
            }
            else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float dec))
            {
                return dec;
            }
            return 1;
        }

        /// <summary>
        /// Convert integer to Roman numeral
        /// </summary>
        private string ToRomanNumeral(int number)
        {
            if (number <= 0 || number > 3999) return number.ToString();
            
            string[] thousands = { "", "M", "MM", "MMM" };
            string[] hundreds = { "", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM" };
            string[] tens = { "", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC" };
            string[] ones = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };
            
            return thousands[number / 1000] +
                   hundreds[(number % 1000) / 100] +
                   tens[(number % 100) / 10] +
                   ones[number % 10];
        }

        /// <summary>
        /// Draw box shadow for an element
        /// </summary>
        private void DrawBoxShadow(SKCanvas canvas, SKRect box, float borderRadius, List<BoxShadowParsed> shadows, float opacity)
        {
            if (shadows == null || shadows.Count == 0) return;
            
            foreach (var shadow in shadows)
            {
                if (shadow.Inset) continue; // Inset shadows drawn separately
                
                var shadowRect = new SKRect(
                    box.Left + shadow.OffsetX - shadow.SpreadRadius,
                    box.Top + shadow.OffsetY - shadow.SpreadRadius,
                    box.Right + shadow.OffsetX + shadow.SpreadRadius,
                    box.Bottom + shadow.OffsetY + shadow.SpreadRadius
                );
                
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Fill;
                    
                    var color = shadow.Color;
                    color = color.WithAlpha((byte)(color.Alpha * opacity));
                    paint.Color = color;
                    
                    if (shadow.BlurRadius > 0)
                    {
                        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.BlurRadius / 2);
                    }
                    
                    if (borderRadius > 0)
                        canvas.DrawRoundRect(shadowRect, borderRadius, borderRadius, paint);
                    else
                        canvas.DrawRect(shadowRect, paint);
                }
            }
        }

        /// <summary>
        /// Draw borders with full style support (solid, dashed, dotted, double, groove, ridge, inset, outset)
        /// </summary>
        private void DrawStyledBorders(SKCanvas canvas, BoxModel box, CssComputed layoutStyle, float borderRadius, float opacity)
        {
            var borderBox = box.BorderBox;
            var border = box.Border;
            
            // Get border color
            SKColor borderColor = SKColors.Black;
            if (layoutStyle?.BorderBrushColor.HasValue == true)
            {
                var c = layoutStyle.BorderBrushColor.Value;
                byte alpha = (byte)(c.Alpha * opacity);
                borderColor = new SKColor(c.Red, c.Green, c.Blue, alpha);
            }
            
            // Get border styles
            string styleTop = layoutStyle?.BorderStyleTop ?? "solid";
            string styleRight = layoutStyle?.BorderStyleRight ?? "solid";
            string styleBottom = layoutStyle?.BorderStyleBottom ?? "solid";
            string styleLeft = layoutStyle?.BorderStyleLeft ?? "solid";
            
            // If all borders are the same style and width, use optimized path
            if (styleTop == styleRight && styleRight == styleBottom && styleBottom == styleLeft &&
                Math.Abs(border.Top - border.Right) < 0.1 && Math.Abs(border.Right - border.Bottom) < 0.1 && 
                Math.Abs(border.Bottom - border.Left) < 0.1)
            {
                float strokeWidth = (float)Math.Max(1, border.Top);
                DrawBorderWithStyle(canvas, borderBox, borderRadius, strokeWidth, borderColor, styleTop, true, true, true, true);
                return;
            }
            
            // Draw each border side separately
            if (border.Top > 0 && styleTop != "none" && styleTop != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Top, borderColor, styleTop, "top", borderRadius);
            }
            if (border.Right > 0 && styleRight != "none" && styleRight != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Right, borderColor, styleRight, "right", borderRadius);
            }
            if (border.Bottom > 0 && styleBottom != "none" && styleBottom != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Bottom, borderColor, styleBottom, "bottom", borderRadius);
            }
            if (border.Left > 0 && styleLeft != "none" && styleLeft != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Left, borderColor, styleLeft, "left", borderRadius);
            }
        }

        /// <summary>
        /// Draw a single border side with the specified style
        /// </summary>
        private void DrawBorderSide(SKCanvas canvas, SKRect borderBox, float width, SKColor color, string style, string side, float borderRadius)
        {
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Color = color;
                paint.StrokeWidth = width;
                paint.Style = SKPaintStyle.Stroke;
                
                // Apply path effect based on style
                ApplyBorderPathEffect(paint, style, width);
                
                // Determine coordinates for this side
                float x1, y1, x2, y2;
                float halfWidth = width / 2;
                
                switch (side)
                {
                    case "top":
                        x1 = borderBox.Left;
                        y1 = borderBox.Top + halfWidth;
                        x2 = borderBox.Right;
                        y2 = borderBox.Top + halfWidth;
                        break;
                    case "right":
                        x1 = borderBox.Right - halfWidth;
                        y1 = borderBox.Top;
                        x2 = borderBox.Right - halfWidth;
                        y2 = borderBox.Bottom;
                        break;
                    case "bottom":
                        x1 = borderBox.Left;
                        y1 = borderBox.Bottom - halfWidth;
                        x2 = borderBox.Right;
                        y2 = borderBox.Bottom - halfWidth;
                        break;
                    case "left":
                    default:
                        x1 = borderBox.Left + halfWidth;
                        y1 = borderBox.Top;
                        x2 = borderBox.Left + halfWidth;
                        y2 = borderBox.Bottom;
                        break;
                }
                
                // Handle 3D styles (groove, ridge, inset, outset)
                if (style == "groove" || style == "ridge" || style == "inset" || style == "outset")
                {
                    Draw3DBorderSide(canvas, x1, y1, x2, y2, width, color, style, side);
                }
                else if (style == "double")
                {
                    DrawDoubleBorderSide(canvas, x1, y1, x2, y2, width, color, side);
                }
                else
                {
                    canvas.DrawLine(x1, y1, x2, y2, paint);
                }
            }
        }

        /// <summary>
        /// Draw a full border (all sides) with a single style
        /// </summary>
        private void DrawBorderWithStyle(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor color, string style, bool top, bool right, bool bottom, bool left)
        {
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Color = color;
                paint.StrokeWidth = strokeWidth;
                paint.Style = SKPaintStyle.Stroke;
                
                ApplyBorderPathEffect(paint, style, strokeWidth);
                
                // Handle 3D styles specially
                if (style == "groove" || style == "ridge")
                {
                    Draw3DBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color, style);
                }
                else if (style == "inset" || style == "outset")
                {
                    Draw3DBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color, style);
                }
                else if (style == "double")
                {
                    DrawDoubleBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color);
                }
                else
                {
                    if (borderRadius > 0)
                        canvas.DrawRoundRect(borderBox, borderRadius, borderRadius, paint);
                    else
                        canvas.DrawRect(borderBox, paint);
                }
            }
        }

        /// <summary>
        /// Apply SKPathEffect based on border style
        /// </summary>
        private void ApplyBorderPathEffect(SKPaint paint, string style, float strokeWidth)
        {
            switch (style)
            {
                case "dashed":
                    // Dashes are typically 3x stroke width with 1x gap
                    float dashLen = Math.Max(6, strokeWidth * 3);
                    float gapLen = Math.Max(3, strokeWidth);
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { dashLen, gapLen }, 0);
                    break;
                    
                case "dotted":
                    // Dots are typically 1:1 ratio
                    float dotSize = Math.Max(1, strokeWidth);
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { dotSize, dotSize * 2 }, 0);
                    paint.StrokeCap = SKStrokeCap.Round;
                    break;
                    
                // solid, double, groove, ridge, inset, outset use solid lines (with special rendering)
                default:
                    paint.PathEffect = null;
                    break;
            }
        }

        /// <summary>
        /// Draw a 3D border side (groove, ridge, inset, outset)
        /// </summary>
        private void Draw3DBorderSide(SKCanvas canvas, float x1, float y1, float x2, float y2, float width, SKColor baseColor, string style, string side)
        {
            // Calculate light and dark colors
            SKColor lightColor = LightenColor(baseColor, 0.5f);
            SKColor darkColor = DarkenColor(baseColor, 0.5f);
            
            // Determine which color goes on which half based on style and side
            SKColor firstColor, secondColor;
            bool isTopOrLeft = side == "top" || side == "left";
            
            switch (style)
            {
                case "groove":
                    firstColor = isTopOrLeft ? darkColor : lightColor;
                    secondColor = isTopOrLeft ? lightColor : darkColor;
                    break;
                case "ridge":
                    firstColor = isTopOrLeft ? lightColor : darkColor;
                    secondColor = isTopOrLeft ? darkColor : lightColor;
                    break;
                case "inset":
                    firstColor = isTopOrLeft ? darkColor : lightColor;
                    secondColor = firstColor;
                    break;
                case "outset":
                    firstColor = isTopOrLeft ? lightColor : darkColor;
                    secondColor = firstColor;
                    break;
                default:
                    firstColor = secondColor = baseColor;
                    break;
            }
            
            float halfWidth = width / 2;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke })
            {
                // Draw first half
                paint.StrokeWidth = halfWidth;
                paint.Color = firstColor;
                
                float offset1 = halfWidth / 2;
                float dx = 0, dy = 0;
                if (side == "top") dy = -offset1;
                else if (side == "bottom") dy = offset1;
                else if (side == "left") dx = -offset1;
                else if (side == "right") dx = offset1;
                
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
                
                // Draw second half
                paint.Color = secondColor;
                dx = -dx; dy = -dy;
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
            }
        }

        /// <summary>
        /// Draw a double border side
        /// </summary>
        private void DrawDoubleBorderSide(SKCanvas canvas, float x1, float y1, float x2, float y2, float width, SKColor color, string side)
        {
            // Double borders: two lines with a gap equal to 1/3 width each
            float lineWidth = width / 3;
            float gap = lineWidth;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke })
            {
                paint.StrokeWidth = lineWidth;
                paint.Color = color;
                
                float offset = lineWidth + gap / 2;
                float dx = 0, dy = 0;
                if (side == "top" || side == "bottom") dy = side == "top" ? -offset/2 : offset/2;
                else dx = side == "left" ? -offset/2 : offset/2;
                
                // Outer line
                canvas.DrawLine(x1 - dx, y1 - dy, x2 - dx, y2 - dy, paint);
                // Inner line
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
            }
        }

        /// <summary>
        /// Draw a 3D box border (all sides with 3D effect)
        /// </summary>
        private void Draw3DBoxBorder(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor baseColor, string style)
        {
            SKColor lightColor = LightenColor(baseColor, 0.5f);
            SKColor darkColor = DarkenColor(baseColor, 0.5f);
            
            SKColor topLeftColor, bottomRightColor;
            
            if (style == "inset" || style == "groove")
            {
                topLeftColor = darkColor;
                bottomRightColor = lightColor;
            }
            else // outset, ridge
            {
                topLeftColor = lightColor;
                bottomRightColor = darkColor;
            }
            
            float halfWidth = strokeWidth / 2;
            
            // For groove/ridge, draw two sets of borders
            if (style == "groove" || style == "ridge")
            {
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = halfWidth })
                {
                    // Outer border
                    var outerBox = new SKRect(borderBox.Left + halfWidth/2, borderBox.Top + halfWidth/2, 
                                              borderBox.Right - halfWidth/2, borderBox.Bottom - halfWidth/2);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(outerBox.Left, outerBox.Bottom, outerBox.Left, outerBox.Top, paint);
                    canvas.DrawLine(outerBox.Left, outerBox.Top, outerBox.Right, outerBox.Top, paint);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(outerBox.Right, outerBox.Top, outerBox.Right, outerBox.Bottom, paint);
                    canvas.DrawLine(outerBox.Right, outerBox.Bottom, outerBox.Left, outerBox.Bottom, paint);
                    
                    // Inner border (reverse colors)
                    var innerBox = new SKRect(borderBox.Left + halfWidth*1.5f, borderBox.Top + halfWidth*1.5f, 
                                              borderBox.Right - halfWidth*1.5f, borderBox.Bottom - halfWidth*1.5f);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(innerBox.Left, innerBox.Bottom, innerBox.Left, innerBox.Top, paint);
                    canvas.DrawLine(innerBox.Left, innerBox.Top, innerBox.Right, innerBox.Top, paint);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(innerBox.Right, innerBox.Top, innerBox.Right, innerBox.Bottom, paint);
                    canvas.DrawLine(innerBox.Right, innerBox.Bottom, innerBox.Left, innerBox.Bottom, paint);
                }
            }
            else // inset, outset
            {
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth })
                {
                    var insetBox = new SKRect(borderBox.Left + halfWidth, borderBox.Top + halfWidth, 
                                              borderBox.Right - halfWidth, borderBox.Bottom - halfWidth);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(insetBox.Left, insetBox.Bottom, insetBox.Left, insetBox.Top, paint);
                    canvas.DrawLine(insetBox.Left, insetBox.Top, insetBox.Right, insetBox.Top, paint);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(insetBox.Right, insetBox.Top, insetBox.Right, insetBox.Bottom, paint);
                    canvas.DrawLine(insetBox.Right, insetBox.Bottom, insetBox.Left, insetBox.Bottom, paint);
                }
            }
        }

        /// <summary>
        /// Draw a double box border (all sides)
        /// </summary>
        private void DrawDoubleBoxBorder(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor color)
        {
            float lineWidth = strokeWidth / 3;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = lineWidth })
            {
                paint.Color = color;
                
                // Outer border
                var outerBox = new SKRect(borderBox.Left + lineWidth/2, borderBox.Top + lineWidth/2, 
                                          borderBox.Right - lineWidth/2, borderBox.Bottom - lineWidth/2);
                if (borderRadius > 0)
                    canvas.DrawRoundRect(outerBox, borderRadius, borderRadius, paint);
                else
                    canvas.DrawRect(outerBox, paint);
                
                // Inner border
                var innerBox = new SKRect(borderBox.Left + strokeWidth - lineWidth/2, borderBox.Top + strokeWidth - lineWidth/2, 
                                          borderBox.Right - strokeWidth + lineWidth/2, borderBox.Bottom - strokeWidth + lineWidth/2);
                if (borderRadius > 0)
                    canvas.DrawRoundRect(innerBox, Math.Max(0, borderRadius - strokeWidth + lineWidth), Math.Max(0, borderRadius - strokeWidth + lineWidth), paint);
                else
                    canvas.DrawRect(innerBox, paint);
            }
        }

        /// <summary>
        /// Lighten a color by a factor (0-1)
        /// </summary>
        private SKColor LightenColor(SKColor color, float factor)
        {
            int r = Math.Min(255, (int)(color.Red + (255 - color.Red) * factor));
            int g = Math.Min(255, (int)(color.Green + (255 - color.Green) * factor));
            int b = Math.Min(255, (int)(color.Blue + (255 - color.Blue) * factor));
            return new SKColor((byte)r, (byte)g, (byte)b, color.Alpha);
        }

        /// <summary>
        /// Darken a color by a factor (0-1)
        /// </summary>
        private SKColor DarkenColor(SKColor color, float factor)
        {
            int r = (int)(color.Red * (1 - factor));
            int g = (int)(color.Green * (1 - factor));
            int b = (int)(color.Blue * (1 - factor));
            return new SKColor((byte)r, (byte)g, (byte)b, color.Alpha);
        }

        /// <summary>
        /// Draw inset shadows
        /// </summary>
        private void DrawInsetShadow(SKCanvas canvas, SKRect box, float borderRadius, List<BoxShadowParsed> shadows, float opacity)
        {
            if (shadows == null) return;
            
            foreach (var shadow in shadows.Where(s => s.Inset))
            {
                canvas.Save();
                
                // Clip to the box
                if (borderRadius > 0)
                    canvas.ClipRoundRect(new SKRoundRect(box, borderRadius), SKClipOperation.Intersect);
                else
                    canvas.ClipRect(box, SKClipOperation.Intersect);
                
                // Draw inner shadow by drawing a larger shadow outside and letting it bleed in
                var shadowRect = new SKRect(
                    box.Left - 1000 + shadow.OffsetX,
                    box.Top - 1000 + shadow.OffsetY,
                    box.Right + 1000 + shadow.OffsetX,
                    box.Bottom + 1000 + shadow.OffsetY
                );
                
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1000;
                    
                    var color = shadow.Color;
                    color = color.WithAlpha((byte)(color.Alpha * opacity));
                    paint.Color = color;
                    
                    if (shadow.BlurRadius > 0)
                    {
                        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.BlurRadius / 2);
                    }
                    
                    canvas.DrawRect(shadowRect, paint);
                }
                
                canvas.Restore();
            }
        }

        /// <summary>
        /// Draw text with word-spacing and letter-spacing support
        /// </summary>
        private void DrawTextWithSpacing(SKCanvas canvas, string text, float x, float y, SKPaint paint, double? wordSpacing, double? letterSpacing)
        {
            if (wordSpacing == null && letterSpacing == null)
            {
                // No spacing adjustments needed, use standard drawing
                try
                {
                    using (var shaper = new SKShaper(paint.Typeface))
                    {
                        canvas.DrawShapedText(shaper, text, x, y, paint);
                    }
                }
                catch
                {
                    canvas.DrawText(text, x, y, paint);
                }
                return;
            }
            
            if (letterSpacing != null && letterSpacing != 0)
            {
                // Draw character by character for letter-spacing
                float currentX = x;
                foreach (char c in text)
                {
                    string charStr = c.ToString();
                    canvas.DrawText(charStr, currentX, y, paint);
                    float charWidth = paint.MeasureText(charStr);
                    currentX += charWidth + (float)letterSpacing.Value;
                }
            }
            else if (wordSpacing != null && wordSpacing != 0)
            {
                // Draw word by word for word-spacing
                var words = text.Split(' ');
                float currentX = x;
                float spaceWidth = paint.MeasureText(" ") + (float)wordSpacing.Value;
                
                for (int i = 0; i < words.Length; i++)
                {
                    if (!string.IsNullOrEmpty(words[i]))
                    {
                        try
                        {
                            using (var shaper = new SKShaper(paint.Typeface))
                            {
                                canvas.DrawShapedText(shaper, words[i], currentX, y, paint);
                            }
                        }
                        catch
                        {
                            canvas.DrawText(words[i], currentX, y, paint);
                        }
                        currentX += paint.MeasureText(words[i]);
                    }
                    
                    if (i < words.Length - 1)
                    {
                        currentX += spaceWidth;
                    }
                }
            }
        }

        /// <summary>
        /// Calculate text width considering word-spacing and letter-spacing
        /// </summary>
        private float MeasureTextWithSpacing(SKPaint paint, string text, double? wordSpacing, double? letterSpacing)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            float baseWidth = paint.MeasureText(text);
            
            if (letterSpacing != null && letterSpacing != 0)
            {
                // Add letter-spacing for each character (except last)
                baseWidth += (text.Length - 1) * (float)letterSpacing.Value;
            }
            
            if (wordSpacing != null && wordSpacing != 0)
            {
                // Count spaces and add word-spacing
                int spaceCount = text.Count(c => c == ' ');
                baseWidth += spaceCount * (float)wordSpacing.Value;
            }
            
            return baseWidth;
        }

        /// <summary>
        /// Draw text decoration (underline, overline, line-through)
        /// </summary>
        private void DrawTextDecoration(SKCanvas canvas, TextDecorationParsed deco, SKRect textBox, float fontSize, SKColor textColor)
        {
            if (!deco.Underline && !deco.Overline && !deco.LineThrough) return;
            
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Color = deco.Color ?? textColor;
                paint.StrokeWidth = Math.Max(1, fontSize / 14);
                paint.Style = SKPaintStyle.Stroke;
                
                // Apply line style
                if (deco.Style == "dashed")
                {
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);
                }
                else if (deco.Style == "dotted")
                {
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 1, 2 }, 0);
                }
                
                // Underline
                if (deco.Underline)
                {
                    float y = textBox.Bottom - 2;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
                
                // Overline
                if (deco.Overline)
                {
                    float y = textBox.Top + 2;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
                
                // Line-through
                if (deco.LineThrough)
                {
                    float y = textBox.MidY;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
            }
        }
        
        /// <summary>
        /// Parse backdrop-filter blur value (e.g., "blur(10px)" -> 10)
        /// </summary>
        private float ParseBackdropBlur(string filter)
        {
            if (string.IsNullOrEmpty(filter)) return 0;
            
            // Look for blur(Xpx) pattern
            var match = System.Text.RegularExpressions.Regex.Match(filter, @"blur\s*\(\s*([\d.]+)(?:px)?\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    return val;
            }
            return 0;
        }
        
        /// <summary>
        /// Parse CSS mix-blend-mode to SKBlendMode
        /// </summary>
        private SKBlendMode ParseBlendMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return SKBlendMode.SrcOver;
            
            switch (mode.ToLowerInvariant().Trim())
            {
                case "multiply": return SKBlendMode.Multiply;
                case "screen": return SKBlendMode.Screen;
                case "overlay": return SKBlendMode.Overlay;
                case "darken": return SKBlendMode.Darken;
                case "lighten": return SKBlendMode.Lighten;
                case "color-dodge": return SKBlendMode.ColorDodge;
                case "color-burn": return SKBlendMode.ColorBurn;
                case "hard-light": return SKBlendMode.HardLight;
                case "soft-light": return SKBlendMode.SoftLight;
                case "difference": return SKBlendMode.Difference;
                case "exclusion": return SKBlendMode.Exclusion;
                case "hue": return SKBlendMode.Hue;
                case "saturation": return SKBlendMode.Saturation;
                case "color": return SKBlendMode.Color;
                case "luminosity": return SKBlendMode.Luminosity;
                default: return SKBlendMode.SrcOver;
            }
        }

        private bool IsTransformContainer(LiteElement element)
        {
            if (element == null) return false;
            // Check if element has any property that establishes a containing block for fixed elements
            if (_styles.TryGetValue(element, out var style))
            {
                if (Check(style, "transform") || 
                    Check(style, "filter") || 
                    Check(style, "perspective") || 
                    Check(style, "backdrop-filter")) 
                    return true;
                    
                if (style.Map.TryGetValue("will-change", out var wc) && 
                   (wc.Contains("transform") || wc.Contains("filter") || wc.Contains("perspective")))
                   return true;

                if (style.Map.TryGetValue("contain", out var c) && 
                   (c.Contains("paint") || c.Contains("layout") || c.Contains("strict") || c.Contains("content")))
                   return true;
            }
            return false;
        }
        
        private bool Check(CssComputed style, string key)
        {
            return style.Map.TryGetValue(key, out var val) && !string.Equals(val, "none", StringComparison.OrdinalIgnoreCase);
        }

        private SKRect FindFixedContainer(LiteElement element)
        {
            var parent = element.Parent;
            while (parent != null)
            {
                if (IsTransformContainer(parent))
                {
                     if (_boxes.TryGetValue(parent, out var box))
                         return box.PaddingBox;
                }
                parent = parent.Parent;
            }
            return _viewport;
        }

        private SKRect FindAbsoluteContainer(LiteElement element)
        {
             var parent = element.Parent;
             while (parent != null)
             {
                 if (IsTransformContainer(parent))
                 {
                     if (_boxes.TryGetValue(parent, out var box)) return box.PaddingBox;
                 }
                 
                 // Also stop at positioned ancestor
                 if (_styles.TryGetValue(parent, out var style) && 
                     style.Position != null && 
                     style.Position != "static")
                 {
                     if (_boxes.TryGetValue(parent, out var box)) return box.PaddingBox;
                 }
                 
                 parent = parent.Parent;
             }
             return _viewport;
        }
        
        /// <summary>
        /// Check if an element creates a new stacking context (CSS spec rules)
        /// </summary>
        private bool CreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;
            
            // 1. position: absolute/relative/fixed/sticky with z-index != auto
            string pos = style.Position?.ToLowerInvariant();
            if (pos == "absolute" || pos == "relative" || pos == "fixed" || pos == "sticky")
            {
                if (style.ZIndex.HasValue) return true;
            }
            
            // 2. opacity < 1
            if (style.Opacity.HasValue && style.Opacity.Value < 1.0) return true;
            
            // 3. transform != none
            if (!string.IsNullOrEmpty(style.Transform) && 
                !style.Transform.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            
            // 4. filter != none
            if (!string.IsNullOrEmpty(style.Filter) && 
                !style.Filter.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            
            // 5. will-change contains transform, opacity, filter
            if (style.Map.TryGetValue("will-change", out var wc) && !string.IsNullOrEmpty(wc))
            {
                if (wc.Contains("transform") || wc.Contains("opacity") || wc.Contains("filter"))
                    return true;
            }
            
            // 6. contain: layout, paint, strict, content
            if (style.Map.TryGetValue("contain", out var contain) && !string.IsNullOrEmpty(contain))
            {
                if (contain.Contains("layout") || contain.Contains("paint") || 
                    contain.Contains("strict") || contain.Contains("content"))
                    return true;
            }
            
            // 7. display: flex/grid with z-index
            string disp = style.Display?.ToLowerInvariant();
            if ((disp == "flex" || disp == "grid" || disp == "inline-flex" || disp == "inline-grid") &&
                style.ZIndex.HasValue)
                return true;
            
            return false;
        }

        /// <summary>
        /// Evaluates a CSS expression string (calc, min, max, clamp, env) into a pixel value.
        /// </summary>
        private float EvaluateCssExpression(string expression, float parentSize)
        {
            if (string.IsNullOrWhiteSpace(expression)) return 0;
            expression = expression.Trim().ToLowerInvariant();

            try 
            {
                // Simple recursive descent approach
                return ParseExpression(expression, parentSize);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[CSS-Eval] Failed to evaluate '{expression}': {ex.Message}", LogCategory.CSS);
                return 0;
            }
        }

        private float ParseExpression(string expr, float parentSize)
        {
            expr = expr.Trim();
            
            // 1. Handle Functions
            if (expr.StartsWith("min(")) return ParseFunctionArgs(expr, "min", parentSize);
            if (expr.StartsWith("max(")) return ParseFunctionArgs(expr, "max", parentSize);
            if (expr.StartsWith("clamp(")) return ParseFunctionArgs(expr, "clamp", parentSize);
            if (expr.StartsWith("calc(")) return ParseCalc(expr.Substring(5, expr.Length - 6), parentSize);
            if (expr.StartsWith("var(")) return 0; // Requires variable resolution context not available here easily
            if (expr.StartsWith("env(")) 
            {
                 // Mock safe-area-inset envs
                 if (expr.Contains("safe-area-inset")) return 0; 
                 return 0;
            }

            // 2. Handle Values
            if (expr.EndsWith("px"))
            {
                if (float.TryParse(expr.Substring(0, expr.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px))
                    return px;
            }
            else if (expr.EndsWith("%"))
            {
                if (float.TryParse(expr.Substring(0, expr.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                    return parentSize * (pct / 100f);
            }
            else if (expr.EndsWith("vh"))
            {
                if (float.TryParse(expr.Substring(0, expr.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vh))
                    return _viewportHeight * (vh / 100f);
            }
            else if (expr.EndsWith("vw"))
            {
                 if (float.TryParse(expr.Substring(0, expr.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vw))
                    return _viewportWidth * (vw / 100f); // Need access to width, assume availableWidth tracking or pass it? 
                    // Note: parentSize passed in is usually the relevant dimension (width for width props, height for height props).
                    // This is a simplification.
            }
            else if (float.TryParse(expr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }

            return 0;
        }

        private float ParseFunctionArgs(string expr, string funcName, float parentSize)
        {
            // Remove func( and last )
            int start = funcName.Length + 1;
            string inner = expr.Substring(start, expr.Length - start - 1);
            
            // Split by comma respecting nested parentheses
            var args = SplitArgs(inner);
            var values = args.Select(a => ParseExpression(a, parentSize)).ToList();

            if (values.Count == 0) return 0;

            if (funcName == "min") return values.Min();
            if (funcName == "max") return values.Max();
            if (funcName == "clamp")
            {
                if (values.Count < 3) return values[0];
                float min = values[0];
                float val = values[1];
                float max = values[2];
                return Math.Min(Math.Max(val, min), max); // clamp(MIN, VAL, MAX)
            }
            return 0;
        }
        
        private float ParseCalc(string inner, float parentSize)
        {
            // Very basic calc support: A + B, A - B
            // Does not support order of operations or * / yet properly
            // Removing simplistic splitting because calc can contain nested (), e.g. calc(100% - (10px + 20px))
            // For now, support simple addition/subtraction
            
            // Normalize spaces
            inner = inner.Replace(" + ", "+").Replace(" - ", "-");
            
            // Recursive descent expression parser would be better, but for MVP:
            // Just handle "100%-20px" style linear combinations
            
            float total = 0;
            // TODO: Proper expression parser. This is a hacky implementation.
            // Split by + and - but respect parens? Complex.
            // Fallback: Just return 0 if complicated, or implement better parser later.
            // Let's at least handle single unit calc(100%)
            return ParseExpression(inner, parentSize);
        }

        private List<string> SplitArgs(string s)
        {
            // Split by comma, ignoring commas inside parens
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0)
                {
                    list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            list.Add(s.Substring(start));
            return list;
        }
    }
}
