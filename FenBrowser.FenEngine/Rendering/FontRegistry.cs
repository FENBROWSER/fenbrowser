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

        public static event Action<string> FontLoaded;
        public static event Action<int> PendingLoadCountChanged;

        public static int PendingLoadCount
        {
            get
            {
                lock (_lock)
                {
                    var count = 0;
                    foreach (var task in _loadingTasks.Values)
                    {
                        if (task != null && !task.IsCompleted)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

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
            public Uri BaseUri { get; set; } // Added for relative path resolution
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

            if (!string.IsNullOrEmpty(descriptor.Source))
            {
                EngineLogCompat.Debug($"[FontRegistry] Registering font: {descriptor.Family}", LogCategory.Rendering);
                // Start loading immediately so LoadPendingFontsAsync sees deterministic state.
                // Network operations remain async inside LoadFontFaceAsync.
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
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Error($"[FontRegistry] Error awaiting pending fonts: {ex.Message}", LogCategory.Rendering);
                }
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
            NotifyPendingLoadCountChanged();

            try
            {
                SKTypeface typeface = null;

                // Try all local(...) candidates in order.
                foreach (var localName in ExtractLocalSources(src))
                {
                    if (string.IsNullOrWhiteSpace(localName))
                    {
                        continue;
                    }

                    typeface = TryCreateLocalTypeface(localName, descriptor.Weight, descriptor.Style);

                    if (typeface != null)
                    {
                        break;
                    }
                }

                // If no local candidate worked, try url(...) candidates in order.
                if (typeface == null)
                {
                    foreach (var sourceUrl in ExtractUrlSources(src))
                    {
                        if (string.IsNullOrWhiteSpace(sourceUrl))
                        {
                            continue;
                        }

                        Uri uri;
                        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out uri) && descriptor.BaseUri != null)
                        {
                            Uri.TryCreate(descriptor.BaseUri, sourceUrl, out uri);
                        }

                        if (uri == null)
                        {
                            continue;
                        }

                        if (uri.Scheme == "http" || uri.Scheme == "https")
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
                        else if (uri.Scheme == "file")
                        {
                            try
                            {
                                typeface = SKTypeface.FromFile(uri.LocalPath);
                            }
                            catch
                            {
                                EngineLogCompat.Debug($"[FontRegistry] Cannot load font from file path: {uri.LocalPath}", LogCategory.Rendering);
                            }
                        }
                        else
                        {
                            EngineLogCompat.Debug($"[FontRegistry] Unsupported scheme '{uri.Scheme}' for font: {descriptor.Family}", LogCategory.Rendering);
                        }

                        if (typeface != null)
                        {
                            break;
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
                    EngineLogCompat.Debug($"[FontRegistry] Loaded font: {descriptor.Family} ({typeface.FamilyName})", LogCategory.Rendering);
                    tcs.SetResult(typeface);
                    try { FontLoaded?.Invoke(descriptor.Family); } catch (Exception ex) { EngineLogCompat.Warn($"[FontRegistry] FontLoaded callback failed: {ex.Message}", LogCategory.Rendering); }
                    return typeface;
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[FontRegistry] Failed to load font {descriptor.Family}: {ex.Message}", LogCategory.Rendering, ex);
            }
            finally
            {
                lock (_lock)
                {
                    _loadingTasks.Remove(cacheKey);
                }
                NotifyPendingLoadCountChanged();
            }

            tcs.SetResult(null);
            return null;
        }

        private static IEnumerable<string> ExtractLocalSources(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                yield break;
            }

            var matches = Regex.Matches(
                source,
                @"local\s*\(\s*([""']?)([^)""']+)\1\s*\)",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500));

            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    yield return match.Groups[2].Value.Trim();
                }
            }
        }

        private static IEnumerable<string> ExtractUrlSources(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                yield break;
            }

            var yielded = false;
            var matches = Regex.Matches(
                source,
                @"url\s*\(\s*([""']?)([^)""']+)\1\s*\)",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500));

            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    yielded = true;
                    yield return match.Groups[2].Value.Trim();
                }
            }

            // Support direct bare URL/path syntax when url(...) wrapper is omitted.
            if (!yielded)
            {
                yield return source.Trim();
            }
        }

        private static SKTypeface TryCreateLocalTypeface(string localName, int weight, SKFontStyleSlant style)
        {
            if (string.IsNullOrWhiteSpace(localName))
            {
                return null;
            }

            try
            {
                var styled = SKTypeface.FromFamilyName(
                    localName,
                    (SKFontStyleWeight)weight,
                    SKFontStyleWidth.Normal,
                    style);
                if (styled != null)
                {
                    return styled;
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[FontRegistry] Styled local typeface load failed for '{localName}': {ex.Message}", LogCategory.Rendering);
            }

            try
            {
                return SKTypeface.FromFamilyName(localName);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[FontRegistry] Local typeface load failed for '{localName}': {ex.Message}", LogCategory.Rendering);
                return null;
            }
        }

        /// <summary>
        /// Parse @font-face block and register it
        /// </summary>
        public static void ParseAndRegister(string fontFaceBlock, Uri baseUri = null)
        {
            if (string.IsNullOrWhiteSpace(fontFaceBlock))
                return;

            try
            {
                var descriptor = new FontFaceDescriptor { BaseUri = baseUri };

                // Parse font-family
                var familyMatch = Regex.Match(fontFaceBlock, @"font-family\s*:\s*([""']?)([^;""']+)\1", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
                if (familyMatch.Success)
                    descriptor.Family = familyMatch.Groups[2].Value.Trim();

                // Parse src
                var srcMatch = Regex.Match(fontFaceBlock, @"src\s*:\s*([^;]+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
                if (srcMatch.Success)
                {
                    descriptor.Source = srcMatch.Groups[1].Value.Trim();
                    // We let LoadFontFaceAsync handle the url()/local() parsing details
                }

                // Parse font-weight
                var weightMatch = Regex.Match(fontFaceBlock, @"font-weight\s*:\s*(\d+|normal|bold|lighter|bolder)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
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
                var styleMatch = Regex.Match(fontFaceBlock, @"font-style\s*:\s*(normal|italic|oblique)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
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

                // Generic Fallbacks
                string lower = familyName.ToLowerInvariant();
                string fallbackName = null;

                if (lower == "serif") fallbackName = "Times New Roman";
                else if (lower == "sans-serif") fallbackName = "Segoe UI"; // Primary for Windows
                else if (lower == "monospace") fallbackName = "Consolas";
                else if (lower == "cursive") fallbackName = "Comic Sans MS";
                else if (lower == "fantasy") fallbackName = "Impact";
                
                // Secondary Fallbacks for high-unicode support if primary fails or generic
                if (fallbackName == "Segoe UI") 
                {
                    // If Segoe UI is not available (rare on Windows) or for better unicode:
                    var alt = SKTypeface.FromFamilyName("Arial");
                    if (alt != null) _loadedFonts["sans-serif-alt"] = alt;
                }

                if (fallbackName != null)
                {
                     try 
                     {
                         // Try to load system font for fallback
                         var skTypeface = SKTypeface.FromFamilyName(
                            fallbackName, 
                            (SKFontStyleWeight)weight, 
                            SKFontStyleWidth.Normal, 
                            style);
                         
                         if (skTypeface != null)
                         {
                             _loadedFonts[familyName] = skTypeface; // Cache under generic name
                             return skTypeface;
                         }
                     }
                     catch (Exception ex) { EngineLogCompat.Warn($"[FontRegistry] Fallback font resolve failed for '{fallbackName}': {ex.Message}", LogCategory.Rendering); }
                }

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
            NotifyPendingLoadCountChanged();
        }

        private static void NotifyPendingLoadCountChanged()
        {
            var handler = PendingLoadCountChanged;
            if (handler == null)
            {
                return;
            }

            int count;
            lock (_lock)
            {
                count = 0;
                foreach (var task in _loadingTasks.Values)
                {
                    if (task != null && !task.IsCompleted)
                    {
                        count++;
                    }
                }
            }

            try
            {
                handler(count);
            }
            catch
            {
            }
        }
    }
}

