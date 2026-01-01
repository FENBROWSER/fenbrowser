using SkiaSharp;
using System;
using System.Collections.Generic;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Color picker widget for CSS color properties.
/// </summary>
public static class ColorPicker
{
    // Predefined color palette (common web colors)
    public static readonly SKColor[] Palette = new SKColor[]
    {
        // Row 1: Grays
        SKColors.Black, new SKColor(51, 51, 51), new SKColor(102, 102, 102), 
        new SKColor(153, 153, 153), new SKColor(204, 204, 204), SKColors.White,
        
        // Row 2: Reds
        new SKColor(128, 0, 0), new SKColor(255, 0, 0), new SKColor(255, 99, 71),
        new SKColor(255, 127, 80), new SKColor(255, 160, 122), new SKColor(255, 192, 203),
        
        // Row 3: Oranges/Yellows
        new SKColor(255, 140, 0), new SKColor(255, 165, 0), new SKColor(255, 215, 0),
        new SKColor(255, 255, 0), new SKColor(255, 255, 224), new SKColor(250, 250, 210),
        
        // Row 4: Greens
        new SKColor(0, 100, 0), new SKColor(0, 128, 0), new SKColor(34, 139, 34),
        new SKColor(50, 205, 50), new SKColor(144, 238, 144), new SKColor(152, 251, 152),
        
        // Row 5: Blues
        new SKColor(0, 0, 139), new SKColor(0, 0, 255), new SKColor(30, 144, 255),
        new SKColor(0, 191, 255), new SKColor(135, 206, 235), new SKColor(173, 216, 230),
        
        // Row 6: Purples
        new SKColor(75, 0, 130), new SKColor(128, 0, 128), new SKColor(148, 0, 211),
        new SKColor(186, 85, 211), new SKColor(218, 112, 214), new SKColor(238, 130, 238),
    };
    
    public const int ColsPerRow = 6;
    public const int Rows = 6;
    public const float SwatchSize = 20f;
    public const float Padding = 2f;
    
    /// <summary>
    /// Check if a property is a color property.
    /// </summary>
    public static bool IsColorProperty(string propertyName)
    {
        var lower = propertyName.ToLower();
        return lower.Contains("color") || lower == "background" || 
               lower == "fill" || lower == "stroke" || 
               lower.Contains("shadow") || lower == "outline";
    }
    
    /// <summary>
    /// Get total width of the picker.
    /// </summary>
    public static float GetWidth() => ColsPerRow * (SwatchSize + Padding) + Padding;
    
    /// <summary>
    /// Get total height of the picker.
    /// </summary>
    public static float GetHeight() => Rows * (SwatchSize + Padding) + Padding;
    
    /// <summary>
    /// Convert color to hex string.
    /// </summary>
    public static string ToHex(SKColor color)
    {
        return $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}".ToLower();
    }
    
    /// <summary>
    /// Try to parse a color string (hex, rgb, or name).
    /// </summary>
    public static bool TryParse(string value, out SKColor color)
    {
        color = SKColors.Transparent;
        if (string.IsNullOrWhiteSpace(value)) return false;
        
        value = value.Trim().ToLower();
        
        // Hex
        if (value.StartsWith("#"))
        {
            try
            {
                if (value.Length == 4) // #RGB
                {
                    var r = Convert.ToByte(new string(value[1], 2), 16);
                    var g = Convert.ToByte(new string(value[2], 2), 16);
                    var b = Convert.ToByte(new string(value[3], 2), 16);
                    color = new SKColor(r, g, b);
                    return true;
                }
                else if (value.Length == 7) // #RRGGBB
                {
                    var r = Convert.ToByte(value.Substring(1, 2), 16);
                    var g = Convert.ToByte(value.Substring(3, 2), 16);
                    var b = Convert.ToByte(value.Substring(5, 2), 16);
                    color = new SKColor(r, g, b);
                    return true;
                }
            }
            catch { }
        }
        
        // Named colors
        var namedColors = new Dictionary<string, SKColor>
        {
            ["black"] = SKColors.Black, ["white"] = SKColors.White,
            ["red"] = SKColors.Red, ["green"] = SKColors.Green, ["blue"] = SKColors.Blue,
            ["yellow"] = SKColors.Yellow, ["orange"] = SKColors.Orange, ["purple"] = SKColors.Purple,
            ["pink"] = SKColors.Pink, ["gray"] = SKColors.Gray, ["grey"] = SKColors.Gray,
            ["transparent"] = SKColors.Transparent
        };
        
        if (namedColors.TryGetValue(value, out var named))
        {
            color = named;
            return true;
        }
        
        return false;
    }
}
