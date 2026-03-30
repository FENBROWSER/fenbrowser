using SkiaSharp;
using Silk.NET.Input;
using FenBrowser.Host.Widgets;

namespace FenBrowser.Host.Context;

/// <summary>
/// Context menu popup widget.
/// Handles rendering, keyboard navigation, Z-ordered display.
/// </summary>
public class ContextMenuWidget : Widget
{
    private readonly List<ContextMenuItem> _items;
    private int _hoveredIndex = -1;
    private const float ITEM_HEIGHT = 28;
    private const float PADDING = 6;
    private const float MIN_WIDTH = 200;
    private const float SHORTCUT_MARGIN = 40;
    
    /// <summary>
    /// Event when menu should close.
    /// </summary>
    public event Action CloseRequested;
    
    /// <summary>
    /// Whether this menu is currently visible.
    /// </summary>
    public bool IsOpen { get; private set; }
    
    public ContextMenuWidget(List<ContextMenuItem> items)
    {
        _items = items ?? new List<ContextMenuItem>();
        CalculateBounds();
    }
    
    private void CalculateBounds()
    {
        if (_items.Count == 0)
        {
            Bounds = SKRect.Empty;
            return;
        }
        
        float maxWidth = MIN_WIDTH;
        
        using var textPaint = new SKPaint { TextSize = 13 };
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            float width = textPaint.MeasureText(item.Label ?? "") + SHORTCUT_MARGIN;
            if (!string.IsNullOrEmpty(item.Shortcut))
            {
                width += textPaint.MeasureText(item.Shortcut) + 20;
            }
            maxWidth = Math.Max(maxWidth, width);
        }
        
        float height = 0;
        foreach (var item in _items)
        {
            height += item.IsSeparator ? 8 : ITEM_HEIGHT;
        }
        
        Bounds = new SKRect(0, 0, maxWidth + PADDING * 2, height + PADDING * 2);
    }
    
    /// <summary>
    /// Show the menu at the given position.
    /// </summary>
    public void Show(float x, float y, float maxX = float.MaxValue, float maxY = float.MaxValue)
    {
        // Ensure menu fits within screen bounds
        float finalX = x;
        float finalY = y;
        
        if (x + Bounds.Width > maxX)
        {
            finalX = x - Bounds.Width;
        }
        if (y + Bounds.Height > maxY)
        {
            finalY = y - Bounds.Height;
        }
        
        Bounds = new SKRect(finalX, finalY, finalX + Bounds.Width, finalY + Bounds.Height);
        IsOpen = true;
        _hoveredIndex = -1;
        Invalidate();
    }
    
    /// <summary>
    /// Hide the menu.
    /// </summary>
    public void Hide()
    {
        IsOpen = false;
        _hoveredIndex = -1;
        CloseRequested?.Invoke();
    }
    
    public override void Paint(SKCanvas canvas)
    {
        if (!IsOpen || _items.Count == 0) return;
        
        // Shadow
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 40),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
        };
        canvas.DrawRoundRect(new SKRect(Bounds.Left + 2, Bounds.Top + 2, Bounds.Right + 2, Bounds.Bottom + 2), 4, 4, shadowPaint);
        
        // Background
        using var bgPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        canvas.DrawRoundRect(Bounds, 4, 4, bgPaint);
        
        // Border
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(200, 200, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRoundRect(Bounds, 4, 4, borderPaint);
        
        // Items
        float y = Bounds.Top + PADDING;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            
            if (item.IsSeparator)
            {
                // Draw separator line
                using var sepPaint = new SKPaint { Color = new SKColor(230, 230, 230), StrokeWidth = 1 };
                canvas.DrawLine(Bounds.Left + PADDING, y + 4, Bounds.Right - PADDING, y + 4, sepPaint);
                y += 8;
                continue;
            }
            
            float itemHeight = ITEM_HEIGHT;
            var itemRect = new SKRect(Bounds.Left + 4, y, Bounds.Right - 4, y + itemHeight);
            
            // Hover highlight
            if (i == _hoveredIndex && item.IsEnabled)
            {
                using var hoverPaint = new SKPaint { Color = new SKColor(230, 240, 255), IsAntialias = true };
                canvas.DrawRoundRect(itemRect, 3, 3, hoverPaint);
            }
            
            // Label
            using var textPaint = new SKPaint
            {
                Color = item.IsEnabled ? SKColors.Black : SKColors.Gray,
                IsAntialias = true,
                TextSize = 13,
                TextAlign = SKTextAlign.Left
            };
            canvas.DrawText(item.Label ?? "", Bounds.Left + PADDING + 4, y + itemHeight / 2 + 4, textPaint);
            
            // Shortcut
            if (!string.IsNullOrEmpty(item.Shortcut))
            {
                using var shortcutPaint = new SKPaint
                {
                    Color = new SKColor(150, 150, 150),
                    IsAntialias = true,
                    TextSize = 12,
                    TextAlign = SKTextAlign.Right
                };
                canvas.DrawText(item.Shortcut, Bounds.Right - PADDING - 4, y + itemHeight / 2 + 4, shortcutPaint);
            }
            
            y += itemHeight;
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        if (!IsOpen) return;
        
        int newHovered = GetItemIndex(x, y);
        if (newHovered != _hoveredIndex)
        {
            _hoveredIndex = newHovered;
            Invalidate();
        }
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (!IsOpen) return;
        
        if (!Bounds.Contains(x, y))
        {
            Hide();
            return;
        }
        
        int index = GetItemIndex(x, y);
        if (index >= 0 && index < _items.Count)
        {
            var item = _items[index];
            if (item.CanInvoke)
            {
                try
                {
                    item.Invoke();
                }
                catch { }
                Hide();
            }
        }
    }

    
    public override void OnKeyDown(Key key, bool ctrl, bool shift, bool alt)
    {
        if (!IsOpen) return;
        
        switch (key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Up:
                MovePrevious();
                break;
            case Key.Down:
                MoveNext();
                break;
            case Key.Enter:
                ActivateItem();
                break;
        }
    }
    
    private int GetItemIndex(float x, float y)
    {
        if (!Bounds.Contains(x, y)) return -1;
        
        float itemY = Bounds.Top + PADDING;
        for (int i = 0; i < _items.Count; i++)
        {
            float itemHeight = _items[i].IsSeparator ? 8 : ITEM_HEIGHT;
            if (y >= itemY && y < itemY + itemHeight)
            {
                return _items[i].IsSeparator ? -1 : i;
            }
            itemY += itemHeight;
        }
        return -1;
    }
    
    private void MoveNext()
    {
        for (int i = _hoveredIndex + 1; i < _items.Count; i++)
        {
            if (!_items[i].IsSeparator && _items[i].IsEnabled)
            {
                _hoveredIndex = i;
                Invalidate();
                return;
            }
        }
    }
    
    private void MovePrevious()
    {
        for (int i = _hoveredIndex - 1; i >= 0; i--)
        {
            if (!_items[i].IsSeparator && _items[i].IsEnabled)
            {
                _hoveredIndex = i;
                Invalidate();
                return;
            }
        }
    }
    
    private void ActivateItem()
    {
        if (_hoveredIndex >= 0 && _hoveredIndex < _items.Count)
        {
            var item = _items[_hoveredIndex];
            if (item.CanInvoke)
            {
                item.Invoke();
                Hide();
            }
        }
    }
}
