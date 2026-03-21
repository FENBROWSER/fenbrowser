using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>
    /// Stores decoded frames for animated GIFs
    /// </summary>
    internal class AnimatedImage
    {
        public SKBitmap[] Frames;
        public int[] Durations; // ms per frame
        public int TotalDuration;
        public long StartTick;

        public SKBitmap GetCurrentFrame()
        {
            if (Frames == null || Frames.Length == 0) return null;
            if (Frames.Length == 1) return Frames[0];
            long elapsed = Environment.TickCount64 - StartTick;
            int pos = (int)(elapsed % TotalDuration);
            int accum = 0;
            for (int i = 0; i < Frames.Length; i++)
            {
                accum += Durations[i];
                if (pos < accum) return Frames[i];
            }
            return Frames[Frames.Length - 1];
        }
    }

    public static class ImageLoader
    {
        // Main cache with metadata for memory tracking
        private static readonly ConcurrentDictionary<string, ImageCacheEntry> _memoryCache = 
            new ConcurrentDictionary<string, ImageCacheEntry>();
        
        // Legacy cache for backward compatibility
        private static readonly ConcurrentDictionary<string, SKBitmap> _legacyCache = 
            new ConcurrentDictionary<string, SKBitmap>();
        
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
        private static readonly ConcurrentQueue<SKBitmap> _pendingBitmapDisposals = new ConcurrentQueue<SKBitmap>();
        private static int _disposeWorkerActive = 0;
        private const int BITMAP_DISPOSAL_GRACE_MS = 1500;
        
        // Debounce mechanism to prevent flickering from rapid repaint requests
        private static Timer _repaintDebounceTimer;
        private static readonly object _timerLock = new object();
        private static bool _repaintPending = false;
        private const int DEBOUNCE_DELAY_MS = 100;

        // ========== Animated GIF Support ==========
        private static readonly ConcurrentDictionary<string, AnimatedImage> _animatedGifs =
            new ConcurrentDictionary<string, AnimatedImage>();
        private static Timer _gifAnimationTimer;
        private static readonly object _gifTimerLock = new object();

        /// <summary>
        /// True when there are active animated GIFs that need periodic repainting
        /// </summary>
        public static bool HasActiveAnimatedImages => !_animatedGifs.IsEmpty;
        
        // RULE 3 & 5: SVG rendering through adapter with safety limits
        private static readonly ISvgRenderer _svgRenderer = new SvgSkiaRenderer();
        
        /// <summary>
        /// Centralized image byte fetch delegate (wired by BrowserHost).
        /// Avoids direct ImageLoader network access paths.
        /// </summary>
        public static Func<Uri, Task<byte[]>> FetchBytesAsync { get; set; }

        // Callback to request a repaint when image loads
        public static Action RequestRepaint { get; set; }
        
        // Callback to request a full re-layout when image dimensions are resolved
        public static Action RequestRelayout { get; set; }

        // Emits authoritative pending network-image load count changes.
        public static event Action<int> PendingLoadCountChanged;

        // ========== Memory Management Properties ==========
        
        /// <summary>
        /// Current memory usage by cached images in bytes
        /// </summary>
        public static long CurrentCacheBytes => _currentCacheBytes;
        
        /// <summary>
        /// Number of images currently cached
        /// </summary>
        public static int CacheCount => _memoryCache.Count + _legacyCache.Count;

        public static bool ContainsCachedImage(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return _memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url) || _animatedGifs.ContainsKey(url);
        }

        /// <summary>
        /// Number of in-flight image fetch/decode operations.
        /// </summary>
        public static int PendingLoadCount
        {
            get
            {
                lock (_pendingLock)
                {
                    return _pendingLoads.Count;
                }
            }
        }
        
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
                    if (TryRegisterPendingLoad(kvp.Key))
                    {
                        _ = LoadImageAsync(kvp.Key, isLazy: true);
                    }
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
            NotifyPendingLoadCountChanged();
        }

        // ========== Memory Management API ==========

        /// <summary>
        /// Clear all cached images and reset memory tracking
        /// </summary>
        public static void ClearCache()
        {
            var disposalSet = new HashSet<SKBitmap>();

            foreach (var entry in _memoryCache.Values)
            {
                if (entry?.Bitmap != null)
                {
                    disposalSet.Add(entry.Bitmap);
                }
            }
            _memoryCache.Clear();

            foreach (var bitmap in _legacyCache.Values)
            {
                if (bitmap != null)
                {
                    disposalSet.Add(bitmap);
                }
            }
            _legacyCache.Clear();

            foreach (var bitmap in disposalSet)
            {
                ScheduleBitmapDispose(bitmap);
            }

            lock (_cacheLock)
            {
                _currentCacheBytes = 0;
            }

            ClearLazyRegistry();

            // Animated GIF cleanup
            foreach (var anim in _animatedGifs.Values)
            {
                if (anim?.Frames == null) continue;
                foreach (var frame in anim.Frames)
                {
                    frame?.Dispose();
                }
            }
            _animatedGifs.Clear();
            StopGifAnimationTimer();
            
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
                    // CRITICAL FIX: Also remove from legacy cache to prevent returning disposed bitmaps
                    _legacyCache.TryRemove(kvp.Key, out _);

                    lock (_cacheLock)
                    {
                        _currentCacheBytes -= entry.ByteSize;
                    }
                    ScheduleBitmapDispose(entry.Bitmap);
                    
                    FenLogger.Debug($"[ImageLoader] Evicted: {kvp.Key.Substring(0, Math.Min(40, kvp.Key.Length))}...", 
                                   LogCategory.Rendering);
                }
            }
        }


        private static void ScheduleBitmapDispose(SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.IsNull)
            {
                return;
            }

            _pendingBitmapDisposals.Enqueue(bitmap);
            EnsureDisposeWorker();
        }


        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[ImageLoader] Detached async operation failed: {ex.Message}", LogCategory.Rendering);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        private static void EnsureDisposeWorker()
        {
            if (Interlocked.CompareExchange(ref _disposeWorkerActive, 1, 0) != 0)
            {
                return;
            }

            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    while (true)
                    {
                        if (_pendingBitmapDisposals.IsEmpty)
                        {
                            return;
                        }

                        await Task.Delay(BITMAP_DISPOSAL_GRACE_MS).ConfigureAwait(false);

                        while (_pendingBitmapDisposals.TryDequeue(out var deferredBitmap))
                        {
                            try
                            {
                                deferredBitmap.Dispose();
                            }
                            catch (Exception ex)
                            {
                                FenLogger.Warn($"[ImageLoader] Deferred bitmap dispose failed: {ex.Message}", LogCategory.Rendering);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _disposeWorkerActive, 0);
                    if (!_pendingBitmapDisposals.IsEmpty)
                    {
                        EnsureDisposeWorker();
                    }
                }
            });
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
            // Ensure SVG Namespace (required for SkiaSharp.Svg)
            if (!svgContent.Contains("xmlns=\"http://www.w3.org/2000/svg\"") && 
                !svgContent.Contains("xmlns='http://www.w3.org/2000/svg'"))
            {
                if (svgContent.Contains("<svg "))
                    svgContent = svgContent.Replace("<svg ", "<svg xmlns=\"http://www.w3.org/2000/svg\" ");
                else if (svgContent.Contains("<svg>"))
                    svgContent = svgContent.Replace("<svg>", "<svg xmlns=\"http://www.w3.org/2000/svg\">");
            }

            // Normalization: SkiaSharp.Svg is case-sensitive for certain attributes
            if (svgContent.Contains("viewbox="))
            {
                svgContent = svgContent.Replace("viewbox=", "viewBox=");
            }

            var result = _svgRenderer.Render(svgContent, SvgRenderLimits.Default);
            
            // CRITICAL FIX: Check for pre-rendered bitmap first (avoids SKSvg disposal issues)
            if (!result.Success)
            {
                FenLogger.Debug($"[ImageLoader] SVG render failed: {result.ErrorMessage}", LogCategory.Rendering);
                return null;
            }
            
            // Use the pre-rendered bitmap from SvgSkiaRenderer (safe after SKSvg disposal)
            if (result.Bitmap != null)
            {
                int w = targetWidth ?? result.Bitmap.Width;
                int h = targetHeight ?? result.Bitmap.Height;
                if (w <= 0 || h <= 0) { w = 300; h = 150; }
                
                // If target size matches bitmap size, use directly
                if (w == result.Bitmap.Width && h == result.Bitmap.Height)
                {
#if DEBUG
                    // DEBUG: Save large SVG rasterizations to diagnostics root for visual inspection.
                    if (w > 200 && h > 80)
                    {
                        try
                        {
                            using var stream = File.Open(
                                DiagnosticPaths.GetRootArtifactPath("svg_debug_bitmap.png"),
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.Read);
                            result.Bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
                            FenLogger.Debug($"[ImageLoader] Saved SVG bitmap {w}x{h} to svg_debug_bitmap.png", LogCategory.Rendering);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Debug($"[ImageLoader] Failed to save SVG bitmap: {ex.Message}", LogCategory.Rendering);
                        }
                    }
#endif
                    return result.Bitmap;
                }
                
                // Scale the pre-rendered bitmap to target size while preserving aspect ratio
                var scaledBitmap = new SKBitmap(w, h);
                using (var canvas = new SKCanvas(scaledBitmap))
                {
                    canvas.Clear(SKColors.Transparent);

                    // Calculate scale that preserves aspect ratio (contain mode)
                    float srcW = result.Bitmap.Width;
                    float srcH = result.Bitmap.Height;
                    float srcAspect = srcW / srcH;
                    float destAspect = (float)w / h;

                    float destW, destH, destX, destY;
                    if (srcAspect > destAspect)
                    {
                        // Source is wider - fit to width
                        destW = w;
                        destH = w / srcAspect;
                        destX = 0;
                        destY = (h - destH) / 2;
                    }
                    else
                    {
                        // Source is taller - fit to height
                        destH = h;
                        destW = h * srcAspect;
                        destX = (w - destW) / 2;
                        destY = 0;
                    }

                    var srcRect = new SKRect(0, 0, srcW, srcH);
                    var destRect = new SKRect(destX, destY, destX + destW, destY + destH);
                    canvas.DrawBitmap(result.Bitmap, srcRect, destRect);
                }
                
#if DEBUG
                // DEBUG: Save large scaled SVG rasterizations for visual inspection.
                if (w > 200 && h > 80)
                {
                    try
                    {
                        using var stream = File.Open(
                            DiagnosticPaths.GetRootArtifactPath("svg_debug_bitmap.png"),
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.Read);
                        scaledBitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
                        FenLogger.Debug($"[ImageLoader] Saved SCALED SVG bitmap {w}x{h} to svg_debug_bitmap.png", LogCategory.Rendering);
                    }
                    catch (Exception ex) { FenLogger.Warn($"[ImageLoader] Failed writing svg_debug_bitmap.png: {ex.Message}", LogCategory.Rendering); }
                }
#endif
                
                return scaledBitmap;
            }
            
            // Fallback to old Picture-based rendering (may produce empty bitmap due to SKSvg disposal)
            if (result.Picture == null)
            {
                FenLogger.Debug("[ImageLoader] SVG render failed: No bitmap or picture available", LogCategory.Rendering);
                return null;
            }
            
            int wFallback = targetWidth ?? (int)result.Width;
            int hFallback = targetHeight ?? (int)result.Height;
            if (wFallback <= 0 || hFallback <= 0) { wFallback = 300; hFallback = 150; }
            
            var bitmap = new SKBitmap(wFallback, hFallback);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);

                // Scale picture to fit target while preserving aspect ratio
                if (result.Width > 0 && result.Height > 0)
                {
                    float srcAspect = result.Width / result.Height;
                    float destAspect = (float)wFallback / hFallback;

                    float scale;
                    float offsetX = 0, offsetY = 0;

                    if (srcAspect > destAspect)
                    {
                        // Source is wider - fit to width
                        scale = wFallback / result.Width;
                        offsetY = (hFallback - result.Height * scale) / 2;
                    }
                    else
                    {
                        // Source is taller - fit to height
                        scale = hFallback / result.Height;
                        offsetX = (wFallback - result.Width * scale) / 2;
                    }

                    canvas.Translate(offsetX, offsetY);
                    canvas.Scale(scale, scale);
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
            FenLogger.Debug($"[ImageLoader] GetImage called for {url}", LogCategory.Rendering);
            if (string.IsNullOrEmpty(url)) return null;

            // Animated GIF: return the current frame based on elapsed time
            if (_animatedGifs.TryGetValue(url, out var anim))
            {
                return anim.GetCurrentFrame();
            }

            // Check new cache first
            if (_memoryCache.TryGetValue(url, out var entry))
            {
                FenLogger.Debug($"[ImageLoader] Found in memory cache: {url}", LogCategory.Rendering);
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Bitmap;
            }
            
            // Check legacy cache
            if (_legacyCache.TryGetValue(url, out var bitmap))
            {
                 FenLogger.Debug($"[ImageLoader] Found in legacy cache: {url}", LogCategory.Rendering);
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
                    FenLogger.Debug($"[ImageLoader] Lazy defer: {url}", LogCategory.Rendering);
                    // Not in viewport - register for lazy loading
                    RegisterLazyImage(url, elementBounds.Value);
                    return null; // Renderer should show placeholder
                }
            }

            // Handle Data URIs synchronously to prevent recursion
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                 FenLogger.Debug($"[ImageLoader] Decoding Data URI: {url.Substring(0, Math.Min(20, url.Length))}...", LogCategory.Rendering);
                var dataBitmap = DecodeDataUri(url, targetWidth, targetHeight);
                if (dataBitmap != null)
                {
                    var dataEntry = new ImageCacheEntry
                    {
                        Bitmap = dataBitmap,
                        ByteSize = dataBitmap.ByteCount,
                        LastAccessed = DateTime.UtcNow,
                        IsLazy = isLazy
                    };
                    _memoryCache[url] = dataEntry;
                    _legacyCache[url] = dataBitmap;
                    lock (_cacheLock) { _currentCacheBytes += dataBitmap.ByteCount; }
                    
                    return dataBitmap;
                }
                return null;
            }

            // Either not lazy, or in viewport - load immediately
            FenLogger.Debug($"[ImageLoader] Queueing load for: {url}", LogCategory.Rendering);
            if (!TryRegisterPendingLoad(url))
            {
                FenLogger.Debug($"[ImageLoader] Already pending: {url}", LogCategory.Rendering);
                return null; // Already loading
            }
            
            FenLogger.Debug($"[ImageLoader] Starting LoadImageAsync: {url}", LogCategory.Rendering);
            _ = LoadImageAsync(url, isLazy, targetWidth, targetHeight);
            return null;
        }

        /// <summary>
        /// Decode and cache a fetched image stream so the first render can consume it synchronously.
        /// Used by navigation-time image prewarming to avoid a second image-fetch path racing after first paint.
        /// </summary>
        public static async Task<bool> PrewarmImageAsync(string url, Stream stream, bool isLazy = false, int? targetWidth = null, int? targetHeight = null)
        {
            if (string.IsNullOrWhiteSpace(url) || stream == null)
            {
                return false;
            }

            if (_memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url) || _animatedGifs.ContainsKey(url))
            {
                return true;
            }

            byte[] data;
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                data = ms.ToArray();
            }

            if (data == null || data.Length == 0)
            {
                return false;
            }

            var bitmap = DecodeBitmapFromBytes(url, data, targetWidth, targetHeight);
            if (bitmap == null)
            {
                return false;
            }

            if (!TryStoreDecodedBitmap(url, bitmap, isLazy))
            {
                try
                {
                    if (!bitmap.IsNull)
                    {
                        bitmap.Dispose();
                    }
                }
                catch
                {
                }

                return _memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url) || _animatedGifs.ContainsKey(url);
            }

            RequestDebouncedRepaint();
            RequestRelayout?.Invoke();
            return true;
        }

        private static SKBitmap DecodeDataUri(string url, int? targetWidth, int? targetHeight)
        {
             try
             {
                 int commaIndex = url.IndexOf(',');
                 if (commaIndex < 0) return null;

                 string metadata = url.Substring(5, commaIndex - 5);
                 string dataStr = url.Substring(commaIndex + 1);

                 bool isBase64 = metadata.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0;
                 string mimeType = metadata.Split(';')[0];
                 
                 if (!string.IsNullOrEmpty(mimeType) && !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                     return null;

                 byte[] bytes = null;
         if (isBase64)
         {
             // For base64, we should NOT unescape the entire string if it contains '+' or '/'
             // But we do need to remove whitespace
             string cleanData = dataStr.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
             
             // If the string contains '%', it's likely URI encoded base64
             if (cleanData.Contains('%'))
             {
                 cleanData = Uri.UnescapeDataString(cleanData);
             }
             
             try { bytes = Convert.FromBase64String(cleanData); } catch { return null; }
         }
         else
         {
             dataStr = Uri.UnescapeDataString(dataStr);
             bytes = System.Text.Encoding.UTF8.GetBytes(dataStr);
         }

                 if (bytes != null && bytes.Length > 0)
                 {
                     if (mimeType.Contains("svg"))
                     {
                         string svgContent = System.Text.Encoding.UTF8.GetString(bytes);
                         return RenderSvgToBitmap(svgContent, targetWidth, targetHeight);
                     }
                     else
                     {
                         return SKBitmap.Decode(bytes);
                     }
                 }
                 return null;
             }
             catch (Exception ex)
             {
                 FenLogger.Error($"[ImageLoader] Data URI Decode Error: {ex.Message}", LogCategory.Rendering);
                 return null;
             }
        }

        public static object GetImageTuple(string url, bool isLazy = false, SKRect? elementBounds = null, int? targetWidth = null, int? targetHeight = null)
        {
            var bmp = GetImage(url, isLazy, elementBounds, targetWidth, targetHeight);
            return (bmp, isLazy);
        }

        private static async Task LoadImageAsync(string url, bool isLazy = false, int? targetWidth = null, int? targetHeight = null)
        {
            try
            {
                // Check caches before loading
                if (_animatedGifs.ContainsKey(url)) return;
                if (_memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url)) return;

                // Handle HTTP
                // Only allow http/https for now
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) 
                {
                     // FenLogger.Warn($"[ImageLoader] Skipped URL (Not HTTP): {url}", LogCategory.Rendering);
                     return;
                }

                FenLogger.Debug($"[ImageLoader] Fetching: {url}", LogCategory.Rendering);

                if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
                {
                    return;
                }

                var fetcher = FetchBytesAsync;
                if (fetcher == null)
                {
                    FenLogger.Warn("[ImageLoader] FetchBytesAsync delegate is not configured; skipping image load", LogCategory.Rendering);
                    return;
                }

                var data = await fetcher(absoluteUri).ConfigureAwait(false);
                if (data == null || data.Length == 0)
                {
                    FenLogger.Warn($"[ImageLoader] Empty image response for: {url}", LogCategory.Rendering);
                    return;
                }
                var bitmap = DecodeBitmapFromBytes(url, data, targetWidth, targetHeight);
                
                if (bitmap != null)
                {
                    if (TryStoreDecodedBitmap(url, bitmap, isLazy))
                    {
                        RequestDebouncedRepaint();
                        RequestRelayout?.Invoke();
                    }
                    else
                    {
                        try
                        {
                            if (!bitmap.IsNull)
                            {
                                bitmap.Dispose();
                            }
                        }
                        catch
                        {
                        }
                    }
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
            finally
            {
                CompletePendingLoad(url);
            }
        }

        private static SKBitmap DecodeBitmapFromBytes(string url, byte[] data, int? targetWidth, int? targetHeight)
        {
            SKBitmap bitmap = null;

            bool isSvg = url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            if (!isSvg && url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                isSvg = true;
            }

            if (!isSvg)
            {
                try
                {
                    string header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 100)).Trim();
                    if (header.StartsWith("<svg") || header.StartsWith("<?xml") || header.Contains("<svg"))
                    {
                        isSvg = true;
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[ImageLoader] SVG header sniff failed: {ex.Message}", LogCategory.Rendering);
                }
            }

            if (isSvg)
            {
                string svgContent = System.Text.Encoding.UTF8.GetString(data);
                bitmap = RenderSvgToBitmap(svgContent, targetWidth, targetHeight);

                if (bitmap == null)
                {
                    FenLogger.Warn($"[ImageLoader] SVG Render Failed for: {url}", LogCategory.Rendering);
                }
            }

            if (bitmap == null)
            {
                bool isAnimatedGif = false;
                try
                {
                    using var codec = SKCodec.Create(new MemoryStream(data));
                    if (codec != null && codec.FrameCount > 1)
                    {
                        isAnimatedGif = true;
                        var animated = DecodeAnimatedGif(codec, data);
                        if (animated != null && animated.Frames?.Length > 0)
                        {
                            _animatedGifs[url] = animated;
                            bitmap = animated.Frames[0];
                            EnsureGifAnimationTimer();
                            FenLogger.Info($"[ImageLoader] Animated GIF: {url} ({animated.Frames.Length} frames, {animated.TotalDuration}ms total)", LogCategory.Rendering);
                        }
                        else
                        {
                            isAnimatedGif = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[ImageLoader] SKCodec check failed: {ex.Message}", LogCategory.Rendering);
                }

                if (!isAnimatedGif && bitmap == null)
                {
                    bitmap = SKBitmap.Decode(data);
                }
            }

            return bitmap;
        }

        private static bool TryStoreDecodedBitmap(string url, SKBitmap bitmap, bool isLazy)
        {
            if (string.IsNullOrWhiteSpace(url) || bitmap == null || bitmap.IsNull || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return false;
            }

            if (_memoryCache.ContainsKey(url) || _legacyCache.ContainsKey(url))
            {
                return false;
            }

            var entry = new ImageCacheEntry
            {
                Bitmap = bitmap,
                ByteSize = bitmap.ByteCount,
                LastAccessed = DateTime.UtcNow,
                IsLazy = isLazy
            };

            if (!_memoryCache.TryAdd(url, entry))
            {
                return false;
            }

            _legacyCache[url] = bitmap;

            lock (_cacheLock)
            {
                _currentCacheBytes += bitmap.ByteCount;
            }

            EvictIfNeeded();
            _lazyRegistry.TryRemove(url, out _);
            FenLogger.Debug($"[ImageLoader] Success: {url} ({bitmap.Width}x{bitmap.Height})", LogCategory.Rendering);
            return true;
        }

        private static bool TryRegisterPendingLoad(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            var registered = false;
            lock (_pendingLock)
            {
                if (_pendingLoads.Contains(url))
                {
                    return false;
                }

                _pendingLoads.Add(url);
                registered = true;
            }

            if (registered)
            {
                NotifyPendingLoadCountChanged();
            }

            return registered;
        }

        private static void CompletePendingLoad(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            var changed = false;
            lock (_pendingLock)
            {
                changed = _pendingLoads.Remove(url);
            }

            if (changed)
            {
                NotifyPendingLoadCountChanged();
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
            lock (_pendingLock)
            {
                count = _pendingLoads.Count;
            }

            try
            {
                handler(count);
            }
            catch
            {
            }
        }

        private static AnimatedImage DecodeAnimatedGif(SKCodec codec, byte[] data)
        {
            int frameCount = codec.FrameCount;
            var frames = new SKBitmap[frameCount];
            var durations = new int[frameCount];
            var info = codec.Info;

            for (int i = 0; i < frameCount; i++)
            {
                var frameInfo = codec.FrameInfo[i];
                var bitmap = new SKBitmap(info);
                bitmap.Erase(SKColors.Transparent);

                // If this frame depends on a previous one, copy it first
                if (frameInfo.RequiredFrame >= 0 && frameInfo.RequiredFrame < frames.Length && frames[frameInfo.RequiredFrame] != null)
                {
                    frames[frameInfo.RequiredFrame].CopyTo(bitmap);
                }

                using var pixmap = bitmap.PeekPixels();
                var options = new SKCodecOptions(i, frameInfo.RequiredFrame);
                var result = codec.GetPixels(info, pixmap.GetPixels(), options);

                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    FenLogger.Warn($"[ImageLoader] GIF frame {i} decode failed: {result}", LogCategory.Rendering);
                    bitmap.Dispose();
                    continue;
                }

                frames[i] = bitmap;
                durations[i] = Math.Max(frameInfo.Duration, 50); // Fallback to 50ms if missing/0
            }

            // Drop null frames if decode failed
            frames = frames.Where(f => f != null).ToArray();
            durations = durations.Take(frames.Length).ToArray();

            if (frames.Length == 0) return null;

            int totalDuration = durations.Sum();
            if (totalDuration <= 0) totalDuration = frames.Length * 100; // fallback 100ms each

            return new AnimatedImage
            {
                Frames = frames,
                Durations = durations,
                TotalDuration = totalDuration,
                StartTick = Environment.TickCount64
            };
        }

        private static void EnsureGifAnimationTimer()
        {
            lock (_gifTimerLock)
            {
                if (_gifAnimationTimer != null) return;

                _gifAnimationTimer = new Timer(_ =>
                {
                    if (_animatedGifs.IsEmpty)
                    {
                        StopGifAnimationTimer();
                        return;
                    }
                    // Immediate repaint for animation frames (skip debounce)
                    RequestRepaint?.Invoke();
                }, null, 50, 50); // ~20fps tick
            }
        }

        private static void StopGifAnimationTimer()
        {
            lock (_gifTimerLock)
            {
                _gifAnimationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _gifAnimationTimer?.Dispose();
                _gifAnimationTimer = null;
            }
        }
    }
}

