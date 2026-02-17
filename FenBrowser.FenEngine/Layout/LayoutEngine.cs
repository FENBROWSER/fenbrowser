using FenBrowser.Core.Dom.V2;
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
            FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            // New Pipeline Entry Point (Active)
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", $"[LayoutEngine] ComputeLayout Called for {node?.GetType().Name}\n"); } catch {}

            // 1. Build Box Tree
            var builder = new FenBrowser.FenEngine.Layout.Tree.BoxTreeBuilder(_context.Styles);
            var rootBox = builder.Build(node);
            
            if (rootBox == null)
            {
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", "[LayoutEngine] WARN: RootBox is null. Layout aborted.\n"); } catch {}
                return null; 
            }
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", $"[LayoutEngine] RootBox built: {rootBox}\n"); } catch {}

            // Check deadline before starting heavy layout pass
            deadline?.Check();

            // 2. Prepare Root Layout State
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
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", $"[LayoutEngine] Resolved Context: {context?.GetType().Name}\n"); } catch {}
            context.Layout(rootBox, initialState);
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", "[LayoutEngine] Layout Pass Complete\n"); } catch {}

            // 4. Transform Result back to Legacy Format (for Renderer compatibility)
            var elementRects = new Dictionary<Element, ElementGeometry>();
            
            // PASS 1: Flatten for Legacy API (Absolute Coordinates)
            FlattenBoxTreeAbsolute(rootBox, elementRects, _context, 0, 0); 
            
            // PASS 2: Collect All Boxes for Renderer (Absolute Coordinates)
            var accumulatedBoxes = new Dictionary<Node, BoxModel>();
            CollectBoxesAbsolute(rootBox, accumulatedBoxes, 0, 0);
            
            _generatedBoxes = accumulatedBoxes;

            
            // DUMP TREE FOR DEBUGGING
            FenLogger.Debug("--- NEW PIPELINE LAYOUT DUMP ---", LogCategory.Rendering);
            DumpBoxTree(rootBox, 0);
            FenLogger.Debug("--- END NEW PIPELINE DUMP ---", LogCategory.Rendering);

            return new LayoutResult(
                elementRects,
                availableWidth,
                availableHeight,
                0,
                rootBox.Geometry.ContentBox.Height // Approx content height
            );
            

            // LEGACY PIPELINE RESTORATION (DISABLED)
            /*
            // Ensure we use the computer to generate boxes
            if (_computer is MinimalLayoutComputer minComp)
            {
                minComp.Deadline = deadline;
            }

            var measureMetrics = _computer.Measure(node, new SKSize(availableWidth, availableHeight));
            _computer.Arrange(node, new SKRect(0, 0, availableWidth, availableHeight));
            
            return BuildResult(measureMetrics.MaxChildWidth, measureMetrics.ContentHeight);
            */
        }
        
        private Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> _generatedBoxes;
        
        private void CollectBoxesAbsolute(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> dict, float parentContentAbsX, float parentContentAbsY)
        {
            if (box == null) return;

            // Calculate relative position offset (applied to this element only, not children)
            float relOffsetX = 0, relOffsetY = 0;
            var style = box.ComputedStyle;
            if (style != null && string.Equals(style.Position, "relative", StringComparison.OrdinalIgnoreCase))
            {
                // CSS spec: top/left take priority over bottom/right
                if (style.Top.HasValue) relOffsetY = (float)style.Top.Value;
                else if (style.Bottom.HasValue) relOffsetY = -(float)style.Bottom.Value;

                if (style.Left.HasValue) relOffsetX = (float)style.Left.Value;
                else if (style.Right.HasValue) relOffsetX = -(float)style.Right.Value;
            }

            if (box.SourceNode != null)
            {
                if (!dict.ContainsKey(box.SourceNode))
                {
                    var absModel = new BoxModel
                    {
                        ContentBox = box.Geometry.ContentBox,
                        PaddingBox = box.Geometry.PaddingBox,
                        BorderBox = box.Geometry.BorderBox,
                        MarginBox = box.Geometry.MarginBox,
                        Lines = box.Geometry.Lines,  // Copy text lines for proper rendering
                        Baseline = box.Geometry.Baseline,
                        LineHeight = box.Geometry.LineHeight,
                        Ascent = box.Geometry.Ascent,
                        Descent = box.Geometry.Descent
                    };

                    FenBrowser.FenEngine.Layout.Contexts.LayoutBoxOps.ShiftBoxModel(absModel, parentContentAbsX + relOffsetX, parentContentAbsY + relOffsetY);

                    dict[box.SourceNode] = absModel;
                }
            }

            // Children use UNSHIFTED coordinates (relative positioning doesn't affect children's flow)
            float currentContentAbsX = parentContentAbsX + (float)box.Geometry.ContentBox.Left;
            float currentContentAbsY = parentContentAbsY + (float)box.Geometry.ContentBox.Top;

            foreach(var c in box.Children)
                CollectBoxesAbsolute(c, dict, currentContentAbsX, currentContentAbsY);
        }

        private void FlattenBoxTreeAbsolute(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Element, ElementGeometry> rects, LayoutContext context, float parentContentAbsX, float parentContentAbsY)
        {
             if (box == null) return;

             // Calculate relative position offset
             float relOffsetX = 0, relOffsetY = 0;
             var style = box.ComputedStyle;
             if (style != null && string.Equals(style.Position, "relative", StringComparison.OrdinalIgnoreCase))
             {
                 // CSS spec: top/left take priority over bottom/right
                 if (style.Top.HasValue) relOffsetY = (float)style.Top.Value;
                 else if (style.Bottom.HasValue) relOffsetY = -(float)style.Bottom.Value;

                 if (style.Left.HasValue) relOffsetX = (float)style.Left.Value;
                 else if (style.Right.HasValue) relOffsetX = -(float)style.Right.Value;
             }

             // Apply relative offset to THIS element's visual position only
             float absBorderX = parentContentAbsX + (float)box.Geometry.BorderBox.Left + relOffsetX;
             float absBorderY = parentContentAbsY + (float)box.Geometry.BorderBox.Top + relOffsetY;

             if (box.SourceNode is Element el)
             {
                 var b = box.Geometry.BorderBox;
                 rects[el] = new ElementGeometry(absBorderX, absBorderY, b.Width, b.Height);
             }

             // Children use UNSHIFTED coordinates (relative positioning doesn't affect children's flow)
             float currentContentAbsX = parentContentAbsX + (float)box.Geometry.ContentBox.Left;
             float currentContentAbsY = parentContentAbsY + (float)box.Geometry.ContentBox.Top;

             foreach(var c in box.Children) FlattenBoxTreeAbsolute(c, rects, context, currentContentAbsX, currentContentAbsY);
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
                    if (node.ChildNodes != null)
                    {
                        foreach (var child in node.ChildNodes)
                        {
                            HitTestRecursive(x, y, child, ref deepestHit);
                        }
                    }
                }
            }
            else
            {
                // No box for this node - still check children (might be a wrapper)
                if (node.ChildNodes != null)
                {
                    foreach (var child in node.ChildNodes)
                    {
                        HitTestRecursive(x, y, child, ref deepestHit);
                    }
                }
            }
        }



        private void DumpBoxTree(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, int depth)
        {
            if (box == null) return;
            
            string indent = new string(' ', depth * 2);
            string tagName = (box.SourceNode as Element)?.TagName ?? box.GetType().Name;
            var r = box.Geometry.BorderBox;
            string rectStr = $"[{r.Left:F1}, {r.Top:F1} {r.Width:F1}x{r.Height:F1}]";
            string extra = "";
            if (box.ComputedStyle?.Display == "flex") extra += " (FLEX)";
            
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\layout_engine_debug.txt", $"{indent}{tagName} {rectStr}{extra}\n"); } catch {}
            
            foreach(var c in box.Children)
            {
                DumpBoxTree(c, depth + 1);
            }
        }
    }
}
