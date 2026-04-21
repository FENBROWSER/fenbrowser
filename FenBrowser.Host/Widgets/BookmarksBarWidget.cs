using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Host.Theme;
using FenBrowser.Core;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Horizontal bar displaying bookmarks (Favorites).
/// Mirroring Edge behavior.
/// </summary>
public class BookmarksBarWidget : Widget
{
    private const float BAR_HEIGHT = 28;
    private const float BUTTON_PADDING = 12;
    private const float BUTTON_SPACING = 4;
    
    public event System.Action<string> BookmarkClicked;
    
    private List<BookmarkButton> _buttons = new();

    public BookmarksBarWidget()
    {
        Name = "Favorites Bar";
        RefreshBookmarks();
    }

    public void RefreshBookmarks()
    {
        // Explicitly set visibility based on setting as requested
        IsVisible = BrowserSettings.Instance.ShowFavoritesBar;

        // Clear existing
        foreach (var btn in _buttons) RemoveChild(btn);
        _buttons.Clear();

        var bookmarks = BrowserSettings.Instance.Bookmarks;
        EngineLogBridge.Info($"[BookmarksBar] RefreshBookmarks - Count: {bookmarks.Count}, ShowFavoritesBar: {BrowserSettings.Instance.ShowFavoritesBar}", FenBrowser.Core.Logging.LogCategory.General);
        foreach (var bm in bookmarks)
        {
            var btn = new BookmarkButton(bm);
            btn.Clicked += () => BookmarkClicked?.Invoke(bm.Url);
            _buttons.Add(btn);
            AddChild(btn);
        }
        
        // Notify parent that layout might have changed (e.g. from 0 to BAR_HEIGHT)
        InvalidateLayout();
        Invalidate(); // Also trigger repaint
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Check setting FIRST. If disabled, always collapse.
        bool show = BrowserSettings.Instance.ShowFavoritesBar;
        try {
            System.IO.File.AppendAllText(
                FenBrowser.Core.Logging.DiagnosticPaths.GetLogArtifactPath("click_debug.log"),
                $"[BookmarksBar] OnMeasure - ShowFavoritesBar={show}, Count={_buttons.Count}\n");
        } catch {}
        
        if (!show)
        {
            return new SKSize(availableSpace.Width, 0);
        }

        // Collapse if no bookmarks
        if (_buttons.Count == 0)
        {
            return new SKSize(availableSpace.Width, 0);
        }

        float totalWidth = BUTTON_SPACING;
        foreach (var btn in _buttons)
        {
            btn.Measure(availableSpace);
            totalWidth += btn.DesiredSize.Width + BUTTON_SPACING;
        }
        return new SKSize(availableSpace.Width, BAR_HEIGHT);
    }

    protected override void OnArrange(SKRect finalRect)
    {
        float x = finalRect.Left + BUTTON_SPACING;
        float y = finalRect.Top + 2;
        float h = BAR_HEIGHT - 4;

        foreach (var btn in _buttons)
        {
            float w = btn.DesiredSize.Width;
            btn.Arrange(new SKRect(x, y, x + w, y + h));
            x += w + BUTTON_SPACING;
        }
    }

    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Background
        using var bgPaint = new SKPaint { Color = theme.Background, Style = SKPaintStyle.Fill };
        canvas.DrawRect(Bounds, bgPaint);
    }

    /// <summary>
    /// Mini button for each bookmark.
    /// </summary>
    private class BookmarkButton : Widget
    {
        private readonly Bookmark _bookmark;
        private bool _isHovered;
        private static SKTypeface _typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);

        public event System.Action Clicked;

        public BookmarkButton(Bookmark bookmark)
        {
            _bookmark = bookmark;
            Name = bookmark.Title;
        }

        protected override SKSize OnMeasure(SKSize availableSpace)
        {
            using var paint = new SKPaint { TextSize = 12, Typeface = _typeface };
            float textWidth = paint.MeasureText(_bookmark.Title);
            return new SKSize(textWidth + BUTTON_PADDING * 2, BAR_HEIGHT - 4);
        }

        protected override void OnArrange(SKRect finalRect) { }

        public override void Paint(SKCanvas canvas)
        {
            var theme = ThemeManager.Current;
            
            if (_isHovered)
            {
                using var hoverPaint = new SKPaint { Color = theme.SurfaceHover, IsAntialias = true };
                canvas.DrawRoundRect(Bounds, 4, 4, hoverPaint);
            }

            using var textPaint = new SKPaint
            {
                Color = theme.Text,
                IsAntialias = true,
                TextSize = 12,
                Typeface = _typeface,
                TextAlign = SKTextAlign.Center
            };
            
            canvas.DrawText(_bookmark.Title, Bounds.MidX, Bounds.MidY + 4, textPaint);
        }

        public override void OnMouseMove(float x, float y)
        {
            bool wasHovered = _isHovered;
            _isHovered = Bounds.Contains(x, y);
            if (wasHovered != _isHovered) Invalidate();
        }

        public override void OnMouseDown(float x, float y, Silk.NET.Input.MouseButton button)
        {
            if (Bounds.Contains(x, y)) Clicked?.Invoke();
        }
    }
}


