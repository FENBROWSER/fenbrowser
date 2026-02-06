using SkiaSharp;
using FenBrowser.Core.Dom.V2;
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
        public List<FloatExclusion> Exclusions { get; init; } // Active floats to clear against
        
        public bool ShrinkToFit { get; init; } // Added for Flex/Grid items needing intrinsic sizing logic
        
        public FenBrowser.Core.Deadlines.FrameDeadline Deadline { get; init; } // Added for Preemption

        // Factory for common use
        public LayoutContext WithSize(SKSize newSize)
        {
             return new LayoutContext {
                 Node = this.Node,
                 Style = this.Style,
                 AvailableSize = newSize,
                 Depth = this.Depth,
                 Computer = this.Computer,
                 FallbackNode = this.FallbackNode,
                 Exclusions = this.Exclusions,
                 Deadline = this.Deadline
             };
        }
    }
}

