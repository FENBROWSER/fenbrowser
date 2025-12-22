using System.Collections.Generic;
using FenBrowser.Core.Dom;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Immutable geometry for a single element.
    /// </summary>
    public readonly struct ElementGeometry
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Height;

        public ElementGeometry(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float Top => Y;
        public float Left => X;
        public float Right => X + Width;
        public float Bottom => Y + Height;

        public SKRect ToSKRect() => new SKRect(X, Y, X + Width, Y + Height);

        public static ElementGeometry FromSKRect(SKRect rect) =>
            new ElementGeometry(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// Immutable snapshot of layout geometry after layout completes.
    /// This is the ONLY source of geometry data for IntersectionObserver and ResizeObserver.
    /// Observers NEVER access renderer internals directly.
    /// </summary>
    public class LayoutResult
    {
        private readonly Dictionary<Element, ElementGeometry> _elementRects;
        
        /// <summary>
        /// Unique identifier for this layout pass. Used for dirty-checking.
        /// </summary>
        public long LayoutId { get; }

        /// <summary>
        /// Viewport width at time of layout.
        /// </summary>
        public float ViewportWidth { get; }

        /// <summary>
        /// Viewport height at time of layout.
        /// </summary>
        public float ViewportHeight { get; }

        /// <summary>
        /// Scroll offset Y at time of layout.
        /// </summary>
        public float ScrollOffsetY { get; }

        /// <summary>
        /// Total content height computed during layout.
        /// </summary>
        public float ContentHeight { get; }

        /// <summary>
        /// Read-only access to element geometry.
        /// </summary>
        public IReadOnlyDictionary<Element, ElementGeometry> ElementRects => _elementRects;

        private static long _nextLayoutId = 0;

        public LayoutResult(
            Dictionary<Element, ElementGeometry> elementRects,
            float viewportWidth,
            float viewportHeight,
            float scrollOffsetY,
            float contentHeight)
        {
            _elementRects = elementRects ?? new Dictionary<Element, ElementGeometry>();
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
            ScrollOffsetY = scrollOffsetY;
            ContentHeight = contentHeight;
            LayoutId = System.Threading.Interlocked.Increment(ref _nextLayoutId);
        }

        /// <summary>
        /// Try to get geometry for an element.
        /// </summary>
        public bool TryGetElementRect(Element element, out ElementGeometry geometry)
        {
            return _elementRects.TryGetValue(element, out geometry);
        }

        /// <summary>
        /// Get the visible viewport rectangle (accounting for scroll).
        /// </summary>
        public ElementGeometry GetVisibleViewport()
        {
            return new ElementGeometry(0, ScrollOffsetY, ViewportWidth, ViewportHeight);
        }
    }
}
