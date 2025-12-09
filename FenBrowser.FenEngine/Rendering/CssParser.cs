using System;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using Avalonia.Media;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Lean CSS utility providing only media environment hints and color parsing that
    /// the rest of the engine (CustomHtmlEngine, DomBasicRenderer, CssLoader) currently uses.
    /// Original CssMini.cs became irreparably corrupted (thousands of syntax errors & duplicate blocks)
    /// and has been removed for build stability. Extend cautiously as needed.
    /// </summary>
    public static class CssParser
    {
        // Media query environment hints (set externally before parsing/cascade)
        public static double? MediaViewportWidth { get; set; }
        public static double? MediaViewportHeight { get; set; }
        public static double? MediaDppx { get; set; }
        public static string MediaPrefersColorScheme { get; set; }

        private static readonly Dictionary<string, Color> _namedColors 
            = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        static CssParser()
        {
            // Pre-populate common colors to ensure they work even if reflection fails
            _namedColors["black"] = Colors.Black;
            _namedColors["white"] = Colors.White;
            _namedColors["gray"] = Colors.Gray;
            _namedColors["grey"] = Colors.Gray;
            _namedColors["red"] = Colors.Red;
            _namedColors["green"] = Colors.Green;
            _namedColors["blue"] = Colors.Blue;
            _namedColors["yellow"] = Colors.Yellow;
            _namedColors["cyan"] = Colors.Cyan;
            _namedColors["magenta"] = Colors.Magenta;
            _namedColors["transparent"] = Colors.Transparent;
            _namedColors["lightcoral"] = Colors.LightCoral;
            _namedColors["purple"] = Colors.Purple;
            _namedColors["orange"] = Colors.Orange;
            _namedColors["gold"] = Colors.Gold;
            _namedColors["brown"] = Colors.Brown;
            _namedColors["pink"] = Colors.Pink;
            _namedColors["teal"] = Colors.Teal;
            _namedColors["lime"] = Colors.Lime;
            _namedColors["maroon"] = Colors.Maroon;
            _namedColors["navy"] = Colors.Navy;
            _namedColors["olive"] = Colors.Olive;
            _namedColors["silver"] = Colors.Silver;

            try
            {
                var props = typeof(Colors).GetRuntimeProperties();
                foreach (var p in props)
                {
                    if (p.PropertyType == typeof(Color))
                    {
                        try 
                        { 
                            var c = (Color)p.GetValue(null);
                            _namedColors[p.Name] = c;
                        } 
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Parse a CSS color value (#hex, rgb/rgba(), or a small set of named colors).
        /// Returns null if unsupported. Alpha defaults to 255 when omitted.
        /// </summary>
        public static Color? ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();

            // transparent keyword
            if (string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb(0, 0, 0, 0);

            var named = GetNamedColor(s);
            if (named.HasValue) return named.Value;

            // Hex forms: #rgb #rgba #rrggbb #rrggbbaa
            if (s[0] == '#')
            {
                var hex = s.Substring(1);
                if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
                if (hex.Length == 4) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]);
                if (hex.Length == 6) hex += "ff"; // append opaque alpha
                if (hex.Length == 8)
                {
                    try
                    {
                        byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                        byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                        byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                        byte a = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
                        return Color.FromArgb(a, r, g, b);
                    }
                    catch { return null; }
                }
                return null;
            }

            // rgb()/rgba() functional notation
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Split(',');
                    if (parts.Length >= 3)
                    {
                        int r = ParseComponent(parts[0]);
                        int g = ParseComponent(parts[1]);
                        int b = ParseComponent(parts[2]);
                        byte a = 255;
                        if (parts.Length >= 4)
                        {
                            var aRaw = parts[3].Trim();
                            if (aRaw.EndsWith("%"))
                            {
                                double pct;
                                if (double.TryParse(aRaw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                                {
                                    pct = Math.Max(0, Math.Min(100, pct));
                                    a = (byte)(pct / 100.0 * 255);
                                }
                            }
                            else
                            {
                                double alpha;
                                if (double.TryParse(aRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
                                {
                                    alpha = Math.Max(0, Math.Min(1, alpha));
                                    a = (byte)(alpha * 255);
                                }
                            }
                        }
                        return Color.FromArgb(a, (byte)r, (byte)g, (byte)b);
                    }
                }
                return null;
            }

            // hsl()/hsla() functional notation
            if (s.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    // Handle both comma and space/slash separated formats
                    var parts = inner.Replace("/", ",").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        // H: 0-360 degrees (may have deg suffix)
                        var hRaw = parts[0].Trim().ToLowerInvariant().Replace("deg", "");
                        double h;
                        if (!double.TryParse(hRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) h = 0;
                        h = ((h % 360) + 360) % 360; // Normalize to 0-360

                        // S: 0-100% 
                        var sRaw = parts[1].Trim().TrimEnd('%');
                        double sat;
                        if (!double.TryParse(sRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out sat)) sat = 0;
                        sat = Math.Max(0, Math.Min(100, sat)) / 100.0;

                        // L: 0-100%
                        var lRaw = parts[2].Trim().TrimEnd('%');
                        double lit;
                        if (!double.TryParse(lRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out lit)) lit = 0;
                        lit = Math.Max(0, Math.Min(100, lit)) / 100.0;

                        // A: optional alpha
                        byte alpha = 255;
                        if (parts.Length >= 4)
                        {
                            var aRaw = parts[3].Trim();
                            if (aRaw.EndsWith("%"))
                            {
                                double pct;
                                if (double.TryParse(aRaw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                                {
                                    pct = Math.Max(0, Math.Min(100, pct));
                                    alpha = (byte)(pct / 100.0 * 255);
                                }
                            }
                            else
                            {
                                double a;
                                if (double.TryParse(aRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                                {
                                    a = Math.Max(0, Math.Min(1, a));
                                    alpha = (byte)(a * 255);
                                }
                            }
                        }

                        // Convert HSL to RGB
                        var (r, g, b) = HslToRgb(h, sat, lit);
                        return Color.FromArgb(alpha, r, g, b);
                    }
                }
                return null;
            }

            return null;
        }

        /// <summary>
        /// Convert HSL (Hue 0-360, Saturation 0-1, Lightness 0-1) to RGB bytes
        /// </summary>
        private static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
        {
            double r, g, b;

            if (s == 0)
            {
                r = g = b = l; // achromatic
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
                g = HueToRgb(p, q, h / 360.0);
                b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
            }

            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        private static int ParseComponent(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.EndsWith("%"))
            {
                double pct;
                if (double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    pct = Math.Max(0, Math.Min(100, pct));
                    return (int)(pct / 100.0 * 255);
                }
                return 0;
            }
            int v;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                if (v < 0) v = 0; else if (v > 255) v = 255;
                return v;
            }
            double dv;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
            {
                dv = Math.Max(0, Math.Min(255, dv));
                return (int)Math.Round(dv);
            }
            return 0;
        }

        private static Color? GetNamedColor(string name)
        {
            Color c;
            if (_namedColors.TryGetValue(name, out c)) return c;
            return null;
        }
    }
}