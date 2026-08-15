using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;

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

    private static readonly IReadOnlyDictionary<string, SKColor> NamedColors =
        new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = SKColors.Black,
            ["white"] = SKColors.White,
            ["red"] = SKColors.Red,
            ["green"] = SKColors.Green,
            ["blue"] = SKColors.Blue,
            ["yellow"] = SKColors.Yellow,
            ["orange"] = SKColors.Orange,
            ["purple"] = SKColors.Purple,
            ["pink"] = SKColors.Pink,
            ["gray"] = SKColors.Gray,
            ["grey"] = SKColors.Gray,
            ["transparent"] = SKColors.Transparent
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
        if (string.IsNullOrWhiteSpace(propertyName))
            return false;

        return propertyName.Contains("color", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("background", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("fill", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("stroke", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Contains("shadow", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("outline", StringComparison.OrdinalIgnoreCase);
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
    /// Convert color to a CSS hex string. Preserve alpha when it is not opaque.
    /// </summary>
    public static string ToHex(SKColor color)
    {
        return color.Alpha == byte.MaxValue
            ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}".ToLowerInvariant()
            : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2}".ToLowerInvariant();
    }

    /// <summary>
    /// Try to parse common CSS color forms used by the DevTools editor.
    /// Supports #RGB, #RGBA, #RRGGBB, #RRGGBBAA, rgb()/rgba(), and common names.
    /// </summary>
    public static bool TryParse(string value, out SKColor color)
    {
        color = SKColors.Transparent;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        if (TryParseHex(value, out color))
            return true;
        if (TryParseRgbFunction(value, out color))
            return true;

        return NamedColors.TryGetValue(value, out color);
    }

    private static bool TryParseHex(string value, out SKColor color)
    {
        color = SKColors.Transparent;
        if (value.Length < 2 || value[0] != '#')
            return false;

        ReadOnlySpan<char> hex = value.AsSpan(1);
        if (hex.Length is 3 or 4)
        {
            if (!TryHexNibble(hex[0], out var r) ||
                !TryHexNibble(hex[1], out var g) ||
                !TryHexNibble(hex[2], out var b))
            {
                return false;
            }

            byte a = byte.MaxValue;
            if (hex.Length == 4 && !TryHexNibble(hex[3], out a))
                return false;

            color = new SKColor(
                (byte)(r * 17),
                (byte)(g * 17),
                (byte)(b * 17),
                (byte)(a * 17));
            return true;
        }

        if (hex.Length is 6 or 8)
        {
            if (!byte.TryParse(hex.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            byte a = byte.MaxValue;
            if (hex.Length == 8 &&
                !byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
            {
                return false;
            }

            color = new SKColor(r, g, b, a);
            return true;
        }

        return false;
    }

    private static bool TryHexNibble(char c, out byte value)
    {
        if (c is >= '0' and <= '9')
        {
            value = (byte)(c - '0');
            return true;
        }
        if (c is >= 'a' and <= 'f')
        {
            value = (byte)(c - 'a' + 10);
            return true;
        }
        if (c is >= 'A' and <= 'F')
        {
            value = (byte)(c - 'A' + 10);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseRgbFunction(string value, out SKColor color)
    {
        color = SKColors.Transparent;
        bool isRgba = value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);
        bool isRgb = !isRgba && value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase);
        if (!isRgb && !isRgba)
            return false;
        if (value.Length < (isRgba ? 7 : 6) || value[^1] != ')')
            return false;

        int open = value.IndexOf('(');
        var parts = value.Substring(open + 1, value.Length - open - 2)
            .Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != (isRgba ? 4 : 3))
            return false;

        if (!TryParseRgbChannel(parts[0], out var r) ||
            !TryParseRgbChannel(parts[1], out var g) ||
            !TryParseRgbChannel(parts[2], out var b))
        {
            return false;
        }

        byte a = byte.MaxValue;
        if (isRgba && !TryParseAlpha(parts[3], out a))
            return false;

        color = new SKColor(r, g, b, a);
        return true;
    }

    private static bool TryParseRgbChannel(string value, out byte channel)
    {
        value = value.Trim();
        if (value.EndsWith('%'))
        {
            if (double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) &&
                percent is >= 0 and <= 100)
            {
                channel = (byte)Math.Round(percent * 255d / 100d, MidpointRounding.AwayFromZero);
                return true;
            }
        }
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) &&
                 numeric is >= 0 and <= 255)
        {
            channel = (byte)Math.Round(numeric, MidpointRounding.AwayFromZero);
            return true;
        }

        channel = 0;
        return false;
    }

    private static bool TryParseAlpha(string value, out byte alpha)
    {
        value = value.Trim();
        double normalized;
        if (value.EndsWith('%'))
        {
            if (!double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
                percent is < 0 or > 100)
            {
                alpha = 0;
                return false;
            }
            normalized = percent / 100d;
        }
        else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out normalized) ||
                 normalized is < 0 or > 1)
        {
            alpha = 0;
            return false;
        }

        alpha = (byte)Math.Round(normalized * 255d, MidpointRounding.AwayFromZero);
        return true;
    }
}
