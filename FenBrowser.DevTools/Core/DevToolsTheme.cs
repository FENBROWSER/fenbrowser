using SkiaSharp;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Dark theme colors and typography for DevTools.
/// Matches VS Code / Edge DevTools aesthetic.
/// </summary>
public static class DevToolsTheme
{
    // Background colors
    public static readonly SKColor Background = SKColor.Parse("#1E1E1E");
    public static readonly SKColor BackgroundLight = SKColor.Parse("#252526");
    public static readonly SKColor BackgroundHover = SKColor.Parse("#2A2D2E");
    public static readonly SKColor BackgroundSelected = SKColor.Parse("#094771");
    
    // Text colors
    public static readonly SKColor TextPrimary = SKColor.Parse("#D4D4D4");
    public static readonly SKColor TextSecondary = SKColor.Parse("#9D9D9D");
    public static readonly SKColor TextMuted = SKColor.Parse("#6A6A6A");
    
    // Syntax highlighting colors
    public static readonly SKColor SyntaxTag = SKColor.Parse("#569CD6");       // Blue - tags
    public static readonly SKColor SyntaxAttribute = SKColor.Parse("#9CDCFE"); // Light blue - attributes
    public static readonly SKColor SyntaxString = SKColor.Parse("#CE9178");    // Orange - strings
    public static readonly SKColor SyntaxNumber = SKColor.Parse("#B5CEA8");    // Green - numbers
    public static readonly SKColor SyntaxKeyword = SKColor.Parse("#C586C0");   // Purple - keywords
    public static readonly SKColor SyntaxComment = SKColor.Parse("#6A9955");   // Green - comments
    public static readonly SKColor SyntaxProperty = SKColor.Parse("#9CDCFE");  // Light blue - CSS properties
    public static readonly SKColor SyntaxValue = SKColor.Parse("#CE9178");     // Orange - CSS values
    
    // UI element colors
    public static readonly SKColor Border = SKColor.Parse("#3C3C3C");
    public static readonly SKColor BorderLight = SKColor.Parse("#474747");
    public static readonly SKColor Scrollbar = SKColor.Parse("#424242");
    public static readonly SKColor ScrollbarHover = SKColor.Parse("#4F4F4F");
    
    // Tab colors
    public static readonly SKColor TabActive = SKColor.Parse("#1E1E1E");
    public static readonly SKColor TabInactive = SKColor.Parse("#2D2D2D");
    public static readonly SKColor TabHover = SKColor.Parse("#323232");
    public static readonly SKColor TabBorder = SKColor.Parse("#007ACC");
    
    // Console colors
    public static readonly SKColor ConsoleLog = SKColor.Parse("#D4D4D4");
    public static readonly SKColor ConsoleWarn = SKColor.Parse("#DDB700");
    public static readonly SKColor ConsoleError = SKColor.Parse("#F14C4C");
    public static readonly SKColor ConsoleInfo = SKColor.Parse("#3794FF");
    
    // Network colors
    public static readonly SKColor NetworkSuccess = SKColor.Parse("#4EC9B0");
    public static readonly SKColor NetworkRedirect = SKColor.Parse("#DDB700");
    public static readonly SKColor NetworkError = SKColor.Parse("#F14C4C");
    public static readonly SKColor NetworkPending = SKColor.Parse("#9D9D9D");
    
    // Box model colors (for Elements panel)
    public static readonly SKColor BoxMargin = SKColor.Parse("#F6BD91");
    public static readonly SKColor BoxBorder = SKColor.Parse("#FDDF8D");
    public static readonly SKColor BoxPadding = SKColor.Parse("#C5DEA1");
    public static readonly SKColor BoxContent = SKColor.Parse("#9FC4E7");
    
    // Typography
    public const string FontFamily = "Consolas";
    public const string FontFamilyUI = "Segoe UI";
    public const float FontSizeSmall = 11f;
    public const float FontSizeNormal = 12f;
    public const float FontSizeMedium = 13f;
    public const float FontSizeLarge = 14f;
    
    // Spacing
    public const float PaddingSmall = 4f;
    public const float PaddingNormal = 8f;
    public const float PaddingLarge = 12f;
    public const float ItemHeight = 22f;
    public const float TabHeight = 30f;
    public const float ToolbarHeight = 28f;
    
    // Create common paints
    public static SKPaint CreateTextPaint(SKColor? color = null, float size = FontSizeNormal, bool bold = false)
    {
        return new SKPaint
        {
            Color = color ?? TextPrimary,
            TextSize = size,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(FontFamily, bold ? SKFontStyle.Bold : SKFontStyle.Normal)
        };
    }
    
    public static SKPaint CreateUITextPaint(SKColor? color = null, float size = FontSizeNormal)
    {
        return new SKPaint
        {
            Color = color ?? TextPrimary,
            TextSize = size,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(FontFamilyUI)
        };
    }
    
    public static SKPaint CreateFillPaint(SKColor color)
    {
        return new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }
    
    public static SKPaint CreateStrokePaint(SKColor color, float width = 1f)
    {
        return new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            IsAntialias = true
        };
    }
}
