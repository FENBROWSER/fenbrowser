using SkiaSharp;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css; // Added
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Layout; // Added

namespace FenBrowser.FenEngine.Layout.Algorithms
{
    /// <summary>
    /// Context passed to layout algorithms containing all necessary state for measurement/arrangement.
    /// </summary>
    public class LayoutContext
    {
        public Element Node { get; init; }
        public CssComputed Style { get; init; }
        public SKSize AvailableSize { get; init; }
        public int Depth { get; init; }
        public MinimalLayoutComputer Computer { get; init; }
        public Node FallbackNode { get; init; } // For pseudo-elements or specific cases where Node is not the only source
        
        // Factory for common use
        public LayoutContext WithSize(SKSize newSize)
        {
             return new LayoutContext {
                 Node = this.Node,
                 Style = this.Style,
                 AvailableSize = newSize,
                 Depth = this.Depth,
                 Computer = this.Computer,
                 FallbackNode = this.FallbackNode
             };
        }
    }
}
