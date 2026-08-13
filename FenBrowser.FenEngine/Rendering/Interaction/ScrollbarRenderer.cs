using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Interaction
{
    /// <summary>
    /// Renders native scrollbars using Skia for elements with overflow: scroll/auto.
    /// Supports both vertical and horizontal scrollbars with modern styling.
    /// </summary>
    public class ScrollbarRenderer
    {
        public const float ScrollbarWidth = 12f;
        public const float ScrollbarMinThumb = 30f;
        public const float ScrollbarPadding = 2f;
        public const float ScrollbarTrackRadius = 4f;
        public const float ScrollbarThumbRadius = 4f;

        private static readonly SKColor TrackColorLight = new SKColor(240, 240, 240, 200);
        private static readonly SKColor TrackColorDark = new SKColor(60, 60, 60, 200);
        private static readonly SKColor ThumbColorLight = new SKColor(180, 180, 180, 230);
        private static readonly SKColor ThumbColorDark = new SKColor(100, 100, 100, 230);
        private static readonly SKColor ThumbHoverLight = new SKColor(150, 150, 150, 240);
        private static readonly SKColor ThumbHoverDark = new SKColor(130, 130, 130, 240);
        private static readonly SKColor ThumbActiveLight = new SKColor(120, 120, 120, 255);
        private static readonly SKColor ThumbActiveDark = new SKColor(160, 160, 160, 255);

        public enum Theme { Light, Dark }

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
            _scrollManager = scrollManager ?? throw new ArgumentNullException(nameof(scrollManager));
        }

        public Theme CurrentTheme
        {
            get => _theme;
            set => _theme = value;
        }

        public void DrawScrollbars(
            SKCanvas canvas,
            Element element,
            SKRect contentBox,
            CssComputed style,
            ScrollbarState state = null)
        {
            if (canvas == null || element == null || style == null || contentBox.Width <= 0 || contentBox.Height <= 0)
                return;

            var scrollState = _scrollManager.GetScrollState(element);
            ResolveVisibility(style, scrollState, out var showVertical, out var showHorizontal);
            state ??= new ScrollbarState();

            if (showVertical)
                DrawVerticalScrollbar(canvas, contentBox, scrollState, state, showHorizontal);

            if (showHorizontal)
                DrawHorizontalScrollbar(canvas, contentBox, scrollState, state, showVertical);
        }

        private void DrawVerticalScrollbar(
            SKCanvas canvas,
            SKRect contentBox,
            ScrollState scrollState,
            ScrollbarState state,
            bool hasHorizontal)
        {
            float trackHeight = Math.Max(0f, contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0));
            if (trackHeight <= ScrollbarPadding * 2f) return;

            var trackRect = new SKRect(
                contentBox.Right - ScrollbarWidth,
                contentBox.Top,
                contentBox.Right,
                contentBox.Top + trackHeight);

            using (var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Color = _theme == Theme.Light ? TrackColorLight : TrackColorDark,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(trackRect, ScrollbarTrackRadius, ScrollbarTrackRadius, trackPaint);
            }

            var thumbHeight = CalculateThumbLength(trackHeight, scrollState.ViewportHeight, scrollState.ContentHeight);
            var scrollRatio = CalculateScrollRatio(scrollState.ScrollY, scrollState.MaxScrollY);
            var thumbTop = trackRect.Top + scrollRatio * Math.Max(0f, trackHeight - thumbHeight);

            var thumbRect = new SKRect(
                trackRect.Left + ScrollbarPadding,
                thumbTop + ScrollbarPadding,
                trackRect.Right - ScrollbarPadding,
                thumbTop + thumbHeight - ScrollbarPadding);

            var thumbColor = state.IsVerticalActive
                ? (_theme == Theme.Light ? ThumbActiveLight : ThumbActiveDark)
                : state.IsVerticalHovered
                    ? (_theme == Theme.Light ? ThumbHoverLight : ThumbHoverDark)
                    : (_theme == Theme.Light ? ThumbColorLight : ThumbColorDark);

            using var thumbPaint = new SKPaint
            {
                IsAntialias = true,
                Color = thumbColor,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRoundRect(thumbRect, ScrollbarThumbRadius, ScrollbarThumbRadius, thumbPaint);
        }

        private void DrawHorizontalScrollbar(
            SKCanvas canvas,
            SKRect contentBox,
            ScrollState scrollState,
            ScrollbarState state,
            bool hasVertical)
        {
            float trackWidth = Math.Max(0f, contentBox.Width - (hasVertical ? ScrollbarWidth : 0));
            if (trackWidth <= ScrollbarPadding * 2f) return;

            var trackRect = new SKRect(
                contentBox.Left,
                contentBox.Bottom - ScrollbarWidth,
                contentBox.Left + trackWidth,
                contentBox.Bottom);

            using (var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Color = _theme == Theme.Light ? TrackColorLight : TrackColorDark,
                Style = SKPaintStyle.Fill
            })
            {
                canvas.DrawRoundRect(trackRect, ScrollbarTrackRadius, ScrollbarTrackRadius, trackPaint);
            }

            var thumbWidth = CalculateThumbLength(trackWidth, scrollState.ViewportWidth, scrollState.ContentWidth);
            var scrollRatio = CalculateScrollRatio(scrollState.ScrollX, scrollState.MaxScrollX);
            var thumbLeft = trackRect.Left + scrollRatio * Math.Max(0f, trackWidth - thumbWidth);

            var thumbRect = new SKRect(
                thumbLeft + ScrollbarPadding,
                trackRect.Top + ScrollbarPadding,
                thumbLeft + thumbWidth - ScrollbarPadding,
                trackRect.Bottom - ScrollbarPadding);

            var thumbColor = state.IsHorizontalActive
                ? (_theme == Theme.Light ? ThumbActiveLight : ThumbActiveDark)
                : state.IsHorizontalHovered
                    ? (_theme == Theme.Light ? ThumbHoverLight : ThumbHoverDark)
                    : (_theme == Theme.Light ? ThumbColorLight : ThumbColorDark);

            using var thumbPaint = new SKPaint
            {
                IsAntialias = true,
                Color = thumbColor,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRoundRect(thumbRect, ScrollbarThumbRadius, ScrollbarThumbRadius, thumbPaint);
        }

        /// <summary>
        /// Hit tests the visible scrollbar tracks.
        /// Returns: 0 = not on scrollbar, 1 = vertical, 2 = horizontal.
        /// </summary>
        public int HitTestScrollbar(
            Element element,
            SKRect contentBox,
            CssComputed style,
            float x,
            float y)
        {
            if (element == null || style == null || contentBox.Width <= 0 || contentBox.Height <= 0) return 0;

            var scrollState = _scrollManager.GetScrollState(element);
            ResolveVisibility(style, scrollState, out var hasVertical, out var hasHorizontal);

            if (hasVertical)
            {
                float trackHeight = Math.Max(0f, contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0));
                var trackRect = new SKRect(
                    contentBox.Right - ScrollbarWidth,
                    contentBox.Top,
                    contentBox.Right,
                    contentBox.Top + trackHeight);

                if (trackRect.Contains(x, y)) return 1;
            }

            if (hasHorizontal)
            {
                float trackWidth = Math.Max(0f, contentBox.Width - (hasVertical ? ScrollbarWidth : 0));
                var trackRect = new SKRect(
                    contentBox.Left,
                    contentBox.Bottom - ScrollbarWidth,
                    contentBox.Left + trackWidth,
                    contentBox.Bottom);

                if (trackRect.Contains(x, y)) return 2;
            }

            return 0;
        }

        public void HandleVerticalDrag(Element element, SKRect contentBox, float y, bool hasHorizontal)
        {
            if (element == null) return;
            var scrollState = _scrollManager.GetScrollState(element);
            float trackHeight = Math.Max(0f, contentBox.Height - (hasHorizontal ? ScrollbarWidth : 0));
            float thumbHeight = CalculateThumbLength(trackHeight, scrollState.ViewportHeight, scrollState.ContentHeight);
            float draggableRange = Math.Max(0f, trackHeight - thumbHeight);
            if (draggableRange <= 0f) return;

            float relativeY = y - contentBox.Top - thumbHeight / 2f;
            float ratio = Math.Clamp(relativeY / draggableRange, 0f, 1f);
            float newScrollY = ratio * Math.Max(0f, scrollState.MaxScrollY);
            _scrollManager.SetScrollPosition(element, scrollState.ScrollX, newScrollY, fromUserInput: true);
        }

        public void HandleHorizontalDrag(Element element, SKRect contentBox, float x, bool hasVertical)
        {
            if (element == null) return;
            var scrollState = _scrollManager.GetScrollState(element);
            float trackWidth = Math.Max(0f, contentBox.Width - (hasVertical ? ScrollbarWidth : 0));
            float thumbWidth = CalculateThumbLength(trackWidth, scrollState.ViewportWidth, scrollState.ContentWidth);
            float draggableRange = Math.Max(0f, trackWidth - thumbWidth);
            if (draggableRange <= 0f) return;

            float relativeX = x - contentBox.Left - thumbWidth / 2f;
            float ratio = Math.Clamp(relativeX / draggableRange, 0f, 1f);
            float newScrollX = ratio * Math.Max(0f, scrollState.MaxScrollX);
            _scrollManager.SetScrollPosition(element, newScrollX, scrollState.ScrollY, fromUserInput: true);
        }

        private static void ResolveVisibility(CssComputed style, ScrollState state, out bool vertical, out bool horizontal)
        {
            var overflowX = style.OverflowX?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            var overflowY = style.OverflowY?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";

            vertical = overflowY == "scroll" ||
                       (overflowY == "auto" && state.ContentHeight > state.ViewportHeight);
            horizontal = overflowX == "scroll" ||
                         (overflowX == "auto" && state.ContentWidth > state.ViewportWidth);
        }

        private static float CalculateThumbLength(float trackLength, float viewportLength, float contentLength)
        {
            if (!float.IsFinite(trackLength) || trackLength <= 0f) return 0f;

            float ratio;
            if (!float.IsFinite(viewportLength) || !float.IsFinite(contentLength) ||
                viewportLength <= 0f || contentLength <= 0f)
            {
                ratio = 1f;
            }
            else
            {
                ratio = Math.Clamp(viewportLength / contentLength, 0f, 1f);
            }

            var minimum = Math.Min(ScrollbarMinThumb, trackLength);
            return Math.Clamp(trackLength * ratio, minimum, trackLength);
        }

        private static float CalculateScrollRatio(float scrollOffset, float maxScroll)
        {
            if (!float.IsFinite(scrollOffset) || !float.IsFinite(maxScroll) || maxScroll <= 0f)
                return 0f;

            return Math.Clamp(scrollOffset / maxScroll, 0f, 1f);
        }
    }
}
