using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Pure layout computation engine.
    /// Currently serves as a facade - methods will be incrementally migrated from SkiaDomRenderer.
    /// 
    /// Goal: No painting, no Skia rendering, no JS execution.
    /// </summary>
    public sealed class LayoutEngine
    {
        private readonly LayoutContext _context;
        private readonly ILayoutComputer _computer;
        private int _layoutDepth = 0;
        
        /// <summary>
        /// Creates a new layout engine with the given context and computer.
        /// </summary>
        public LayoutEngine(LayoutContext context, ILayoutComputer computer)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _computer = computer;
        }
        
        /// <summary>
        /// Creates a new layout engine from style dictionary and viewport.
        /// </summary>
        public LayoutEngine(
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            ILayoutComputer computer = null,
            string baseUri = null)
        {
            _context = new LayoutContext(styles, viewportWidth, viewportHeight);
            _computer = computer ?? new MinimalLayoutComputer(styles, viewportWidth, viewportHeight, baseUri);
        }
        
        /// <summary>
        /// Creates a default layout engine (for simple use cases).
        /// </summary>
        public LayoutEngine()
        {
            _context = new LayoutContext(new Dictionary<Node, CssComputed>(), 1920, 1080);
            _computer = new MinimalLayoutComputer(_context.Styles, 1920, 1080, null);
        }
        
        /// <summary>
        /// The layout context containing computed boxes and state.
        /// </summary>
        public LayoutContext Context => _context;
        

        
        /// <summary>
        /// Compute layout for the entire tree using the 2-pass Measure/Arrange protocol.
        /// Returns an immutable LayoutResult.
        /// </summary>
        public LayoutResult ComputeLayout(
            Node node, 
            float x, 
            float y, 
            float availableWidth, 
            bool shrinkToContent = false, 
            float availableHeight = 0, 
            bool hasTargetAncestor = false)
        {
            // Sanitize inputs
            if (float.IsNaN(x)) x = 0;
            if (float.IsNaN(y)) y = 0;
            if (float.IsNaN(availableWidth) || availableWidth < 0) 
            {
                availableWidth = _context.ViewportWidth > 0 ? _context.ViewportWidth : 800;
            }
            if (float.IsNaN(availableHeight) || availableHeight < 0) availableHeight = 0;

            // CRITICAL CHECK: Verify this code is running
            if (_layoutDepth == 0)
            {
                FenLogger.Log("NEW layout algorithm active", LogCategory.Layout);
            }

            _layoutDepth++;
            try
            {
                // Ensure we have enough stack to proceed
                try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
                catch (InsufficientExecutionStackException) { return null; }

                if (_layoutDepth > 100) throw new Exception($"Layout recursion too deep on {node.Tag ?? "unknown"}");
                
                if (_computer != null)
                {
                    // PASS 1: Measure
                    // Node says: "I need this much space based on my children."
                    SKSize availableSize = new SKSize(availableWidth, availableHeight > 0 ? availableHeight : _context.ViewportHeight);
                    
                    // ARCHITECTURAL FIX: Force Root Node to measure against the ACTUAL screen width
                    // This prevents "Black Hole" collapse by ensuring the root knows it has the full screen.
                    if (_layoutDepth == 1)
                    {
                        availableSize = new SKSize(
                            _context.ViewportWidth > 0 ? _context.ViewportWidth : availableWidth,
                            _context.ViewportHeight > 0 ? _context.ViewportHeight : availableHeight
                        );
                    }

                    FenLogger.Debug($"[LayoutEngine] Starting Measure for {node.Tag ?? "#text"} availableSize={availableSize}", LogCategory.Rendering);
                    var desiredSize = _computer.Measure(node, availableSize);
                    FenLogger.Debug($"[LayoutEngine] Measure Result for {node.Tag ?? "#text"}: {desiredSize}", LogCategory.Rendering);

                    // PASS 2: Arrange
                    // Parent commands: "Here is your rectangle. Live in it."
                    
                    float finalX = x;
                    float finalY = y;
                    float finalW = shrinkToContent ? desiredSize.MaxChildWidth : availableWidth;
                    float finalH = desiredSize.ContentHeight;

                    // ========== ENGINE-LEVEL ICB INVARIANT ==========
                    // The Initial Containing Block (ICB) MUST be viewport-sized.
                    // Root element (depth 1) gets placed at (0,0) and MUST fill viewport.
                    // This is NOT optional. This is NOT a CSS property.
                    // This is the layout engine's responsibility.
                    //
                    // Even if content is 200px, root box must be 899px (viewport height).
                    // This enables:
                    //   - body { min-height: 100vh } to work
                    //   - Flex containers to stretch
                    //   - Vertical centering to work
                    // ================================================
                    if (_layoutDepth == 1)
                    {
                        finalX = 0;
                        finalY = 0;
                        
                        // CRITICAL: Root MUST be at least viewport height
                        // This is the ICB constraint - content can be smaller but box cannot
                        finalH = Math.Max(finalH, _context.ViewportHeight);
                        
                        // Root width should also fill viewport (standard block behavior)
                        finalW = Math.Max(finalW, _context.ViewportWidth);
                    }

                    var finalRect = new SKRect(finalX, finalY, finalX + finalW, finalY + finalH);
                    FenLogger.Debug($"[LayoutEngine] Starting Arrange for {node.Tag ?? "#text"} finalRect={finalRect}", LogCategory.Rendering);
                    _computer.Arrange(node, finalRect);
                    FenLogger.Debug($"[LayoutEngine] Arrange Complete for {node.Tag ?? "#text"}", LogCategory.Rendering);
                    
                    // PASS 3: Verification Hooks
                    if (_layoutDepth == 1)
                    {
                        global::FenBrowser.Core.Verification.ContentVerifier.RegisterZeroSizedCount(_computer.GetZeroSizedCount());
                    }
                    
                    // Build result only at root level
                    if (_layoutDepth == 1)
                    {
                        // ICB VALIDATION LOG: Verify html height >= viewport
                        string htmlTag = node?.Tag?.ToUpperInvariant() ?? "unknown";
                        FenLogger.Log($"[ICB] ICB: {_context.ViewportWidth}x{_context.ViewportHeight} | {htmlTag}: {finalW}x{finalH}", LogCategory.Layout);
                        if (finalH < _context.ViewportHeight)
                        {
                            FenLogger.Log($"[ICB] WARNING: Root height {finalH} < viewport {_context.ViewportHeight} - BUG!", LogCategory.Layout);
                        }
                        
                        // Optional: Dump tree for debugging
                        try { _computer.DumpLayoutTree(node); } catch { }
                        return BuildResult(availableWidth, finalH);
                    }
                    return null;
                }
            }
            finally
            {
                _layoutDepth--;
            }
            
            return new LayoutResult(
                new Dictionary<Element, ElementGeometry>(),
                availableWidth,
                availableHeight,
                0, // scrollOffsetY
                0  // contentHeight
            );
        }
        
        /// <summary>
        /// Simplified entry point for root layout.
        /// </summary>
        public LayoutResult ComputeLayout(Node root, float availableWidth, float availableHeight)
        {
            return ComputeLayout(root, 0, 0, availableWidth, false, availableHeight, false);
        }

        
        /// <summary>
        /// Creates a LayoutResult from the current context state.
        /// Used after layout is computed to build an immutable result.
        /// </summary>
        public LayoutResult BuildResult(float contentWidth, float contentHeight)
        {
            // Convert internal BoxModel to the LayoutResult format
            var elementRects = new Dictionary<Element, ElementGeometry>();
            
            // Start with local boxes
            IEnumerable<KeyValuePair<Node, BoxModel>> allBoxes = _context.Boxes;
            
            // If delegating, include boxes from the computer
            if (_computer != null)
            {
                allBoxes = allBoxes.Concat(_computer.GetAllBoxes());
            }
            
            foreach (var kvp in allBoxes)
            {
                if (kvp.Key is Element elem && kvp.Value != null)
                {
                    var box = kvp.Value.BorderBox;
                    // Last write wins for same element
                    elementRects[elem] = new ElementGeometry(box.Left, box.Top, box.Width, box.Height);
                }
            }
            
            return new LayoutResult(
                elementRects,
                _context.ViewportWidth,
                _context.ViewportHeight,
                0, // scrollY - will be injected from ScrollModel
                contentHeight
            );
        }
        
        public CssComputed GetStyle(Node node) => _context.GetStyle(node);
        public BoxModel GetBox(Node node) => _context.GetBox(node);
        
        /// <summary>
        /// Expose all calculated boxes for rendering and debugging.
        /// </summary>
        public IReadOnlyDictionary<Node, BoxModel> AllBoxes 
        { 
            get 
            {
                 var dict = new Dictionary<Node, BoxModel>();
                 foreach(var kvp in _context.Boxes) dict[kvp.Key] = kvp.Value;
                 if (_computer != null)
                 {
                     foreach(var kvp in _computer.GetAllBoxes()) dict[kvp.Key] = kvp.Value;
                 }
                 return dict;
            }
        }
        
        // --- Phase 3: Reverse Pipeline (HitTest) ---
        
        /// <summary>
        /// Performs hit testing to find the deepest DOM node at the given physical coordinates.
        /// Implements the reverse pipeline: Click (x,y) → Layout → DOM Node.
        /// </summary>
        /// <param name="x">Physical X coordinate.</param>
        /// <param name="y">Physical Y coordinate.</param>
        /// <param name="root">Root node to start search from.</param>
        /// <returns>The deepest DOM node containing the point, or null if none found.</returns>
        public Node HitTest(float x, float y, Node root)
        {
            if (root == null) return null;
            
            Node result = null;
            HitTestRecursive(x, y, root, ref result);
            return result;
        }
        
        private void HitTestRecursive(float x, float y, Node node, ref Node deepestHit)
        {
            // Get box for this node
            BoxModel box = null;
            
            // Try computer first (most boxes are stored there)
            if (_computer != null)
            {
                box = _computer.GetBox(node);
            }
            
            // Fallback to context
            if (box == null)
            {
                box = _context.GetBox(node);
            }
            
            if (box != null)
            {
                // Check if point is inside this node's border box
                var rect = box.BorderBox;
                if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom)
                {
                    // This node contains the point - it's a candidate
                    deepestHit = node;
                    
                    // Continue searching children for a deeper hit
                    if (node.Children != null)
                    {
                        foreach (var child in node.Children)
                        {
                            HitTestRecursive(x, y, child, ref deepestHit);
                        }
                    }
                }
            }
            else
            {
                // No box for this node - still check children (might be a wrapper)
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        HitTestRecursive(x, y, child, ref deepestHit);
                    }
                }
            }
        }
    }
}