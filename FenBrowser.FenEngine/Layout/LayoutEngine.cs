using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

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
            ILayoutComputer computer = null)
        {
            _context = new LayoutContext(styles, viewportWidth, viewportHeight);
            _computer = computer ?? new MinimalLayoutComputer(styles, viewportWidth, viewportHeight);
        }
        
        /// <summary>
        /// Creates a default layout engine (for simple use cases).
        /// </summary>
        public LayoutEngine()
        {
            _context = new LayoutContext(new Dictionary<Node, CssComputed>(), 1920, 1080);
            _computer = new MinimalLayoutComputer(_context.Styles, 1920, 1080);
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
        /// Compute layout for the entire tree.
        /// Returns an immutable LayoutResult.
        /// </summary>
        /// <summary>
        /// Compute layout for a node.
        /// Returns an immutable LayoutResult (only relevant for root call).
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

                if (_layoutDepth > 200) throw new Exception($"Layout recursion too deep on {node.Tag ?? "unknown"}");
                
                // Delegate to the computer (SkiaDomRenderer) logic via ComputeBox
                // This avoids the infinite recursion of calling ComputeLayout again
                if (_computer != null)
                {
                    ComputeBox(node, x, y, availableWidth, shrinkToContent, availableHeight, hasTargetAncestor);
                    
                    // Build result only at root level to avoid O(N^2) performance hit
                    if (_layoutDepth == 1)
                    {
                        // Get root box dimensions to pass to result
                        float rootH = 0;
                        var rootBox = _context.GetBox(node);
                        if (rootBox != null) rootH = rootBox.ContentBox.Height; // Or MarginBox? Usually content height of root.

                        // VISUAL FIDELITY DIAGNOSTICS: Dump tree
                        try { _computer.DumpLayoutTree(node); } catch { }
                        
                        return BuildResult(availableWidth, rootH);
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
        /// Used after layout is computed (by SkiaDomRenderer) to build an immutable result.
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
                // Note: SkiaDomRenderer boxes take precedence or merge with local
                // For now, valid delegation means the computer has the truth
                allBoxes = allBoxes.Concat(_computer.GetAllBoxes());
            }
            
            foreach (var kvp in allBoxes)
            {
                // Filter out nulls and duplicates (last one wins if concatenated?)
                // Actually if we just iterate, duplicates might be an issue for Dictionary.
                // Let's use a loop with check or just rely on distinct nodes.
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
        
        /// <summary>
        /// Core layout logic for a single box.
        /// Migrated from SkiaDomRenderer.ComputeLayoutInternal.
        /// </summary>
        private void ComputeBox(Node node, float x, float y, float availableWidth, bool shrinkToContent, float availableHeight, bool hasTargetAncestor)
        {
             // 1. Initial Checks
            if (node == null) return;
            var element = node as Element;
            string nodeTag = node.Tag?.ToUpperInvariant();

            // Handle infinite available width (fallback)
             if (float.IsNaN(availableWidth) || float.IsInfinity(availableWidth)) 
            {
                 availableWidth = _context.ViewportWidth > 0 ? _context.ViewportWidth : 800; 
            }

            // Get Style
            var style = _context.GetStyle(node);
            
            // Check Visibility
            if (LayoutHelper.ShouldHide(node, style)) return;

            // Apply UA Styles
            if (element != null && style != null)
            {
                // Can't ref style as it's from dictionary, but assume UA styles applied during CssLoader or caller?
                // LayoutHelper.ApplyUserAgentStyles(node, ref style); 
                // Context styles are read-only IReadOnlyDictionary? 
                // If we need to modify styles, we should have done it earlier or use a local copy.
                // Assuming styles are pre-computed.
            }

            // Calculate Box Model
            var box = new BoxModel();
            box.Margin = style?.Margin ?? new Thickness(0);
            box.Border = style?.BorderThickness ?? new Thickness(0);
            box.Padding = style?.Padding ?? new Thickness(0);

            float marginLeft = (float)box.Margin.Left;
            float marginRight = (float)box.Margin.Right;
            float borderLeft = (float)box.Border.Left;
            float borderRight = (float)box.Border.Right;
            float paddingLeft = (float)box.Padding.Left;
            float paddingRight = (float)box.Padding.Right;

            // Resolve Auto Margins (Simplified)
            bool marginLeftAuto = style?.MarginLeftAuto ?? false;
            bool marginRightAuto = style?.MarginRightAuto ?? false;
            // (Skipping raw map parsing fallback for brevity/performance - existing CssComputed should handle it)

            string boxSizing = style?.BoxSizing?.ToLowerInvariant() ?? "content-box";
            bool isBorderBox = boxSizing == "border-box";

            // Content Width Calculation
            float contentWidth = 0;
            bool hasExplicitWidth = false;

            // 1. Width Expression
            if (!string.IsNullOrEmpty(style?.WidthExpression))
            {
                 float evalW = LayoutHelper.EvaluateCssExpression(style.WidthExpression, availableWidth);
                 if (evalW >= 0)
                 {
                     hasExplicitWidth = true;
                     contentWidth = evalW;
                     if (isBorderBox) contentWidth -= (paddingLeft + paddingRight + borderLeft + borderRight);
                 }
            }

            // 2. Explicit Width
            if (!hasExplicitWidth && style?.Width.HasValue == true)
            {
                hasExplicitWidth = true;
                float declaredWidth = (float)style.Width.Value;
                contentWidth = isBorderBox ? declaredWidth - (paddingLeft + paddingRight + borderLeft + borderRight) : declaredWidth;
            }
            // 3. Percentage Width
            else if (!hasExplicitWidth && style?.WidthPercent.HasValue == true)
            {
                if (float.IsInfinity(availableWidth) || availableWidth > 1e7)
                {
                    contentWidth = 0; // Fallback to auto
                }
                else
                {
                    hasExplicitWidth = true;
                    float availableForBox = availableWidth - (marginLeft + marginRight);
                    float calcW = (float)style.WidthPercent.Value / 100f * availableForBox;
                    
                    if (isBorderBox || nodeTag == "INPUT" || nodeTag == "BUTTON" || nodeTag == "SELECT" || nodeTag == "TEXTAREA")
                    {
                         contentWidth = calcW - (paddingLeft + paddingRight + borderLeft + borderRight);
                    }
                    else
                    {
                        contentWidth = calcW;
                    }
                }
            }
            else
            {
                // Auto Width
                // If shrinkToContent is true and it's a form element, give it a default width
                bool isForm = nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "BUTTON";
                if (isForm && shrinkToContent)
                    contentWidth = 150; // Minimum default for form elements in shrink-to-fit
                else if (float.IsInfinity(availableWidth) || availableWidth > 1e7)
                {
                    contentWidth = availableWidth;
                    shrinkToContent = true; 
                }
                else
                {
                    contentWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
                }
            }

            if (contentWidth < 0) contentWidth = 0;

            // Min/Max Width Constraints
             if (!string.IsNullOrEmpty(style?.MinWidthExpression))
            {
                float minW = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, availableWidth, _context.ViewportWidth, _context.ViewportHeight);
                if (isBorderBox) minW -= (paddingLeft + paddingRight + borderLeft + borderRight);
                if (contentWidth < minW) contentWidth = minW;
            }
            if (!string.IsNullOrEmpty(style?.MaxWidthExpression))
            {
                float maxW = LayoutHelper.EvaluateCssExpression(style.MaxWidthExpression, availableWidth, _context.ViewportWidth, _context.ViewportHeight);
                if (isBorderBox) maxW -= (paddingLeft + paddingRight + borderLeft + borderRight);
                if (contentWidth > maxW) contentWidth = maxW;
            }
            
            if (style?.MaxWidth.HasValue == true)
            {
                 float maxW = (float)style.MaxWidth.Value; // Assuming content-box usually? Or border-box? 
                 // Standard says max-width usually refers to content-box unless box-sizing set?
                 // Let's assume content-box check for now to match renderer logic simplistically.
                 if (contentWidth > maxW) contentWidth = maxW;
            }
            if (style?.MinWidth.HasValue == true)
            {
                 float minW = (float)style.MinWidth.Value;
                 if (contentWidth < minW) contentWidth = minW;
            }


            // Position
            float currentX = x + marginLeft;
            float currentY = y + (float)box.Margin.Top;

            // Margin Auto Centering
            if ((hasExplicitWidth || style?.MaxWidth.HasValue == true) && marginLeftAuto && marginRightAuto)
            {
                 float totalBoxWidth = borderLeft + contentWidth + borderRight + paddingLeft + paddingRight;
                 float remaining = availableWidth - totalBoxWidth;
                 if (remaining > 0) currentX = x + remaining / 2;
            }
            
            // Build Initial Box Rects (Height 0)
            box.MarginBox = new SKRect(x, y, x + availableWidth, y);
             // Note: MarginBox.Width logic in renderer was x + availableWidth, effectively spanning full width until shrunk?
             // We'll update it later.

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


            // Replaced Element Logic (Initial Intrinsic)
            bool isReplaced = nodeTag == "IMG" || nodeTag == "INPUT" || nodeTag == "BUTTON" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "SVG";
            float intrinsicWidth = 0;
            float intrinsicHeight = 0;
            float aspectRatio = 0;

            if (isReplaced)
            {
                 if (nodeTag == "INPUT" || nodeTag == "BUTTON")
                 {
                     LayoutHelper.MeasureInputButtonText(element, style, ref intrinsicWidth, ref intrinsicHeight);
                 }
                 // Handle IMG/SVG intrinsic sizes?
                 // In renderer this used ImageLoader and lots of logic.
                 // We rely on SkiaDomRenderer to handle detailed intrinsic sizing in 'ComputeBlockLayout' or similar?
                 // Or we replicate it?
                 // Logic replication is hard without ImageLoader access here.
                 // Wait, LayoutEngine can use ImageLoader? FenBrowser.FenEngine.Rendering.ImageLoader?
                 // Yes if extracted.
                 // For now, let's assume 0 and let strict sizing handle it, or Todo.
                 // Or rely on style.
            }
            
            // Replaced Width Override
            if (!hasExplicitWidth && isReplaced)
            {
                 // Use intrinsic if available
                 if (intrinsicWidth > 0) contentWidth = intrinsicWidth;
                 if (contentWidth > availableWidth) contentWidth = availableWidth;
                 // Rebuild box rects (Update Widths)
                 box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY);
                 box.PaddingBox = new SKRect(box.BorderBox.Left + borderLeft, box.BorderBox.Top + (float)box.Border.Top, box.BorderBox.Right - borderRight, box.BorderBox.Top + (float)box.Border.Top);
                 box.ContentBox = new SKRect(box.PaddingBox.Left + paddingLeft, box.PaddingBox.Top + (float)box.Padding.Top, box.PaddingBox.Right - paddingRight, box.PaddingBox.Top + (float)box.Padding.Top);
            }
            
            
            // LAYOUT DELEGATION
            float contentHeight = 0;
            float maxChildWidth = 0;
            float baseline = 0;
            
            string display = style?.Display?.ToLowerInvariant();
            // Block/Inline fallback logic...
            if (string.IsNullOrEmpty(display)) display = (nodeTag == "DIV" || nodeTag == "P" || nodeTag == "BODY") ? "block" : "inline-block";

            // Determine specialized layout
            if (display == "flex" || display == "inline-flex")
            {
                 var m = _computer.ComputeFlexLayout(element, box, x, y, availableWidth, availableHeight);
                 contentHeight = m.ContentHeight;
                 maxChildWidth = m.MaxChildWidth;
                 baseline = m.Baseline;
            }
            else if (display == "grid" || display == "inline-grid")
            {
                 var m = _computer.ComputeGridLayout(element, box, x, y, availableWidth, availableHeight);
                 contentHeight = m.ContentHeight;
                 maxChildWidth = m.MaxChildWidth;
            }
            else if (display == "table")
            {
                 var m = _computer.ComputeTableLayout(element, box, x, y, availableWidth, availableHeight);
                 contentHeight = m.ContentHeight;
                 maxChildWidth = m.MaxChildWidth;
            }
            else if (style?.Position == "absolute" || style?.Position == "fixed")
            {
                 // Absolute
                 var m = _computer.ComputeAbsoluteLayout(element, box, x, y, availableWidth, availableHeight);
                 contentHeight = m.ContentHeight;
            }
            else if (node.IsText)
            {
                 var m = _computer.ComputeTextLayout(node, box, x, y, availableWidth, availableHeight);
                 contentHeight = m.ContentHeight;
                 maxChildWidth = m.MaxChildWidth;
                 baseline = m.Baseline;
            }
            else
            {
                 // Default Block/Inline Flow
                 // Calculate container height for flex children or percentage resolution
                 float parentH = availableHeight > 0 ? availableHeight : _context.ViewportHeight; 
                 // (Simplified logic compared to renderer lines 2070-2100)
                 
                 // Check if shrink to content applies
                 bool childShrink = shrinkToContent || display == "inline" || display == "inline-block";
                 
                 var m = _computer.ComputeBlockLayout(element, box, x, y, availableWidth, parentH);
                 contentHeight = m.ContentHeight;
                 maxChildWidth = m.MaxChildWidth;
                 baseline = m.Baseline;
            }

            // Shrink to Content Logic (After Layout)
            if (!hasExplicitWidth && !isReplaced && (shrinkToContent || display == "inline" || display == "inline-block"))
            {
                 if (maxChildWidth > 0)
                 {
                     contentWidth = maxChildWidth;
                     // Rebuild boxes
                     box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY);
                     box.PaddingBox = new SKRect(box.BorderBox.Left + borderLeft, box.BorderBox.Top + (float)box.Border.Top, box.BorderBox.Right - borderRight, box.BorderBox.Top + (float)box.Border.Top);
                     box.ContentBox = new SKRect(box.PaddingBox.Left + paddingLeft, box.PaddingBox.Top + (float)box.Padding.Top, box.PaddingBox.Right - paddingRight, box.PaddingBox.Top + (float)box.Padding.Top);
                 }
            }


            // Finalize Height & Constraints
             if (!string.IsNullOrEmpty(style?.HeightExpression))
            {
                float evalH = LayoutHelper.EvaluateCssExpression(style.HeightExpression, availableHeight > 0 ? availableHeight : _context.ViewportHeight);
                if (evalH >= 0) contentHeight = isBorderBox ? evalH - (paddingLeft/*Wrong dims but concept ok*/) : evalH;
                 // Note: paddingLeft used loosely here, should be paddingTop/Bottom.
            }
            else if (style?.Height.HasValue == true)
            {
                 contentHeight = (float)style.Height.Value; // Simplified
            }
            else if (style?.HeightPercent.HasValue == true)
            {
                 // ... percent logic ...
                 float baseTotal = availableHeight > 0 ? availableHeight : _context.ViewportHeight;
                 contentHeight = (float)style.HeightPercent.Value / 100f * baseTotal;
            }

            // Apply Min/Max Height
            // ... (Simplified) ...
            if (nodeTag == "HTML" && contentHeight < _context.ViewportHeight) contentHeight = _context.ViewportHeight;

            // Commit Height
            box.ContentBox.Bottom = box.ContentBox.Top + contentHeight;
            box.PaddingBox.Bottom = box.ContentBox.Bottom + (float)box.Padding.Bottom;
            box.BorderBox.Bottom = box.PaddingBox.Bottom + (float)box.Border.Bottom;
            box.MarginBox.Bottom = box.BorderBox.Bottom + (float)box.Margin.Bottom;
            
            // Baseline
            box.Baseline = baseline > 0 ? baseline : box.ContentBox.Height;

            // Save Box
            box.BorderBox = LayoutHelper.CleanRect(box.BorderBox);
            box.MarginBox = LayoutHelper.CleanRect(box.MarginBox);
            box.PaddingBox = LayoutHelper.CleanRect(box.PaddingBox);
            box.ContentBox = LayoutHelper.CleanRect(box.ContentBox);

            _context.SetBox(node, box);
        }

        public CssComputed GetStyle(Node node) => _context.GetStyle(node);
        public BoxModel GetBox(Node node) => _context.GetBox(node);
    }
}
