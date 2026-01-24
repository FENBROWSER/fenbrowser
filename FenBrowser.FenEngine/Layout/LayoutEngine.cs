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
            bool hasTargetAncestor = false,
            FenBrowser.FenEngine.Core.RenderDeadline deadline = null)
        {
            if (_layoutDepth == -1) // DISABLED: Reverting to legacy pipeline (Patched)
            {
                // New Pipeline Entry Point
                
                // 1. Build Box Tree
                // Note: We need styles to be pre-calculated.
                // Assuming styles are passed in constructor or context.
                var builder = new FenBrowser.FenEngine.Layout.Tree.BoxTreeBuilder(_context.Styles);
                var rootBox = builder.Build(node);
                
                if (rootBox == null) return null; // Nothing to layout

                // Check deadline before starting heavy layout pass
                deadline?.Check();

                // 2. Prepare Root Layout State
                // The Initial Containing Block (ICB)
                availableWidth = Math.Max(availableWidth, _context.ViewportWidth);
                availableHeight = Math.Max(availableHeight, _context.ViewportHeight);
                
                var initialState = new FenBrowser.FenEngine.Layout.Contexts.LayoutState(
                    new SKSize(availableWidth, availableHeight),
                    availableWidth,
                    availableHeight,
                    _context.ViewportWidth,
                    _context.ViewportHeight,
                    deadline
                );

                // 3. Layout!
                var context = FenBrowser.FenEngine.Layout.Contexts.FormattingContext.Resolve(rootBox);
                context.Layout(rootBox, initialState);

                // 4. Transform Result back to Legacy Format (for Renderer compatibility)
                // We need to verify what LayoutEngine usually returns.
                // It returns a LayoutResult containing 'elementRects'.
                // AND it populates _context.Boxes / _computer.AllBoxes.
                
                // We must populate _computer.AllBoxes (or a new storage) so that HitTest and Renderer work.
                // Since _computer is ILayoutComputer (legacy), we might need to bypass it or adapt.
                // Let's populate a dictionary and wrap it?
                
                var elementRects = new Dictionary<Element, ElementGeometry>();
                FlattenBoxTree(rootBox, elementRects, _context); // Helper to flatten
                
                // Also update the legacy Computer if it's a minimal one?
                // MinimalLayoutComputer has its own cache.
                // We are REPLACING MinimalLayoutComputer's logic here effectively.
                // But Renderer calls 'AllBoxes'.
                // We should probably stash the results in _context.Boxes directly?
                // _context.Boxes is a Dictionary<Node, BoxModel>.
                
                // Let's clear and re-fill _context.Boxes
                // (Note: LayoutContext.Boxes is typically read-only or managed by context?? 
                // Checks LayoutContext definition... it's a valid property?)
                // Assuming we can write to it or we need to expose a way.
                
                // Wait, LayoutContext doesn't expose a setter for Boxes dictionary usually.
                // It exposes 'GetBox'.
                // Use a temporary workaround: Reflective set or assume we are the source of truth.
                
                // Actually, LayoutEngine is the Facade. 'AllBoxes' property compiles bounds.
                // We should create a new LayoutResult that encapsulates this.
                // AND we need to ensure subsequent calls to GetBox(node) return our new boxes.
                
                // Implementation Detail: Flatten tree into a Dictionary<Node, BoxModel>
                // and pass it to a new Result or update internal state.
                
                var accumulatedBoxes = new Dictionary<Node, BoxModel>();
                CollectBoxes(rootBox, accumulatedBoxes);
                
                // HACK: Updating the legacy computer's internal cache if possible, or just trusting logic.
                // Better: Create a 'BoxTreeLayoutResult' and make LayoutEngine return that.
                // But call site expects 'LayoutResult'.
                
                // For now, let's just return the Result and rely on the fact that 
                // SkiaDomRenderer calls 'layoutEngine.AllBoxes' or similar?
                // SkiaDomRenderer.cs: 
                // _lastLayout = layoutEngine.ComputeLayout(...)
                // _boxes.Clear(); foreach(var b in layoutEngine.AllBoxes) ...
                
                // So we need 'layoutEngine.AllBoxes' to return our new boxes.
                // LayoutEngine.AllBoxes joins _context.Boxes + _computer.GetAllBoxes().
                
                // We can't easily inject into _computer (MinimalLayoutComputer).
                // WE SHOULD INSTANTIATE A NEW 'TreeLayoutComputer' ADAPTER!
                // But we are inside LayoutEngine.
                
                // Solution: We implement ILayoutComputer on a new class 'BoxTreeAdapter'
                // and swap _computer to use it? 
                // Or we update LayoutEngine to hold these boxes directly.
                
                // Let's store them in a local field in LayoutEngine and have AllBoxes return them?
                _generatedBoxes = accumulatedBoxes;

                return new LayoutResult(
                    elementRects,
                    availableWidth,
                    availableHeight,
                    0,
                    rootBox.Geometry.ContentBox.Height // Approx content height
                );
            }

            // LEGACY PIPELINE RESTORATION
            // Ensure we use the computer to generate boxes
            var measureMetrics = _computer.Measure(node, new SKSize(availableWidth, availableHeight));
            _computer.Arrange(node, new SKRect(0, 0, availableWidth, availableHeight));
            
            return BuildResult(measureMetrics.MaxChildWidth, measureMetrics.ContentHeight);
        }
        
        private Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> _generatedBoxes;
        
        private void CollectBoxes(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> dict)
        {
            if (box == null) return;
            if (box.SourceNode != null)
            {
                // Key collision check? 
                // If a node has multiple boxes (fragmentation), we only store the first for now.
                if (!dict.ContainsKey(box.SourceNode))
                {
                    dict[box.SourceNode] = box.Geometry;
                }
            }
            foreach(var c in box.Children) CollectBoxes(c, dict);
        }

        private void FlattenBoxTree(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Element, ElementGeometry> rects, LayoutContext context)
        {
             if (box == null) return;
             
             if (box.SourceNode is Element el)
             {
                 var b = box.Geometry.BorderBox;
                 rects[el] = new ElementGeometry(b.Left, b.Top, b.Width, b.Height);
             }
             
             if (box.SourceNode != null)
             {
                 context.SetBox(box.SourceNode, box.Geometry);
             }
             
             foreach(var c in box.Children) FlattenBoxTree(c, rects, context);
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
                 if (_generatedBoxes != null) return _generatedBoxes;

                 var dict = new Dictionary<Node, BoxModel>();
                 foreach(var kvp in _context.Boxes) dict[kvp.Key] = kvp.Value;
                 
                 if (_computer != null)
                 {
                     foreach(var kvp in _computer.GetAllBoxes()) 
                     {
                         if (!dict.ContainsKey(kvp.Key))
                             dict[kvp.Key] = kvp.Value;
                     }
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