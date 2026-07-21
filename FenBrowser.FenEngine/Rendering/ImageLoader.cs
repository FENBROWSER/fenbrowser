using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Linq;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
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
        public long ByteSize;
        public DateTime LastAccessed;

        // Phase 8: tracks the last known frame index so the GIF timer only repaints
        // when the frame actually changes, not on every tick.
        public int CurrentFrameIndex;

        public SKBitmap GetCurrentFrame()
        {
            if (Frames == null || Frames.Length == 0) return null;
            LastAccessed = DateTime.UtcNow;
            if (Frames.Length == 1) return Frames[0];
            long elapsed = Environment.TickCount64 - StartTick;
            int pos = (int)(elapsed % TotalDuration);
            int accum = 0;
            for (int i = 0; i < Frames.Length; i++)
            {
                accum += Durations[i];
                if (pos < accum)
                {
                    CurrentFrameIndex = i;
                    return Frames[i];
                }
            }
            CurrentFrameIndex = Frames.Length - 1;
            return Frames[Frames.Length - 1];
        }
    }

    public readonly record struct ImageCacheSnapshot(
        int StaticImageCount,
        int AnimatedImageCount,
        int AnimatedFrameCount,
        long ApproximateBytes,
        int PendingLoadCount,
        int LazyPendingCount,
        long HitCount,
        long MissCount,
        long EvictionCount);

    public static class ImageLoader
    {
        public sealed class ImageLoaderRequestContext
        {
            public string OwnerId { get; set; }
            public Func<Uri, Task<byte[]>> FetchBytesAsync { get; set; }
            public Func<Uri, Task<BinaryFetchResult>> FetchDetailedAsync { get; set; }
            public Func<Uri, Document, Task<byte[]>> FetchBytesForDocumentAsync { get; set; }
            public Func<Uri, Document, Task<BinaryFetchResult>> FetchDetailedForDocumentAsync { get; set; }
            public Action RequestRepaint { get; set; }
            public Action RequestRelayout { get; set; }
            /// <summary>
            /// Phase 6: set to true when the owning browsing context (tab/document)
            /// is navigated away or disposed. Callbacks belonging to disposed
            /// contexts are silently skipped.
            /// </summary>
            public bool IsDisposed { get; set; }
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly ImageLoaderRequestContext _previousContext;
            private bool _disposed;

            public ContextScope(ImageLoaderRequestContext context)
            {
                _previousContext = _ambientContext.Value;
                _ambientContext.Value = context;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _ambientContext.Value = _previousContext;
                _disposed = true;
            }
        }

        private static readonly AsyncLocal<ImageLoaderRequestContext> _ambientContext = new AsyncLocal<ImageLoaderRequestContext>();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ImageLoaderRequestContext>> _pendingLoadContexts =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, ImageLoaderRequestContext>>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, BinaryFetchResult> _lastLoadResults =
            new ConcurrentDictionary<string, BinaryFetchResult>(StringComparer.Ordinal);

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
        private static long _cacheHitCount = 0;
        private static long _cacheMissCount = 0;
        private static long _cacheEvictionCount = 0;
        private static readonly object _cacheLock = new object();
        private static readonly ConcurrentQueue<SKBitmap> _pendingBitmapDisposals = new ConcurrentQueue<SKBitmap>();
        private static int _disposeWorkerActive = 0;
        private const int BITMAP_DISPOSAL_GRACE_MS = 1500;
        
        // Monotonic version counter incremented on each successful cache store.
        // The renderer reads this to detect when new images have arrived since the
        // last paint-tree build, forcing a rebuild so fresh bitmaps are picked up.
        private static long _cacheVersion;
        public static long CacheVersion => Volatile.Read(ref _cacheVersion);

        // Debounce mechanism to prevent flickering from rapid repaint requests
        private static Timer _repaintDebounceTimer;
        private static readonly object _timerLock = new object();
        private static bool _repaintPending = false;
        private static Timer _relayoutDebounceTimer;
        private static readonly object _relayoutTimerLock = new object();
        private static bool _relayoutPending = false;
        private const int DEBOUNCE_DELAY_MS = 100;

        // ========== Animated GIF Support ==========
        private static readonly ConcurrentDictionary<string, AnimatedImage> _animatedGifs =
            new ConcurrentDictionary<string, AnimatedImage>();
        private static Timer _gifAnimationTimer;
        private static readonly object _gifTimerLock = new object();

        // Phase 5: Animated-GIF playback state is tracked per browsing context owner
        // so a GIF in one tab only ever repaints that tab. The process-global
        // `RequestRepaint` callback historically pointed at whichever host registered
        // last, so an animating GIF in tab A would wake tab B. We instead remember the
        // owner that actually displayed each GIF and repaint only those owners.
        private static readonly ConcurrentDictionary<string, ImageLoaderRequestContext> _animatedGifOwners =
            new ConcurrentDictionary<string, ImageLoaderRequestContext>(StringComparer.Ordinal);

        // Phase 7: per-owner cache invalidation generations. When an image decode
        // completes, only the generations of owners that use that image are bumped.
        // This prevents an image arriving in tab A from making tab B think its image
        // state changed.
        private static readonly ConcurrentDictionary<string, long> _ownerCacheGenerations = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, HashSet<string>> _imageToOwners = new(StringComparer.Ordinal);

        /// <summary>
        /// Phase 7: returns the cache generation for a specific owner. Used by
        /// BrowserIntegration to scope RepaintReady suppression per-document.
        /// </summary>
        public static long GetCacheGeneration(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return CacheVersion;
            return _ownerCacheGenerations.TryGetValue(ownerId, out var gen) ? gen : 0;
        }

        /// <summary>
        /// Phase 7: increments the cache generation only for owners that use the
        /// given cache key. Called when a decode completes.
        /// </summary>
        private static void BumpOwnerGenerations(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return;
            if (_imageToOwners.TryGetValue(cacheKey, out var owners))
            {
                foreach (var ownerId in owners)
                {
                    _ownerCacheGenerations.AddOrUpdate(ownerId, 1, (_, v) => v + 1);
                }
            }
        }

        /// <summary>
        /// Phase 7: register that an owner uses a particular image. Called when an
        /// image is requested for rendering.
        /// </summary>
        public static void RegisterImageOwner(string cacheKey, string ownerId)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(ownerId))
                return;
            _imageToOwners.AddOrUpdate(
                cacheKey,
                _ => new HashSet<string>(StringComparer.Ordinal) { ownerId },
                (_, set) => { lock (set) { set.Add(ownerId); } return set; });
        }

        /// <summary>
        /// Phase 7: remove all image ownership registrations for a disposed owner.
        /// </summary>
        public static void ReleaseOwner(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return;
            _ownerCacheGenerations.TryRemove(ownerId, out _);
            _animatedGifOwners.TryRemove(ownerId, out _);
            // Clean up image-to-owner mappings.
            foreach (var kv in _imageToOwners)
            {
                lock (kv.Value)
                {
                    kv.Value.Remove(ownerId);
                }
            }
        }

        /// <summary>
        /// True when there are active animated GIFs that need periodic repainting
        /// </summary>
        public static bool HasActiveAnimatedImages => !_animatedGifs.IsEmpty;

        /// <summary>
        /// Records that the given request context (browsing-context owner) is currently
        /// displaying an animated GIF, so the GIF animation timer can repaint only the
        /// owners that actually use animated images.
        /// </summary>
        private static void NoteAnimatedImageOwner(ImageLoaderRequestContext context)
        {
            var ownerId = context?.OwnerId;
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                return;
            }

            _animatedGifOwners[ownerId] = context;
        }
        
        // RULE 3 & 5: SVG rendering through adapter with safety limits
        private static readonly ISvgRenderer _svgRenderer = new SvgSkiaRenderer();
        
        /// <summary>
        /// Centralized image byte fetch delegate (wired by BrowserHost).
        /// Avoids direct ImageLoader network access paths.
        /// </summary>
        public static Func<Uri, Task<byte[]>> FetchBytesAsync { get; set; }
        public static Func<Uri, Task<BinaryFetchResult>> FetchDetailedAsync { get; set; }

        // Callback to request a repaint when image loads
        public static Action RequestRepaint { get; set; }
        
        // Callback to request a full re-layout when image dimensions are resolved
        public static Action RequestRelayout { get; set; }

        public static IDisposable EnterRequestContext(ImageLoaderRequestContext context)
        {
            return new ContextScope(context);
        }

        public static async Task<byte[]> FetchBytesForCurrentContextAsync(Uri uri, Document ownerDocument = null)
        {
            if (uri == null)
            {
                return null;
            }

            var context = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            var detailedFetcher = context?.FetchDetailedAsync ?? FetchDetailedAsync;
            if (detailedFetcher != null)
            {
                var result = await detailedFetcher(uri).ConfigureAwait(false);
                if (result != null)
                {
                    RecordLoadResult(uri.AbsoluteUri, CreateCacheKey(uri.AbsoluteUri, context), result);
                }
                return result?.Body;
            }

            var fetcher = context?.FetchBytesAsync ?? FetchBytesAsync;
            if (fetcher == null)
            {
                return null;
            }

            return await fetcher(uri).ConfigureAwait(false);
        }

        public static bool HasDocumentAwareFetcher(Document ownerDocument)
        {
            var context = _ambientContext.Value;
            return ownerDocument != null && context != null &&
                (context.FetchBytesForDocumentAsync != null || context.FetchDetailedForDocumentAsync != null);
        }

        public static bool TryGetLastLoadResult(string url, out BinaryFetchResult result)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                result = null;
                return false;
            }

            var context = _ambientContext.Value;
            var resultKey = CreateCacheKey(url, context);
            if (_lastLoadResults.TryGetValue(resultKey, out result))
            {
                return true;
            }

            // Unscoped callers retain the legacy URL-only lookup. Scoped callers must
            // not observe another browsing context's in-flight or failed result.
            return context == null && _lastLoadResults.TryGetValue(url, out result);
        }

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
        public static int CacheCount => _memoryCache.Count + _animatedGifs.Count;

        public static bool ContainsCachedImage(string url)
        {
            return ContainsCachedImage(url, ownerDocument: null);
        }

        public static bool ContainsCachedImage(string url, Document ownerDocument)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var context = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            var cacheKey = CreateCacheKey(url, context);
            return _memoryCache.ContainsKey(cacheKey) ||
                _legacyCache.ContainsKey(cacheKey) ||
                _animatedGifs.ContainsKey(cacheKey);
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
        public static void RegisterLazyImage(string url, SKRect elementBounds, string cacheKey = null)
        {
            if (string.IsNullOrEmpty(url)) return;
            cacheKey ??= url;

            // Already cached? No need to register as lazy
            if (_memoryCache.ContainsKey(cacheKey) || _legacyCache.ContainsKey(cacheKey) || _animatedGifs.ContainsKey(cacheKey)) return;

            _lazyRegistry[cacheKey] = new LazyImageInfo
            {
                Url = url,
                ElementBounds = elementBounds,
                LoadStarted = false
            };
            
            EngineLogCompat.Debug($"[ImageLoader] Registered lazy image: {url.Substring(0, Math.Min(50, url.Length))}...", 
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
                        _ = LoadImageAsync(
                            kvp.Value.Url,
                            kvp.Key,
                            isLazy: true,
                            context: GetPendingLoadContext(kvp.Key));
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
            // Phase 6: mark all pending request contexts as disposed before clearing
            // so that any in-flight async callback (debounced repaint or relayout)
            // can detect the stale context and skip it silently.
            foreach (var urlEntry in _pendingLoadContexts)
            {
                foreach (var context in urlEntry.Value.Values)
                {
                    if (context != null) context.IsDisposed = true;
                }
            }

            _lazyRegistry.Clear();
            _pendingLoadContexts.Clear();
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
            _lastLoadResults.Clear();

            foreach (var bitmap in disposalSet)
            {
                ScheduleBitmapDispose(bitmap);
            }

            lock (_cacheLock)
            {
                _currentCacheBytes = 0;
                _cacheHitCount = 0;
                _cacheMissCount = 0;
                _cacheEvictionCount = 0;
            }

            ClearLazyRegistry();

            lock (_timerLock)
            {
                _repaintPending = false;
                _repaintDebounceTimer?.Dispose();
                _repaintDebounceTimer = null;
            }

            lock (_relayoutTimerLock)
            {
                _relayoutPending = false;
                _relayoutDebounceTimer?.Dispose();
                _relayoutDebounceTimer = null;
            }

            // Animated GIF cleanup
            foreach (var anim in _animatedGifs.Values)
            {
                if (anim?.Frames == null) continue;
                foreach (var frame in anim.Frames)
                {
                    ScheduleBitmapDispose(frame);
                }
            }
            _animatedGifs.Clear();
            StopGifAnimationTimer();
            
            EngineLogCompat.Info("[ImageLoader] Cache cleared", LogCategory.Rendering);
        }

        /// <summary>
        /// Evict oldest images to stay under memory limit
        /// </summary>
        private static void EvictIfNeeded()
        {
            var config = NetworkConfiguration.Instance;
            var maxBytes = config.MaxImageCacheBytes;
            var maxCount = config.MaxImageCacheCount;

            lock (_cacheLock)
            {
                if (_currentCacheBytes <= maxBytes && CacheCount <= maxCount)
                {
                    return;
                }
            }

            var candidates = new List<(string Key, DateTime LastAccessed, long Bytes, bool IsAnimated)>();
            candidates.AddRange(_memoryCache.Select(kvp => (kvp.Key, kvp.Value.LastAccessed, kvp.Value.ByteSize, false)));
            candidates.AddRange(_animatedGifs.Select(kvp => (kvp.Key, kvp.Value.LastAccessed, kvp.Value.ByteSize, true)));

            foreach (var candidate in candidates.OrderBy(entry => entry.LastAccessed))
            {
                bool withinBudget;
                lock (_cacheLock)
                {
                    withinBudget = _currentCacheBytes <= maxBytes && CacheCount <= maxCount;
                }

                if (withinBudget)
                {
                    break;
                }

                if (candidate.IsAnimated)
                {
                    if (_animatedGifs.TryRemove(candidate.Key, out var animated))
                    {
                        if (animated?.Frames != null)
                        {
                            foreach (var frame in animated.Frames)
                            {
                                ScheduleBitmapDispose(frame);
                            }
                        }

                        lock (_cacheLock)
                        {
                            _currentCacheBytes -= candidate.Bytes;
                            _cacheEvictionCount++;
                        }

                        EngineLogCompat.Debug($"[ImageLoader] Evicted animated image: {candidate.Key.Substring(0, Math.Min(40, candidate.Key.Length))}...",
                            LogCategory.Rendering);
                    }

                    continue;
                }

                if (_memoryCache.TryRemove(candidate.Key, out var entry))
                {
                    _legacyCache.TryRemove(candidate.Key, out _);

                    lock (_cacheLock)
                    {
                        _currentCacheBytes -= entry.ByteSize;
                        _cacheEvictionCount++;
                    }

                    ScheduleBitmapDispose(entry.Bitmap);
                    EngineLogCompat.Debug($"[ImageLoader] Evicted: {candidate.Key.Substring(0, Math.Min(40, candidate.Key.Length))}...",
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

            // Image paint nodes hold raw SKBitmap references across immutable paint trees.
            // Disposing cache-owned bitmaps during eviction or cache clear can invalidate an
            // in-flight frame and crash native Skia access. Once cache ownership is dropped,
            // let normal GC/finalization reclaim the bitmap after the last renderer reference
            // is gone instead of forcing eager disposal here.
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
                    EngineLogCompat.Warn($"[ImageLoader] Detached async operation failed: {ex.Message}", LogCategory.Rendering);
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
                                EngineLogCompat.Warn($"[ImageLoader] Deferred bitmap dispose failed: {ex.Message}", LogCategory.Rendering);
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
            var snapshot = GetCacheSnapshot();
            return $"Images: {snapshot.StaticImageCount + snapshot.AnimatedImageCount}, Memory: {snapshot.ApproximateBytes / (1024 * 1024)}MB, " +
                   $"Animated: {snapshot.AnimatedImageCount}, Lazy Pending: {snapshot.LazyPendingCount}, Pending Loads: {snapshot.PendingLoadCount}, " +
                   $"Hits: {snapshot.HitCount}, Misses: {snapshot.MissCount}, Evictions: {snapshot.EvictionCount}";
        }

        public static ImageCacheSnapshot GetCacheSnapshot()
        {
            long bytes;
            long hits;
            long misses;
            long evictions;
            lock (_cacheLock)
            {
                bytes = _currentCacheBytes;
                hits = _cacheHitCount;
                misses = _cacheMissCount;
                evictions = _cacheEvictionCount;
            }

            var animatedFrameCount = _animatedGifs.Values.Sum(anim => anim?.Frames?.Length ?? 0);
            return new ImageCacheSnapshot(
                _memoryCache.Count,
                _animatedGifs.Count,
                animatedFrameCount,
                bytes,
                PendingLoadCount,
                _lazyRegistry.Count(x => !x.Value.LoadStarted),
                hits,
                misses,
                evictions);
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
                EngineLogCompat.Debug($"[ImageLoader] SVG render failed: {result.ErrorMessage}", LogCategory.Rendering);
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
                            EngineLogCompat.Debug($"[ImageLoader] Saved SVG bitmap {w}x{h} to svg_debug_bitmap.png", LogCategory.Rendering);
                        }
                        catch (Exception ex)
                        {
                            EngineLogCompat.Debug($"[ImageLoader] Failed to save SVG bitmap: {ex.Message}", LogCategory.Rendering);
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
                        EngineLogCompat.Debug($"[ImageLoader] Saved SCALED SVG bitmap {w}x{h} to svg_debug_bitmap.png", LogCategory.Rendering);
                    }
                    catch (Exception ex) { EngineLogCompat.Warn($"[ImageLoader] Failed writing svg_debug_bitmap.png: {ex.Message}", LogCategory.Rendering); }
                }
#endif
                
                return scaledBitmap;
            }
            
            // Fallback to old Picture-based rendering (may produce empty bitmap due to SKSvg disposal)
            if (result.Picture == null)
            {
                EngineLogCompat.Debug("[ImageLoader] SVG render failed: No bitmap or picture available", LogCategory.Rendering);
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
        private static void RequestDebouncedRepaint(string url = null, ImageLoaderRequestContext fallbackContext = null)
        {
            var callbackTargets = CollectPendingLoadContexts(url, fallbackContext);
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
                            InvokeRepaint(callbackTargets);
                        }
                    }
                }, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
            }
        }

        private static void RequestDebouncedRelayout(string url = null, ImageLoaderRequestContext fallbackContext = null)
        {
            var callbackTargets = CollectPendingLoadContexts(url, fallbackContext);
            lock (_relayoutTimerLock)
            {
                _relayoutPending = true;

                _relayoutDebounceTimer?.Dispose();
                _relayoutDebounceTimer = new Timer(_ =>
                {
                    lock (_relayoutTimerLock)
                    {
                        if (_relayoutPending)
                        {
                            _relayoutPending = false;
                            InvokeRelayout(callbackTargets);
                        }
                    }
                }, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
            }
        }

        private static List<ImageLoaderRequestContext> CollectPendingLoadContexts(string url, ImageLoaderRequestContext fallbackContext)
        {
            var contexts = new Dictionary<string, ImageLoaderRequestContext>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(url) && _pendingLoadContexts.TryGetValue(url, out var urlContexts))
            {
                foreach (var context in urlContexts.Values)
                {
                    if (context == null)
                    {
                        continue;
                    }

                    // Phase 6: skip contexts belonging to navigated-away documents.
                    if (context.IsDisposed)
                    {
                        continue;
                    }

                    var ownerId = string.IsNullOrWhiteSpace(context.OwnerId) ? "_default" : context.OwnerId;
                    contexts[ownerId] = context;
                }
            }

            if (fallbackContext != null && !fallbackContext.IsDisposed)
            {
                var ownerId = string.IsNullOrWhiteSpace(fallbackContext.OwnerId) ? "_default" : fallbackContext.OwnerId;
                contexts[ownerId] = fallbackContext;
            }

            return contexts.Values.ToList();
        }

        private static void InvokeRepaint(List<ImageLoaderRequestContext> contexts)
        {
            // Phase 6: prefer document/browsing-context scoped callbacks. Only fall
            // back to the process-global RequestRepaint when no scoped callback could
            // be invoked, so a single decoded image produces at most one repaint per
            // owning document and never a duplicate global signal.
            // Phase 6: skip disposed contexts (navigated-away tabs) and deduplicate
            // by OwnerId so multiple image completions in one debounce window invoke
            // each owner at most once.
            bool invokedScopedCallback = false;
            var invokedOwners = new HashSet<string>(StringComparer.Ordinal);

            if (contexts != null)
            {
                for (int i = contexts.Count - 1; i >= 0; i--)
                {
                    var context = contexts[i];
                    if (context?.RequestRepaint == null)
                    {
                        continue;
                    }

                    if (context.IsDisposed)
                    {
                        contexts.RemoveAt(i);
                        continue;
                    }

                    var ownerId = context.OwnerId ?? "_default";
                    if (!invokedOwners.Add(ownerId))
                    {
                        continue; // already invoked for this owner
                    }

                    try
                    {
                        context.RequestRepaint.Invoke();
                        invokedScopedCallback = true;
                    }
                    catch
                    {
                    }
                }
            }

            if (!invokedScopedCallback)
            {
                try { RequestRepaint?.Invoke(); }
                catch { }
            }
        }

        private static void InvokeRelayout(List<ImageLoaderRequestContext> contexts)
        {
            // Phase 6: same scoped-first + disposal + per-owner dedup policy as InvokeRepaint.
            bool invokedScopedCallback = false;
            var invokedOwners = new HashSet<string>(StringComparer.Ordinal);

            if (contexts != null)
            {
                for (int i = contexts.Count - 1; i >= 0; i--)
                {
                    var context = contexts[i];
                    if (context?.RequestRelayout == null)
                    {
                        continue;
                    }

                    if (context.IsDisposed)
                    {
                        contexts.RemoveAt(i);
                        continue;
                    }

                    var ownerId = context.OwnerId ?? "_default";
                    if (!invokedOwners.Add(ownerId))
                    {
                        continue;
                    }

                    try
                    {
                        context.RequestRelayout.Invoke();
                        invokedScopedCallback = true;
                    }
                    catch
                    {
                    }
                }
            }

            if (!invokedScopedCallback)
            {
                try { RequestRelayout?.Invoke(); }
                catch { }
            }
        }

        /// <summary>
        /// Get image from cache, or trigger load. Supports lazy loading.
        /// </summary>
        /// <param name="url">Image URL</param>
        /// <param name="isLazy">If true, only register for lazy loading if not in viewport</param>
        /// <param name="elementBounds">Element bounds for lazy loading registration</param>
        public static SKBitmap GetImage(
            string url,
            bool isLazy = false,
            SKRect? elementBounds = null,
            int? targetWidth = null,
            int? targetHeight = null,
            Document ownerDocument = null)
        {
            if (string.IsNullOrEmpty(url)) return null;
            url = url.Trim();
            var loadContext = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            var cacheKey = CreateCacheKey(url, loadContext);

            if (IsCssImageFunction(url))
            {
                EngineLogCompat.Debug($"[ImageLoader] Ignoring non-fetchable CSS image function: {url.Substring(0, Math.Min(80, url.Length))}...", LogCategory.Rendering);
                return null;
            }

            // Animated GIF: return the current frame based on elapsed time
            if (_animatedGifs.TryGetValue(cacheKey, out var anim))
            {
                RegisterCacheHit();
                NoteAnimatedImageOwner(loadContext);
                return anim.GetCurrentFrame();
            }

            // Check new cache first
            if (_memoryCache.TryGetValue(cacheKey, out var entry))
            {
                entry.LastAccessed = DateTime.UtcNow;
                RegisterCacheHit();
                return entry.Bitmap;
            }
            
            // Check legacy cache
            if (_legacyCache.TryGetValue(cacheKey, out var bitmap))
            {
                RegisterCacheHit();
                return bitmap;
            }

            RegisterCacheMiss();

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
                    EngineLogCompat.Debug($"[ImageLoader] Lazy defer: {url}", LogCategory.Rendering);
                    // Not in viewport - register for lazy loading
                    CapturePendingLoadContext(cacheKey, loadContext);
                    RegisterLazyImage(url, elementBounds.Value, cacheKey);
                    return null; // Renderer should show placeholder
                }
            }

            // Handle Data URIs synchronously to prevent recursion
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                 EngineLogCompat.Debug($"[ImageLoader] Decoding Data URI: {url.Substring(0, Math.Min(20, url.Length))}...", LogCategory.Rendering);
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
                    _memoryCache[cacheKey] = dataEntry;
                    _legacyCache[cacheKey] = dataBitmap;
                    lock (_cacheLock) { _currentCacheBytes += dataBitmap.ByteCount; }
                    EvictIfNeeded();
                    
                    return dataBitmap;
                }
                return null;
            }

            // Either not lazy, or in viewport - load immediately
            CapturePendingLoadContext(cacheKey, loadContext);
            if (!TryRegisterPendingLoad(cacheKey))
            {
                return null; // Already loading
            }

            _ = LoadImageAsync(
                url,
                cacheKey,
                isLazy,
                targetWidth,
                targetHeight,
                loadContext ?? GetPendingLoadContext(cacheKey));
            return null;
        }

        private static bool IsCssImageFunction(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.TrimStart();
            return trimmed.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("repeating-linear-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("repeating-radial-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("conic-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("repeating-conic-gradient(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("image-set(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("cross-fade(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("element(", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("paint(", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Decode and cache a fetched image stream so the first render can consume it synchronously.
        /// Used by navigation-time image prewarming to avoid a second image-fetch path racing after first paint.
        /// </summary>
        public static async Task<bool> PrewarmImageAsync(
            string url,
            Stream stream,
            bool isLazy = false,
            int? targetWidth = null,
            int? targetHeight = null,
            Document ownerDocument = null)
        {
            if (string.IsNullOrWhiteSpace(url) || stream == null)
            {
                return false;
            }

            var context = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            var cacheKey = CreateCacheKey(url, context);
            if (_memoryCache.ContainsKey(cacheKey) || _legacyCache.ContainsKey(cacheKey) || _animatedGifs.ContainsKey(cacheKey))
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

            var bitmap = DecodeBitmapFromBytes(url, data, targetWidth, targetHeight, cacheKey);
            if (bitmap == null)
            {
                return false;
            }

            if (!TryStoreDecodedBitmap(cacheKey, url, bitmap, isLazy))
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

                return _memoryCache.ContainsKey(cacheKey) ||
                    _legacyCache.ContainsKey(cacheKey) ||
                    _animatedGifs.ContainsKey(cacheKey);
            }

            RequestDebouncedRepaint();
            RequestDebouncedRelayout();
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
                 cleanData = cleanData.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "").Replace("\f", "");
             }

             int remainder = cleanData.Length % 4;
             if (remainder == 2)
             {
                 cleanData += "==";
             }
             else if (remainder == 3)
             {
                 cleanData += "=";
             }
             
             try { bytes = Convert.FromBase64String(cleanData); }
             catch (FormatException fe)
             {
                 var preview = cleanData.Length > 60 ? cleanData.Substring(0, 60) + "..." : cleanData;
                 EngineLogCompat.Warn($"[ImageLoader] Base64 decode failed (len={cleanData.Length}, preview={preview}): {fe.Message}", LogCategory.Rendering);
                 return null;
             }
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
                         var bmp = SKBitmap.Decode(bytes);
                        // Fallback for SkiaSharp 4.x compatibility (see DecodeBitmapFromBytes)
                        if (bmp == null)
                        {
                            try
                            {
                                using var skData = SKData.CreateCopy(bytes);
                                if (skData != null && !skData.IsEmpty)
                                {
                                    using var image = SKImage.FromEncodedData(skData);
                                    if (image != null)
                                    {
                                        bmp = SKBitmap.FromImage(image);
                                    }
                                }
                            }
                            catch { }
                        }
                        return bmp;
                     }
                 }
                 return null;
             }
             catch (Exception ex)
             {
                 EngineLogCompat.Error($"[ImageLoader] Data URI Decode Error: {ex.Message}", LogCategory.Rendering);
                 return null;
             }
        }

        public static object GetImageTuple(string url, bool isLazy = false, SKRect? elementBounds = null, int? targetWidth = null, int? targetHeight = null)
        {
            var bmp = GetImage(url, isLazy, elementBounds, targetWidth, targetHeight);
            return (bmp, isLazy);
        }

        private static async Task LoadImageAsync(
            string url,
            string cacheKey,
            bool isLazy = false,
            int? targetWidth = null,
            int? targetHeight = null,
            ImageLoaderRequestContext context = null)
        {
            try
            {
                // Check caches before loading
                if (_animatedGifs.ContainsKey(cacheKey)) return;
                if (_memoryCache.ContainsKey(cacheKey) || _legacyCache.ContainsKey(cacheKey)) return;

                // Only allow http/https for now — data URIs are handled synchronously above.
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                     EngineLogCompat.Warn($"[ImageLoader] Skipped non-HTTP URL: {(url?.Length > 80 ? url?.Substring(0, 80) + "..." : url)}", LogCategory.Rendering);
                     return;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
                {
                    EngineLogCompat.Warn($"[ImageLoader] Invalid absolute URI, skipping: {(url?.Length > 80 ? url?.Substring(0, 80) + "..." : url)}", LogCategory.Rendering);
                    return;
                }

                var effectiveContext = context ?? _ambientContext.Value;
                var hasScopedFetcher = effectiveContext?.FetchDetailedAsync != null || effectiveContext?.FetchBytesAsync != null;
                var detailedFetcher = hasScopedFetcher ? effectiveContext.FetchDetailedAsync : FetchDetailedAsync;
                var fetcher = hasScopedFetcher ? effectiveContext.FetchBytesAsync : FetchBytesAsync;
                if (detailedFetcher == null && fetcher == null)
                {
                    EngineLogCompat.Warn("[ImageLoader] No image fetch delegate is configured; skipping image load", LogCategory.Rendering);
                    return;
                }

                BinaryFetchResult fetchResult;
                if (detailedFetcher != null)
                {
                    fetchResult = await detailedFetcher(absoluteUri).ConfigureAwait(false);
                }
                else
                {
                    var legacyBody = await fetcher(absoluteUri).ConfigureAwait(false);
                    fetchResult = new BinaryFetchResult
                    {
                        Body = legacyBody,
                        FinalUri = absoluteUri,
                        FailureReason = legacyBody == null || legacyBody.Length == 0
                            ? BinaryFetchFailureReason.BodyReadFailed
                            : BinaryFetchFailureReason.None,
                        FailureDetail = legacyBody == null || legacyBody.Length == 0 ? "Empty image response" : null
                    };
                }

                if (fetchResult == null)
                {
                    fetchResult = new BinaryFetchResult
                    {
                        FinalUri = absoluteUri,
                        FailureReason = BinaryFetchFailureReason.TransportFailure,
                        FailureDetail = "Image fetch returned no result"
                    };
                }
                RecordLoadResult(url, cacheKey, fetchResult);

                if (!fetchResult.Succeeded)
                {
                    EngineLogCompat.Warn($"[ImageLoader] Fetch failed: url={url} reason={fetchResult.FailureReason} status={fetchResult.StatusCode}", LogCategory.Rendering);
                    return;
                }

                var data = fetchResult.Body;
                if (data == null || data.Length == 0)
                {
                    EngineLogCompat.Warn($"[ImageLoader] Empty image response for: {url}", LogCategory.Rendering);
                    return;
                }
                var decodeFormat = DetectDecodeFormat(url, data, fetchResult.ContentType);
                SKBitmap bitmap;
                try
                {
                    bitmap = DecodeBitmapFromBytes(url, data, targetWidth, targetHeight, cacheKey);
                }
                catch (Exception ex)
                {
                    RecordLoadResult(url, cacheKey, fetchResult with
                    {
                        DecodeFormat = decodeFormat,
                        DecodeFailureReason = ex.Message
                    });
                    EngineLogCompat.Warn($"[ImageLoader] Decode failed: url={url} format={decodeFormat ?? "unknown"}", LogCategory.Rendering);
                    return;
                }
                
                if (bitmap != null)
                {
                    if (TryStoreDecodedBitmap(cacheKey, url, bitmap, isLazy))
                    {
                        RecordLoadResult(url, cacheKey, fetchResult with { DecodeFormat = decodeFormat });
                        RequestDebouncedRepaint(cacheKey, context);
                        RequestDebouncedRelayout(cacheKey, context);
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
                    RecordLoadResult(url, cacheKey, fetchResult with
                    {
                        DecodeFormat = decodeFormat,
                        DecodeFailureReason = "Decoder returned no bitmap"
                    });
                    EngineLogCompat.Warn($"[ImageLoader] Decode Failed: {url}", LogCategory.Rendering);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[ImageLoader] Error loading {url}: {ex.Message}", LogCategory.Rendering);
            }
            finally
            {
                CompletePendingLoad(cacheKey);
            }
        }

        private static string DetectDecodeFormat(string url, byte[] data, string contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                return contentType;
            }
            if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                return "image/svg+xml";
            }
            try
            {
                using var codec = SKCodec.Create(new MemoryStream(data));
                return codec?.EncodedFormat.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static SKBitmap DecodeBitmapFromBytes(
            string url,
            byte[] data,
            int? targetWidth,
            int? targetHeight,
            string cacheKey = null)
        {
            SKBitmap bitmap = null;
            cacheKey ??= url;

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
                    EngineLogCompat.Warn($"[ImageLoader] SVG header sniff failed: {ex.Message}", LogCategory.Rendering);
                }
            }

            if (isSvg)
            {
                string svgContent = System.Text.Encoding.UTF8.GetString(data);
                bitmap = RenderSvgToBitmap(svgContent, targetWidth, targetHeight);

                if (bitmap == null)
                {
                    EngineLogCompat.Warn($"[ImageLoader] SVG Render Failed for: {url}", LogCategory.Rendering);
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
                            animated.LastAccessed = DateTime.UtcNow;
                            _animatedGifs[cacheKey] = animated;
                            lock (_cacheLock)
                            {
                                _currentCacheBytes += animated.ByteSize;
                            }
                            bitmap = animated.Frames[0];
                            EvictIfNeeded();
                            EnsureGifAnimationTimer();
                            EngineLogCompat.Log(
                                LogCategory.Rendering,
                                LogLevel.Debug,
                                $"[ImageLoader] Animated GIF: {url} ({animated.Frames.Length} frames, {animated.TotalDuration}ms total)");
                        }
                        else
                        {
                            isAnimatedGif = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[ImageLoader] SKCodec check failed: {ex.Message}", LogCategory.Rendering);
                }

                if (!isAnimatedGif && bitmap == null)
                {
                    bitmap = SKBitmap.Decode(data);

                    // Fallback: SkiaSharp 4.x may return null from SKBitmap.Decode for some
                    // formats that the older overload handled.  Try the modern two-step path
                    // (SKImage.FromEncodedData → SKBitmap.FromImage) as a safety net.
                    if (bitmap == null)
                    {
                        try
                        {
                            using var skData = SKData.CreateCopy(data);
                            if (skData != null && !skData.IsEmpty)
                            {
                                using var image = SKImage.FromEncodedData(skData);
                                if (image != null)
                                {
                                    bitmap = SKBitmap.FromImage(image);
                                }
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            EngineLogCompat.Debug($"[ImageLoader] SKImage.FromEncodedData fallback also failed: {fallbackEx.Message}", LogCategory.Rendering);
                        }
                    }
                }
            }

            // Downscale to target display size to save GPU memory. A 4000×3000
            // image displayed at 200×150 wastes ~400× memory if stored at native
            // resolution. Only downscale when both dimensions are specified and
            // the decoded bitmap is meaningfully larger.
            if (bitmap != null &&
                !bitmap.IsNull &&
                targetWidth.HasValue &&
                targetHeight.HasValue &&
                targetWidth.Value > 0 &&
                targetHeight.Value > 0)
            {
                int tw = targetWidth.Value;
                int th = targetHeight.Value;
                if (bitmap.Width > tw * 2 || bitmap.Height > th * 2)
                {
                    try
                    {
                        float scaleX = (float)tw / bitmap.Width;
                        float scaleY = (float)th / bitmap.Height;
                        float scale = Math.Min(scaleX, scaleY);
                        int newW = Math.Max(1, (int)(bitmap.Width * scale));
                        int newH = Math.Max(1, (int)(bitmap.Height * scale));

                        var resized = bitmap.Resize(new SKSizeI(newW, newH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        if (resized != null && !resized.IsNull)
                        {
                            bitmap.Dispose();
                            bitmap = resized;
                        }
                    }
                    catch (Exception resizeEx)
                    {
                        EngineLogCompat.Debug(
                            $"[ImageLoader] Resize-to-target failed for {url}: {resizeEx.Message}",
                            LogCategory.Rendering);
                        // Keep the original bitmap on resize failure
                    }
                }
            }

            return bitmap;
        }

        private static bool TryStoreDecodedBitmap(string cacheKey, string url, SKBitmap bitmap, bool isLazy)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || bitmap == null || bitmap.IsNull || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return false;
            }

            if (_animatedGifs.ContainsKey(cacheKey))
            {
                _lazyRegistry.TryRemove(cacheKey, out _);
                return true;
            }

            if (_memoryCache.ContainsKey(cacheKey) || _legacyCache.ContainsKey(cacheKey))
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

            if (!_memoryCache.TryAdd(cacheKey, entry))
            {
                return false;
            }

            _legacyCache[cacheKey] = bitmap;

            lock (_cacheLock)
            {
                _currentCacheBytes += bitmap.ByteCount;
            }

            EvictIfNeeded();
            _lazyRegistry.TryRemove(cacheKey, out _);
            Interlocked.Increment(ref _cacheVersion);
            // Phase 7: bump per-owner generations so only affected documents
            // see the image change, not every tab in the process.
            BumpOwnerGenerations(cacheKey);
            EngineLogCompat.Log(
                LogCategory.Rendering,
                LogLevel.Debug,
                $"[ImageLoader] SUCCESS: {url} ({bitmap.Width}x{bitmap.Height})");
            return true;
        }

        private static void CapturePendingLoadContext(
            string loadKey,
            ImageLoaderRequestContext context)
        {
            if (string.IsNullOrWhiteSpace(loadKey) || context == null)
            {
                return;
            }

            var ownerId = string.IsNullOrWhiteSpace(context.OwnerId) ? "_default" : context.OwnerId;
            var contexts = _pendingLoadContexts.GetOrAdd(
                loadKey,
                _ => new ConcurrentDictionary<string, ImageLoaderRequestContext>(StringComparer.Ordinal));
            contexts[ownerId] = context;
        }

        private static ImageLoaderRequestContext CreateDocumentRequestContext(
            ImageLoaderRequestContext context,
            Document ownerDocument)
        {
            if (context == null || ownerDocument == null ||
                (context.FetchDetailedForDocumentAsync == null && context.FetchBytesForDocumentAsync == null))
            {
                return context;
            }

            return new ImageLoaderRequestContext
            {
                OwnerId = $"{context.OwnerId}:{RuntimeHelpers.GetHashCode(ownerDocument)}",
                FetchDetailedAsync = context.FetchDetailedForDocumentAsync == null
                    ? context.FetchDetailedAsync
                    : uri => context.FetchDetailedForDocumentAsync(uri, ownerDocument),
                FetchBytesAsync = context.FetchBytesForDocumentAsync == null
                    ? context.FetchBytesAsync
                    : uri => context.FetchBytesForDocumentAsync(uri, ownerDocument),
                RequestRepaint = context.RequestRepaint,
                RequestRelayout = context.RequestRelayout
            };
        }

        private static void RecordLoadResult(
            string url,
            string cacheKey,
            BinaryFetchResult result)
        {
            _lastLoadResults[cacheKey] = result;
            if (!string.Equals(cacheKey, url, StringComparison.Ordinal))
            {
                _lastLoadResults[url] = result;
            }
        }

        private static string CreateCacheKey(string url, ImageLoaderRequestContext context)
        {
            if (string.IsNullOrWhiteSpace(context?.OwnerId))
            {
                return url;
            }

            return $"{context.OwnerId}\n{url}";
        }

        private static ImageLoaderRequestContext GetPendingLoadContext(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!_pendingLoadContexts.TryGetValue(url, out var contexts) || contexts == null || contexts.IsEmpty)
            {
                return _ambientContext.Value;
            }

            if (_ambientContext.Value != null)
            {
                return _ambientContext.Value;
            }

            foreach (var entry in contexts.Values)
            {
                if (entry != null)
                {
                    return entry;
                }
            }

            return null;
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
            _pendingLoadContexts.TryRemove(url, out _);

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
                    EngineLogCompat.Warn($"[ImageLoader] GIF frame {i} decode failed: {result}", LogCategory.Rendering);
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

            long byteSize = 0;
            foreach (var frame in frames)
            {
                byteSize += frame?.ByteCount ?? 0;
            }

            return new AnimatedImage
            {
                Frames = frames,
                Durations = durations,
                TotalDuration = totalDuration,
                StartTick = Environment.TickCount64,
                ByteSize = byteSize,
                LastAccessed = DateTime.UtcNow
            };
        }

        private static void RegisterCacheHit()
        {
            lock (_cacheLock)
            {
                _cacheHitCount++;
            }
        }

        private static void RegisterCacheMiss()
        {
            lock (_cacheLock)
            {
                _cacheMissCount++;
            }
        }

        private static void EnsureGifAnimationTimer()
        {
            lock (_gifTimerLock)
            {
                if (_gifAnimationTimer != null) return;

                // Phase 8: schedule to the nearest frame deadline instead of a fixed
                // 50ms tick. The callback computes the actual next deadline from all
                // active animated GIFs and reschedules the timer accordingly.
                _gifAnimationTimer = new Timer(_ =>
                {
                    if (_animatedGifs.IsEmpty)
                    {
                        StopGifAnimationTimer();
                        return;
                    }

                    bool anyFrameChanged = false;
                    long nowTicks = DateTime.UtcNow.Ticks;
                    long nearestDeadlineTicks = long.MaxValue;

                    foreach (var (cacheKey, anim) in _animatedGifs)
                    {
                        if (anim.Frames == null || anim.Frames.Length == 0) continue;

                        // Phase 8: compute current frame by elapsed time, not tick count.
                        long elapsedMs = Environment.TickCount64 - anim.StartTick;
                        int totalFrames = anim.Frames.Length;
                        double totalDurationMs = anim.TotalDuration;

                        // Which frame should be displayed now?
                        double posInCycle = totalDurationMs > 0
                            ? elapsedMs % totalDurationMs
                            : 0;
                        double accumulated = 0;
                        int newFrameIndex = 0;
                        for (int i = 0; i < totalFrames; i++)
                        {
                            accumulated += Math.Max(anim.Durations[i], 20);
                            if (posInCycle < accumulated)
                            {
                                newFrameIndex = i;
                                break;
                            }
                        }

                        // Only repaint if the frame index actually changed.
                        bool frameChanged = newFrameIndex != anim.CurrentFrameIndex;
                        anim.CurrentFrameIndex = newFrameIndex;

                        if (frameChanged)
                        {
                            anyFrameChanged = true;
                        }

                        // Compute next deadline for this GIF.
                        double remainingInFrame = accumulated - posInCycle;
                        long frameEndTicks = nowTicks +
                            (long)(remainingInFrame * TimeSpan.TicksPerMillisecond);
                        if (frameEndTicks < nearestDeadlineTicks)
                            nearestDeadlineTicks = frameEndTicks;
                    }

                    // Only repaint when at least one GIF actually changed frames.
                    if (anyFrameChanged)
                    {
                        RepaintAnimatedImageOwners();
                    }

                    // Reschedule to nearest deadline (clamp to 16ms-200ms range).
                    long delayMs = nearestDeadlineTicks == long.MaxValue
                        ? 50
                        : Math.Max(16, Math.Min(200,
                            (nearestDeadlineTicks - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond));

                    lock (_gifTimerLock)
                    {
                        if (_gifAnimationTimer != null)
                        {
                            try { _gifAnimationTimer.Change(delayMs, Timeout.Infinite); }
                            catch { }
                        }
                    }
                }, null, 50, Timeout.Infinite); // One-shot; rescheduled after each tick.
            }
        }

        private static void RepaintAnimatedImageOwners()
        {
            // Phase 6: skip disposed contexts and deduplicate by owner so a GIF in
            // two sibling frames with the same owner invokes the callback only once.
            bool invokedScoped = false;
            var invokedOwners = new HashSet<string>(StringComparer.Ordinal);
            var staleOwners = new List<string>();

            foreach (var kvp in _animatedGifOwners)
            {
                var context = kvp.Value;
                if (context?.RequestRepaint == null)
                {
                    continue;
                }

                if (context.IsDisposed)
                {
                    staleOwners.Add(kvp.Key);
                    continue;
                }

                var ownerId = context.OwnerId ?? "_default";
                if (!invokedOwners.Add(ownerId))
                {
                    continue;
                }

                try
                {
                    context.RequestRepaint.Invoke();
                    invokedScoped = true;
                }
                catch
                {
                }
            }

            // Prune disposed entries so the dictionary doesn't grow unbounded.
            foreach (var key in staleOwners)
            {
                _animatedGifOwners.TryRemove(key, out _);
            }

            if (!invokedScoped)
            {
                try { RequestRepaint?.Invoke(); }
                catch { }
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

            // Drop stale owner→context mappings so a disposed browsing context is
            // never repainted after its GIFs stop animating.
            _animatedGifOwners.Clear();
        }
    }
}

