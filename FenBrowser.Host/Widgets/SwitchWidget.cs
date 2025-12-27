using System;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Theme;
using Silk.NET.Input;
using SkiaSharp;

namespace FenBrowser.Host.Widgets;

public class SwitchWidget : Widget
{
    private bool _isChecked;
    public event Action<bool> CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                Invalidate();
            }
        }
    }

    public string Label { get; set; } // Optional internal label, though usually external

    public SwitchWidget()
    {
        // Fixed size for the switch itself
        // We'll calculate bounds in OnMeasure/OnArrange
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Standard switch size: 40x20
        return new SKSize(44, 24);
    }

    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            IsChecked = !IsChecked;
            CheckedChanged?.Invoke(IsChecked);
        }
    }

    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        var rect = new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
        
        // Track
        using var trackPaint = new SKPaint
        {
            Color = _isChecked ? SKColors.DodgerBlue : SKColors.Gray, // Simple accent or gray
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        // Rounded pill shape
        canvas.DrawRoundRect(rect, 12, 12, trackPaint);
        
        // Thumb
        float thumbRadius = 10;
        float thumbPadding = 2;
        float thumbX = _isChecked 
            ? Bounds.Right - thumbRadius - thumbPadding 
            : Bounds.Left + thumbRadius + thumbPadding;
        float thumbY = Bounds.MidY;
        
        using var thumbPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        // Shadow for depth
        using var shadowPaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(50),
            IsAntialias = true
        };
        canvas.DrawCircle(thumbX, thumbY + 1, thumbRadius, shadowPaint);
        
        canvas.DrawCircle(thumbX, thumbY, thumbRadius, thumbPaint);
    }
}
