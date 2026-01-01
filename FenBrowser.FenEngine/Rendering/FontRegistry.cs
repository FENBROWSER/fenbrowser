using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Registry for @font-face definitions. Maps font-family names to font sources.
    /// Supports url() and local() font sources with font-weight and font-style matching.
    /// Manages async downloading of fonts.
    /// </summary>
    public static class FontRegistry
    {
        private static readonly Dictionary<string, List<FontFaceDescriptor>> _fontFaces 
            = new Dictionary<string, List<FontFaceDescriptor>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, SKTypeface> _loadedFonts 
            = new Dictionary<string, SKTypeface>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Task<SKTypeface>> _loadingTasks 
            = new Dictionary<string, Task<SKTypeface>>(StringComparer.OrdinalIgnoreCase);

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
            public SKFontStyleSlant Style { get; set; } = SKFontStyleSlant.Upright;
            public string UnicodeRange { get; set; }    // Optional unicode-range
            public string Display { get; set; } = "auto"; 
            public string Stretch { get; set; }         
            public string FeatureSettings { get; set; } 
            public string VariationSettings { get; set; } 
        }

        /// <summary>
        /// Register a @font-face rule (legacy compatibility)
        /// </summary>
        public static void Register(string familyName, string uri)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(uri)) return;
            // Legacy direct registration - assumes local file or system font for now
             lock (_lock)
            {
                // If it's a file path, load it? For now, just trust SKTypeface.
                _loadedFonts[familyName.Trim().Trim('"', '\'')] = SKTypeface.FromFamilyName(uri);
            }
        }

        /// <summary>
        /// Register a @font-face descriptor and trigger load if needed.
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

            // Trigger load in background
            if (!string.IsNullOrEmpty(descriptor.Source))
            {
                FenLogger.Debug($"[FontRegistry] Registering font: {descriptor.Family}", LogCategory.Rendering);
                _ = LoadFontFaceAsync(descriptor);
            }
        }

        public static async Task LoadPendingFontsAsync()
        {
            List<Task<SKTypeface>> tasks;
            lock (_lock)
            {
                tasks = _loadingTasks.Values.ToList();
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private static async Task<SKTypeface> LoadFontFaceAsync(FontFaceDescriptor descriptor)
        {
            string src = descriptor.Source;
            if (string.IsNullOrEmpty(src)) return null;

            // Check if already loading/loaded
            string cacheKey = $"{descriptor.Family}|{descriptor.Weight}|{descriptor.Style}";
            Task<SKTypeface> existingTask = null;
            lock (_lock)
            {
                 if (_loadedFonts.ContainsKey(cacheKey)) return _loadedFonts[cacheKey];
                 if (_loadingTasks.TryGetValue(cacheKey, out existingTask)) 
                 {
                     // Found existing task, will await outside lock
                 }
            }
            if (existingTask != null) return await existingTask;

            var tcs = new TaskCompletionSource<SKTypeface>();
            lock (_lock) _loadingTasks[cacheKey] = tcs.Task;

            try
            {
                SKTypeface typeface = null;

                // local() check
                var localMatch = Regex.Match(src, @"local\s*\(\s*([""']?)([^)""']+)\1\s*\)", RegexOptions.IgnoreCase);
                if (localMatch.Success)
                {
                    string localName = localMatch.Groups[2].Value;
                    typeface = SKTypeface.FromFamilyName(localName, 
                        (SKFontStyleWeight)descriptor.Weight, 
                        SKFontStyleWidth.Normal, 
                        descriptor.Style);
                }
                else
                {
                    // url() check - assumed if not local
                    // Sanitize url(...) wrapper if present (CssLoader might pass raw src string)
                    string url = src;
                    var urlMatch = Regex.Match(src, @"url\s*\(\s*([""']?)([^)""']+)\1\s*\)", RegexOptions.IgnoreCase);
                    if (urlMatch.Success) url = urlMatch.Groups[2].Value;

                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var httpClient = HttpClientFactory.GetSharedClient();
                        using (var stream = await httpClient.GetStreamAsync(uri))
                        using (var ms = new MemoryStream())
                        {
                            await stream.CopyToAsync(ms);
                            ms.Position = 0;
                            typeface = SKTypeface.FromStream(ms);
                        }
                    }
                }

                if (typeface != null)
                {
                    lock (_lock)
                    {
                        _loadedFonts[cacheKey] = typeface;
                        // Also map family name directly if it's the first/only one
                        if (!_loadedFonts.ContainsKey(descriptor.Family))
                            _loadedFonts[descriptor.Family] = typeface;
                    }
                    FenLogger.Debug($"[FontRegistry] Loaded font: {descriptor.Family} ({typeface.FamilyName})", LogCategory.Rendering);
                    tcs.SetResult(typeface);
                    return typeface;
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[FontRegistry] Failed to load font {descriptor.Family}: {ex.Message}", LogCategory.Rendering, ex);
            }

            tcs.SetResult(null);
            return null;
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
                    descriptor.Source = srcMatch.Groups[1].Value.Trim();
                    // We let LoadFontFaceAsync handle the url()/local() parsing details
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
                    if (styleVal == "italic") descriptor.Style = SKFontStyleSlant.Italic;
                    else if (styleVal == "oblique") descriptor.Style = SKFontStyleSlant.Oblique;
                    else descriptor.Style = SKFontStyleSlant.Upright;
                }

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
        public static SKTypeface TryResolve(string familyName, int weight = 400, SKFontStyleSlant style = SKFontStyleSlant.Upright)
        {
            if (string.IsNullOrEmpty(familyName))
                return null;

            familyName = familyName.Trim().Trim('"', '\'');

            lock (_lock)
            {
                // Check specific cache key first
                var cacheKey = $"{familyName}|{weight}|{style}";
                if (_loadedFonts.TryGetValue(cacheKey, out var cached))
                    return cached;

                // Check generic family name
                if (_loadedFonts.TryGetValue(familyName, out cached))
                    return cached;

                // If registered but not loaded, it might be loading or failed. 
                // We return null here which triggers fallback.
                // Ideally we'd have a way to know if it's "loading" to maybe show a placeholder.
                
                return null;
            }
        }

        /// <summary>
        /// Legacy single-argument resolve (uses default weight/style)
        /// </summary>
        public static SKTypeface TryResolve(string familyName)
        {
            return TryResolve(familyName, 400, SKFontStyleSlant.Upright);
        }

        public static bool IsRegistered(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return false;
            familyName = familyName.Trim().Trim('"', '\'');
            lock (_lock)
            {
                return _fontFaces.ContainsKey(familyName) || _loadedFonts.ContainsKey(familyName);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _fontFaces.Clear();
                _loadedFonts.Clear();
                _loadingTasks.Clear();
            }
        }
    }
}
