using FenBrowser.Core.Dom.V2;
using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Layout
{
    public struct LayoutMetrics
    {
        public float ContentHeight;
        public float ActualHeight; // Unconstrained content height (for scrolling overflow)
        public float MaxChildWidth;
        public float MinContentWidth; // NEW: Smallest width without overflow
        public float MaxContentWidth; // NEW: Ideal width without wrapping
        public float Baseline;
        public float MarginTop;
        public float MarginBottom;
        
        // Spec-compliant margin collapse tracking
        public float MarginTopPos;
        public float MarginTopNeg;
        public float MarginBottomPos;
        public float MarginBottomNeg;
    }

    /// <summary>
    /// Interface for layout computation used by the render-frame pipeline.
    /// </summary>
    public interface ILayoutComputer
    {
        /// <summary>
        /// Pass 1: Measure - determine desired size.
        /// </summary>
        LayoutMetrics Measure(Node node, SKSize availableSize);

        /// <summary>
        /// Pass 2: Arrange - position node and set final bounds.
        /// </summary>
        void Arrange(Node node, SKRect finalRect);

        /// <summary>
        /// Gets the computed box model for a node.
        /// </summary>
        BoxModel GetBox(Node node);
        
        /// <summary>
        /// Gets the parent of a node in the layout tree.
        /// </summary>
        Node GetParent(Node node);
        
        /// <summary>
        /// Gets all computed boxes.
        /// </summary>
        IEnumerable<KeyValuePair<Node, BoxModel>> GetAllBoxes();
        
        // Measure specialized
        LayoutMetrics MeasureBlock(Element element, SKSize availableSize, int depth);
        LayoutMetrics MeasureFlex(Element element, SKSize availableSize, int depth);
        LayoutMetrics MeasureGrid(Element element, SKSize availableSize, int depth);
        LayoutMetrics MeasureText(Node node, SKSize availableSize);

        // Arrange specialized
        void ArrangeBlock(Element element, SKRect finalRect, int depth);
        void ArrangeFlex(Element element, SKRect finalRect, int depth);
        void ArrangeGrid(Element element, SKRect finalRect, int depth);
        void ArrangeText(Node node, SKRect finalRect);

        void DumpLayoutTree(Node root);
        int GetZeroSizedCount();
    }


}
