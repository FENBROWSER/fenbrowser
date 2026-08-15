using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS Filter parser and SKImageFilter generator.
    /// Supports: blur, brightness, contrast, grayscale, sepia, saturate, hue-rotate, invert, opacity, drop-shadow
    /// </summary>
    public static class CssFilterParser
    {
        private readonly struct CssFunction
        {
            public CssFunction(string name, string arguments)
            {
                Name = name;
                Arguments = arguments;
            }

            public string Name { get; }
            public string Arguments { get; }
        }

        private static bool TryParseFunctions(string input, out List<CssFunction> functions)
        {
            functions = new List<CssFunction>();
            if (string.IsNullOrWhiteSpace(input))
                return false;

            int i = 0;
            while (i < input.Length)
            {
                while (i < input.Length && IsCssWhitespace(input[i])) i++;
                if (i >= input.Length) break;

                int nameStart = i;
                while (i < input.Length && IsFunctionNameChar(input[i])) i++;
                if (i == nameStart) return false;

                string name = input.Substring(nameStart, i - nameStart).ToLowerInvariant();
                while (i < input.Length && IsCssWhitespace(input[i])) i++;
                if (i >= input.Length || input[i] != '(') return false;

                int argumentsStart = ++i;
                int depth = 1;
                char quote = '\0';
                bool escaped = false;
                while (i < input.Length && depth > 0)
                {
                    char c = input[i];
                    if (quote != '\0')
                    {
                        if (escaped)
                            escaped = false;
                        else if (c == '\\')
                            escaped = true;
                        else if (c == quote)
                            quote = '\0';
                        i++;
                        continue;
                    }

                    if (c is '\'' or '"')
                    {
                        quote = c;
                        i++;
                        continue;
                    }
                    if (c == '\\')
                    {
                        i += Math.Min(2, input.Length - i);
                        continue;
                    }
                    if (c == '(')
                    {
                        depth++;
                        i++;
                        continue;
                    }
                    if (c == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            string arguments = input.Substring(argumentsStart, i - argumentsStart).Trim();
                            functions.Add(new CssFunction(name, arguments));
                            i++;
                            break;
                        }
                    }
                    i++;
                }

                if (depth != 0 || quote != '\0')
                    return false;
            }

            return functions.Count > 0;
        }

        private static bool IsFunctionNameChar(char c) =>
            (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') ||
            c == '-';

        private static bool IsCssWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        /// <summary>
        /// Parse a CSS filter string and return a combined SKImageFilter
        /// </summary>
        public static SKImageFilter Parse(string filterString)
        {
            if (string.IsNullOrWhiteSpace(filterString) || filterString == "none")
                return null;

            if (!TryParseFunctions(filterString, out var functions))
                return null;

            SKImageFilter combined = null;
            foreach (var function in functions)
            {
                SKImageFilter filter = CreateFilter(function.Name, function.Arguments);
                if (filter == null)
                {
                    continue;
                }

                if (combined == null)
                {
                    combined = filter;
                    continue;
                }

                // CreateCompose retains the native filter graph. Dispose the two
                // temporary managed/native wrappers after constructing the next link.
                var previous = combined;
                combined = null;
                try
                {
                    combined = SKImageFilter.CreateCompose(filter, previous);
                }
                finally
                {
                    filter.Dispose();
                    previous.Dispose();
                }
            }

            return combined;
        }

        /// <summary>
        /// Returns a conservative visual outset for filters whose output bounds can
        /// be determined locally. Callers must not cull when this returns false.
        /// </summary>
        internal static bool TryGetVisualOutset(string filterString, out float outset)
        {
            outset = 0f;
            if (string.IsNullOrWhiteSpace(filterString) ||
                string.Equals(filterString.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryParseFunctions(filterString, out var functions))
                return false;

            foreach (var function in functions)
            {
                var arguments = function.Arguments;
                switch (function.Name)
                {
                    case "blur":
                        // Skia's blur argument is sigma; three sigma contains the
                        // practical filter support used for raster bounds.
                        outset += Math.Max(0f, ParseLength(arguments, 0f)) * 3f;
                        break;
                    case "brightness":
                    case "contrast":
                    case "grayscale":
                    case "sepia":
                    case "saturate":
                    case "hue-rotate":
                    case "invert":
                    case "opacity":
                        break;
                    default:
                        // Drop shadows and unknown filters can move output
                        // asymmetrically, so keep the conservative no-cull path.
                        return false;
                }
            }

            return true;
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
            if (matrix == null || matrix.Length != 20) return null;
            using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            return colorFilter == null ? null : SKImageFilter.CreateColorFilter(colorFilter);
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
            float blur = Math.Max(0f, parts.Count > 2 ? ParseLength(parts[2], 0) : 0);
            SKColor color = SKColors.Black;
            
            if (parts.Count > 3)
            {
                // Preserve any spaces inside newer color syntaxes instead of silently
                // discarding everything after the first color token.
                var colorText = string.Join(" ", parts.GetRange(3, parts.Count - 3));
                color = CssColorParser.Parse(colorText) ?? SKColors.Black;
            }

            return SKImageFilter.CreateDropShadow(dx, dy, blur, blur, color);
        }

        private static float ParseLength(string s, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            s = s.Trim();

            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 2), out var px) ? px : defaultValue;
            if (s.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 2), out var em) ? em * 16f : defaultValue;
            return TryParseFiniteFloat(s.AsSpan(), out var number) ? number : defaultValue;
        }

        private static float ParseNumber(string s, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            s = s.Trim();

            if (s.EndsWith("%", StringComparison.Ordinal))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 1), out var percent) ? percent / 100f : defaultValue;
            return TryParseFiniteFloat(s.AsSpan(), out var number) ? number : defaultValue;
        }

        private static float ParseAngle(string s, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            s = s.Trim();

            if (s.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 3), out var degrees) ? degrees : defaultValue;
            if (s.EndsWith("rad", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 3), out var radians) ? radians * 180f / (float)Math.PI : defaultValue;
            if (s.EndsWith("turn", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 4), out var turns) ? turns * 360f : defaultValue;
            return TryParseFiniteFloat(s.AsSpan(), out var number) ? number : defaultValue;
        }

        private static bool TryParseFiniteFloat(ReadOnlySpan<char> text, out float value)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                float.IsFinite(value))
            {
                return true;
            }

            value = 0f;
            return false;
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
                        float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float a) &&
                        float.IsFinite(a))
                    {
                        a = Math.Clamp(a, 0f, 1f);
                        return new SKColor(r, g, b, (byte)Math.Round(a * 255f));
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
