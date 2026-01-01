using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Image entry with metadata for memory tracking and LRU eviction
    /// </summary>
    internal class ImageCacheEntry
    {
        public SKBitmap Bitmap { get; set; }
        public long ByteSize { get; set; }
        public DateTime LastAccessed { get; set; }
        public bool IsLazy { get; set; }
    }

    /// <summary>
    /// Lazy image registration for viewport-based loading
    /// </summary>
    internal class LazyImageInfo
    {
        public string Url { get; set; }
        public SKRect ElementBounds { get; set; }
        public bool LoadStarted { get; set; }
    }

    public static class ImageLoader
    {
        // Main cache with metadata for memory tracking
        private static readonly ConcurrentDictionary<string, ImageCacheEntry> _memoryCache = 
            new ConcurrentDictionary<string, ImageCacheEntry>();
        
        // Legacy cache for backward compatibility
        private static readonly ConcurrentDictionary<string, SKBitmap> _legacyCache = 
            new ConcurrentDictionary<string, SKBitmap>();
        
        private static readonly HttpClient _httpClient;
        
        // ========== Lazy Loading Support ==========
        private static readonly ConcurrentDictionary<string, LazyImageInfo> _lazyRegistry = 
            new ConcurrentDictionary<string, LazyImageInfo>();
        private static readonly HashSet<string> _pendingLoads = new HashSet<string>();
        private static readonly object _pendingLock = new object();
        private static SKRect _currentViewport = SKRect.Empty;
        private static readonly SemaphoreSlim _loadSemaphore = new SemaphoreSlim(4); // Max concurrent loads
        
        // ========== Memory Management ==========
        private static long _currentCacheBytes = 0;
        private static readonly object _cacheLock = new object();
        
        // Debounce mechanism to prevent flickering from rapid repaint requests
        private static Timer _repaintDebounceTimer;
        private static readonly object _timerLock = new object();
        private static bool _repaintPending = false;
        private const int DEBOUNCE_DELAY_MS = 100;
        
        // RULE 3 & 5: SVG rendering through adapter with safety limits
        private static readonly ISvgRenderer _svgRenderer = new SvgSkiaRenderer();
        
        static ImageLoader()
        {
            _httpClient = FenBrowser.Core.Network.HttpClientFactory.GetSharedClient();
        }
        
        // Callback to request a repaint when image loads
        public static Action RequestRepaint { get; set; }
        
        // Callback to request a full re-layout when image dimensions are resolved
        public static Action RequestRelayout { get; set; }

        // ========== Memory Management Properties ==========
        
        /// <summary>
        /// Current memory usage by cached images in bytes
        /// </summary>
        public static long CurrentCacheBytes => _currentCacheBytes;
        
        /// <summary>
        /// Number of images currently cached
        /// </summary>
        public static int CacheCount => _memoryCache.Count + _legacyCache.Count;
        
        /// <summary>
        /// Number of images registered for lazy loading
        /// </summary>
        public static int LazyRegistryCount => _lazyRegistry.Count;

        // ========== Lazy Loading API ==========

        /// <summary>
        /// Register an image for lazy loading. Will not load until visible in viewport.
        /// </summary>
        public static void RegisterLazyImage(string url, SKRect elementBounds)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            // Already cached? No need to register as lazy
            if (_memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url)) return;
            
            _lazyRegistry[url] = new LazyImageInfo
            {
                Url = url,
                ElementBounds = elementBounds,
                LoadStarted = false
            };
            
            FenLogger.Debug($"[ImageLoader] Registered lazy image: {url.Substring(0, Math.Min(50, url.Length))}...", 
                           LogCategory.Rendering);
        }

        /// <summary>
        /// Update the current viewport bounds. Triggers loading of visible lazy images.
        /// </summary>
        public static void UpdateViewport(SKRect viewportBounds)
        {
            _currentViewport = viewportBounds;
            
            // Check which lazy images are now visible
            var config = NetworkConfiguration.Instance;
            var threshold = config.LazyLoadThresholdPx;
            var expandedViewport = new SKRect(
                viewportBounds.Left - threshold,
                viewportBounds.Top - threshold,
                viewportBounds.Right + threshold,
                viewportBounds.Bottom + threshold
            );
            
            foreach (var kvp in _lazyRegistry)
            {
                if (kvp.Value.LoadStarted) continue;
                
                // Check if element is within expanded viewport
                if (expandedViewport.IntersectsWith(kvp.Value.ElementBounds))
                {
                    kvp.Value.LoadStarted = true;
                    LoadImageAsync(kvp.Key, isLazy: true);
                }
            }
        }

        /// <summary>
        /// Check if an image should be lazy loaded (not yet visible)
        /// </summary>
        public static bool IsLazyPending(string url)
        {
            return _lazyRegistry.TryGetValue(url, out var info) && !info.LoadStarted;
        }

        /// <summary>
        /// Clear all lazy registrations (call on navigation)
        /// </summary>
        public static void ClearLazyRegistry()
        {
            _lazyRegistry.Clear();
            lock (_pendingLock)
            {
                _pendingLoads.Clear();
            }
        }

        // ========== Memory Management API ==========

        /// <summary>
        /// Clear all cached images and reset memory tracking
        /// </summary>
        public static void ClearCache()
        {
            foreach (var entry in _memoryCache.Values)
            {
                entry.Bitmap?.Dispose();
            }
            _memoryCache.Clear();
            
            foreach (var bitmap in _legacyCache.Values)
            {
                bitmap?.Dispose();
            }
            _legacyCache.Clear();
            
            lock (_cacheLock)
            {
                _currentCacheBytes = 0;
            }
            
            ClearLazyRegistry();
            
            FenLogger.Info("[ImageLoader] Cache cleared", LogCategory.Rendering);
        }

        /// <summary>
        /// Evict oldest images to stay under memory limit
        /// </summary>
        private static void EvictIfNeeded()
        {
            var config = NetworkConfiguration.Instance;
            var maxBytes = config.MaxImageCacheBytes;
            var maxCount = config.MaxImageCacheCount;
            
            if (_currentCacheBytes <= maxBytes && _memoryCache.Count <= maxCount) return;
            
            // Sort by last accessed time and evict oldest
            var toEvict = _memoryCache
                .OrderBy(kvp => kvp.Value.LastAccessed)
                .Take(Math.Max(1, _memoryCache.Count / 4)) // Evict 25%
                .ToList();
            
            foreach (var kvp in toEvict)
            {
                if (_memoryCache.TryRemove(kvp.Key, out var entry))
                {
                    lock (_cacheLock)
                    {
                        _currentCacheBytes -= entry.ByteSize;
                    }
                    entry.Bitmap?.Dispose();
                    
                    FenLogger.Debug($"[ImageLoader] Evicted: {kvp.Key.Substring(0, Math.Min(40, kvp.Key.Length))}...", 
                                   LogCategory.Rendering);
                }
            }
        }

        /// <summary>
        /// Get cache statistics for debugging
        /// </summary>
        public static string GetCacheStats()
        {
            return $"Images: {CacheCount}, Memory: {_currentCacheBytes / (1024 * 1024)}MB, " +
                   $"Lazy Pending: {_lazyRegistry.Count(x => !x.Value.LoadStarted)}";
        }
        
        /// <summary>
        /// RULE 3 & 5: Render SVG content to bitmap using adapter with safety limits
        /// </summary>
        private static SKBitmap RenderSvgToBitmap(string svgContent, int? targetWidth, int? targetHeight)
        {
            var result = _svgRenderer.Render(svgContent, SvgRenderLimits.Default);
            
            if (!result.Success || result.Picture == null)
            {
                FenLogger.Debug($"[ImageLoader] SVG render failed: {result.ErrorMessage}", LogCategory.Rendering);
                return null;
            }
            
            int w = targetWidth ?? (int)result.Width;
            int h = targetHeight ?? (int)result.Height;
            if (w <= 0 || h <= 0) { w = 300; h = 150; }
            
            var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                
                // Scale picture to fit target
                if (result.Width > 0 && result.Height > 0)
                {
                    float scaleX = w / result.Width;
                    float scaleY = h / result.Height;
                    canvas.Scale(scaleX, scaleY);
                }
                
                canvas.DrawPicture(result.Picture);
            }
            
            return bitmap;
        }
        
        /// <summary>
        /// Request a debounced repaint. Multiple calls within DEBOUNCE_DELAY_MS
        /// will result in only a single repaint after the delay.
        /// </summary>
        private static void RequestDebouncedRepaint()
        {
            lock (_timerLock)
            {
                _repaintPending = true;
                
                // Dispose existing timer and create new one to reset the delay
                _repaintDebounceTimer?.Dispose();
                _repaintDebounceTimer = new Timer(_ =>
                {
                    lock (_timerLock)
                    {
                        if (_repaintPending)
                        {
                            _repaintPending = false;
                            RequestRepaint?.Invoke();
                        }
                    }
                }, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Get image from cache, or trigger load. Supports lazy loading.
        /// </summary>
        /// <param name="url">Image URL</param>
        /// <param name="isLazy">If true, only register for lazy loading if not in viewport</param>
        /// <param name="elementBounds">Element bounds for lazy loading registration</param>
        public static SKBitmap GetImage(string url, bool isLazy = false, SKRect? elementBounds = null, int? targetWidth = null, int? targetHeight = null)
        {
            if (string.IsNullOrEmpty(url)) return null;

            // Check new cache first
            if (_memoryCache.TryGetValue(url, out var entry))
            {
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Bitmap;
            }
            
            // Check legacy cache
            if (_legacyCache.TryGetValue(url, out var bitmap))
            {
                return bitmap;
            }

            // For lazy images, check if we should defer loading
            if (isLazy && elementBounds.HasValue && NetworkConfiguration.Instance.EnableLazyLoading)
            {
                // Check if element is currently in viewport
                var config = NetworkConfiguration.Instance;
                var threshold = config.LazyLoadThresholdPx;
                var expandedViewport = new SKRect(
                    _currentViewport.Left - threshold,
                    _currentViewport.Top - threshold,
                    _currentViewport.Right + threshold,
                    _currentViewport.Bottom + threshold
                );
                
                if (!expandedViewport.IsEmpty && !expandedViewport.IntersectsWith(elementBounds.Value))
                {
                    // Not in viewport - register for lazy loading
                    RegisterLazyImage(url, elementBounds.Value);
                    return null; // Renderer should show placeholder
                }
            }

            // Either not lazy, or in viewport - load immediately
            lock (_pendingLock)
            {
                if (_pendingLoads.Contains(url)) return null; // Already loading
                _pendingLoads.Add(url);
            }
            
            LoadImageAsync(url, isLazy, targetWidth, targetHeight);
            return null;
        }



        public static object GetImageTuple(string url, bool isLazy = false, SKRect? elementBounds = null, int? targetWidth = null, int? targetHeight = null)
        {
            var bmp = GetImage(url, isLazy, elementBounds, targetWidth, targetHeight);
            return (bmp, isLazy);
        }

        private static async void LoadImageAsync(string url, bool isLazy = false, int? targetWidth = null, int? targetHeight = null)
        {
            try
            {
                // Check caches before loading
                if (_memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url)) return;

                // Handle base64 / Data URIs
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                     // Format: data:[<mediatype>][;base64],<data>
                     int commaIndex = url.IndexOf(',');
                     if (commaIndex < 0) return;

                     string metadata = url.Substring(5, commaIndex - 5); // between data: and ,
                     string dataStr = url.Substring(commaIndex + 1);

                     bool isBase64 = metadata.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0;
                     string mimeType = metadata.Split(';')[0];
                     
                     // Security/Feature Check: Only allow images
                     if (!string.IsNullOrEmpty(mimeType) && !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                     {
                         FenLogger.Warn($"[ImageLoader] Blocked non-image Data URI: {mimeType}", LogCategory.Rendering);
                         return;
                     }

                     byte[] bytes = null;
                     if (isBase64)
                     {
                         // Robust Base64 cleanup
                         dataStr = Uri.UnescapeDataString(dataStr);
                         dataStr = dataStr.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
                         try 
                         {
                             bytes = Convert.FromBase64String(dataStr);
                         }
                         catch (Exception b64Ex)
                         {
                             FenLogger.Error($"[ImageLoader] Base64 Parse Error: {b64Ex.Message}", LogCategory.Rendering);
                             return;
                         }
                     }
                     else
                     {
                         // URL encoded raw data
                         dataStr = Uri.UnescapeDataString(dataStr);
                         bytes = System.Text.Encoding.UTF8.GetBytes(dataStr);
                     }

                     if (bytes != null && bytes.Length > 0)
                     {
                         SKBitmap bmp = null;
                         // RULE 3 & 5: Use SvgSkiaRenderer adapter for SVG rendering with safety limits
                         if (mimeType.Contains("svg"))
                         {
                             string svgContent = System.Text.Encoding.UTF8.GetString(bytes);
                             bmp = RenderSvgToBitmap(svgContent, targetWidth, targetHeight);
                         }
                         else
                         {
                             bmp = SKBitmap.Decode(bytes);
                         }

                         if (bmp != null)
                         {
                             _legacyCache[url] = bmp;
                             // Log success for specific debugging
                             if (url.Length > 50) 
                                 FenLogger.Debug($"[ImageLoader] Loaded Data URI (len={bytes.Length})", LogCategory.Rendering);
                             
                             RequestDebouncedRepaint();
                             RequestRelayout?.Invoke();
                         }
                     }
                     return;
                }

                // Handle HTTP
                // Only allow http/https for now
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) 
                {
                     FenLogger.Warn($"[ImageLoader] Skipped URL (Not HTTP): {url}", LogCategory.Rendering);
                     return;
                }

                FenLogger.Debug($"[ImageLoader] Fetching: {url}", LogCategory.Rendering);

                var data = await _httpClient.GetByteArrayAsync(url);
                SKBitmap bitmap = null;
                
                // SVG Detection (Basic)
                bool isSvg = url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                if (!isSvg && url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    isSvg = true;
                }
                
                if (!isSvg)
                {
                     // Peek bytes?
                     // If it starts with <svg or <?xml, it might be SVG. 
                     // Simple check:
                     try {
                         string header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 100)).Trim();
                         if (header.StartsWith("<svg") || header.StartsWith("<?xml") || header.Contains("<svg")) isSvg = true;
                     } catch {}
                }

                if (isSvg)
                {
                    // RULE 3 & 5: Use SvgSkiaRenderer adapter for SVG rendering with safety limits
                    string svgContent = System.Text.Encoding.UTF8.GetString(data);
                    bitmap = RenderSvgToBitmap(svgContent, targetWidth, targetHeight);
                    
                    if (bitmap == null)
                    {
                        FenLogger.Warn($"[ImageLoader] SVG Render Error: {url}", LogCategory.Rendering);
                    }
                }
                
                // Fallback / Standard Image
                if (bitmap == null)
                {
                    bitmap = SKBitmap.Decode(data);
                }
                
                if (bitmap != null)
                {
                    // Standardize on _memoryCache for all new loads
                    var entry = new ImageCacheEntry
                    {
                        Bitmap = bitmap,
                        ByteSize = bitmap.ByteCount,
                        LastAccessed = DateTime.UtcNow,
                        IsLazy = isLazy
                    };
                    
                    _memoryCache[url] = entry;
                    _legacyCache[url] = bitmap; // Keep legacy for fallback
                    
                    // Track memory usage
                    lock (_cacheLock)
                    {
                        _currentCacheBytes += bitmap.ByteCount;
                    }
                    EvictIfNeeded();
                    
                    // Remove from pending
                    lock (_pendingLock)
                    {
                        _pendingLoads.Remove(url);
                    }
                    
                    // Remove from lazy registry if present
                    _lazyRegistry.TryRemove(url, out _);
                    
                    FenLogger.Debug($"[ImageLoader] Success: {url} ({bitmap.Width}x{bitmap.Height})", LogCategory.Rendering);
                    RequestDebouncedRepaint();
                    RequestRelayout?.Invoke();
                }
                else
                {
                    FenLogger.Warn($"[ImageLoader] Decode Failed: {url}", LogCategory.Rendering);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[ImageLoader] Error loading {url}: {ex.Message}", LogCategory.Rendering);
            }
        }
    }
}
