using SkiaSharp;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Simple button widget with text or icon.
/// </summary>
public class ButtonWidget : Widget
{
    public string Text { get; set; } = "";
    public string Icon { get; set; } // SVG path or emoji
    public bool IsPressed { get; private set; }
    public bool IsHovered { get; private set; }
    
    public event Action Clicked;
    
    // Styling
    public SKColor BackgroundColor { get; set; } = new SKColor(240, 240, 240);
    public SKColor HoverColor { get; set; } = new SKColor(220, 220, 220);
    public SKColor PressedColor { get; set; } = new SKColor(200, 200, 200);
    public SKColor TextColor { get; set; } = SKColors.Black;
    public float CornerRadius { get; set; } = 4;
    public float FontSize { get; set; } = 14;
    
    public override void Paint(SKCanvas canvas)
    {
        // Determine background color based on state
        SKColor bgColor = BackgroundColor;
        if (IsPressed) bgColor = PressedColor;
        else if (IsHovered) bgColor = HoverColor;
        
        // Draw background
        using var bgPaint = new SKPaint
        {
            Color = bgColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        var rect = new SKRoundRect(Bounds, CornerRadius);
        canvas.DrawRoundRect(rect, bgPaint);
        
        // Draw text
        if (!string.IsNullOrEmpty(Text))
        {
            using var textPaint = new SKPaint
            {
                Color = IsEnabled ? TextColor : new SKColor(160, 160, 160),
                IsAntialias = true,
                TextSize = FontSize,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };
            
            // Center text vertically
            var metrics = textPaint.FontMetrics;
            float textY = Bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
            
            canvas.DrawText(Text, Bounds.MidX, textY, textPaint);
        }
        
        // Draw icon if present
        if (!string.IsNullOrEmpty(Icon))
        {
            using var iconPaint = new SKPaint
            {
                Color = IsEnabled ? TextColor : new SKColor(160, 160, 160),
                IsAntialias = true,
                TextSize = FontSize + 4,
                TextAlign = SKTextAlign.Center
            };
            
            var metrics = iconPaint.FontMetrics;
            float iconY = Bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
            canvas.DrawText(Icon, Bounds.MidX, iconY, iconPaint);
        }
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left && IsEnabled)
        {
            IsPressed = true;
            Invalidate();
        }
    }
    
    public override void OnMouseUp(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left && IsPressed)
        {
            IsPressed = false;
            if (HitTest(x, y))
            {
                Clicked?.Invoke();
            }
            Invalidate();
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        bool wasHovered = IsHovered;
        IsHovered = HitTest(x, y);
        if (wasHovered != IsHovered)
        {
            Invalidate();
        }
    }
}
