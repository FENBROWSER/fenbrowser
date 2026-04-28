using System;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using SkiaSharp;

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
        // Reference: https://www.w3.org/TR/mediaqueries-5/

        // === Viewport Dimensions ===
        public static double? MediaViewportWidth { get; set; }
        public static double? MediaViewportHeight { get; set; }

        // === Resolution ===
        public static double? MediaDppx { get; set; }  // Device pixel ratio (1.0 for standard, 2.0 for retina)

        // === User Preferences ===
        public static string MediaPrefersColorScheme { get; set; }  // "light" or "dark"
        public static string MediaPrefersReducedMotion { get; set; }  // "no-preference" or "reduce"
        public static string MediaPrefersContrast { get; set; }  // "no-preference", "more", "less", "custom"
        public static string MediaPrefersReducedTransparency { get; set; }  // "no-preference" or "reduce"
        public static string MediaPrefersReducedData { get; set; }  // "no-preference" or "reduce"
        public static string MediaForcedColors { get; set; }  // "none" or "active"
        public static string MediaInvertedColors { get; set; }  // "none" or "inverted"

        // === Device Capabilities ===
        public static string MediaPointer { get; set; }  // "none", "coarse", or "fine"
        public static string MediaHover { get; set; }  // "none" or "hover"
        public static string MediaAnyPointer { get; set; }  // "none", "coarse", or "fine"
        public static string MediaAnyHover { get; set; }  // "none" or "hover"

        // === Display Capabilities ===
        public static string MediaColorGamut { get; set; }  // "srgb", "p3", or "rec2020"
        public static string MediaDynamicRange { get; set; }  // "standard" or "high"
        public static int? MediaColorIndex { get; set; }  // Number of colors in color lookup table
        public static int? MediaMonochrome { get; set; }  // Bits per pixel in monochrome frame buffer
        public static int? MediaColor { get; set; }  // Bits per color component

        // === Scripting ===
        public static string MediaScripting { get; set; }  // "none", "initial-only", or "enabled"

        // === Update Frequency ===
        public static string MediaUpdate { get; set; }  // "none", "slow", or "fast"

        // === Display Mode ===
        public static string MediaDisplayMode { get; set; }  // "fullscreen", "standalone", "minimal-ui", "browser"

        /// <summary>
        /// Evaluates a media query condition string against the current viewport state.
        /// Supports basic conditions: screen, all, min-width, max-width, and.
        /// </summary>
        public static bool EvaluateMediaQuery(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true; // Empty condition implies 'all'

            condition = condition.ToLowerInvariant().Trim();

            // Handle "only" prefix (ignore it)
            if (condition.StartsWith("only ")) condition = condition.Substring(5).Trim();

            // Handle media types
            if (condition.StartsWith("print")) return false; // We are screen
            if (condition.StartsWith("screen")) 
            {
                // Strip 'screen' and optional 'and'
                condition = condition.Substring(6).Trim();
                if (condition.StartsWith("and")) condition = condition.Substring(3).Trim();
                else if (condition.Length > 0) return true; // Just "screen"
            }
            else if (condition.StartsWith("all"))
            {
                 condition = condition.Substring(3).Trim();
                 if (condition.StartsWith("and")) condition = condition.Substring(3).Trim();
            }

            if (string.IsNullOrWhiteSpace(condition)) return true;

            // Simple AND parser - specialized split needed to respect parens? 
            // For now, assume simple "feature and feature"
            var parts = condition.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                if (!EvaluateMediaFeature(part.Trim())) return false;
            }

            return true;
        }

        private static bool EvaluateMediaFeature(string feature)
        {
            feature = feature.Trim();
            if (feature.StartsWith("(") && feature.EndsWith(")"))
                feature = feature.Substring(1, feature.Length - 2).Trim();

            int colon = feature.IndexOf(':');
            if (colon < 0) return false;

            string name = feature.Substring(0, colon).Trim();
            string val = feature.Substring(colon + 1).Trim();

            double pxVal = 0;
            if (val.EndsWith("px")) double.TryParse(val.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out pxVal);
            // Handle em/rem broadly if needed, for now assumepx
            
            if (name == "min-width")
            {
                if (MediaViewportWidth.HasValue) return MediaViewportWidth.Value >= pxVal;
                return false; // Spec: if unknown, condition is false
            }
            if (name == "max-width")
            {
                if (MediaViewportWidth.HasValue) return MediaViewportWidth.Value <= pxVal;
                return false;
            }
             if (name == "min-height")
            {
                if (MediaViewportHeight.HasValue) return MediaViewportHeight.Value >= pxVal;
                return false; 
            }
            if (name == "max-height")
            {
                 if (MediaViewportHeight.HasValue) return MediaViewportHeight.Value <= pxVal;
                 return false;
            }

            if (name == "orientation")
            {
                if (MediaViewportWidth.HasValue && MediaViewportHeight.HasValue)
                {
                    bool isLandscape = MediaViewportWidth.Value >= MediaViewportHeight.Value;
                    if (val == "landscape") return isLandscape;
                    if (val == "portrait") return !isLandscape;
                }
                return false;
            }

            // Media Query Level 5 User Preferences
            if (name == "prefers-color-scheme")
                return string.Equals(MediaPrefersColorScheme, val, StringComparison.OrdinalIgnoreCase);
            
            if (name == "prefers-reduced-motion")
                return string.Equals(MediaPrefersReducedMotion, val, StringComparison.OrdinalIgnoreCase);

            if (name == "prefers-contrast")
                return string.Equals(MediaPrefersContrast, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "prefers-reduced-transparency")
                return string.Equals(MediaPrefersReducedTransparency, val, StringComparison.OrdinalIgnoreCase);

            if (name == "prefers-reduced-data")
                return string.Equals(MediaPrefersReducedData, val, StringComparison.OrdinalIgnoreCase);

            if (name == "forced-colors")
                return string.Equals(MediaForcedColors, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "inverted-colors")
                return string.Equals(MediaInvertedColors, val, StringComparison.OrdinalIgnoreCase);

            // Device Capabilities
            if (name == "hover")
                return string.Equals(MediaHover, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "any-hover")
                return string.Equals(MediaAnyHover, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "pointer")
                return string.Equals(MediaPointer, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "any-pointer")
                return string.Equals(MediaAnyPointer, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "scripting")
                return string.Equals(MediaScripting, val, StringComparison.OrdinalIgnoreCase);
                
            if (name == "display-mode")
                return string.Equals(MediaDisplayMode, val, StringComparison.OrdinalIgnoreCase);

            // Range context syntax: width >= 500px, width <= 500px, width > 500px etc.
            return EvaluateRangeFeature(feature);
        }

        private static bool EvaluateRangeFeature(string feature)
        {
            // Try operators in order: >= before >, <= before < to avoid partial matches
            int idx;
            string featureName, valueStr;

            if ((idx = feature.IndexOf(">=", StringComparison.Ordinal)) >= 0)
            {
                featureName = feature.Substring(0, idx).Trim();
                valueStr = feature.Substring(idx + 2).Trim();
                return CompareRangeDimension(featureName, ParseDimensionPx(valueStr), ">=");
            }
            if ((idx = feature.IndexOf("<=", StringComparison.Ordinal)) >= 0)
            {
                featureName = feature.Substring(0, idx).Trim();
                valueStr = feature.Substring(idx + 2).Trim();
                return CompareRangeDimension(featureName, ParseDimensionPx(valueStr), "<=");
            }
            if ((idx = feature.IndexOf('>')) >= 0)
            {
                featureName = feature.Substring(0, idx).Trim();
                valueStr = feature.Substring(idx + 1).Trim();
                return CompareRangeDimension(featureName, ParseDimensionPx(valueStr), ">");
            }
            if ((idx = feature.IndexOf('<')) >= 0)
            {
                featureName = feature.Substring(0, idx).Trim();
                valueStr = feature.Substring(idx + 1).Trim();
                return CompareRangeDimension(featureName, ParseDimensionPx(valueStr), "<");
            }
            if ((idx = feature.IndexOf('=')) >= 0)
            {
                featureName = feature.Substring(0, idx).Trim();
                valueStr = feature.Substring(idx + 1).Trim();
                return CompareRangeDimension(featureName, ParseDimensionPx(valueStr), "=");
            }
            return false;
        }

        private static double ParseDimensionPx(string val)
        {
            val = val.Trim();
            if (val.EndsWith("px") && double.TryParse(val.AsSpan(0, val.Length - 2),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double px))
                return px;
            if (val.EndsWith("em") && double.TryParse(val.AsSpan(0, val.Length - 2),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double em))
                return em * 16; // Approximate 1em = 16px
            return 0;
        }

        private static bool CompareRangeDimension(string name, double value, string op)
        {
            double? current = name switch
            {
                "width" => MediaViewportWidth,
                "height" => MediaViewportHeight,
                _ => null
            };
            if (!current.HasValue) return false;
            return op switch
            {
                ">=" => current.Value >= value,
                "<=" => current.Value <= value,
                ">"  => current.Value > value,
                "<"  => current.Value < value,
                "="  => Math.Abs(current.Value - value) < 0.5,
                _ => false
            };
        }

        private static readonly Dictionary<string, SKColor> _namedColors 
            = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase);

        static CssParser()
        {
            // Pre-populate common colors to ensure they work even if reflection fails
            _namedColors["black"] = SKColors.Black;
            _namedColors["white"] = SKColors.White;
            _namedColors["gray"] = SKColors.Gray;
            _namedColors["grey"] = SKColors.Gray;
            _namedColors["red"] = SKColors.Red;
            _namedColors["green"] = SKColors.Green;
            _namedColors["blue"] = SKColors.Blue;
            _namedColors["yellow"] = SKColors.Yellow;
            _namedColors["cyan"] = SKColors.Cyan;
            _namedColors["magenta"] = SKColors.Magenta;
            _namedColors["transparent"] = SKColors.Transparent;
            _namedColors["lightcoral"] = SKColors.LightCoral;
            _namedColors["purple"] = SKColors.Purple;
            _namedColors["orange"] = SKColors.Orange;
            _namedColors["gold"] = SKColors.Gold;
            _namedColors["brown"] = SKColors.Brown;
            _namedColors["pink"] = SKColors.Pink;
            _namedColors["teal"] = SKColors.Teal;
            _namedColors["lime"] = SKColors.Lime;
            _namedColors["maroon"] = SKColors.Maroon;
            _namedColors["navy"] = SKColors.Navy;
            _namedColors["olive"] = SKColors.Olive;
            _namedColors["silver"] = SKColors.Silver;

            try
            {
                // check properties
                var props = typeof(SKColors).GetRuntimeProperties();
                foreach (var p in props)
                {
                    if (p.PropertyType == typeof(SKColor))
                    {
                        try 
                        { 
                            var c = (SKColor)p.GetValue(null);
                            _namedColors[p.Name] = c;
                        } 
                        catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[CssParser] Failed reading SKColors property value: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
                    }
                }
                
                // check fields (SKColors typically uses static fields)
                var fields = typeof(SKColors).GetRuntimeFields();
                foreach (var f in fields)
                {
                    if (f.FieldType == typeof(SKColor) && f.IsStatic && f.IsPublic)
                    {
                        try
                        {
                            var c = (SKColor)f.GetValue(null);
                            _namedColors[f.Name] = c;
                        }
                        catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[CssParser] Failed reading SKColors field value: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
                    }
                }
            }
            catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[CssParser] InitializeNamedColors failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
        }

        /// <summary>
        /// Parse a CSS color value (#hex, rgb/rgba(), or a small set of named colors).
        /// Returns null if unsupported. Alpha defaults to 255 when omitted.
        /// </summary>
        public static SKColor? ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            
            // currentColor keyword - returns a sentinel value for later resolution
            // CssLoader will detect this and use the element's computed 'color' property
            if (string.Equals(s, "currentcolor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "currentColor", StringComparison.Ordinal))
            {
                // Return a special sentinel color (unique ARGB) that CssLoader can detect
                // Using ARGB 1, 255, 0, 255 as sentinel (nearly transparent magenta)
                return new SKColor(255, 0, 255, 1);
            }

            // transparent keyword
            if (string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase))
                return SKColors.Transparent;

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
                        return new SKColor(r, g, b, a);
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
                    byte a = 255;

                    // Legacy comma-separated syntax: rgb(10, 20, 30), rgba(10, 20, 30, 0.5)
                    if (inner.Contains(","))
                    {
                        var parts = inner.Split(',');
                        if (parts.Length < 3) return null;

                        int r = ParseComponent(parts[0]);
                        int g = ParseComponent(parts[1]);
                        int b = ParseComponent(parts[2]);
                        if (parts.Length >= 4)
                        {
                            a = ParseAlpha(parts[3].Trim());
                        }
                        return new SKColor((byte)r, (byte)g, (byte)b, a);
                    }

                    // Modern space/slash syntax: rgb(10 20 30 / 50%)
                    var normalized = inner.Replace("/", " / ");
                    var tokens = normalized.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 3)
                    {
                        int r = ParseComponent(tokens[0]);
                        int g = ParseComponent(tokens[1]);
                        int b = ParseComponent(tokens[2]);

                        if (tokens.Length >= 5 && tokens[3] == "/")
                        {
                            a = ParseAlpha(tokens[4]);
                        }
                        else if (tokens.Length >= 4 && tokens[3] != "/")
                        {
                            // Lenient fallback for rgba-like 4th token without slash.
                            a = ParseAlpha(tokens[3]);
                        }

                        return new SKColor((byte)r, (byte)g, (byte)b, a);
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
                        bool hasExplicitPercentSaturation = parts[1].Trim().EndsWith("%", StringComparison.Ordinal);
                        bool hasExplicitPercentLightness = parts[2].Trim().EndsWith("%", StringComparison.Ordinal);
                        if (!hasExplicitPercentSaturation || !hasExplicitPercentLightness)
                        {
                            return null;
                        }

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
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }

            // hwb() functional notation (CSS Color Level 4)
            // Syntax: hwb(H W B [/ alpha]) where H is hue 0-360, W is whiteness 0-100%, B is blackness 0-100%
            if (s.StartsWith("hwb", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        // H: hue 0-360 (may have deg suffix)
                        var hRaw = parts[0].Trim().ToLowerInvariant().Replace("deg", "");
                        double h = 0;
                        double.TryParse(hRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out h);
                        h = ((h % 360) + 360) % 360;
                        
                        // W: whiteness 0-100%
                        double w = ParsePercentOrDecimal(parts[1].Trim(), 100) / 100.0;
                        w = Math.Max(0, Math.Min(1, w));
                        
                        // B: blackness 0-100%
                        double bVal = ParsePercentOrDecimal(parts[2].Trim(), 100) / 100.0;
                        bVal = Math.Max(0, Math.Min(1, bVal));
                        
                        // Alpha
                        byte alpha = 255;
                        if (parts.Length >= 4)
                            alpha = ParseAlpha(parts[3].Trim());
                        
                        // Convert HWB to RGB
                        var (r, g, b) = HwbToRgb(h, w, bVal);
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }

            // oklch() functional notation (CSS Color Level 4)
            // Syntax: oklch(L C H [/ alpha]) where L is 0-100%, C is chroma 0-0.4+, H is hue 0-360
            if (s.StartsWith("oklch", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        // L: lightness 0-1 (may be percentage)
                        var lRaw = parts[0].Trim();
                        double l = ParsePercentOrDecimal(lRaw, 1.0);
                        
                        // C: chroma 0-0.4+ (usually 0-0.4 but can exceed)
                        var cRaw = parts[1].Trim();
                        double c = 0;
                        if (cRaw.EndsWith("%"))
                            c = ParsePercentOrDecimal(cRaw, 0.4);
                        else if (double.TryParse(cRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var cv))
                            c = cv;
                        
                        // H: hue 0-360 (may have deg suffix)
                        var hRaw = parts[2].Trim().ToLowerInvariant().Replace("deg", "");
                        double h = 0;
                        double.TryParse(hRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out h);
                        h = ((h % 360) + 360) % 360;
                        
                        // Alpha
                        byte alpha = 255;
                        if (parts.Length >= 4)
                        {
                            var aRaw = parts[3].Trim();
                            alpha = ParseAlpha(aRaw);
                        }
                        
                        // Convert OKLCH to RGB
                        var (r, g, b) = OklchToRgb(l, c, h);
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }
            
            // oklab() functional notation (CSS Color Level 4)
            // Syntax: oklab(L a b [/ alpha]) where L is 0-1, a and b are -0.4 to +0.4
            if (s.StartsWith("oklab", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        double l = ParsePercentOrDecimal(parts[0].Trim(), 1.0);
                        
                        double a = 0, bVal = 0;
                        if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var av))
                            a = av;
                        if (double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bv))
                            bVal = bv;
                        
                        byte alpha = 255;
                        if (parts.Length >= 4)
                            alpha = ParseAlpha(parts[3].Trim());
                        
                        // Convert OKLAB to RGB
                        var (r, g, b) = OklabToRgb(l, a, bVal);
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }

            // lch() functional notation (CSS Color Level 4) - CIE LCH
            // Syntax: lch(L C H [/ alpha]) where L is 0-100, C is 0-150+, H is 0-360
            if (s.StartsWith("lch", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("oklch", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        // L: lightness 0-100, C: chroma 0-150+, H: hue 0-360
                        double l = ParsePercentOrDecimal(parts[0].Trim(), 100.0);
                        double c = 0;
                        if (double.TryParse(parts[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cv))
                            c = cv;
                        var hRaw = parts[2].Trim().ToLowerInvariant().Replace("deg", "");
                        double h = 0;
                        double.TryParse(hRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out h);
                        h = ((h % 360) + 360) % 360;
                        
                        byte alpha = 255;
                        if (parts.Length >= 4)
                            alpha = ParseAlpha(parts[3].Trim());
                        
                        // Convert CIE LCH to RGB via Lab
                        var (r, g, b) = LchToRgb(l, c, h);
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }
            
            // lab() functional notation (CSS Color Level 4) - CIE Lab
            // Syntax: lab(L a b [/ alpha]) where L is 0-100, a and b are -125 to +125
            if (s.StartsWith("lab", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("oklab", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        double l = ParsePercentOrDecimal(parts[0].Trim(), 100.0);
                        
                        double a = 0, bVal = 0;
                        if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var av))
                            a = av;
                        if (double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bv))
                            bVal = bv;
                        
                        byte alpha = 255;
                        if (parts.Length >= 4)
                            alpha = ParseAlpha(parts[3].Trim());
                        
                        // Convert CIE Lab to RGB
                        var (r, g, b) = LabToRgb(l, a, bVal);
                        return new SKColor(r, g, b, alpha);
                    }
                }
                return null;
            }

            // color() functional notation (CSS Color Level 4)
            // Syntax: color(colorspace r g b [/ alpha])
            // colorspace: srgb, srgb-linear, display-p3, a98-rgb, prophoto-rgb, rec2020, xyz, xyz-d50, xyz-d65
            if (s.StartsWith("color(", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1).Trim();
                    var parts = inner.Replace("/", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    if (parts.Length >= 4)
                    {
                        string colorspace = parts[0].Trim().ToLowerInvariant();
                        
                        double r = ParsePercentOrDecimal(parts[1].Trim(), 1.0);
                        double g = ParsePercentOrDecimal(parts[2].Trim(), 1.0);
                        double b = ParsePercentOrDecimal(parts[3].Trim(), 1.0);
                        
                        byte alpha = 255;
                        if (parts.Length >= 5)
                            alpha = ParseAlpha(parts[4].Trim());
                        
                        // Convert to sRGB based on colorspace
                        // For now, treat sRGB, srgb-linear similar (basic support)
                        // display-p3 needs gamut mapping
                        switch (colorspace)
                        {
                            case "srgb":
                            case "srgb-linear":
                                // Linear sRGB - apply gamma
                                r = LinearToSrgb(r);
                                g = LinearToSrgb(g);
                                b = LinearToSrgb(b);
                                break;
                            case "display-p3":
                                // Display P3 to sRGB (simplified - gamut clip)
                                // P3 has wider gamut than sRGB
                                r = Math.Min(1, Math.Max(0, r * 1.2249 - 0.2249));
                                g = Math.Min(1, Math.Max(0, g));
                                b = Math.Min(1, Math.Max(0, b * 0.9317 + 0.0683));
                                break;
                            default:
                                // Other colorspaces - just use values directly (approximate)
                                break;
                        }
                        
                        return new SKColor(
                            (byte)Math.Max(0, Math.Min(255, (int)Math.Round(r * 255))),
                            (byte)Math.Max(0, Math.Min(255, (int)Math.Round(g * 255))),
                            (byte)Math.Max(0, Math.Min(255, (int)Math.Round(b * 255))),
                            alpha);
                    }
                }
                return null;
            }

            // color-mix() functional notation (CSS Color Level 5)
            // Syntax: color-mix(in srgb, color1 [percentage], color2 [percentage])
            if (s.StartsWith("color-mix", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    // Split by comma, being careful of nested functions
                    var parts = SplitColorMixArgs(inner);
                    
                    if (parts.Count >= 3)
                    {
                        // parts[0] = "in srgb" or similar (color space)
                        // parts[1] = color1 [percentage]
                        // parts[2] = color2 [percentage]
                        
                        // Parse first color and percentage
                        var (color1, pct1) = ParseColorWithPercentage(parts[1].Trim());
                        var (color2, pct2) = ParseColorWithPercentage(parts[2].Trim());
                        
                        if (color1.HasValue && color2.HasValue)
                        {
                            // Default percentages: 50% each if not specified
                            double p1 = pct1 ?? 50;
                            double p2 = pct2 ?? 50;
                            
                            // Normalize percentages if both are specified
                            double total = p1 + p2;
                            if (total > 0)
                            {
                                p1 = p1 / total;
                                p2 = p2 / total;
                            }
                            else
                            {
                                p1 = p2 = 0.5;
                            }
                            
                            // Blend colors
                            var c1 = color1.Value;
                            var c2 = color2.Value;
                            // SKColor stores as byte Red, Green, Blue, Alpha
                            byte r = (byte)(c1.Red * p1 + c2.Red * p2);
                            byte g = (byte)(c1.Green * p1 + c2.Green * p2);
                            byte b = (byte)(c1.Blue * p1 + c2.Blue * p2);
                            byte a = (byte)(c1.Alpha * p1 + c2.Alpha * p2);
                            
                            return new SKColor(r, g, b, a);
                        }
                    }
                }
                return null;
            }

            // light-dark() functional notation (CSS Color Level 5)
            // Syntax: light-dark(light-color, dark-color)
            // Returns light-color in light mode, dark-color in dark mode
            if (s.StartsWith("light-dark", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = SplitColorMixArgs(inner);
                    
                    if (parts.Count >= 2)
                    {
                        var lightColor = ParseColor(parts[0].Trim());
                        var darkColor = ParseColor(parts[1].Trim());
                        
                        if (lightColor.HasValue && darkColor.HasValue)
                        {
                            // Check if dark mode is active (use preference)
                            bool isDarkMode = PrefersDarkMode;
                            return isDarkMode ? darkColor : lightColor;
                        }
                    }
                }
                return null;
            }

            return null;
        }
        
        /// <summary>
        /// System preference for dark mode (used by light-dark() function)
        /// </summary>
        public static bool PrefersDarkMode { get; set; } = false;
        
        /// <summary>
        /// Split color-mix arguments, respecting nested parentheses
        /// </summary>
        private static List<string> SplitColorMixArgs(string inner)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(inner.Substring(start, i - start));
                    start = i + 1;
                }
            }
            
            if (start < inner.Length)
                result.Add(inner.Substring(start));
            
            return result;
        }
        
        /// <summary>
        /// Parse a color value that may have a percentage suffix (e.g., "red 30%")
        /// </summary>
        private static (SKColor? color, double? percentage) ParseColorWithPercentage(string value)
        {
            value = value.Trim();
            
            // Check for percentage at end
            int lastSpace = value.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                var possiblePct = value.Substring(lastSpace + 1).Trim();
                if (possiblePct.EndsWith("%"))
                {
                    if (double.TryParse(possiblePct.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    {
                        var colorPart = value.Substring(0, lastSpace).Trim();
                        var color = ParseColor(colorPart);
                        return (color, pct);
                    }
                }
            }
            
            // No percentage, just parse as color
            return (ParseColor(value), null);
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

        private static SKColor? GetNamedColor(string name)
        {
            SKColor c;
            if (_namedColors.TryGetValue(name, out c)) return c;
            return null;
        }
        
        /// <summary>
        /// Parse alpha value (0-1 or 0-100%)
        /// </summary>
        private static byte ParseAlpha(string aRaw)
        {
            if (aRaw.EndsWith("%"))
            {
                if (double.TryParse(aRaw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    pct = Math.Max(0, Math.Min(100, pct));
                    return (byte)(pct / 100.0 * 255);
                }
            }
            else
            {
                if (double.TryParse(aRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
                {
                    a = Math.Max(0, Math.Min(1, a));
                    return (byte)(a * 255);
                }
            }
            return 255;
        }
        
        /// <summary>
        /// Parse a percentage or decimal value, scaling percentage by maxValue
        /// </summary>
        private static double ParsePercentOrDecimal(string value, double maxValue)
        {
            value = value.Trim();
            if (value.EndsWith("%"))
            {
                if (double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    return (pct / 100.0) * maxValue;
            }
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return 0;
        }
        
        /// <summary>
        /// Convert OKLCH (lightness, chroma, hue) to sRGB
        /// </summary>
        private static (byte r, byte g, byte b) OklchToRgb(double l, double c, double h)
        {
            // Convert LCH to Lab
            double hRad = h * Math.PI / 180.0;
            double a = c * Math.Cos(hRad);
            double bVal = c * Math.Sin(hRad);
            return OklabToRgb(l, a, bVal);
        }
        
        /// <summary>
        /// Convert OKLAB to sRGB using the standard transformation matrices
        /// </summary>
        private static (byte r, byte g, byte b) OklabToRgb(double l, double a, double bVal)
        {
            // Oklab to linear LMS
            double l_ = l + 0.3963377774 * a + 0.2158037573 * bVal;
            double m_ = l - 0.1055613458 * a - 0.0638541728 * bVal;
            double s_ = l - 0.0894841775 * a - 1.2914855480 * bVal;
            
            // Cube l_, m_, s_
            double lc = l_ * l_ * l_;
            double mc = m_ * m_ * m_;
            double sc = s_ * s_ * s_;
            
            // LMS to linear sRGB
            double r = +4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
            double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
            double b = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;
            
            // Linear sRGB to sRGB (gamma correction)
            r = LinearToSrgb(r);
            g = LinearToSrgb(g);
            b = LinearToSrgb(b);
            
            // Clamp and convert to byte
            return (
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(r * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(g * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(b * 255)))
            );
        }
        
        /// <summary>
        /// Apply sRGB gamma transfer function
        /// </summary>
        private static double LinearToSrgb(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            return x <= 0.0031308
                ? 12.92 * x
                : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055;
        }
        
        /// <summary>
        /// Convert HWB (Hue, Whiteness, Blackness) to RGB
        /// </summary>
        private static (byte r, byte g, byte b) HwbToRgb(double h, double w, double bVal)
        {
            // Normalize whiteness and blackness
            if (w + bVal >= 1)
            {
                // Achromatic gray
                double gray = w / (w + bVal);
                byte grayByte = (byte)Math.Round(gray * 255);
                return (grayByte, grayByte, grayByte);
            }
            
            // Convert to RGB via HSL (with S=100%, L=50%)
            var (r, g, b) = HslToRgb(h, 1.0, 0.5);
            
            // Apply whiteness and blackness
            double factor = 1 - w - bVal;
            r = (byte)Math.Round(r * factor / 255.0 * 255 + w * 255);
            g = (byte)Math.Round(g * factor / 255.0 * 255 + w * 255);
            b = (byte)Math.Round(b * factor / 255.0 * 255 + w * 255);
            
            return (r, g, b);
        }
        
        /// <summary>
        /// Convert CIE LCH (lightness, chroma, hue) to sRGB
        /// </summary>
        private static (byte r, byte g, byte b) LchToRgb(double l, double c, double h)
        {
            // Convert LCH to Lab
            double hRad = h * Math.PI / 180.0;
            double a = c * Math.Cos(hRad);
            double bVal = c * Math.Sin(hRad);
            return LabToRgb(l, a, bVal);
        }
        
        /// <summary>
        /// Convert CIE Lab to sRGB using D65 illuminant
        /// </summary>
        private static (byte r, byte g, byte b) LabToRgb(double l, double a, double bVal)
        {
            // Lab to XYZ (D65 illuminant)
            double fy = (l + 16) / 116.0;
            double fx = a / 500.0 + fy;
            double fz = fy - bVal / 200.0;
            
            // D65 reference white
            const double Xn = 0.95047;
            const double Yn = 1.00000;
            const double Zn = 1.08883;
            
            double x = fx > 0.206893 ? Math.Pow(fx, 3) : (fx - 16.0 / 116.0) / 7.787;
            double y = fy > 0.206893 ? Math.Pow(fy, 3) : (fy - 16.0 / 116.0) / 7.787;
            double z = fz > 0.206893 ? Math.Pow(fz, 3) : (fz - 16.0 / 116.0) / 7.787;
            
            x *= Xn;
            y *= Yn;
            z *= Zn;
            
            // XYZ to linear sRGB
            double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
            double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
            double bv = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
            
            // sRGB gamma correction
            r = LinearToSrgb(r);
            g = LinearToSrgb(g);
            bv = LinearToSrgb(bv);
            
            return (
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(r * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(g * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(bv * 255)))
            );
        }
    }
}


