using SkiaSharp;
using Silk.NET.Input;
using FenBrowser.Host.Input;
using FenBrowser.Host.Theme;
using System;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Simple button widget with text or icon.
/// </summary>
public class ButtonWidget : Widget
{
    public ButtonWidget()
    {
        Role = WidgetRole.Button;
    }
    
    public string Text { get; set; } = "";
    public string Icon { get; set; } // SVG path or emoji
    public SKPath IconPath { get; set; } // Vector icon
    public bool IsPressed { get; private set; }
    public bool IsHovered { get; private set; }
    
    public event Action Clicked;
    
    // Styling
    // Styling properties (optional overrides)
    public SKColor? BackgroundColor { get; set; }
    public SKColor? HoverBackgroundColor { get; set; }
    public SKColor? BorderColor { get; set; }
    public SKColor? TextColor { get; set; }
    public SKColor? HoverTextColor { get; set; }
    public float CornerRadius { get; set; } = 4;
    public float FontSize { get; set; } = 14;
    public SKPaintStyle IconPaintStyle { get; set; } = SKPaintStyle.Fill;
    public float IconStrokeWidth { get; set; } = 1.5f;
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Default button size if not specified. 
        // In a real toolkit we'd measure the text here.
        float width = string.IsNullOrEmpty(Text) ? 36 : 80;
        return new SKSize(Math.Min(width, availableSpace.Width), Math.Min(36, availableSpace.Height));
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        // Leaf widget, no children to arrange
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Determine background and text color based on state
        // Modern Style: Transparent by default unless hovered/pressed (Ghost buttons)
        SKColor bgColor = BackgroundColor ?? SKColors.Empty;
        SKColor textColor = TextColor ?? theme.Text;
        SKColor borderColor = BorderColor ?? SKColors.Empty;
        
        if (IsPressed) 
        {
            bgColor = HoverBackgroundColor ?? theme.SurfacePressed;
            textColor = HoverTextColor ?? theme.Text; // Usually same as text but could vary
        }
        else if (IsHovered) 
        {
            bgColor = HoverBackgroundColor ?? theme.SurfaceHover;
            textColor = HoverTextColor ?? theme.Text;
        }
        
        canvas.Save();
        
        // Micro-animation: subtle scale down on press
        if (IsPressed)
        {
            canvas.Scale(0.98f, 0.98f, Bounds.MidX, Bounds.MidY);
        }
        
        // Draw background only if visible
        if (bgColor != SKColors.Empty)
        {
            using var bgPaint = new SKPaint
            {
                Color = bgColor,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRoundRect(Bounds, CornerRadius, CornerRadius, bgPaint);
        }
        
        // Draw border
        using var borderPaint = new SKPaint
        {
            Color = borderColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        canvas.DrawRoundRect(Bounds, CornerRadius, CornerRadius, borderPaint);
        
        // Draw text
        if (!string.IsNullOrEmpty(Text))
        {
            using var textPaint = new SKPaint
            {
                Color = IsEnabled ? textColor : theme.TextMuted,
                IsAntialias = true,
                TextSize = FontSize,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };
            
            var metrics = textPaint.FontMetrics;
            float textY = Bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
            
            canvas.DrawText(Text, Bounds.MidX, textY, textPaint);
        }
        
    // Draw IconPath if present
        if (IconPath != null)
        {
             using var iconPaint = new SKPaint
             {
                 Color = IsEnabled ? textColor : theme.TextMuted,
                 IsAntialias = true,
                 Style = IconPaintStyle,
                 StrokeWidth = IconStrokeWidth
             };
             
             // Scale/Center path
             var pathBounds = IconPath.Bounds;
             float targetSize = FontSize + 4;
             // For strokes, we might need consistent scaling
             float scale = targetSize / Math.Max(pathBounds.Width, pathBounds.Height);
             
             canvas.Save();
             canvas.Translate(Bounds.MidX, Bounds.MidY);
             canvas.Scale(scale);
             canvas.Translate(-pathBounds.MidX, -pathBounds.MidY);
             
             canvas.DrawPath(IconPath, iconPaint);
             canvas.Restore();
        }
        // Fallback to text icon if no path
        else if (!string.IsNullOrEmpty(Icon))
        {
            using var iconPaint = new SKPaint
            {
                Color = IsEnabled ? textColor : theme.TextMuted,
                IsAntialias = true,
                TextSize = FontSize + 4,
                TextAlign = SKTextAlign.Center
            };
            
            var metrics = iconPaint.FontMetrics;
            float iconY = Bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
            canvas.DrawText(Icon, Bounds.MidX, iconY, iconPaint);
        }
        
        canvas.Restore();
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left && IsEnabled)
        {
            IsPressed = true;
            InputManager.Instance.SetCapture(this);
            Invalidate();
        }
    }
    
    public override void OnMouseUp(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left && IsPressed)
        {
            IsPressed = false;
            // Capture is auto-released in Program.cs OnMouseUp but we can be defensive
            InputManager.Instance.ReleaseCapture();
            
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
    public override bool CanFocus => true;
}
