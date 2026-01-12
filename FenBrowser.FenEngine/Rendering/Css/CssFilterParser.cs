using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS Filter parser and SKImageFilter generator.
    /// Supports: blur, brightness, contrast, grayscale, sepia, saturate, hue-rotate, invert, opacity, drop-shadow
    /// </summary>
    public static class CssFilterParser
    {
        private static readonly Regex FunctionRegex = new Regex(
            @"(\w+(?:-\w+)?)\s*\(\s*([^)]*)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parse a CSS filter string and return a combined SKImageFilter
        /// </summary>
        public static SKImageFilter Parse(string filterString)
        {
            if (string.IsNullOrWhiteSpace(filterString) || filterString == "none")
                return null;

            SKImageFilter combined = null;
            var matches = FunctionRegex.Matches(filterString);

            foreach (Match match in matches)
            {
                string funcName = match.Groups[1].Value.ToLowerInvariant();
                string argsStr = match.Groups[2].Value.Trim();

                SKImageFilter filter = CreateFilter(funcName, argsStr);
                if (filter != null)
                {
                    // Chain filters (each filter takes the previous as input)
                    combined = combined == null ? filter : SKImageFilter.CreateCompose(filter, combined);
                }
            }

            return combined;
        }

        private static SKImageFilter CreateFilter(string name, string args)
        {
            switch (name)
            {
                case "blur":
                    float sigma = ParseLength(args, 0);
                    if (sigma > 0)
                        return SKImageFilter.CreateBlur(sigma, sigma);
                    break;

                case "brightness":
                    float brightness = ParseNumber(args, 1);
                    // Convert to color matrix: multiply RGB by brightness
                    return CreateColorMatrixFilter(new float[]
                    {
                        brightness, 0, 0, 0, 0,
                        0, brightness, 0, 0, 0,
                        0, 0, brightness, 0, 0,
                        0, 0, 0, 1, 0
                    });

                case "contrast":
                    float contrast = ParseNumber(args, 1);
                    float t = (1 - contrast) / 2;
                    return CreateColorMatrixFilter(new float[]
                    {
                        contrast, 0, 0, 0, t,
                        0, contrast, 0, 0, t,
                        0, 0, contrast, 0, t,
                        0, 0, 0, 1, 0
                    });

                case "grayscale":
                    float gray = ParseNumber(args, 1);
                    gray = Math.Clamp(gray, 0, 1);
                    // Luminosity-based grayscale
                    float r = 0.2126f, g = 0.7152f, b = 0.0722f;
                    float ir = 1 - gray;
                    return CreateColorMatrixFilter(new float[]
                    {
                        ir + gray * r, gray * g, gray * b, 0, 0,
                        gray * r, ir + gray * g, gray * b, 0, 0,
                        gray * r, gray * g, ir + gray * b, 0, 0,
                        0, 0, 0, 1, 0
                    });

                case "sepia":
                    float sepia = ParseNumber(args, 1);
                    sepia = Math.Clamp(sepia, 0, 1);
                    float is_ = 1 - sepia;
                    return CreateColorMatrixFilter(new float[]
                    {
                        is_ + sepia * 0.393f, sepia * 0.769f, sepia * 0.189f, 0, 0,
                        sepia * 0.349f, is_ + sepia * 0.686f, sepia * 0.168f, 0, 0,
                        sepia * 0.272f, sepia * 0.534f, is_ + sepia * 0.131f, 0, 0,
                        0, 0, 0, 1, 0
                    });

                case "saturate":
                    float sat = ParseNumber(args, 1);
                    // Saturation matrix
                    float sr = (1 - sat) * 0.2126f;
                    float sg = (1 - sat) * 0.7152f;
                    float sb = (1 - sat) * 0.0722f;
                    return CreateColorMatrixFilter(new float[]
                    {
                        sr + sat, sg, sb, 0, 0,
                        sr, sg + sat, sb, 0, 0,
                        sr, sg, sb + sat, 0, 0,
                        0, 0, 0, 1, 0
                    });

                case "hue-rotate":
                    float degrees = ParseAngle(args, 0);
                    return CreateHueRotateFilter(degrees);

                case "invert":
                    float inv = ParseNumber(args, 1);
                    inv = Math.Clamp(inv, 0, 1);
                    float invComp = 1 - 2 * inv;
                    return CreateColorMatrixFilter(new float[]
                    {
                        invComp, 0, 0, 0, inv,
                        0, invComp, 0, 0, inv,
                        0, 0, invComp, 0, inv,
                        0, 0, 0, 1, 0
                    });

                case "opacity":
                    float alpha = ParseNumber(args, 1);
                    alpha = Math.Clamp(alpha, 0, 1);
                    return CreateColorMatrixFilter(new float[]
                    {
                        1, 0, 0, 0, 0,
                        0, 1, 0, 0, 0,
                        0, 0, 1, 0, 0,
                        0, 0, 0, alpha, 0
                    });

                case "drop-shadow":
                    return ParseDropShadow(args);
            }

            return null;
        }

        private static SKImageFilter CreateColorMatrixFilter(float[] matrix)
        {
            if (matrix.Length != 20) return null;
            var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            return SKImageFilter.CreateColorFilter(colorFilter);
        }

        private static SKImageFilter CreateHueRotateFilter(float degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);

            // Hue rotation matrix (from CSS spec)
            float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            
            return CreateColorMatrixFilter(new float[]
            {
                lr + cos * (1 - lr) + sin * (-lr), lg + cos * (-lg) + sin * (-lg), lb + cos * (-lb) + sin * (1 - lb), 0, 0,
                lr + cos * (-lr) + sin * (0.143f), lg + cos * (1 - lg) + sin * (0.140f), lb + cos * (-lb) + sin * (-0.283f), 0, 0,
                lr + cos * (-lr) + sin * (-(1 - lr)), lg + cos * (-lg) + sin * (lg), lb + cos * (1 - lb) + sin * (lb), 0, 0,
                0, 0, 0, 1, 0
            });
        }

        private static SKImageFilter ParseDropShadow(string args)
        {
            // Parse: offset-x offset-y blur-radius color
            // Example: "2px 4px 6px rgba(0,0,0,0.5)"
            var parts = new List<string>();
            int parenDepth = 0;
            int start = 0;

            for (int i = 0; i <= args.Length; i++)
            {
                char c = i < args.Length ? args[i] : ' ';
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if ((c == ' ' || i == args.Length) && parenDepth == 0)
                {
                    var part = args.Substring(start, i - start).Trim();
                    if (!string.IsNullOrEmpty(part))
                        parts.Add(part);
                    start = i + 1;
                }
            }

            if (parts.Count < 3) return null;

            float dx = ParseLength(parts[0], 0);
            float dy = ParseLength(parts[1], 0);
            float blur = parts.Count > 2 ? ParseLength(parts[2], 0) : 0;
            SKColor color = SKColors.Black;
            
            if (parts.Count > 3)
            {
                // Try to parse color (could be named color, hex, rgb, rgba)
                color = CssColorParser.Parse(parts[3]) ?? SKColors.Black;
            }

            return SKImageFilter.CreateDropShadow(dx, dy, blur, blur, color);
        }

        private static float ParseLength(string s, float defaultValue)
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            s = s.Trim().ToLowerInvariant();
            
            if (s.EndsWith("px"))
            {
                if (float.TryParse(s.Replace("px", ""), out float px))
                    return px;
            }
            else if (s.EndsWith("em"))
            {
                if (float.TryParse(s.Replace("em", ""), out float em))
                    return em * 16; // Approximate
            }
            else if (float.TryParse(s, out float num))
            {
                return num;
            }
            
            return defaultValue;
        }

        private static float ParseNumber(string s, float defaultValue)
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            s = s.Trim();
            
            if (s.EndsWith("%"))
            {
                if (float.TryParse(s.Replace("%", ""), out float pct))
                    return pct / 100;
            }
            else if (float.TryParse(s, out float num))
            {
                return num;
            }
            
            return defaultValue;
        }

        private static float ParseAngle(string s, float defaultValue)
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            s = s.Trim().ToLowerInvariant();
            
            if (s.EndsWith("deg"))
            {
                if (float.TryParse(s.Replace("deg", ""), out float deg))
                    return deg;
            }
            else if (s.EndsWith("rad"))
            {
                if (float.TryParse(s.Replace("rad", ""), out float rad))
                    return rad * 180 / (float)Math.PI;
            }
            else if (s.EndsWith("turn"))
            {
                if (float.TryParse(s.Replace("turn", ""), out float turn))
                    return turn * 360;
            }
            else if (float.TryParse(s, out float num))
            {
                return num; // Assume degrees
            }
            
            return defaultValue;
        }
    }

    /// <summary>
    /// Simple CSS color parser (subset for filter use)
    /// </summary>
    public static class CssColorParser
    {
        public static SKColor? Parse(string color)
        {
            if (string.IsNullOrEmpty(color)) return null;
            color = color.Trim().ToLowerInvariant();

            // Named colors
            if (color == "black") return SKColors.Black;
            if (color == "white") return SKColors.White;
            if (color == "red") return SKColors.Red;
            if (color == "transparent") return SKColors.Transparent;

            // Hex
            if (color.StartsWith("#"))
            {
                if (SKColor.TryParse(color, out var hexColor))
                    return hexColor;
            }

            // rgba(r, g, b, a)
            if (color.StartsWith("rgba(") && color.EndsWith(")"))
            {
                var inner = color.Substring(5, color.Length - 6);
                var parts = inner.Split(',');
                if (parts.Length >= 4)
                {
                    if (byte.TryParse(parts[0].Trim(), out byte r) &&
                        byte.TryParse(parts[1].Trim(), out byte g) &&
                        byte.TryParse(parts[2].Trim(), out byte b) &&
                        float.TryParse(parts[3].Trim(), out float a))
                    {
                        return new SKColor(r, g, b, (byte)(a * 255));
                    }
                }
            }

            // rgb(r, g, b)
            if (color.StartsWith("rgb(") && color.EndsWith(")"))
            {
                var inner = color.Substring(4, color.Length - 5);
                var parts = inner.Split(',');
                if (parts.Length >= 3)
                {
                    if (byte.TryParse(parts[0].Trim(), out byte r) &&
                        byte.TryParse(parts[1].Trim(), out byte g) &&
                        byte.TryParse(parts[2].Trim(), out byte b))
                    {
                        return new SKColor(r, g, b);
                    }
                }
            }

            return null;
        }
    }
}
