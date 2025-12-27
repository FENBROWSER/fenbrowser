using FenBrowser.Core.Dom;

namespace FenBrowser.FenEngine.Layout
{
    public struct LayoutMetrics
    {
        public float ContentHeight;
        public float MaxChildWidth;
        public float Baseline;
    }

    /// <summary>
    /// Interface for layout computation.
    /// Allows SkiaDomRenderer to implement layout methods while they are being migrated to LayoutEngine.
    /// </summary>
    public interface ILayoutComputer
    {
        /// <summary>
        /// Compute layout for a node at the given position.
        /// </summary>
        void ComputeLayout(
            Node node, 
            float x, 
            float y, 
            float availableWidth, 
            bool shrinkToContent = false, 
            float availableHeight = 0, 
            bool hasTargetAncestor = false);
            
        /// <summary>
        /// Perform the actual layout logic for a node (without recursion checks).
        /// Called by LayoutEngine after validation.
        /// </summary>
        void RawLayout(
            Node node, 
            float x, 
            float y, 
            float availableWidth, 
            bool shrinkToContent = false, 
            float availableHeight = 0, 
            bool hasTargetAncestor = false);

        
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
        
        // Specialized layout methods (Temporary exposure for migration)
        // Specialized layout methods (Temporary exposure for migration)
        LayoutMetrics ComputeBlockLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, bool shrinkToContent = false, int depth = 0);
        LayoutMetrics ComputeFlexLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0);
        LayoutMetrics ComputeGridLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0);
        LayoutMetrics ComputeTableLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0);
        LayoutMetrics ComputeAbsoluteLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0);
        LayoutMetrics ComputeTextLayout(Node node, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0);
        float ComputeInlineContext(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0); 
        
        void DumpLayoutTree(Node root);
    }


}
