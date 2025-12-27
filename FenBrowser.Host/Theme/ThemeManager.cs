using SkiaSharp;
using System;

namespace FenBrowser.Host.Theme;

public class Theme
{
    public SKColor Background { get; set; }
    public SKColor Surface { get; set; }
    public SKColor SurfaceHover { get; set; }
    public SKColor SurfacePressed { get; set; }
    public SKColor Border { get; set; }
    public SKColor Text { get; set; }
    public SKColor TextMuted { get; set; }
    public SKColor Accent { get; set; }
    public SKColor AccentMuted { get; set; }
}

public static class ThemeManager
{
    public static Theme Current { get; private set; }
    
    public static bool IsDark { get; private set; }

    static ThemeManager()
    {
        SetTheme(false); // Default to Light
    }

    public static void SetTheme(bool dark)
    {
        IsDark = dark;
        if (dark)
        {
            Current = new Theme
            {
                Background = new SKColor(18, 18, 18),
                Surface = new SKColor(30, 30, 30),
                SurfaceHover = new SKColor(45, 45, 45),
                SurfacePressed = new SKColor(60, 60, 60),
                Border = new SKColor(50, 50, 50),
                Text = new SKColor(230, 230, 230),
                TextMuted = new SKColor(160, 160, 160),
                Accent = new SKColor(66, 133, 244),
                AccentMuted = new SKColor(66, 133, 244, 40)
            };
        }
        else
        {
            Current = new Theme
            {
                Background = new SKColor(255, 255, 255), // Pure White
                Surface = new SKColor(235, 235, 235),    // Distinct Light Gray
                SurfaceHover = new SKColor(225, 225, 225),
                SurfacePressed = new SKColor(210, 210, 210),
                Border = new SKColor(200, 200, 200),     // Darker border
                Text = new SKColor(0, 0, 0),             // Pure Black text
                TextMuted = new SKColor(100, 100, 100),
                Accent = new SKColor(0, 120, 215),       // Edge Blue
                AccentMuted = new SKColor(0, 120, 215, 30)
            };
        }
    }

    public static void ToggleTheme()
    {
        SetTheme(!IsDark);
    }
    
    /// <summary>
    /// Helper to draw a glass-like rectangle (acrylic effect).
    /// </summary>
    public static void DrawGlassRect(SKCanvas canvas, SKRect rect, float opacity = 0.7f, float blur = 10f)
    {
        using var paint = new SKPaint
        {
            Color = Current.Surface.WithAlpha((byte)(255 * opacity)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur)
        };
        
        // In a real glass effect, we'd copy the background and blur it,
        // but for now, we'll use a translucent blur overlay as a close approximation.
        canvas.DrawRoundRect(rect, 8, 8, paint);
        
        // Add a subtle border highlight
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 30),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 8, 8, borderPaint);
    }
}
