using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Owns all scrolling state. Renderer only reads from this.
    /// Provides clean scroll state management separate from rendering.
    /// </summary>
    public sealed class ScrollModel
    {
        /// <summary>
        /// Current scroll offset.
        /// </summary>
        public SKPoint Offset { get; private set; }
        
        /// <summary>
        /// Total content extent (full scrollable area).
        /// </summary>
        public SKSize ContentExtent { get; private set; }
        
        /// <summary>
        /// Current viewport size.
        /// </summary>
        public SKSize ViewportSize { get; private set; }

        /// <summary>
        /// Whether vertical scrollbar is needed.
        /// </summary>
        public bool NeedsVerticalScrollbar => ContentExtent.Height > ViewportSize.Height;
        
        /// <summary>
        /// Whether horizontal scrollbar is needed.
        /// </summary>
        public bool NeedsHorizontalScrollbar => ContentExtent.Width > ViewportSize.Width;

        /// <summary>
        /// Maximum Y scroll position.
        /// </summary>
        public float MaxScrollY => Math.Max(0, ContentExtent.Height - ViewportSize.Height);
        
        /// <summary>
        /// Maximum X scroll position.
        /// </summary>
        public float MaxScrollX => Math.Max(0, ContentExtent.Width - ViewportSize.Width);
        
        /// <summary>
        /// Current X scroll position.
        /// </summary>
        public float ScrollX => Offset.X;
        
        /// <summary>
        /// Current Y scroll position.
        /// </summary>
        public float ScrollY => Offset.Y;
        
        /// <summary>
        /// Scrollbar thumb height (proportional to viewport/content ratio).
        /// </summary>
        public float VerticalScrollbarThumbHeight
        {
            get
            {
                if (ContentExtent.Height <= 0) return ViewportSize.Height;
                float ratio = ViewportSize.Height / ContentExtent.Height;
                return Math.Max(20, ViewportSize.Height * ratio); // Min thumb size 20px
            }
        }
        
        /// <summary>
        /// Scrollbar thumb width (proportional to viewport/content ratio).
        /// </summary>
        public float HorizontalScrollbarThumbWidth
        {
            get
            {
                if (ContentExtent.Width <= 0) return ViewportSize.Width;
                float ratio = ViewportSize.Width / ContentExtent.Width;
                return Math.Max(20, ViewportSize.Width * ratio);
            }
        }

        /// <summary>
        /// Creates a new scroll model with default values.
        /// </summary>
        public ScrollModel()
        {
            Offset = SKPoint.Empty;
            ContentExtent = SKSize.Empty;
            ViewportSize = SKSize.Empty;
        }

        /// <summary>
        /// Sets the viewport size.
        /// </summary>
        public void SetViewport(SKSize size)
        {
            ViewportSize = size;
            // Re-clamp scroll position
            ClampOffset();
        }
        
        /// <summary>
        /// Sets the content extent (total scrollable area).
        /// </summary>
        public void SetContentExtent(SKSize size)
        {
            ContentExtent = size;
            // Re-clamp scroll position
            ClampOffset();
        }
        
        /// <summary>
        /// Sets the content extent from dimensions.
        /// </summary>
        public void SetContentExtent(float width, float height)
        {
            SetContentExtent(new SKSize(width, height));
        }

        /// <summary>
        /// Scrolls to an absolute position.
        /// </summary>
        public void ScrollTo(float x, float y)
        {
            Offset = new SKPoint(
                Math.Clamp(x, 0, MaxScrollX),
                Math.Clamp(y, 0, MaxScrollY));
        }

        /// <summary>
        /// Scrolls by a delta amount.
        /// </summary>
        public void ScrollBy(float dx, float dy)
        {
            ScrollTo(Offset.X + dx, Offset.Y + dy);
        }
        
        /// <summary>
        /// Scrolls to the top of the document.
        /// </summary>
        public void ScrollToTop()
        {
            ScrollTo(Offset.X, 0);
        }
        
        /// <summary>
        /// Scrolls to the bottom of the document.
        /// </summary>
        public void ScrollToBottom()
        {
            ScrollTo(Offset.X, MaxScrollY);
        }
        
        /// <summary>
        /// Scrolls to make an element visible.
        /// </summary>
        public void ScrollIntoView(SKRect elementRect)
        {
            float newY = Offset.Y;
            float newX = Offset.X;
            
            // Scroll vertically if needed
            if (elementRect.Top < Offset.Y)
            {
                newY = elementRect.Top;
            }
            else if (elementRect.Bottom > Offset.Y + ViewportSize.Height)
            {
                newY = elementRect.Bottom - ViewportSize.Height;
            }
            
            // Scroll horizontally if needed
            if (elementRect.Left < Offset.X)
            {
                newX = elementRect.Left;
            }
            else if (elementRect.Right > Offset.X + ViewportSize.Width)
            {
                newX = elementRect.Right - ViewportSize.Width;
            }
            
            ScrollTo(newX, newY);
        }
        
        /// <summary>
        /// Checks if a rect is visible in the current viewport.
        /// </summary>
        public bool IsVisible(SKRect rect)
        {
            var viewportRect = new SKRect(
                Offset.X, 
                Offset.Y, 
                Offset.X + ViewportSize.Width, 
                Offset.Y + ViewportSize.Height);
            return viewportRect.IntersectsWith(rect);
        }

        /// <summary>
        /// Clamps the offset to valid range.
        /// </summary>
        private void ClampOffset()
        {
            Offset = new SKPoint(
                Math.Clamp(Offset.X, 0, MaxScrollX),
                Math.Clamp(Offset.Y, 0, MaxScrollY));
        }
    }
}
