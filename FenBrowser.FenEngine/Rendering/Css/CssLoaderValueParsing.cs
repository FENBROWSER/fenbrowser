// CssLoaderValueParsing.cs - CSS Value Parsing Utilities
// Extracted from CssLoader.cs for modularity
// Part of CssLoader partial class

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    public static partial class CssLoader
    {
        #region CSS Value Parsing

        public static bool TryDouble(string s, out double v)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        public static bool TryPx(string s, out double px, double emBase = 16.0, double percentBase = 0, bool allowUnitless = false)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            var sl = s.ToLowerInvariant();

            // Handle calc() expressions
            if (sl.StartsWith("calc("))
            {
                return TryParseCalc(s, out px, emBase, percentBase);
            }

            // Handle var() expressions directly
            if (sl.StartsWith("var("))
            {
                px = EvaluateCssValue(s, emBase, percentBase);
                return true;
            }

            // Handle clamp(min, preferred, max)
            if (sl.StartsWith("clamp("))
            {
                return TryParseClamp(s, out px, emBase, percentBase);
            }

            // Handle min(a, b, ...)
            if (sl.StartsWith("min("))
            {
                return TryParseMin(s, out px, emBase, percentBase);
            }

            // Handle max(a, b, ...)
            if (sl.StartsWith("max("))
            {
                return TryParseMax(s, out px, emBase, percentBase);
            }

            // px
            if (sl.EndsWith("px"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v; return true; }
                
                // FALLBACK: Heavy-handed parse for robust engine behavior
                // (Matches TryGapShorthand workaround logic)
                if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    px = v;
                    return true;
                }
                
                return false;
            }

            // rem (uses root font-size, defaulting to 16px)
            if (sl.EndsWith("rem"))
            {
                var num = s.Substring(0, s.Length - 3).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * _rootFontSize; return true; }
                return false;
            }

            // em (uses provided base, usually 16px if root or inherited)
            if (sl.EndsWith("em") && !sl.EndsWith("rem"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase; return true; }
                return false;
            }

            // Viewport units - use CssParser.MediaViewportWidth/Height if available
            double vpWidth = CssParser.MediaViewportWidth ?? 1920.0;
            double vpHeight = CssParser.MediaViewportHeight ?? 1080.0;

            // vw (viewport width percentage)
            if (sl.EndsWith("vw"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * vpWidth / 100.0; return true; }
                return false;
            }

            // vh (viewport height percentage)
            if (sl.EndsWith("vh"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * vpHeight / 100.0; return true; }
                return false;
            }

            // vmin (smaller of vw or vh)
            if (sl.EndsWith("vmin"))
            {
                var num = s.Substring(0, s.Length - 4).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * Math.Min(vpWidth, vpHeight) / 100.0; return true; }
                return false;
            }

            // vmax (larger of vw or vh)
            if (sl.EndsWith("vmax"))
            {
                var num = s.Substring(0, s.Length - 4).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * Math.Max(vpWidth, vpHeight) / 100.0; return true; }
                return false;
            }

            // ch (width of '0' character, approximate as 0.5em)
            if (sl.EndsWith("ch"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase * 0.5; return true; }
                return false;
            }

            // ex (x-height, approximate as 0.5em)
            if (sl.EndsWith("ex"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase * 0.5; return true; }
                return false;
            }

            // pt (points: 1pt = 1/72 inch at 96 DPI = 96/72 = 1.333... px)
            if (sl.EndsWith("pt"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 72.0); return true; }
                return false;
            }

            // pc (picas: 1pc = 12pt = 16px)
            if (sl.EndsWith("pc"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 16.0; return true; }
                return false;
            }

            // in (inches: 1in = 96px at 96 DPI)
            if (sl.EndsWith("in"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 96.0; return true; }
                return false;
            }

            // cm (centimeters: 1cm = 96/2.54 px)
            if (sl.EndsWith("cm"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 2.54); return true; }
                return false;
            }

            // mm (millimeters: 1mm = 96/25.4 px)
            if (sl.EndsWith("mm"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 25.4); return true; }
                return false;
            }

            // % handled by TryPercent separately for properties that support it.
            // TryPx should not blindly convert % to emBase-relative pixels.
            if (sl.EndsWith("%") && percentBase == 0)
            {
                return false;
            }
            if (sl.EndsWith("%") && percentBase > 0)
            {
                 var num = s.Substring(0, s.Length - 1).Trim();
                 double v;
                 if (TryDouble(num, out v)) { px = v * percentBase / 100.0; return true; }
                 return false;
            }

            // raw number -> px
            {
                double v;
                if (TryDouble(sl, out v)) 
                { 
                    if (Math.Abs(v) < 0.001 || allowUnitless)
                    {
                        px = v;
                        return true; 
                    }
                    return false; 
                }
            }
            return false;
        }

        public static bool TryThickness(string s, out Thickness th, double emBase = 16.0)
        {
            th = new Thickness(0);
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Tokenize with function-awareness so calc()/min()/max() values that contain
            // internal whitespace stay intact as a single thickness component.
            var parts = SplitCssValues(s);
            double a = 0, b = 0, c = 0, d = 0;

            bool ParsePart(string p, out double val) {
                if (p.ToLowerInvariant() == "auto") { val = 0; return true; }
                return TryPx(p, out val, emBase);
            }

            if (parts.Count == 1)
            {
                if (!ParsePart(parts[0], out a)) return false;
                th = new Thickness(a);
                return true;
            }
            if (parts.Count == 2)
            {
                if (!ParsePart(parts[0], out a) || !ParsePart(parts[1], out b)) return false;
                th = new Thickness(b, a, b, a);
                return true;
            }
            if (parts.Count == 3)
            {
                if (!ParsePart(parts[0], out a) || !ParsePart(parts[1], out b) || !ParsePart(parts[2], out c)) return false;
                th = new Thickness(b, a, b, c);
                return true;
            }
            if (parts.Count != 4)
            {
                return false;
            }

            if (!ParsePart(parts[0], out a) ||
                !ParsePart(parts[1], out b) ||
                !ParsePart(parts[2], out c) ||
                !ParsePart(parts[3], out d))
            {
                return false;
            }

            th = new Thickness(d, a, b, c);
            return true;
        }

        private static SKColor FromHex(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 3)
            {
                var r = Convert.ToByte(new string(hex[0], 2), 16);
                var g = Convert.ToByte(new string(hex[1], 2), 16);
                var b = Convert.ToByte(new string(hex[2], 2), 16);
                return new SKColor(r, g, b, 255);
            }
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new SKColor(r, g, b, 255);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return new SKColor(r, g, b, a);
            }
            return SKColors.Black;
        }

        public static SKColor? TryColor(string css)
        {
            return CssParser.ParseColor(css);
        }

        public static bool TryPercent(string s, out double pct)
        {
            pct = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.EndsWith("%"))
            {
                var num = s.Substring(0, s.Length - 1).Trim();
                return TryDouble(num, out pct);
            }
            return false;
        }

        #endregion

        #region Border/Background Helpers

        private static string ExtractBackgroundColor(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("background-color", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("background", out v) && !string.IsNullOrWhiteSpace(v))
            {
                string sanitized = Regex.Replace(v, @"url\([^)]+\)", " ", RegexOptions.IgnoreCase);
                sanitized = Regex.Replace(sanitized, @"(?:linear|radial|conic|repeating-linear|repeating-radial)-gradient\([^)]+\)", " ", RegexOptions.IgnoreCase);

                foreach (var token in SplitCssValues(sanitized))
                {
                    var val = token.Trim();
                    if (string.IsNullOrEmpty(val))
                    {
                        continue;
                    }

                    if (val.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("repeat", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("repeat-x", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("repeat-y", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("no-repeat", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("fixed", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("contain", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("center", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("top", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("bottom", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("right", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryColor(val).HasValue)
                    {
                        return val;
                    }
                }
            }
            return null;
        }

        private static string ExtractBorderColor(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("border-color", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("border", out v) && !string.IsNullOrWhiteSpace(v))
            {
                var matches = Regex.Matches(v, @"(#[0-9a-fA-F]{3,8}|rgba?\([^)]+\)|[a-zA-Z]+)");
                foreach (Match m in matches)
                {
                    var val = m.Value;
                    if (IsBorderStyle(val)) continue;
                    if (val.Equals("thin", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("thick", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return val;
                }
                return null; 
            }
            return null;
        }

        private static bool IsBorderStyle(string s)
        {
            s = s.ToLowerInvariant();
            return s == "none" || s == "hidden" || s == "dotted" || s == "dashed" ||
                   s == "solid" || s == "double" || s == "groove" || s == "ridge" ||
                   s == "inset" || s == "outset";
        }

        private static string ExtractBorderThickness(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("border-width", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("border", out v) && !string.IsNullOrWhiteSpace(v))
            {
                if (v.IndexOf("thin", StringComparison.OrdinalIgnoreCase) >= 0) return "1px";
                if (v.IndexOf("medium", StringComparison.OrdinalIgnoreCase) >= 0) return "3px";
                if (v.IndexOf("thick", StringComparison.OrdinalIgnoreCase) >= 0) return "5px";

                var m = Regex.Match(v, @"([0-9.]+)(px|em|rem)");
                if (m.Success) return m.Groups[0].Value;
            }
            return null;
        }

        #endregion
    }
}
