using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
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
        public sealed class FontLoaderRequestContext
        {
            public string OwnerId { get; init; }
            public Func<Uri, Task<BinaryFetchResult>> FetchDetailedAsync { get; init; }
            public Func<Uri, Document, Task<BinaryFetchResult>> FetchDetailedForDocumentAsync { get; init; }
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly FontLoaderRequestContext _previous;
            private bool _disposed;

            public ContextScope(FontLoaderRequestContext context)
            {
                _previous = _ambientContext.Value;
                _ambientContext.Value = context;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _ambientContext.Value = _previous;
                _disposed = true;
            }
        }

        private static readonly AsyncLocal<FontLoaderRequestContext> _ambientContext = new();

        public static Func<Uri, Task<BinaryFetchResult>> FetchDetailedAsync { get; set; }
        public static Func<Uri, Document, Task<BinaryFetchResult>> FetchDetailedForDocumentAsync { get; set; }

        private static readonly Dictionary<string, List<FontFaceDescriptor>> _fontFaces 
            = new Dictionary<string, List<FontFaceDescriptor>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, SKTypeface> _loadedFonts 
            = new Dictionary<string, SKTypeface>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Task<SKTypeface>> _loadingTasks 
            = new Dictionary<string, Task<SKTypeface>>(StringComparer.Ordinal);

        private static readonly HashSet<string> _failedFonts
            = new HashSet<string>(StringComparer.Ordinal);

        // Keyed by the resolved font URL rather than by the @font-face rule that
        // asked for it, so one file is fetched and decoded once however many
        // rules or documents name it.
        private static readonly Dictionary<string, SKTypeface> _typefacesByUrl
            = new Dictionary<string, SKTypeface>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Task<SKTypeface>> _typefaceLoadsByUrl
            = new Dictionary<string, Task<SKTypeface>>(StringComparer.Ordinal);

        // Files that downloaded fine but Skia could not decode - WOFF2, for one.
        // That answer cannot change on a retry, so remember it and stop paying
        // for the download again on every rule and every document.
        private static readonly HashSet<string> _undecodableFontUrls
            = new HashSet<string>(StringComparer.Ordinal);

        private static readonly object _lock = new object();

        public static event Action<string> FontLoaded;
        public static event Action<int> PendingLoadCountChanged;

        public static IDisposable EnterRequestContext(FontLoaderRequestContext context)
        {
            return new ContextScope(context);
        }

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
            public Document OwnerDocument { get; set; }
            internal FontLoaderRequestContext RequestContext { get; set; }
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

            descriptor.RequestContext ??= CreateDocumentRequestContext(
                _ambientContext.Value ?? CreateFallbackRequestContext(),
                descriptor.OwnerDocument);

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

        /// <summary>
        /// Downloads and decodes a font file at most once per URL, and hands the
        /// same typeface to every rule that names it. The load cache above is
        /// keyed by the @font-face rule — family, weight, style, unicode-range
        /// and the document's base URI — so a file shared between rules, or a
        /// family a second document declares again, was fetched and decoded once
        /// per rule. Google's robot check asked for nine Roboto subsets
        /// twenty-seven times for the anchor frame, then all nine again for the
        /// challenge frame. Typefaces are immutable and never disposed here, so
        /// sharing one instance is safe.
        /// </summary>
        private static async Task<SKTypeface> LoadTypefaceFromUrlAsync(Uri uri, FontLoaderRequestContext context)
        {
            var key = uri.AbsoluteUri;
            TaskCompletionSource<SKTypeface> completion = null;
            Task<SKTypeface> pending;

            lock (_lock)
            {
                if (_typefacesByUrl.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                if (_undecodableFontUrls.Contains(key))
                {
                    return null;
                }

                if (!_typefaceLoadsByUrl.TryGetValue(key, out pending))
                {
                    completion = new TaskCompletionSource<SKTypeface>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = completion.Task;
                    _typefaceLoadsByUrl[key] = pending;
                }
            }

            if (completion == null)
            {
                return await pending.ConfigureAwait(false);
            }

            SKTypeface typeface = null;
            var undecodable = false;
            try
            {
                var fetcher = context?.FetchDetailedAsync;
                if (fetcher == null)
                {
                    EngineLogCompat.Warn(
                        $"[FontRegistry] No browser font fetcher is configured for {uri.Host}",
                        LogCategory.Network);
                }
                else
                {
                    var result = await fetcher(uri).ConfigureAwait(false);
                    if (result?.Succeeded == true && result.Body != null && result.Body.Length > 0)
                    {
                        using var stream = new MemoryStream(result.Body, writable: false);
                        typeface = SKTypeface.FromStream(stream);
                        undecodable = typeface == null;
                        if (undecodable)
                        {
                            EngineLogCompat.Log(
                                LogCategory.Rendering,
                                LogLevel.Debug,
                                $"[FontRegistry] Font downloaded but not decodable: {uri}");
                        }
                    }
                    else
                    {
                        EngineLogCompat.Log(
                            LogCategory.Network,
                            LogLevel.Debug,
                            $"[FontRegistry] Font fetch failed: host={uri.Host} reason={result?.FailureReason}");
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn(
                    $"[FontRegistry] Font fetch threw for {uri.Host}: {ex.Message}",
                    LogCategory.Network);
            }
            finally
            {
                lock (_lock)
                {
                    _typefaceLoadsByUrl.Remove(key);
                    if (typeface != null)
                    {
                        _typefacesByUrl[key] = typeface;
                    }
                    else if (undecodable)
                    {
                        // Only a decode failure is remembered. A transport
                        // failure may be transient, so leave that one retryable.
                        _undecodableFontUrls.Add(key);
                    }
                }

                completion.TrySetResult(typeface);
            }

            return typeface;
        }

        private static async Task<SKTypeface> LoadFontFaceAsync(FontFaceDescriptor descriptor)
        {
            string src = descriptor.Source;
            if (string.IsNullOrEmpty(src)) return null;

            string cacheKey = BuildLoadCacheKey(descriptor);
            string failureCacheKey = BuildFailureCacheKey(descriptor, cacheKey);
            TaskCompletionSource<SKTypeface> completion = null;
            Task<SKTypeface> loadTask;
            lock (_lock)
            {
                if (_loadedFonts.TryGetValue(cacheKey, out var loaded))
                {
                    return loaded;
                }

                if (_failedFonts.Contains(failureCacheKey))
                {
                    return null;
                }

                if (_loadingTasks.TryGetValue(cacheKey, out loadTask))
                {
                    completion = null;
                }
                else
                {
                    completion = new TaskCompletionSource<SKTypeface>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    loadTask = completion.Task;
                    _loadingTasks.Add(cacheKey, loadTask);
                }
            }

            if (completion == null)
            {
                return await loadTask.ConfigureAwait(false);
            }

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

                        Uri uri = null;

                        // Handle protocol-relative URLs manually to prevent Windows interpreting them as UNC file paths
                        if (sourceUrl.StartsWith("//") && descriptor.BaseUri != null)
                        {
                            Uri.TryCreate(descriptor.BaseUri, sourceUrl, out uri);
                        }
                        else if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out uri) && descriptor.BaseUri != null)
                        {
                            Uri.TryCreate(descriptor.BaseUri, sourceUrl, out uri);
                        }

                        if (uri == null)
                        {
                            continue;
                        }

                        if (uri.Scheme == "http" || uri.Scheme == "https")
                        {
                            typeface = await LoadTypefaceFromUrlAsync(uri, descriptor.RequestContext)
                                .ConfigureAwait(false);
                            if (typeface == null)
                            {
                                continue;
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
                    completion.TrySetResult(typeface);
                    try { FontLoaded?.Invoke(descriptor.Family); } catch (Exception ex) { EngineLogCompat.Warn($"[FontRegistry] FontLoaded callback failed: {ex.Message}", LogCategory.Rendering); }
                    return typeface;
                }

                lock (_lock)
                {
                    _failedFonts.Add(failureCacheKey);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[FontRegistry] Failed to load font {descriptor.Family}: {ex.Message}", LogCategory.Rendering, ex);
                lock (_lock)
                {
                    _failedFonts.Add(failureCacheKey);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (_loadingTasks.TryGetValue(cacheKey, out var currentTask) &&
                        ReferenceEquals(currentTask, loadTask))
                    {
                        _loadingTasks.Remove(cacheKey);
                    }
                }
                NotifyPendingLoadCountChanged();
            }

            completion.TrySetResult(null);
            return null;
        }

        private static string BuildLoadCacheKey(FontFaceDescriptor descriptor)
        {
            var family = descriptor.Family?.Trim().Trim('"', '\'').ToUpperInvariant() ?? string.Empty;
            var baseUri = descriptor.BaseUri?.AbsoluteUri ?? string.Empty;
            var source = descriptor.Source?.Trim() ?? string.Empty;
            var unicodeRange = descriptor.UnicodeRange?.Trim() ?? string.Empty;
            var stretch = descriptor.Stretch?.Trim() ?? string.Empty;
            return $"{family}|{descriptor.Weight}|{descriptor.Style}|{stretch}|{unicodeRange}|{baseUri}|{source}";
        }

        private static string BuildFailureCacheKey(FontFaceDescriptor descriptor, string loadCacheKey)
        {
            var requestOwner = descriptor.RequestContext?.OwnerId ?? "_default";
            return $"{requestOwner}|{loadCacheKey}";
        }

        private static FontLoaderRequestContext CreateFallbackRequestContext()
        {
            if (FetchDetailedAsync == null && FetchDetailedForDocumentAsync == null)
            {
                return null;
            }

            return new FontLoaderRequestContext
            {
                OwnerId = "_global",
                FetchDetailedAsync = FetchDetailedAsync,
                FetchDetailedForDocumentAsync = FetchDetailedForDocumentAsync
            };
        }

        private static FontLoaderRequestContext CreateDocumentRequestContext(
            FontLoaderRequestContext context,
            Document ownerDocument)
        {
            if (context == null || ownerDocument == null || context.FetchDetailedForDocumentAsync == null)
            {
                return context;
            }

            return new FontLoaderRequestContext
            {
                OwnerId = $"{context.OwnerId}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ownerDocument)}",
                FetchDetailedAsync = uri => context.FetchDetailedForDocumentAsync(uri, ownerDocument),
                FetchDetailedForDocumentAsync = context.FetchDetailedForDocumentAsync
            };
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
        public static void ParseAndRegister(
            string fontFaceBlock,
            Uri baseUri = null,
            Document ownerDocument = null)
        {
            if (string.IsNullOrWhiteSpace(fontFaceBlock))
                return;

            try
            {
                var descriptor = new FontFaceDescriptor
                {
                    BaseUri = baseUri,
                    OwnerDocument = ownerDocument
                };

                // Parse font-family
                string family = ExtractLastCssPropertyValue(fontFaceBlock, "font-family");
                if (!string.IsNullOrEmpty(family))
                {
                    descriptor.Family = family.Trim('\'', '"', ' ');
                }

                // Parse src. Keep the last declaration so fallback lists that override
                // an earlier legacy src (for example EOT) are honored.
                string src = ExtractLastCssPropertyValue(fontFaceBlock, "src");
                if (!string.IsNullOrEmpty(src))
                {
                    descriptor.Source = src;
                }

                // Parse font-weight
                string weightVal = ExtractLastCssPropertyValue(fontFaceBlock, "font-weight");
                if (!string.IsNullOrEmpty(weightVal))
                {
                    weightVal = weightVal.ToLowerInvariant();
                    if (weightVal == "normal") descriptor.Weight = 400;
                    else if (weightVal == "bold") descriptor.Weight = 700;
                    else if (weightVal == "lighter") descriptor.Weight = 300;
                    else if (weightVal == "bolder") descriptor.Weight = 700;
                    else if (int.TryParse(weightVal, out var w)) descriptor.Weight = w;
                }

                // Parse font-style
                string styleVal = ExtractLastCssPropertyValue(fontFaceBlock, "font-style");
                if (!string.IsNullOrEmpty(styleVal))
                {
                    styleVal = styleVal.ToLowerInvariant();
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
                _failedFonts.Clear();
                _typefacesByUrl.Clear();
                _typefaceLoadsByUrl.Clear();
                _undecodableFontUrls.Clear();
            }
            NotifyPendingLoadCountChanged();
        }

        public static void ClearDocument(Document document)
        {
            if (document == null)
            {
                return;
            }

            var ownerSuffix = $":{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(document)}";
            lock (_lock)
            {
                foreach (var family in _fontFaces.Keys.ToList())
                {
                    var descriptors = _fontFaces[family];
                    descriptors.RemoveAll(face => ReferenceEquals(face?.OwnerDocument, document));
                    if (descriptors.Count == 0)
                    {
                        _fontFaces.Remove(family);
                    }
                }

                _failedFonts.RemoveWhere(key =>
                {
                    var separator = key.IndexOf('|');
                    return separator > 0 &&
                           key.Substring(0, separator).EndsWith(ownerSuffix, StringComparison.Ordinal);
                });
            }
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

        private static string ExtractLastCssPropertyValue(string block, string propertyName)
        {
            string result = null;
            int idx = 0;
            while ((idx = block.IndexOf(propertyName, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (idx > 0 && char.IsLetterOrDigit(block[idx - 1]))
                {
                    idx += propertyName.Length;
                    continue;
                }

                int colonIdx = block.IndexOf(':', idx + propertyName.Length);
                if (colonIdx >= 0)
                {
                    bool onlySpaces = true;
                    for (int j = idx + propertyName.Length; j < colonIdx; j++)
                    {
                        if (!char.IsWhiteSpace(block[j]))
                        {
                            onlySpaces = false;
                            break;
                        }
                    }

                    if (onlySpaces)
                    {
                        int semiIdx = block.IndexOf(';', colonIdx);
                        if (semiIdx < 0) semiIdx = block.Length;
                        result = block.Substring(colonIdx + 1, semiIdx - colonIdx - 1).Trim();
                    }
                }
                idx += propertyName.Length;
            }
            return result;
        }
    }
}

