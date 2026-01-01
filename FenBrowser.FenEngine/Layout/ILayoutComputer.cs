using FenBrowser.Core.Dom;
using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Layout
{
    public struct LayoutMetrics
    {
        public float ContentHeight;
        public float ActualHeight; // Unconstrained content height (for scrolling overflow)
        public float MaxChildWidth;
        public float Baseline;
        public float MarginTop;
        public float MarginBottom;
    }

    /// <summary>
    /// Interface for layout computation.
    /// Allows SkiaDomRenderer to implement layout methods while they are being migrated to LayoutEngine.
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
        LayoutMetrics MeasureBlock(Element element, SKSize availableSize);
        LayoutMetrics MeasureFlex(Element element, SKSize availableSize);
        LayoutMetrics MeasureGrid(Element element, SKSize availableSize);
        LayoutMetrics MeasureText(Node node, SKSize availableSize);

        // Arrange specialized
        void ArrangeBlock(Element element, SKRect finalRect);
        void ArrangeFlex(Element element, SKRect finalRect);
        void ArrangeGrid(Element element, SKRect finalRect);
        void ArrangeText(Node node, SKRect finalRect);

        void DumpLayoutTree(Node root);
    }


}
