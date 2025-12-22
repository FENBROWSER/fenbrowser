using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Interaction
{
    /// <summary>
    /// Renders native scrollbars using Skia for elements with overflow: scroll/auto.
    /// Supports both vertical and horizontal scrollbars with modern styling.
    /// </summary>
    public class ScrollbarRenderer
    {
        // Scrollbar dimensions
        public const float ScrollbarWidth = 12f;
        public const float ScrollbarMinThumb = 30f;
        public const float ScrollbarPadding = 2f;
        public const float ScrollbarTrackRadius = 4f;
        public const float ScrollbarThumbRadius = 4f;
        
        // Colors (modern dark/light theme)
        private static readonly SKColor TrackColorLight = new SKColor(240, 240, 240, 200);
        private static readonly SKColor TrackColorDark = new SKColor(60, 60, 60, 200);
        private static readonly SKColor ThumbColorLight = new SKColor(180, 180, 180, 230);
        private static readonly SKColor ThumbColorDark = new SKColor(100, 100, 100, 230);
        private static readonly SKColor ThumbHoverLight = new SKColor(150, 150, 150, 240);
        private static readonly SKColor ThumbHoverDark = new SKColor(130, 130, 130, 240);
        private static readonly SKColor ThumbActiveLight = new SKColor(120, 120, 120, 255);
        private static readonly SKColor ThumbActiveDark = new SKColor(160, 160, 160, 255);
        
        /// <summary>
        /// Scrollbar theme
        /// </summary>
        public enum Theme { Light, Dark }
        
        /// <summary>
        /// Scrollbar state for hover/active effects
        /// </summary>
        public class ScrollbarState
        {
            public bool IsHovered { get; set; }
            public bool IsActive { get; set; }
            public bool IsVerticalHovered { get; set; }
            public bool IsHorizontalHovered { get; set; }
            public bool IsVerticalActive { get; set; }
            public bool IsHorizontalActive { get; set; }
        }
        
        private readonly ScrollManager _scrollManager;
        private Theme _theme = Theme.Light;
        
        public ScrollbarRenderer(ScrollManager scrollManager)
        {
            _scrollManager = scrollManager;
        }
        
        public Theme CurrentTheme
        {
            get => _theme;
            set => _theme = value;
        }
        
        /// <summary>
        /// Draw scrollbars for a scrollable element.
        /// </summary>
        public void DrawScrollbars(
            SKCanvas canvas, 
            Element element, 
            SKRect contentBox, 
            CssComputed style,
            ScrollbarState state = null)
        {
            if (element == null || style == null) return;
            
            var overflowX = style.OverflowX?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            var overflowY = style.OverflowY?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            
            var scrollState = _scrollManager.GetScrollState(element);
            
            bool showVertical = (overflowY == "scroll" || overflowY == "auto") && 
                                scrollState.ContentHeight > scrollState.ViewportHeight;
            bool showHorizontal = (overflowX == "scroll" || overflowX == "auto") && 
                                  scrollState.ContentWidth > scrollState.ViewportWidth;
            
            // Force show if overflow: scroll
            if (overflowY == "scroll") showVertical = true;
            if (overflowX == "scroll") showHorizontal = true;
            
            state ??= new ScrollbarState();
            
            if (showVertical)
            {
                DrawVerticalScrollbar(canvas, contentBox, scrollState, state, showHorizontal);
            }
            
            if (showHorizontal)
            {
                DrawHorizontalScrollbar(canvas, contentBox, scrollState, state, showVertical);
            }
        }
        
        /// <summary>
        /// Draw vertical scrollbar track and thumb.
        /// </summary>
        private void DrawVerticalScrollbar(
            SKCanvas canvas, 
            SKRect contentBox, 
            ScrollState scrollState,
            ScrollbarState state,
            bool hasHorizontal)
        {
            // Calculate track rect (on right side of content box)
            float trackHeight = contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0);
            var trackRect = new SKRect(
                contentBox.Right - ScrollbarWidth,
                contentBox.Top,
                contentBox.Right,
                contentBox.Top + trackHeight
            );
            
            // Draw track
            using (var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Color = _theme == Theme.Light ? TrackColorLight : TrackColorDark,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(trackRect, ScrollbarTrackRadius, ScrollbarTrackRadius, trackPaint);
            }
            
            // Calculate thumb
            float contentRatio = scrollState.ViewportHeight / scrollState.ContentHeight;
            float thumbHeight = Math.Max(ScrollbarMinThumb, trackHeight * contentRatio);
            float scrollRatio = scrollState.MaxScrollY > 0 
                ? scrollState.ScrollY / scrollState.MaxScrollY 
                : 0;
            float thumbTop = trackRect.Top + scrollRatio * (trackHeight - thumbHeight);
            
            var thumbRect = new SKRect(
                trackRect.Left + ScrollbarPadding,
                thumbTop + ScrollbarPadding,
                trackRect.Right - ScrollbarPadding,
                thumbTop + thumbHeight - ScrollbarPadding
            );
            
            // Draw thumb with hover/active states
            SKColor thumbColor;
            if (state.IsVerticalActive)
                thumbColor = _theme == Theme.Light ? ThumbActiveLight : ThumbActiveDark;
            else if (state.IsVerticalHovered)
                thumbColor = _theme == Theme.Light ? ThumbHoverLight : ThumbHoverDark;
            else
                thumbColor = _theme == Theme.Light ? ThumbColorLight : ThumbColorDark;
            
            using (var thumbPaint = new SKPaint
            {
                IsAntialias = true,
                Color = thumbColor,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(thumbRect, ScrollbarThumbRadius, ScrollbarThumbRadius, thumbPaint);
            }
        }
        
        /// <summary>
        /// Draw horizontal scrollbar track and thumb.
        /// </summary>
        private void DrawHorizontalScrollbar(
            SKCanvas canvas, 
            SKRect contentBox, 
            ScrollState scrollState,
            ScrollbarState state,
            bool hasVertical)
        {
            // Calculate track rect (on bottom of content box)
            float trackWidth = contentBox.Width - (hasVertical ? ScrollbarWidth : 0);
            var trackRect = new SKRect(
                contentBox.Left,
                contentBox.Bottom - ScrollbarWidth,
                contentBox.Left + trackWidth,
                contentBox.Bottom
            );
            
            // Draw track
            using (var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Color = _theme == Theme.Light ? TrackColorLight : TrackColorDark,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(trackRect, ScrollbarTrackRadius, ScrollbarTrackRadius, trackPaint);
            }
            
            // Calculate thumb
            float contentRatio = scrollState.ViewportWidth / scrollState.ContentWidth;
            float thumbWidth = Math.Max(ScrollbarMinThumb, trackWidth * contentRatio);
            float scrollRatio = scrollState.MaxScrollX > 0 
                ? scrollState.ScrollX / scrollState.MaxScrollX 
                : 0;
            float thumbLeft = trackRect.Left + scrollRatio * (trackWidth - thumbWidth);
            
            var thumbRect = new SKRect(
                thumbLeft + ScrollbarPadding,
                trackRect.Top + ScrollbarPadding,
                thumbLeft + thumbWidth - ScrollbarPadding,
                trackRect.Bottom - ScrollbarPadding
            );
            
            // Draw thumb with hover/active states
            SKColor thumbColor;
            if (state.IsHorizontalActive)
                thumbColor = _theme == Theme.Light ? ThumbActiveLight : ThumbActiveDark;
            else if (state.IsHorizontalHovered)
                thumbColor = _theme == Theme.Light ? ThumbHoverLight : ThumbHoverDark;
            else
                thumbColor = _theme == Theme.Light ? ThumbColorLight : ThumbColorDark;
            
            using (var thumbPaint = new SKPaint
            {
                IsAntialias = true,
                Color = thumbColor,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(thumbRect, ScrollbarThumbRadius, ScrollbarThumbRadius, thumbPaint);
            }
        }
        
        /// <summary>
        /// Hit test to determine if a point is over a scrollbar thumb.
        /// Returns: 0 = not on scrollbar, 1 = vertical thumb, 2 = horizontal thumb
        /// </summary>
        public int HitTestScrollbar(
            Element element, 
            SKRect contentBox, 
            CssComputed style, 
            float x, float y)
        {
            if (element == null || style == null) return 0;
            
            var scrollState = _scrollManager.GetScrollState(element);
            
            var overflowY = style.OverflowY?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            var overflowX = style.OverflowX?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            
            bool hasVertical = (overflowY == "scroll" || overflowY == "auto") && 
                               scrollState.ContentHeight > scrollState.ViewportHeight;
            bool hasHorizontal = (overflowX == "scroll" || overflowX == "auto") && 
                                 scrollState.ContentWidth > scrollState.ViewportWidth;
            
            // Check vertical scrollbar area
            if (hasVertical)
            {
                float trackHeight = contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0);
                var trackRect = new SKRect(
                    contentBox.Right - ScrollbarWidth,
                    contentBox.Top,
                    contentBox.Right,
                    contentBox.Top + trackHeight
                );
                
                if (trackRect.Contains(x, y))
                    return 1;
            }
            
            // Check horizontal scrollbar area
            if (hasHorizontal)
            {
                float trackWidth = contentBox.Width - (hasVertical ? ScrollbarWidth : 0);
                var trackRect = new SKRect(
                    contentBox.Left,
                    contentBox.Bottom - ScrollbarWidth,
                    contentBox.Left + trackWidth,
                    contentBox.Bottom
                );
                
                if (trackRect.Contains(x, y))
                    return 2;
            }
            
            return 0;
        }
        
        /// <summary>
        /// Handle scrollbar drag - converts a Y position to scroll position.
        /// </summary>
        public void HandleVerticalDrag(Element element, SKRect contentBox, float y, bool hasHorizontal)
        {
            var scrollState = _scrollManager.GetScrollState(element);
            
            float trackHeight = contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0);
            float contentRatio = scrollState.ViewportHeight / scrollState.ContentHeight;
            float thumbHeight = Math.Max(ScrollbarMinThumb, trackHeight * contentRatio);
            float draggableRange = trackHeight - thumbHeight;
            
            if (draggableRange <= 0) return;
            
            float relativeY = y - contentBox.Top - thumbHeight / 2;
            float ratio = Math.Max(0, Math.Min(1, relativeY / draggableRange));
            float newScrollY = ratio * scrollState.MaxScrollY;
            
            _scrollManager.SetScrollPosition(element, scrollState.ScrollX, newScrollY);
        }
        
        /// <summary>
        /// Handle horizontal scrollbar drag.
        /// </summary>
        public void HandleHorizontalDrag(Element element, SKRect contentBox, float x, bool hasVertical)
        {
            var scrollState = _scrollManager.GetScrollState(element);
            
            float trackWidth = contentBox.Width - (hasVertical ? ScrollbarWidth : 0);
            float contentRatio = scrollState.ViewportWidth / scrollState.ContentWidth;
            float thumbWidth = Math.Max(ScrollbarMinThumb, trackWidth * contentRatio);
            float draggableRange = trackWidth - thumbWidth;
            
            if (draggableRange <= 0) return;
            
            float relativeX = x - contentBox.Left - thumbWidth / 2;
            float ratio = Math.Max(0, Math.Min(1, relativeX / draggableRange));
            float newScrollX = ratio * scrollState.MaxScrollX;
            
            _scrollManager.SetScrollPosition(element, newScrollX, scrollState.ScrollY);
        }
    }
}


