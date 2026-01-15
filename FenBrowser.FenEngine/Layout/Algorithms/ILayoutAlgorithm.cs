using SkiaSharp;

namespace FenBrowser.FenEngine.Layout.Algorithms
{
    /// <summary>
    /// Interface for a specific layout mode (Block, Inline, Flex, Grid, etc.)
    /// </summary>
    public interface ILayoutAlgorithm
    {
        /// <summary>
        /// Calculates the desired size of the node given the constraints.
        /// </summary>
        LayoutMetrics Measure(LayoutContext context);

        /// <summary>
        /// Arranges the node's children within the final rectangle.
        /// </summary>
        void Arrange(LayoutContext context, SKRect finalRect);
    }
}
