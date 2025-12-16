using SkiaSharp;
using System;
using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Interface for layout computation callback.
    /// Allows extracted layout modules (FlexLayout, GridLayout, etc.) to call back
    /// into the main layout engine for recursive layout computation.
    /// </summary>
    public interface ILayoutEngine
    {
        /// <summary>
        /// Compute layout for a single element and all its children.
        /// </summary>
        /// <param name="node">The element to layout</param>
        /// <param name="x">X position in parent coordinate space</param>
        /// <param name="y">Y position in parent coordinate space</param>
        /// <param name="availableWidth">Available width for this element</param>
        /// <param name="shrinkToContent">If true, element should shrink to fit content</param>
        /// <param name="availableHeight">Available height (optional, for flex-grow)</param>
        void ComputeLayout(LiteElement node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0);
        
        /// <summary>
        /// Shift an element and all its children by the given delta.
        /// Used for alignment adjustments after initial layout.
        /// </summary>
        void ShiftTree(LiteElement node, float deltaX, float deltaY);
        
        /// <summary>
        /// Get the render context containing shared state.
        /// </summary>
        RenderContext Context { get; }
    }
    
    /// <summary>
    /// Result of a layout computation.
    /// </summary>
    public struct LayoutResult
    {
        /// <summary>
        /// Total content height after layout.
        /// </summary>
        public float ContentHeight { get; set; }
        
        /// <summary>
        /// Maximum child width (for shrink-to-content).
        /// </summary>
        public float MaxChildWidth { get; set; }
    }
}
