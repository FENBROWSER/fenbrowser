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
            // Syntax: color-mix(in <colorspace>, color1 [percentage], color2 [percentage])
            if (s.StartsWith("color-mix", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('('); int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var inner = s.Substring(open + 1, close - open - 1);
                    var parts = SplitColorMixArgs(inner);

                    if (parts.Count >= 3)
                    {
                        // Parse color space and optional hue method from parts[0], e.g. "in srgb" or "in lch shorter hue"
                        var spaceSpec = parts[0].Trim();
                        var (colorSpace, hueMethod) = ParseColorMixSpace(spaceSpec);

                        var (color1, pct1) = ParseColorWithPercentage(parts[1].Trim());
                        var (color2, pct2) = ParseColorWithPercentage(parts[2].Trim());

                        if (color1.HasValue && color2.HasValue)
                        {
                            double p1 = pct1 ?? 50;
                            double p2 = pct2 ?? 50;

                            // Normalize percentages
                            double total = p1 + p2;
                            double w1 = total > 0 ? p1 / total : 0.5;
                            double w2 = total > 0 ? p2 / total : 0.5;

                            var c1 = color1.Value;
                            var c2 = color2.Value;

                            // Fast path: sRGB blending (backward compatible)
                            if (colorSpace == "srgb" && hueMethod == null)
                            {
                                byte r = (byte)(c1.Red * w1 + c2.Red * w2);
                                byte g = (byte)(c1.Green * w1 + c2.Green * w2);
                                byte b = (byte)(c1.Blue * w1 + c2.Blue * w2);
                                byte a = (byte)(c1.Alpha * w1 + c2.Alpha * w2);
                                return new SKColor(r, g, b, a);
                            }

                            // Convert both colors to the interpolation space
                            var comps1 = ConvertColorForInterpolation(c1, colorSpace);
                            var comps2 = ConvertColorForInterpolation(c2, colorSpace);

                            // Mix in that space and convert back
                            var mixed = MixInColorSpace(comps1, comps2, w1, colorSpace, hueMethod);
                            var result = InterpolationResultToSrgb(mixed, colorSpace);

                            // Alpha: blend separately
                            byte alpha = (byte)(c1.Alpha * w1 + c2.Alpha * w2);

                            return new SKColor(result.r, result.g, result.b, alpha);
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

        // ── Color interpolation space helpers ──

        private static double SrgbToLinear(double c)
        {
            c = Math.Max(0, Math.Min(1, c));
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static double LinearToSrgbComponent(double c)
        {
            if (c <= 0) return 0;
            if (c >= 1) return 1;
            return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        }

        // sRGB-linear → XYZ-D65 conversion matrices
        private static readonly double[] sRgbToXyzM = {
            0.4124564, 0.3575761, 0.1804375,
            0.2126729, 0.7151522, 0.0721750,
            0.0193339, 0.1191920, 0.9503041
        };

        private static readonly double[] xyzToSRgbM = {
             3.2404542, -1.5371385, -0.4985314,
            -0.9692660,  1.8760108,  0.0415560,
             0.0556434, -0.2040259,  1.0572252
        };

        private static (double X, double Y, double Z) LinearRgbToXyzD65(double r, double g, double b)
        {
            double X = sRgbToXyzM[0]*r + sRgbToXyzM[1]*g + sRgbToXyzM[2]*b;
            double Y = sRgbToXyzM[3]*r + sRgbToXyzM[4]*g + sRgbToXyzM[5]*b;
            double Z = sRgbToXyzM[6]*r + sRgbToXyzM[7]*g + sRgbToXyzM[8]*b;
            return (X, Y, Z);
        }

        private static (double r, double g, double b) XyzD65ToLinearRgb(double X, double Y, double Z)
        {
            double r = xyzToSRgbM[0]*X + xyzToSRgbM[1]*Y + xyzToSRgbM[2]*Z;
            double g = xyzToSRgbM[3]*X + xyzToSRgbM[4]*Y + xyzToSRgbM[5]*Z;
            double b = xyzToSRgbM[6]*X + xyzToSRgbM[7]*Y + xyzToSRgbM[8]*Z;
            return (r, g, b);
        }

        // D65 reference white for Lab
        private const double LabXn = 0.95047;
        private const double LabYn = 1.00000;
        private const double LabZn = 1.08883;

        private static double LabF(double t)
        {
            const double delta = 6.0 / 29.0;
            const double delta2 = delta * delta;
            const double delta3 = delta2 * delta;
            return t > delta3 ? Math.Pow(t, 1.0 / 3.0) : t / (3 * delta2) + 4.0 / 29.0;
        }

        private static double LabFInv(double t)
        {
            const double delta = 6.0 / 29.0;
            const double delta2 = delta * delta;
            double t3 = t * t * t;
            return t3 > delta2 * delta ? t3 : 3 * delta2 * (t - 4.0 / 29.0);
        }

        private static double[] XyzToLab(double X, double Y, double Z)
        {
            double fy = LabF(Y / LabYn);
            double fx = LabF(X / LabXn);
            double fz = LabF(Z / LabZn);
            double L = 116.0 * fy - 16.0;
            double a = 500.0 * (fx - fy);
            double b = 200.0 * (fy - fz);
            return new[] { L, a, b };
        }

        private static (double X, double Y, double Z) LabToXyz(double L, double a, double b)
        {
            double fy = (L + 16.0) / 116.0;
            double fx = a / 500.0 + fy;
            double fz = fy - b / 200.0;
            double X = LabFInv(fx) * LabXn;
            double Y = LabFInv(fy) * LabYn;
            double Z = LabFInv(fz) * LabZn;
            return (X, Y, Z);
        }

        private static double[] LabToLch(double L, double a, double b)
        {
            double C = Math.Sqrt(a * a + b * b);
            double H = Math.Atan2(b, a) * 180.0 / Math.PI;
            if (H < 0) H += 360.0;
            return new[] { L, C, H };
        }

        private static (double L, double a, double b) LchToLab(double L, double C, double H)
        {
            double hRad = H * Math.PI / 180.0;
            double a = C * Math.Cos(hRad);
            double b = C * Math.Sin(hRad);
            return (L, a, b);
        }

        // Oklab matrices (from sRGB-linear)
        private static readonly double[] M1 = {
            0.8189330101, 0.3618667424, -0.1288597137,
            0.0329845436, 0.9293118715,  0.0361456387,
            0.0482003018, 0.2643662691,  0.6338517070
        };

        private static readonly double[] M2 = {
             0.2104542553,  0.7936177850, -0.0040720468,
             1.9779984951, -2.4285922050,  0.4505937099,
             0.0259040371,  0.7827717662, -0.8086757660
        };

        private static readonly double[] M1Inv = {
             1.2270138511, -0.5577999807,  0.2812561490,
            -0.0405801784,  1.1122568696, -0.0716766787,
            -0.0763812845, -0.4214819784,  1.5861632204
        };

        private static readonly double[] M2Inv = {
             1.0000000000,  0.3963377774,  0.2158037573,
             1.0000000000, -0.1055613458, -0.0638541728,
             1.0000000000, -0.0894841775, -1.2914855480
        };

        private static (double L, double a, double b) LinearRgbToOklab(double r, double g, double bl)
        {
            double lms0 = M1[0]*r + M1[1]*g + M1[2]*bl;
            double lms1 = M1[3]*r + M1[4]*g + M1[5]*bl;
            double lms2 = M1[6]*r + M1[7]*g + M1[8]*bl;
            double l0 = Math.Pow(lms0, 1.0 / 3.0);
            double l1 = Math.Pow(lms1, 1.0 / 3.0);
            double l2 = Math.Pow(lms2, 1.0 / 3.0);
            double L = M2[0]*l0 + M2[1]*l1 + M2[2]*l2;
            double a = M2[3]*l0 + M2[4]*l1 + M2[5]*l2;
            double b = M2[6]*l0 + M2[7]*l1 + M2[8]*l2;
            return (L, a, b);
        }

        private static (double r, double g, double b) OklabToLinearRgb(double L, double a, double b)
        {
            double l0 = M2Inv[0]*L + M2Inv[1]*a + M2Inv[2]*b;
            double l1 = M2Inv[3]*L + M2Inv[4]*a + M2Inv[5]*b;
            double l2 = M2Inv[6]*L + M2Inv[7]*a + M2Inv[8]*b;
            double lms0 = l0 * l0 * l0;
            double lms1 = l1 * l1 * l1;
            double lms2 = l2 * l2 * l2;
            double r = M1Inv[0]*lms0 + M1Inv[1]*lms1 + M1Inv[2]*lms2;
            double g = M1Inv[3]*lms0 + M1Inv[4]*lms1 + M1Inv[5]*lms2;
            double bl = M1Inv[6]*lms0 + M1Inv[7]*lms1 + M1Inv[8]*lms2;
            return (r, g, bl);
        }

        // Display-P3 linear matrices (via XYZ-D65)
        private static readonly double[] xyzToP3M = {
             2.4934969119, -0.9313836179, -0.4027107845,
            -0.8294889696,  1.7626640603,  0.0236246858,
             0.0358458302, -0.0761723893,  0.9568845240
        };

        private static readonly double[] p3ToXyzM = {
             0.4865709486, 0.2656676932, 0.1982172852,
             0.2289745641, 0.6917385218, 0.0792869141,
             0.0000000000, 0.0451133819, 1.0439443689
        };

        private static (double r, double g, double b) LinearRgbToP3Linear(double r, double g, double b)
        {
            var (X, Y, Z) = LinearRgbToXyzD65(r, g, b);
            double rp = xyzToP3M[0]*X + xyzToP3M[1]*Y + xyzToP3M[2]*Z;
            double gp = xyzToP3M[3]*X + xyzToP3M[4]*Y + xyzToP3M[5]*Z;
            double bp = xyzToP3M[6]*X + xyzToP3M[7]*Y + xyzToP3M[8]*Z;
            return (rp, gp, bp);
        }

        private static (double r, double g, double b) P3LinearToLinearRgb(double r, double g, double b)
        {
            double X = p3ToXyzM[0]*r + p3ToXyzM[1]*g + p3ToXyzM[2]*b;
            double Y = p3ToXyzM[3]*r + p3ToXyzM[4]*g + p3ToXyzM[5]*b;
            double Z = p3ToXyzM[6]*r + p3ToXyzM[7]*g + p3ToXyzM[8]*b;
            return XyzD65ToLinearRgb(X, Y, Z);
        }

        // sRGB → HSL (returns H 0-360, S 0-1, L 0-1)
        private static (double H, double S, double L) SrgbToHsl(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double L = (max + min) / 2.0;
            if (Math.Abs(max - min) < 1e-10) return (0, 0, L);
            double d = max - min;
            double S = L > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            double H;
            if (Math.Abs(max - r) < 1e-10)
                H = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (Math.Abs(max - g) < 1e-10)
                H = (b - r) / d + 2.0;
            else
                H = (r - g) / d + 4.0;
            H *= 60.0;
            return (H, S, L);
        }

        // sRGB → HWB (returns H 0-360, W 0-1, B 0-1)
        private static (double H, double W, double B) SrgbToHwb(double r, double g, double b)
        {
            var (H, _, L) = SrgbToHsl(r, g, b);
            double W = Math.Min(r, Math.Min(g, b));
            double Bk = 1.0 - Math.Max(r, Math.Max(g, b));
            return (H, W, Bk);
        }

        private static double HueInterpolation(double h1, double h2, double t, string hueMethod)
        {
            double diff = h2 - h1;
            switch (hueMethod ?? "shorter")
            {
                case "shorter":
                    if (diff > 180) h1 += 360;
                    else if (diff < -180) h2 += 360;
                    break;
                case "longer":
                    if (diff > 0 && diff < 180) h1 += 360;
                    else if (diff < 0 && diff > -180) h2 += 360;
                    break;
                case "increasing":
                    while (h2 < h1) h2 += 360;
                    break;
                case "decreasing":
                    while (h2 > h1) h2 -= 360;
                    break;
            }
            return (h1 + (h2 - h1) * t) % 360.0;
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Parse "in srgb", "in oklch", "in lch shorter hue", etc.
        /// </summary>
        private static (string colorSpace, string hueMethod) ParseColorMixSpace(string spec)
        {
            string colorSpace = "srgb";
            string hueMethod = null;
            spec = spec.Trim();
            if (spec.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
                spec = spec.Substring(3).Trim();

            var tokens = new List<string>(spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (tokens.Count == 0) return ("srgb", null);

            colorSpace = tokens[0].ToLowerInvariant();

            // Check for hue method keywords
            for (int i = 1; i < tokens.Count; i++)
            {
                var t = tokens[i].ToLowerInvariant();
                if (t == "shorter" || t == "longer" || t == "increasing" || t == "decreasing")
                {
                    hueMethod = t;
                    break;
                }
            }

            return (colorSpace, hueMethod);
        }

        /// <summary>
        /// Convert an SKColor (sRGB bytes) to a double[] representation in the target color space.
        /// </summary>
        private static double[] ConvertColorForInterpolation(SKColor color, string colorSpace)
        {
            double r = color.Red / 255.0;
            double g = color.Green / 255.0;
            double b = color.Blue / 255.0;

            switch (colorSpace)
            {
                case "srgb":
                    return new[] { r, g, b };

                case "srgb-linear":
                    return new[] { SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b) };

                case "xyz-d65":
                case "xyz":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (X, Y, Z) = LinearRgbToXyzD65(rl, gl, bl);
                        return new[] { X, Y, Z };
                    }

                case "xyz-d50":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (X65, Y65, Z65) = LinearRgbToXyzD65(rl, gl, bl);
                        // Bradford adaptation D65 -> D50 (simplified)
                        double X50 =  1.0479297 * X65 + 0.0229466 * Y65 - 0.0501922 * Z65;
                        double Y50 =  0.0296278 * X65 + 0.9904344 * Y65 - 0.0170738 * Z65;
                        double Z50 = -0.0092430 * X65 + 0.0150550 * Y65 + 0.7518740 * Z65;
                        return new[] { X50, Y50, Z50 };
                    }

                case "lab":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (X, Y, Z) = LinearRgbToXyzD65(rl, gl, bl);
                        return XyzToLab(X, Y, Z);
                    }

                case "lch":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (X, Y, Z) = LinearRgbToXyzD65(rl, gl, bl);
                        var lab = XyzToLab(X, Y, Z);
                        return LabToLch(lab[0], lab[1], lab[2]);
                    }

                case "oklab":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (L, a, bb) = LinearRgbToOklab(rl, gl, bl);
                        return new[] { L, a, bb };
                    }

                case "oklch":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (L, a, bb) = LinearRgbToOklab(rl, gl, bl);
                        var lch = LabToLch(L, a, bb);
                        return lch;
                    }

                case "hsl":
                    {
                        var (H, S, L) = SrgbToHsl(r, g, b);
                        return new[] { H, S, L };
                    }

                case "hwb":
                    {
                        var (H, W, Bk) = SrgbToHwb(r, g, b);
                        return new[] { H, W, Bk };
                    }

                case "display-p3":
                    {
                        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
                        var (rp, gp, bp) = LinearRgbToP3Linear(rl, gl, bl);
                        return new[] { rp, gp, bp };
                    }

                default:
                    return new[] { r, g, b };
            }
        }

        /// <summary>
        /// Linearly mix two color arrays in the given color space.
        /// For hue-bearing spaces, the hue component (always index 0 in our convention)
        /// is interpolated with the specified hue method.
        /// </summary>
        private static double[] MixInColorSpace(double[] c1, double[] c2, double w1, string colorSpace, string hueMethod)
        {
            double w2 = 1.0 - w1;
            bool hasHue = colorSpace == "hsl" || colorSpace == "hwb" || colorSpace == "lch" || colorSpace == "oklch";
            var result = new double[c1.Length];

            for (int i = 0; i < c1.Length; i++)
            {
                if (hasHue && i == 0)
                    result[i] = HueInterpolation(c1[i], c2[i], w2, hueMethod);
                else
                    result[i] = Lerp(c1[i], c2[i], w2);
            }
            return result;
        }

        /// <summary>
        /// Convert a mixed double[] from any interpolation space back to sRGB (byte r, g, b).
        /// </summary>
        private static (byte r, byte g, byte b) InterpolationResultToSrgb(double[] comps, string colorSpace)
        {
            double r, g, b;

            switch (colorSpace)
            {
                case "srgb":
                    r = comps[0]; g = comps[1]; b = comps[2];
                    break;

                case "srgb-linear":
                    r = LinearToSrgbComponent(comps[0]);
                    g = LinearToSrgbComponent(comps[1]);
                    b = LinearToSrgbComponent(comps[2]);
                    break;

                case "xyz-d65":
                case "xyz":
                    {
                        var (rr, gg, bb) = XyzD65ToLinearRgb(comps[0], comps[1], comps[2]);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "xyz-d50":
                    {
                        // Bradford adaptation D50 -> D65 (simplified)
                        double X50 = comps[0], Y50 = comps[1], Z50 = comps[2];
                        double X65 =  0.9554734 * X50 - 0.0230985 * Y50 + 0.0632596 * Z50;
                        double Y65 = -0.0283697 * X50 + 1.0099938 * Y50 + 0.0210408 * Z50;
                        double Z65 =  0.0123120 * X50 - 0.0204753 * Y50 + 1.3300598 * Z50;
                        var (rr, gg, bb) = XyzD65ToLinearRgb(X65, Y65, Z65);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "lab":
                    {
                        var (X, Y, Z) = LabToXyz(comps[0], comps[1], comps[2]);
                        var (rr, gg, bb) = XyzD65ToLinearRgb(X, Y, Z);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "lch":
                    {
                        var (La, aa, bba) = LchToLab(comps[0], comps[1], comps[2]);
                        var (X, Y, Z) = LabToXyz(La, aa, bba);
                        var (rr, gg, bb) = XyzD65ToLinearRgb(X, Y, Z);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "oklab":
                    {
                        var (rr, gg, bb) = OklabToLinearRgb(comps[0], comps[1], comps[2]);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "oklch":
                    {
                        var (La, aa, bba) = LchToLab(comps[0], comps[1], comps[2]);
                        var (rr, gg, bb) = OklabToLinearRgb(La, aa, bba);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                case "hsl":
                    {
                        var rgbBytes = HslToRgb(comps[0], comps[1], comps[2]);
                        return rgbBytes;
                    }

                case "hwb":
                    {
                        var rgbBytes = HwbToRgb(comps[0], comps[1], comps[2]);
                        return rgbBytes;
                    }

                case "display-p3":
                    {
                        var (rr, gg, bb) = P3LinearToLinearRgb(comps[0], comps[1], comps[2]);
                        r = LinearToSrgbComponent(rr);
                        g = LinearToSrgbComponent(gg);
                        b = LinearToSrgbComponent(bb);
                    }
                    break;

                default:
                    r = comps[0]; g = comps[1]; b = comps[2];
                    break;
            }

            return (
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(r * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(g * 255))),
                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(b * 255)))
            );
        }

    }
}


