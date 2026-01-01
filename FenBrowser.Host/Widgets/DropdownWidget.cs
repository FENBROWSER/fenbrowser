using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Theme;
using Silk.NET.Input;
using SkiaSharp;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Dropdown select widget for settings options.
/// </summary>
public class DropdownWidget : Widget
{
    private bool _isOpen = false;
    public bool IsOpen => _isOpen;
    private int _selectedIndex = 0;
    private int _hoveredIndex = -1;
    private List<string> _options = new();
    
    private float _animationProgress = 0f; // 0 = closed, 1 = open
    private DateTime _lastUpdate = DateTime.Now;
    
    public event Action<int, string> SelectionChanged;
    
    public List<string> Options
    {
        get => _options;
        set
        {
            _options = value ?? new List<string>();
            if (_selectedIndex >= _options.Count)
                _selectedIndex = 0;
            Invalidate();
        }
    }
    
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value >= 0 && value < _options.Count && _selectedIndex != value)
            {
                _selectedIndex = value;
                Invalidate();
            }
        }
    }
    
    public string SelectedValue => _options.Count > 0 ? _options[_selectedIndex] : "";
    
    private static SKTypeface _font = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    
    public DropdownWidget()
    {
        Name = "Dropdown";
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(180, 32);
    }
    
    public override bool HitTest(float x, float y)
    {
        if (_isOpen)
        {
            float itemHeight = 28;
            float popupHeight = Math.Min(_options.Count * itemHeight, 200);
            var totalBounds = new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom + 2 + popupHeight);
            return totalBounds.Contains(x, y);
        }
        return base.HitTest(x, y);
    }

    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Background
        using var bgPaint = new SKPaint
        {
            Color = theme.Surface,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(Bounds, 4, 4, bgPaint);
        
        // Border
        using var borderPaint = new SKPaint
        {
            Color = theme.Border,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        canvas.DrawRoundRect(Bounds, 4, 4, borderPaint);
        
        // Selected text
        using var textPaint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true,
            TextSize = 14,
            Typeface = _font
        };
        
        string displayText = SelectedValue;
        if (string.IsNullOrEmpty(displayText)) displayText = "Select...";
        
        canvas.DrawText(displayText, Bounds.Left + 10, Bounds.MidY + 5, textPaint);
        
        // Dropdown arrow
        using var arrowPaint = new SKPaint
        {
            Color = theme.TextMuted,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        float arrowX = Bounds.Right - 20;
        float arrowY = Bounds.MidY - 3;
        var arrowPath = new SKPath();
        arrowPath.MoveTo(arrowX, arrowY);
        arrowPath.LineTo(arrowX + 8, arrowY);
        arrowPath.LineTo(arrowX + 4, arrowY + 6);
        arrowPath.Close();
        canvas.DrawPath(arrowPath, arrowPaint);
        
        // Update animation
        var now = DateTime.Now;
        float dt = (float)(now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        if (_isOpen && _animationProgress < 1f)
        {
            _animationProgress = Math.Min(1f, _animationProgress + 8f * dt);
            Invalidate();
        }
        else if (!_isOpen && _animationProgress > 0f)
        {
            _animationProgress = Math.Max(0f, _animationProgress - 12f * dt);
            Invalidate();
        }

        // Dropdown popup
        if (_animationProgress > 0)
        {
            float itemHeight = 28;
            float maxPopupHeight = Math.Min(_options.Count * itemHeight, 200);
            float currentHeight = maxPopupHeight * _animationProgress;
            var popupRect = new SKRect(Bounds.Left, Bounds.Bottom + 2, Bounds.Right, Bounds.Bottom + 2 + currentHeight);
            
            canvas.Save();
            canvas.ClipRect(popupRect);

            // Popup background
            using var popupBg = new SKPaint
            {
                Color = theme.Background.WithAlpha((byte)(255 * _animationProgress)),
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRect(Bounds.Left, Bounds.Bottom + 2, Bounds.Right, Bounds.Bottom + 2 + maxPopupHeight), 4, 4, popupBg);
            
            using var popupBorderPaint = new SKPaint
            {
                Color = theme.Border.WithAlpha((byte)(255 * _animationProgress)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1
            };
            canvas.DrawRoundRect(new SKRect(Bounds.Left, Bounds.Bottom + 2, Bounds.Right, Bounds.Bottom + 2 + maxPopupHeight), 4, 4, popupBorderPaint);
            
            // Options
            float startY = Bounds.Bottom + 2;
            float y = startY;
            for (int i = 0; i < _options.Count; i++)
            {
                var itemRect = new SKRect(popupRect.Left, y, popupRect.Right, y + itemHeight);
                
                // Only draw if within current animated height
                if (y + itemHeight <= Bounds.Bottom + 2 + currentHeight + 5)
                {
                    // Highlight selected or hovered
                    if (i == _selectedIndex || i == _hoveredIndex)
                    {
                        using var highlightPaint = new SKPaint
                        {
                            Color = (i == _selectedIndex ? theme.SurfaceHover : theme.SurfaceHover.WithAlpha(128)).WithAlpha((byte)(theme.SurfaceHover.Alpha * _animationProgress)),
                            IsAntialias = true
                        };
                        canvas.DrawRect(itemRect, highlightPaint);
                    }
                    
                    using var optionTextPaint = new SKPaint
                    {
                        Color = theme.Text.WithAlpha((byte)(255 * _animationProgress)),
                        IsAntialias = true,
                        TextSize = 14,
                        Typeface = _font
                    };
                    canvas.DrawText(_options[i], itemRect.Left + 10, itemRect.MidY + 5, optionTextPaint);
                }
                y += itemHeight;
            }
            canvas.Restore();
        }
    }
    
    public void Close()
    {
        if (_isOpen)
        {
            _isOpen = false;
            _hoveredIndex = -1;
            Invalidate();
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        if (_isOpen)
        {
            float itemHeight = 28;
            float popupTop = Bounds.Bottom + 2;
            float popupBottom = popupTop + Math.Min(_options.Count * itemHeight, 200);
            
            if (y >= popupTop && y <= popupBottom && x >= Bounds.Left && x <= Bounds.Right)
            {
                int newHovered = (int)((y - popupTop) / itemHeight);
                if (newHovered != _hoveredIndex && newHovered >= 0 && newHovered < _options.Count)
                {
                    _hoveredIndex = newHovered;
                    Invalidate();
                }
            }
            else if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Invalidate();
            }
        }
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        
        if (_isOpen)
        {
            // Check if clicking on an option
            float itemHeight = 28;
            float popupTop = Bounds.Bottom + 2;
            float maxPopupHeight = Math.Min(_options.Count * itemHeight, 200);
            float currentHeight = maxPopupHeight * _animationProgress;
            float popupBottom = popupTop + currentHeight;
            
            if (y >= popupTop && y <= popupBottom && x >= Bounds.Left && x <= Bounds.Right)
            {
                int clickedIndex = (int)((y - popupTop) / itemHeight);
                if (clickedIndex >= 0 && clickedIndex < _options.Count)
                {
                    _selectedIndex = clickedIndex;
                    SelectionChanged?.Invoke(_selectedIndex, SelectedValue);
                }
                _isOpen = false;
            }
            else
            {
                _isOpen = false;
            }
        }
        else
        {
            _isOpen = true;
        }
        
        _lastUpdate = DateTime.Now;
        Invalidate();
    }
}
