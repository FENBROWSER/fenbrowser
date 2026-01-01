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
        /// All computed boxes, including those from the layout computer.
        /// </summary>
        public IEnumerable<KeyValuePair<Node, BoxModel>> AllBoxes
        {
            get
            {
                var boxes = _context.Boxes.AsEnumerable();
                if (_computer != null)
                {
                    boxes = boxes.Concat(_computer.GetAllBoxes());
                }
                return boxes;
            }
        }
        
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

                    // ARCHITECTURAL FIX: Force Root Node to fill the Viewport
                    // This prevents "Black Hole" collapse and provides the anchor for centering.
                    if (_layoutDepth == 1)
                    {
                        finalX = 0;
                        finalY = 0;
                        finalW = _context.ViewportWidth > 0 ? _context.ViewportWidth : availableWidth;
                        // ARCHITECTURAL FIX: 
                        // Root should be AT LEAST the viewport height, but can grow if content is taller.
                        finalH = Math.Max(desiredSize.ActualHeight, _context.ViewportHeight > 0 ? _context.ViewportHeight : availableHeight);
                    }

                    var finalRect = new SKRect(finalX, finalY, finalX + finalW, finalY + finalH);
                    FenLogger.Debug($"[LayoutEngine] Starting Arrange for {node.Tag ?? "#text"} finalRect={finalRect}", LogCategory.Rendering);
                    _computer.Arrange(node, finalRect);
                    FenLogger.Debug($"[LayoutEngine] Arrange Complete for {node.Tag ?? "#text"}", LogCategory.Rendering);
                    
                    // Build result only at root level
                    if (_layoutDepth == 1)
                    {
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
    }
}