using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Registry for @font-face definitions. Maps font-family names to font sources.
    /// Supports url() and local() font sources with font-weight and font-style matching.
    /// </summary>
    public static class FontRegistry
    {
        private static readonly Dictionary<string, List<FontFaceDescriptor>> _fontFaces 
            = new Dictionary<string, List<FontFaceDescriptor>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, FontFamily> _loadedFonts 
            = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        /// <summary>
        /// Represents a parsed @font-face rule
        /// </summary>
        public class FontFaceDescriptor
        {
            public string Family { get; set; }
            public string Source { get; set; }          // url() or local() value
            public string Format { get; set; }          // woff2, woff, truetype, etc.
            public int Weight { get; set; } = 400;      // 100-900
            public FontStyle Style { get; set; } = FontStyle.Normal;
            public string UnicodeRange { get; set; }    // Optional unicode-range
            public string Display { get; set; } = "auto"; // auto, block, swap, fallback, optional
            public string Stretch { get; set; }         // normal, condensed, expanded, etc.
            public string FeatureSettings { get; set; } // font-feature-settings
            public string VariationSettings { get; set; } // font-variation-settings (for variable fonts)
        }

        /// <summary>
        /// Register a @font-face rule (legacy compatibility)
        /// </summary>
        public static void Register(string familyName, string uri)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(uri)) return;
            try 
            { 
                lock (_lock)
                {
                    _loadedFonts[familyName.Trim().Trim('"', '\'')] = new FontFamily(uri); 
                }
            } 
            catch { }
        }

        /// <summary>
        /// Register a @font-face descriptor
        /// </summary>
        public static void RegisterFontFace(FontFaceDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Family))
                return;

            lock (_lock)
            {
                if (!_fontFaces.TryGetValue(descriptor.Family, out var list))
                {
                    list = new List<FontFaceDescriptor>();
                    _fontFaces[descriptor.Family] = list;
                }
                list.Add(descriptor);
            }
        }

        /// <summary>
        /// Parse @font-face block and register it
        /// </summary>
        public static void ParseAndRegister(string fontFaceBlock)
        {
            if (string.IsNullOrWhiteSpace(fontFaceBlock))
                return;

            try
            {
                var descriptor = new FontFaceDescriptor();

                // Parse font-family
                var familyMatch = Regex.Match(fontFaceBlock, @"font-family\s*:\s*([""']?)([^;""']+)\1", RegexOptions.IgnoreCase);
                if (familyMatch.Success)
                    descriptor.Family = familyMatch.Groups[2].Value.Trim();

                // Parse src
                var srcMatch = Regex.Match(fontFaceBlock, @"src\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (srcMatch.Success)
                {
                    var srcValue = srcMatch.Groups[1].Value;
                    
                    // Extract url()
                    var urlMatch = Regex.Match(srcValue, @"url\s*\(\s*([""']?)([^)""']+)\1\s*\)", RegexOptions.IgnoreCase);
                    if (urlMatch.Success)
                        descriptor.Source = urlMatch.Groups[2].Value.Trim();

                    // Extract format() if present
                    var formatMatch = Regex.Match(srcValue, @"format\s*\(\s*([""']?)([^)""']+)\1\s*\)", RegexOptions.IgnoreCase);
                    if (formatMatch.Success)
                        descriptor.Format = formatMatch.Groups[2].Value.Trim().ToLowerInvariant();
                }

                // Parse font-weight
                var weightMatch = Regex.Match(fontFaceBlock, @"font-weight\s*:\s*(\d+|normal|bold|lighter|bolder)", RegexOptions.IgnoreCase);
                if (weightMatch.Success)
                {
                    var weightVal = weightMatch.Groups[1].Value.ToLowerInvariant();
                    if (weightVal == "normal") descriptor.Weight = 400;
                    else if (weightVal == "bold") descriptor.Weight = 700;
                    else if (weightVal == "lighter") descriptor.Weight = 300;
                    else if (weightVal == "bolder") descriptor.Weight = 700;
                    else if (int.TryParse(weightVal, out var w)) descriptor.Weight = w;
                }

                // Parse font-style
                var styleMatch = Regex.Match(fontFaceBlock, @"font-style\s*:\s*(normal|italic|oblique)", RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    var styleVal = styleMatch.Groups[1].Value.ToLowerInvariant();
                    if (styleVal == "italic") descriptor.Style = FontStyle.Italic;
                    else if (styleVal == "oblique") descriptor.Style = FontStyle.Oblique;
                    else descriptor.Style = FontStyle.Normal;
                }

                // Parse unicode-range (optional)
                var rangeMatch = Regex.Match(fontFaceBlock, @"unicode-range\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (rangeMatch.Success)
                    descriptor.UnicodeRange = rangeMatch.Groups[1].Value.Trim();

                // Parse font-display (auto, block, swap, fallback, optional)
                var displayMatch = Regex.Match(fontFaceBlock, @"font-display\s*:\s*(auto|block|swap|fallback|optional)", RegexOptions.IgnoreCase);
                if (displayMatch.Success)
                    descriptor.Display = displayMatch.Groups[1].Value.ToLowerInvariant();

                // Parse font-stretch (normal, condensed, expanded, etc.)
                var stretchMatch = Regex.Match(fontFaceBlock, @"font-stretch\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (stretchMatch.Success)
                    descriptor.Stretch = stretchMatch.Groups[1].Value.Trim();

                // Parse font-feature-settings
                var featureMatch = Regex.Match(fontFaceBlock, @"font-feature-settings\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (featureMatch.Success)
                    descriptor.FeatureSettings = featureMatch.Groups[1].Value.Trim();

                // Parse font-variation-settings (for variable fonts)
                var variationMatch = Regex.Match(fontFaceBlock, @"font-variation-settings\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (variationMatch.Success)
                    descriptor.VariationSettings = variationMatch.Groups[1].Value.Trim();

                if (!string.IsNullOrEmpty(descriptor.Family) && !string.IsNullOrEmpty(descriptor.Source))
                {
                    RegisterFontFace(descriptor);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FontRegistry] Error parsing @font-face: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to resolve a font-family name to a FontFamily object.
        /// </summary>
        public static FontFamily TryResolve(string familyName, int weight = 400, FontStyle style = FontStyle.Normal)
        {
            if (string.IsNullOrEmpty(familyName))
                return null;

            familyName = familyName.Trim().Trim('"', '\'');

            lock (_lock)
            {
                // Check if already loaded (legacy or cached)
                if (_loadedFonts.TryGetValue(familyName, out var cached))
                    return cached;

                var cacheKey = $"{familyName}|{weight}|{style}";
                if (_loadedFonts.TryGetValue(cacheKey, out cached))
                    return cached;

                // Check if registered via @font-face
                if (!_fontFaces.TryGetValue(familyName, out var descriptors) || descriptors.Count == 0)
                    return null;

                // Find best matching descriptor
                FontFaceDescriptor best = null;
                int bestWeightDiff = int.MaxValue;

                foreach (var desc in descriptors)
                {
                    if (desc.Style != style) continue;
                    var weightDiff = Math.Abs(desc.Weight - weight);
                    if (weightDiff < bestWeightDiff)
                    {
                        bestWeightDiff = weightDiff;
                        best = desc;
                    }
                }

                // If no style match, try any
                if (best == null)
                {
                    foreach (var desc in descriptors)
                    {
                        var weightDiff = Math.Abs(desc.Weight - weight);
                        if (weightDiff < bestWeightDiff)
                        {
                            bestWeightDiff = weightDiff;
                            best = desc;
                        }
                    }
                }

                if (best == null) return null;

                // Try to create FontFamily
                try
                {
                    FontFamily fontFamily = null;

                    // Check for local font reference
                    if (best.Source.StartsWith("local(", StringComparison.OrdinalIgnoreCase))
                    {
                        var localName = Regex.Match(best.Source, @"local\s*\(\s*([""']?)([^)""']+)\1\s*\)", RegexOptions.IgnoreCase);
                        if (localName.Success)
                            fontFamily = new FontFamily(localName.Groups[2].Value);
                    }
                    else
                    {
                        // For URL fonts, use family name (works if installed on system)
                        fontFamily = new FontFamily(familyName);
                    }

                    if (fontFamily != null)
                        _loadedFonts[cacheKey] = fontFamily;

                    return fontFamily;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Legacy single-argument resolve (uses default weight/style)
        /// </summary>
        public static FontFamily TryResolve(string familyName)
        {
            return TryResolve(familyName, 400, FontStyle.Normal);
        }

        /// <summary>
        /// Check if a font family has been registered
        /// </summary>
        public static bool IsRegistered(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return false;
            familyName = familyName.Trim().Trim('"', '\'');
            lock (_lock)
            {
                return _fontFaces.ContainsKey(familyName) || _loadedFonts.ContainsKey(familyName);
            }
        }

        /// <summary>
        /// Clear all registered fonts
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _fontFaces.Clear();
                _loadedFonts.Clear();
            }
        }
    }
}
