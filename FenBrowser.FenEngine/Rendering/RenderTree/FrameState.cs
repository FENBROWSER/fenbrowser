using SkiaSharp;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Immutable snapshot of all state needed for one frame.
    /// Passed between phases to prevent half-updated state bugs.
    /// </summary>
    public sealed class FrameState
    {
        /// <summary>
        /// Unique identifier for this frame.
        /// </summary>
        public int FrameId { get; }
        
        /// <summary>
        /// Root element of the DOM tree.
        /// </summary>
        public Element RootElement { get; }
        
        /// <summary>
        /// Computed styles for all nodes.
        /// </summary>
        public IReadOnlyDictionary<Node, CssComputed> Styles { get; }
        
        /// <summary>
        /// Layout result containing all box models.
        /// </summary>
        public Layout.LayoutResult Layout { get; }
        
        /// <summary>
        /// Paint tree for rendering order.
        /// </summary>
        public PaintTree PaintTree { get; }
        
        /// <summary>
        /// Viewport dimensions.
        /// </summary>
        public SKSize Viewport { get; }
        
        /// <summary>
        /// Current scroll offset.
        /// </summary>
        public SKPoint ScrollOffset { get; }
        
        /// <summary>
        /// Timestamp when this frame was created.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Creates a new immutable frame state.
        /// </summary>
        public FrameState(
            int frameId,
            Element root,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Layout.LayoutResult layout,
            PaintTree paintTree,
            SKSize viewport,
            SKPoint scrollOffset)
        {
            FrameId = frameId;
            RootElement = root ?? throw new ArgumentNullException(nameof(root));
            Styles = styles ?? throw new ArgumentNullException(nameof(styles));
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            PaintTree = paintTree ?? throw new ArgumentNullException(nameof(paintTree));
            Viewport = viewport;
            ScrollOffset = scrollOffset;
            Timestamp = DateTimeOffset.UtcNow;
        }
    }
}

