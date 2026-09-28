using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
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
using FenBrowser.FenEngine.Svg;

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
        public string OwnerId { get; set; }
        public SKRect ElementBounds { get; set; }
        public int? TargetWidth { get; set; }
        public int? TargetHeight { get; set; }
        public Uri SvgBaseUri { get; set; }
        public bool LoadStarted { get; set; }
    }

    /// <summary>
    /// Stores decoded frames for animated GIFs
    /// </summary>
    internal class AnimatedImage
    {
        public SKBitmap[] Frames;
        public int[] Durations; // ms per frame
        public int[] FrameEndOffsets;
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
            CurrentFrameIndex = ResolveFrameIndex(pos);
            return Frames[CurrentFrameIndex];
        }

        public int ResolveFrameIndex(int position)
        {
            if (FrameEndOffsets == null || FrameEndOffsets.Length == 0)
            {
                return 0;
            }

            var index = Array.BinarySearch(FrameEndOffsets, position + 1);
            if (index < 0)
            {
                index = ~index;
            }

            return Math.Clamp(index, 0, Frames.Length - 1);
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
        private const int MaxSvgResourcePreloadConcurrency = 4;
        private const int MaxSvgResourcePreloadMilliseconds = 5_000;
        private const int MaxAnimatedGifFrameCount = 512;

        private sealed class SvgResourceSnapshot : ISvgResourceResolver
        {
            private readonly Dictionary<Uri, SvgResolvedResource> _resources = new();

            public int Count => _resources.Count;

            public void Add(Uri requestedUri, string contentType, byte[] content)
            {
                _resources[requestedUri] = new SvgResolvedResource(
                    requestedUri,
                    contentType ?? string.Empty,
                    content.ToArray());
            }

            public bool TryResolve(
                Uri absoluteUri,
                SvgResourceKind kind,
                out SvgResolvedResource resource,
                out string error)
            {
                if (kind is (SvgResourceKind.Image or SvgResourceKind.SvgDocument) &&
                    _resources.TryGetValue(absoluteUri, out resource))
                {
                    error = string.Empty;
                    return true;
                }
                resource = default;
                error = "preloaded SVG resource snapshot has no authorized entry";
                return false;
            }
        }

        public sealed class ImageLoaderRequestContext
        {
            internal sealed class OwnerLifetime
            {
                private int _isDisposed;

                public bool IsDisposed
                {
                    get => Volatile.Read(ref _isDisposed) != 0;
                    set => Volatile.Write(ref _isDisposed, value ? 1 : 0);
                }
            }

            internal OwnerLifetime OwnerLifetimeState { get; set; } = new OwnerLifetime();

            public string OwnerId { get; set; }
            public string OwnerRootId { get; set; }
            public Func<Uri, Task<byte[]>> FetchBytesAsync { get; set; }
            public Func<Uri, Task<BinaryFetchResult>> FetchDetailedAsync { get; set; }
            public Func<Uri, Document, Task<byte[]>> FetchBytesForDocumentAsync { get; set; }
            public Func<Uri, Document, Task<BinaryFetchResult>> FetchDetailedForDocumentAsync { get; set; }
            public Action RequestRepaint { get; set; }
            public Action RequestRelayout { get; set; }
            public bool IsDisposed { get; set; }
            internal bool OwnerIsDisposed
            {
                get => OwnerLifetimeState.IsDisposed;
                set => OwnerLifetimeState.IsDisposed = value;
            }
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
        private static readonly ConcurrentDictionary<string, SvgRequestMetadata> _svgRequestMetadata =
            new ConcurrentDictionary<string, SvgRequestMetadata>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _failedDataUriCache =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> _failedDataUriOrder = new ConcurrentQueue<string>();
        private const int MAX_FAILED_DATA_URI_CACHE_ENTRIES = 256;

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
        private static readonly ConcurrentDictionary<string, SKRect> _ownerViewports = new(StringComparer.Ordinal);
        private static readonly SemaphoreSlim _loadSemaphore = new SemaphoreSlim(4); // Max concurrent loads
        private const int LoadAdmissionPollMilliseconds = 100;
        
        // ========== Memory Management ==========
        private static long _currentCacheBytes = 0;
        private static long _cacheHitCount = 0;
        private static long _cacheMissCount = 0;
        private static long _cacheEvictionCount = 0;
        private static readonly object _cacheLock = new object();
        
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
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _imageToOwners = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _ownerToImages = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> _ownerAliases =
            new(StringComparer.Ordinal);

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
                foreach (var ownerId in owners.Keys)
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
            var owners = _imageToOwners.GetOrAdd(
                cacheKey,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            owners[ownerId] = 0;

            var images = _ownerToImages.GetOrAdd(
                ownerId,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            images[cacheKey] = 0;
        }

        private static void RegisterRequestOwner(
            string cacheKey,
            ImageLoaderRequestContext context,
            Document ownerDocument = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || context == null ||
                IsContextUnavailable(context))
            {
                return;
            }

            string rootOwnerId = context.OwnerRootId;
            if (string.IsNullOrWhiteSpace(rootOwnerId))
            {
                rootOwnerId = context.OwnerId;
            }

            if (string.IsNullOrWhiteSpace(rootOwnerId))
            {
                return;
            }

            RegisterImageOwner(cacheKey, rootOwnerId);
            if (!string.Equals(context.OwnerId, rootOwnerId, StringComparison.Ordinal))
            {
                RegisterImageOwner(cacheKey, context.OwnerId);
            }

            RegisterOwnerAlias(rootOwnerId, rootOwnerId);
            if (!string.Equals(context.OwnerId, rootOwnerId, StringComparison.Ordinal))
            {
                RegisterOwnerAlias(context.OwnerId, rootOwnerId);
            }

            Uri documentUri = ResolveDocumentBaseUri(ownerDocument);
            if (documentUri != null && documentUri.IsAbsoluteUri)
            {
                string documentOwnerId = documentUri.AbsoluteUri;
                RegisterImageOwner(cacheKey, documentOwnerId);
                RegisterOwnerAlias(documentOwnerId, rootOwnerId);
            }
        }

        private static void RegisterOwnerAlias(string alias, string rootOwnerId)
        {
            if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(rootOwnerId))
            {
                _ownerAliases[alias] = rootOwnerId;
            }
        }

        private static bool IsContextUnavailable(ImageLoaderRequestContext context) =>
            context?.IsDisposed == true || context?.OwnerIsDisposed == true;

        private static bool OwnerMatches(string candidate, string ownerId)
        {
            return !string.IsNullOrWhiteSpace(candidate) &&
                !string.IsNullOrWhiteSpace(ownerId) &&
                (string.Equals(candidate, ownerId, StringComparison.Ordinal) ||
                 candidate.StartsWith(ownerId + ":", StringComparison.Ordinal));
        }

        private static bool IsOwnerScopedKey(string key, string ownerId)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                !string.IsNullOrWhiteSpace(ownerId) &&
                key.StartsWith(ownerId + "\n", StringComparison.Ordinal);
        }

        /// <summary>
        /// Phase 7: remove all image ownership registrations for a disposed owner.
        /// </summary>
        public static void ReleaseOwner(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                return;
            }

            var ownerIds = new HashSet<string>(StringComparer.Ordinal)
            {
                ownerId
            };
            foreach (var alias in _ownerAliases)
            {
                if (OwnerMatches(alias.Key, ownerId))
                {
                    ownerIds.Add(alias.Key);
                    if (!string.IsNullOrWhiteSpace(alias.Value))
                    {
                        ownerIds.Add(alias.Value);
                    }
                }
                else if (OwnerMatches(alias.Value, ownerId))
                {
                    ownerIds.Add(alias.Key);
                }
            }
            foreach (var registeredOwner in _ownerCacheGenerations.Keys)
            {
                if (OwnerMatches(registeredOwner, ownerId))
                {
                    ownerIds.Add(registeredOwner);
                }
            }
            foreach (var registeredOwner in _ownerToImages.Keys)
            {
                if (OwnerMatches(registeredOwner, ownerId))
                {
                    ownerIds.Add(registeredOwner);
                }
            }
            foreach (var registeredOwner in _ownerViewports.Keys)
            {
                if (OwnerMatches(registeredOwner, ownerId))
                {
                    ownerIds.Add(registeredOwner);
                }
            }
            foreach (var registeredOwner in _animatedGifOwners.Keys)
            {
                if (OwnerMatches(registeredOwner, ownerId))
                {
                    ownerIds.Add(registeredOwner);
                }
            }

            foreach (var registeredOwner in ownerIds)
            {
                _ownerCacheGenerations.TryRemove(registeredOwner, out _);
                _animatedGifOwners.TryRemove(registeredOwner, out _);
                _ownerViewports.TryRemove(registeredOwner, out _);
            }

            foreach (var entry in _ownerToImages)
            {
                if (!ownerIds.Contains(entry.Key) && !OwnerMatches(entry.Key, ownerId))
                {
                    continue;
                }

                if (_ownerToImages.TryRemove(entry.Key, out _))
                {
                    foreach (var cacheKey in entry.Value.Keys)
                    {
                        if (_imageToOwners.TryGetValue(cacheKey, out var owners))
                        {
                            owners.TryRemove(entry.Key, out _);
                        }
                    }
                }
            }

            foreach (var entry in _imageToOwners)
            {
                foreach (var registeredOwner in entry.Value.Keys)
                {
                    if (ownerIds.Contains(registeredOwner) || OwnerMatches(registeredOwner, ownerId))
                    {
                        entry.Value.TryRemove(registeredOwner, out _);
                    }
                }

                if (entry.Value.IsEmpty)
                {
                    _imageToOwners.TryRemove(entry.Key, out _);
                }
            }

            foreach (var entry in _lazyRegistry)
            {
                if (entry.Value != null &&
                    (ownerIds.Contains(entry.Value.OwnerId) || OwnerMatches(entry.Value.OwnerId, ownerId)))
                {
                    _lazyRegistry.TryRemove(entry.Key, out _);
                }
            }

            var pendingKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _pendingLoadContexts)
            {
                bool owned = false;
                foreach (var contextEntry in entry.Value)
                {
                    var context = contextEntry.Value;
                    if (context != null &&
                        (ownerIds.Contains(context.OwnerId) || OwnerMatches(context.OwnerId, ownerId)))
                    {
                        context.IsDisposed = true;
                        owned = true;
                    }
                }

                if (owned)
                {
                    pendingKeys.Add(entry.Key);
                }

                foreach (var contextEntry in entry.Value)
                {
                    var context = contextEntry.Value;
                    if (context != null &&
                        (ownerIds.Contains(context.OwnerId) || OwnerMatches(context.OwnerId, ownerId)))
                    {
                        entry.Value.TryRemove(contextEntry.Key, out _);
                    }
                }

                if (entry.Value.IsEmpty)
                {
                    _pendingLoadContexts.TryRemove(entry.Key, out _);
                }
            }

            bool pendingChanged = false;
            lock (_pendingLock)
            {
                foreach (var pendingKey in pendingKeys)
                {
                    pendingChanged |= _pendingLoads.Remove(pendingKey);
                }
            }
            if (pendingChanged)
            {
                NotifyPendingLoadCountChanged();
            }

            foreach (var registeredOwner in ownerIds)
            {
                foreach (var key in _lastLoadResults.Keys)
                {
                    if (IsOwnerScopedKey(key, registeredOwner))
                    {
                        _lastLoadResults.TryRemove(key, out _);
                    }
                }
                foreach (var key in _svgRequestMetadata.Keys)
                {
                    if (IsOwnerScopedKey(key, registeredOwner))
                    {
                        _svgRequestMetadata.TryRemove(key, out _);
                    }
                }
                foreach (var key in _failedDataUriCache.Keys)
                {
                    if (IsOwnerScopedKey(key, registeredOwner))
                    {
                        _failedDataUriCache.TryRemove(key, out _);
                    }
                }
            }

            DrainFailedDataUriOrder();

            foreach (var alias in _ownerAliases)
            {
                if (ownerIds.Contains(alias.Key) ||
                    OwnerMatches(alias.Key, ownerId) ||
                    OwnerMatches(alias.Value, ownerId))
                {
                    _ownerAliases.TryRemove(alias.Key, out _);
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
        
        /// <summary>
        /// The image pipeline renders SVG with the first-party renderer only. The
        /// ambient backend selection is deliberately not consulted here, so no
        /// compatibility or hybrid backend can ever produce cached image pixels.
        /// </summary>
        internal static ISvgRenderer CreateSvgRenderer()
        {
            return SvgRendererFactory.GetRenderer(SvgRendererBackend.FirstParty);
        }
        
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
                    var cacheKey = CreateRequestCacheKey(uri.AbsoluteUri, context);
                    RecordLoadResult(uri.AbsoluteUri, cacheKey, result, context);
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
            return TryGetLastLoadResult(url, _ambientContext.Value, out result);
        }

        private static bool TryGetLastLoadResult(
            string url,
            ImageLoaderRequestContext context,
            out BinaryFetchResult result)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                result = null;
                return false;
            }

            Uri svgBaseUri = ResolveSvgRequestBaseUri(normalizedUrl, null);
            bool isSvgRequest = IsSvgRequest(normalizedUrl) ||
                IsKnownSvgRequest(normalizedUrl, context);
            string resultKey = CreateLoadResultKey(
                normalizedUrl,
                context,
                svgBaseUri,
                isSvgRequest);
            if (_lastLoadResults.TryGetValue(resultKey, out result))
            {
                return true;
            }

            if (!isSvgRequest)
            {
                string svgResultKey = CreateLoadResultKey(
                    normalizedUrl,
                    context,
                    svgBaseUri,
                    true);
                if (_lastLoadResults.TryGetValue(svgResultKey, out result))
                {
                    return true;
                }
            }

            return context == null && _lastLoadResults.TryGetValue(normalizedUrl, out result);
        }

        // Emits authoritative pending network-image load count changes.
        public static event Action<int> PendingLoadCountChanged;

        // ========== Memory Management Properties ==========
        
        /// <summary>
        /// Current memory usage by cached images in bytes
        /// </summary>
        public static long CurrentCacheBytes => Volatile.Read(ref _currentCacheBytes);
        
        /// <summary>
        /// Number of images currently cached
        /// </summary>
        public static int CacheCount
        {
            get
            {
                lock (_cacheLock)
                {
                    return _memoryCache.Count + _animatedGifs.Count;
                }
            }
        }

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
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return false;
            }

            string cacheKey = CreateRequestCacheKey(
                normalizedUrl,
                context,
                svgBaseUri: ResolveSvgRequestBaseUri(normalizedUrl, ownerDocument));
            RegisterRequestOwner(cacheKey, context, ownerDocument);
            if (HasExactCacheKey(cacheKey))
            {
                return true;
            }

            return HasCacheEntryForRequest(
                normalizedUrl,
                context,
                svgBaseUri: ResolveSvgRequestBaseUri(normalizedUrl, ownerDocument));
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
        public static void RegisterLazyImage(
            string url,
            SKRect elementBounds,
            string cacheKey = null,
            string ownerId = null,
            int? targetWidth = null,
            int? targetHeight = null,
            Uri svgBaseUri = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return;
            }

            var limits = GetActiveSvgRenderLimits();
            bool isDataUri = normalizedUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            bool? isSvgOverride = null;
            if (isDataUri)
            {
                if (!TryClassifyDataUri(normalizedUrl, limits, out bool dataIsSvg, out _))
                {
                    return;
                }

                isSvgOverride = dataIsSvg;
            }

            var requestContext = new ImageLoaderRequestContext { OwnerId = ownerId };
            cacheKey = CreateRequestCacheKey(
                normalizedUrl,
                requestContext,
                targetWidth,
                targetHeight,
                svgBaseUri,
                isSvgRequestOverride: isSvgOverride);
            RegisterRequestOwner(cacheKey, requestContext);
            if (string.IsNullOrEmpty(cacheKey) || HasExactCacheKey(cacheKey))
            {
                return;
            }

            _lazyRegistry[cacheKey] = new LazyImageInfo
            {
                Url = normalizedUrl,
                OwnerId = ownerId,
                ElementBounds = elementBounds,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
                SvgBaseUri = svgBaseUri,
                LoadStarted = false
            };

            EngineLogCompat.Debug($"[ImageLoader] Registered lazy image: {LogUrl.Describe(normalizedUrl)}",
                           LogCategory.Rendering);
        }

        /// <summary>
        /// Update the current viewport bounds. Triggers loading of visible lazy images.
        /// </summary>
        public static void UpdateViewport(SKRect viewportBounds)
        {
            var requestContext = _ambientContext.Value;
            if (IsContextUnavailable(requestContext))
            {
                return;
            }

            var ownerId = requestContext?.OwnerId ?? string.Empty;
            _ownerViewports[ownerId] = viewportBounds;

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
                if (kvp.Value.LoadStarted ||
                    !string.Equals(kvp.Value.OwnerId ?? string.Empty, ownerId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (expandedViewport.IntersectsWith(kvp.Value.ElementBounds))
                {
                    kvp.Value.LoadStarted = true;
                    if (TryRegisterPendingLoad(kvp.Key))
                    {
                        _ = LoadImageAsync(
                            kvp.Value.Url,
                            kvp.Key,
                            isLazy: true,
                            targetWidth: kvp.Value.TargetWidth,
                            targetHeight: kvp.Value.TargetHeight,
                            context: GetPendingLoadContext(kvp.Key),
                            svgBaseUri: kvp.Value.SvgBaseUri);
                    }
                }
            }
        }

        /// <summary>
        /// Check if an image should be lazy loaded (not yet visible)
        /// </summary>
        public static bool IsLazyPending(
            string url,
            int? targetWidth = null,
            int? targetHeight = null,
            Document ownerDocument = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return false;
            }

            var context = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            bool isSvgRequest = IsSvgRequest(normalizedUrl) ||
                IsKnownSvgRequest(normalizedUrl, context);
            Uri svgBaseUri = ResolveSvgRequestBaseUri(normalizedUrl, ownerDocument);
            string requestPrefix = CreateRequestKeyPrefix(
                normalizedUrl,
                context,
                isSvgRequest,
                svgBaseUri);
            string variantPrefix = requestPrefix + "\nsize:";
            string ownerId = context?.OwnerId ?? string.Empty;

            foreach (var entry in _lazyRegistry)
            {
                var info = entry.Value;
                if (info == null ||
                    !string.Equals(info.OwnerId ?? string.Empty, ownerId, StringComparison.Ordinal) ||
                    (!string.Equals(entry.Key, requestPrefix, StringComparison.Ordinal) &&
                     !entry.Key.StartsWith(variantPrefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (targetWidth.HasValue && info.TargetWidth != targetWidth)
                {
                    continue;
                }

                if (targetHeight.HasValue && info.TargetHeight != targetHeight)
                {
                    continue;
                }

                if (!info.LoadStarted)
                {
                    return true;
                }
            }

            return false;
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

            lock (_cacheLock)
            {
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
                _currentCacheBytes = 0;
                _cacheHitCount = 0;
                _cacheMissCount = 0;
                _cacheEvictionCount = 0;
            }

            _lastLoadResults.Clear();
            _svgRequestMetadata.Clear();
            _failedDataUriCache.Clear();
            DrainFailedDataUriOrder();

            foreach (var bitmap in disposalSet)
            {
                ScheduleBitmapDispose(bitmap);
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
             _imageToOwners.Clear();
             _ownerToImages.Clear();
             _ownerAliases.Clear();
             _ownerViewports.Clear();
             _ownerCacheGenerations.Clear();
             _animatedGifOwners.Clear();

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
                    AnimatedImage removedAnimated = null;
                    lock (_cacheLock)
                    {
                        if (_animatedGifs.TryRemove(candidate.Key, out var animated))
                        {
                            removedAnimated = animated;
                            _currentCacheBytes -= animated.ByteSize;
                            _cacheEvictionCount++;
                        }
                    }

                    if (removedAnimated != null)
                    {
                        if (removedAnimated.Frames != null)
                        {
                            foreach (var frame in removedAnimated.Frames)
                            {
                                ScheduleBitmapDispose(frame);
                            }
                        }

                        EngineLogCompat.Debug($"[ImageLoader] Evicted animated image: {LogUrl.Describe(candidate.Key)}",
                            LogCategory.Rendering);
                    }

                    continue;
                }

                ImageCacheEntry removedEntry = null;
                lock (_cacheLock)
                {
                    if (_memoryCache.TryRemove(candidate.Key, out var entry))
                    {
                        _legacyCache.TryRemove(candidate.Key, out _);
                        removedEntry = entry;
                        _currentCacheBytes -= entry.ByteSize;
                        _cacheEvictionCount++;
                    }
                }

                if (removedEntry != null)
                {
                    ScheduleBitmapDispose(removedEntry.Bitmap);
                    EngineLogCompat.Debug($"[ImageLoader] Evicted: {LogUrl.Describe(candidate.Key)}",
                        LogCategory.Rendering);
                }
            }
        }


        private static void ScheduleBitmapDispose(SKBitmap bitmap)
        {
            // Image paint nodes hold raw SKBitmap references across immutable paint trees.
            // Disposing cache-owned bitmaps during eviction or cache clear can invalidate an
            // in-flight frame and crash native Skia access. Once cache ownership is dropped,
            // let normal GC/finalization reclaim the bitmap after the last renderer reference
            // is gone instead of forcing eager disposal here.
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
            int staticImageCount;
            int animatedImageCount;
            int animatedFrameCount;
            lock (_cacheLock)
            {
                bytes = _currentCacheBytes;
                hits = _cacheHitCount;
                misses = _cacheMissCount;
                evictions = _cacheEvictionCount;
                staticImageCount = _memoryCache.Count;
                animatedImageCount = _animatedGifs.Count;
                animatedFrameCount = _animatedGifs.Values.Sum(anim => anim?.Frames?.Length ?? 0);
            }

            return new ImageCacheSnapshot(
                staticImageCount,
                animatedImageCount,
                animatedFrameCount,
                bytes,
                PendingLoadCount,
                _lazyRegistry.Count(x => !x.Value.LoadStarted),
                hits,
                misses,
                evictions);
        }
        
        /// <summary>
        /// Single first-party admission invariant for decoded SVG pixels. Composes the
        /// shared fail-closed predicate with the backend requirement: only a first-party
        /// result that reports success, declares no fallback, and rejected no resource may
        /// reach the image cache or a paint tree.
        /// </summary>
        internal static bool IsAdmissibleSvgPixels(SvgRenderResult result) =>
            SvgRenderResult.IsAdmissible(result) &&
            result.Backend == SvgRendererBackend.FirstParty;

        /// <summary>
        /// RULE 3 & 5: Render SVG content to bitmap using adapter with safety limits
        /// </summary>
        private static SKBitmap RenderSvgToBitmap(
            string svgContent,
            int? targetWidth,
            int? targetHeight,
            Uri baseUri = null,
            ISvgResourceResolver resourceResolver = null,
            SvgRenderLimits? limits = null,
            string diagnosticSource = null)
        {
            var activeLimits = SvgRenderLimits.Normalize(limits ?? SvgRenderLimits.Default);
            if (resourceResolver != null)
            {
                activeLimits.AllowExternalReferences = true;
            }
            if (string.IsNullOrWhiteSpace(svgContent) ||
                svgContent.Length > activeLimits.MaxSourceChars ||
                !TryAdmitSvgTargetSize(targetWidth, targetHeight, activeLimits))
            {
                return null;
            }

            // The markup reaches the parser verbatim. The first-party parser already
            // treats an undeclared root <svg> as SVG, and XML names are case-sensitive,
            // so textual namespace or 'viewbox' fix-ups would only rewrite author text.
            using var result = CreateSvgRenderer().Render(new SvgRenderRequest(
                svgContent, activeLimits)
            {
                BaseUri = baseUri,
                ResourceResolver = resourceResolver,
                DiagnosticSource = diagnosticSource
            });

            if (!IsAdmissibleSvgPixels(result))
            {
                EngineLogCompat.Debug(
                    $"[ImageLoader] SVG render rejected: {SvgRenderResult.DescribeRejection(result) ?? "non-first-party backend"}",
                    LogCategory.Rendering);
                return null;
            }

            if (result.Bitmap == null)
            {
                EngineLogCompat.Debug("[ImageLoader] SVG render failed: no bitmap produced", LogCategory.Rendering);
                return null;
            }

            // The document's natural size and the requested size must both fit the
            // raster caps. This is judged on the one real render: a separate probe
            // render only to learn the natural size doubled every SVG cache miss.
            if (!TryAdmitSvgRasterDimensions(result.Width, result.Height, activeLimits) ||
                !TryAdmitSvgRasterDimensions(
                    targetWidth ?? result.Width, targetHeight ?? result.Height, activeLimits))
            {
                return null;
            }

            if (!TryResolveSvgRasterSize(
                    result.Bitmap.Width,
                    result.Bitmap.Height,
                    targetWidth,
                    targetHeight,
                    activeLimits,
                    out int w,
                    out int h))
            {
                return null;
            }

            if (w == result.Bitmap.Width && h == result.Bitmap.Height)
            {
                return result.DetachBitmap();
            }

            var scaledBitmap = new SKBitmap();
            var scaledInfo = new SKImageInfo(
                w,
                h,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            if (!scaledBitmap.TryAllocPixels(scaledInfo))
            {
                scaledBitmap.Dispose();
                return null;
            }
            using (var canvas = new SKCanvas(scaledBitmap))
            {
                canvas.Clear(SKColors.Transparent);

                float srcW = result.Bitmap.Width;
                float srcH = result.Bitmap.Height;
                float srcAspect = srcW / srcH;
                float destAspect = (float)w / h;

                float destW, destH, destX, destY;
                if (srcAspect > destAspect)
                {
                    destW = w;
                    destH = w / srcAspect;
                    destX = 0;
                    destY = (h - destH) / 2;
                }
                else
                {
                    destH = h;
                    destW = h * srcAspect;
                    destX = (w - destW) / 2;
                    destY = 0;
                }

                var srcRect = new SKRect(0, 0, srcW, srcH);
                var destRect = new SKRect(destX, destY, destX + destW, destY + destH);
                canvas.DrawBitmap(result.Bitmap, srcRect, destRect, SKSamplingOptions.Default);
            }

            return scaledBitmap;
        }

        private static bool TryAdmitSvgTargetSize(
            int? targetWidth,
            int? targetHeight,
            SvgRenderLimits limits)
        {
            if (targetWidth.HasValue &&
                (targetWidth.Value <= 0 || targetWidth.Value > limits.MaxRasterWidth))
            {
                return false;
            }

            if (targetHeight.HasValue &&
                (targetHeight.Value <= 0 || targetHeight.Value > limits.MaxRasterHeight))
            {
                return false;
            }

            if (targetWidth.HasValue && targetHeight.HasValue &&
                (long)targetWidth.Value * targetHeight.Value > limits.MaxRasterPixels)
            {
                return false;
            }

            return true;
        }

        private static bool TryAdmitSvgRasterDimensions(
            double width,
            double height,
            SvgRenderLimits limits)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width <= 0 || height <= 0)
            {
                return false;
            }

            double rasterWidth = Math.Ceiling(width);
            double rasterHeight = Math.Ceiling(height);
            return rasterWidth <= limits.MaxRasterWidth &&
                rasterHeight <= limits.MaxRasterHeight &&
                rasterWidth * rasterHeight <= limits.MaxRasterPixels;
        }

        private static bool TryResolveSvgRasterSize(
            double naturalWidth,
            double naturalHeight,
            int? requestedWidth,
            int? requestedHeight,
            SvgRenderLimits limits,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;

            double candidateWidth = requestedWidth.HasValue && requestedWidth.Value > 0
                ? requestedWidth.Value
                : naturalWidth;
            double candidateHeight = requestedHeight.HasValue && requestedHeight.Value > 0
                ? requestedHeight.Value
                : naturalHeight;

            if (!double.IsFinite(candidateWidth) || !double.IsFinite(candidateHeight) ||
                candidateWidth <= 0 || candidateHeight <= 0)
            {
                return false;
            }

            double rasterWidth = Math.Ceiling(candidateWidth);
            double rasterHeight = Math.Ceiling(candidateHeight);
            if (rasterWidth > limits.MaxRasterWidth || rasterHeight > limits.MaxRasterHeight ||
                rasterWidth * rasterHeight > limits.MaxRasterPixels)
            {
                return false;
            }

            width = (int)rasterWidth;
            height = (int)rasterHeight;
            return width > 0 && height > 0;
        }

        private static SvgRenderLimits GetActiveSvgRenderLimits() =>
            SvgRenderLimits.Normalize(SvgRenderLimits.Default);

        private const int MaxImageUrlChars = 8_192;
        private const int MaxDataUriUrlChars = 12 * 1024 * 1024;

        private static int GetMaxImageUrlChars(string url) =>
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? MaxDataUriUrlChars
                : MaxImageUrlChars;

        private static string NormalizeImageUrl(string url)
        {
            if (url == null)
            {
                return string.Empty;
            }

            int first = 0;
            while (first < url.Length && char.IsWhiteSpace(url[first]))
            {
                first++;
            }

            int last = url.Length - 1;
            while (last >= first && char.IsWhiteSpace(url[last]))
            {
                last--;
            }

            if (last < first)
            {
                return string.Empty;
            }

            string normalized = (first == 0 && last == url.Length - 1)
                ? url
                : url.Substring(first, last - first + 1);

            return normalized.Length <= GetMaxImageUrlChars(normalized)
                ? normalized
                : string.Empty;
        }

        private const int MaxDataUriHeaderChars = 4096;
        private const int MaxDataUriPreviewBytes = 2048;

        private readonly record struct DataUriInfo(
            string MimeType,
            bool IsBase64,
            int PayloadStart,
            int PayloadLength);

        private static bool IsSvgRequest(
            string url,
            string contentType = null,
            byte[] data = null,
            Uri responseUri = null)
        {
            if (IsSvgContentType(contentType))
            {
                return true;
            }

            if (TryParseDataUri(url, out DataUriInfo dataUri))
            {
                return IsSvgContentType(dataUri.MimeType) || LooksLikeSvgPayload(data);
            }

            if (HasSvgUriPath(url) ||
                (responseUri != null && responseUri.IsAbsoluteUri && HasSvgUriPath(responseUri.AbsoluteUri)))
            {
                return true;
            }

            return LooksLikeSvgPayload(data);
        }

        private static bool IsSvgContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            contentType = contentType.Trim();
            int separator = contentType.IndexOf(';');
            string mimeType = separator >= 0
                ? contentType.Substring(0, separator)
                : contentType;
            return mimeType.Trim().Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDataUriMimeType(string url, out string mimeType)
        {
            mimeType = null;
            return TryParseDataUri(url, out DataUriInfo info) &&
                (mimeType = info.MimeType) != null;
        }

        private static bool TryParseDataUri(string url, out DataUriInfo info)
        {
            info = default;
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            ReadOnlySpan<char> value = url.AsSpan().TrimStart();
            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int leadingWhitespace = url.Length - value.Length;
            int comma = value.IndexOf(',');
            if (comma < 5 || comma - 5 > MaxDataUriHeaderChars)
            {
                return false;
            }

            ReadOnlySpan<char> header = value.Slice(5, comma - 5);
            int separator = header.IndexOf(';');
            ReadOnlySpan<char> media = (separator >= 0 ? header.Slice(0, separator) : header).Trim();
            if (media.Length == 0)
            {
                return false;
            }

            bool isBase64 = false;
            int parameterStart = separator >= 0 ? separator + 1 : header.Length;
            while (parameterStart <= header.Length)
            {
                int next = header.Slice(parameterStart).IndexOf(';');
                int end = next < 0 ? header.Length : parameterStart + next;
                ReadOnlySpan<char> parameter = header.Slice(parameterStart, end - parameterStart).Trim();
                if (parameter.Length > 0 && parameter.Equals("base64", StringComparison.OrdinalIgnoreCase))
                {
                    isBase64 = next < 0;
                }

                if (next < 0)
                {
                    break;
                }
                parameterStart = end + 1;
            }

            int payloadStart = leadingWhitespace + comma + 1;
            info = new DataUriInfo(
                media.ToString(),
                isBase64,
                payloadStart,
                value.Length - payloadStart);
            return true;
        }

        private static bool IsImageMimeType(string mimeType) =>
            !string.IsNullOrWhiteSpace(mimeType) &&
            mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        private static bool HasSvgUriPath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeSvgPayload(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return false;
            }

            try
            {
                string prefix = System.Text.Encoding.UTF8.GetString(
                    data,
                    0,
                    Math.Min(data.Length, MaxDataUriPreviewBytes));
                return prefix.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAdmitSvgDataUriPayload(
            string url,
            DataUriInfo info,
            SvgRenderLimits limits)
        {
            int maxBytes = GetMaxSvgDataBytes(limits);
            if (info.PayloadLength < 0)
            {
                return false;
            }

            if (info.IsBase64)
            {
                return TryValidateBase64PayloadLength(
                    url,
                    info.PayloadStart,
                    maxBytes,
                    out _);
            }

            return TryValidatePercentPayloadLength(
                url,
                info.PayloadStart,
                maxBytes);
        }

        private static bool TryAdmitDataUriPayload(
            string url,
            DataUriInfo info,
            SvgRenderLimits limits,
            int maxBytes)
        {
            if (info.PayloadLength < 0)
            {
                return false;
            }

            return info.IsBase64
                ? TryValidateBase64PayloadLength(url, info.PayloadStart, maxBytes, out _)
                : TryValidatePercentPayloadLength(url, info.PayloadStart, maxBytes);
        }

        private static bool TryValidateBase64PayloadLength(
            string url,
            int payloadStart,
            int maxBytes,
            out int decodedLength)
        {
            decodedLength = 0;
            if (maxBytes <= 0 || payloadStart < 0 || payloadStart > url.Length)
            {
                return false;
            }

            long maxEncodedChars = ((long)maxBytes + 2L) / 3L * 4L;
            long effectiveChars = 0;
            int padding = 0;
            bool sawPadding = false;
            for (int i = payloadStart; i < url.Length;)
            {
                if (!TryReadDataUriCharacter(url, ref i, out char value))
                {
                    return false;
                }
                if (IsSvgBase64Whitespace(value))
                {
                    continue;
                }

                if (value == '=')
                {
                    sawPadding = true;
                    padding++;
                    if (padding > 2)
                    {
                        return false;
                    }
                }
                else
                {
                    if (sawPadding || !IsBase64Character(value))
                    {
                        return false;
                    }
                }

                effectiveChars++;
                if (effectiveChars > maxEncodedChars)
                {
                    return false;
                }
            }

            if (effectiveChars == 0 || effectiveChars % 4 == 1 ||
                (padding > 0 && effectiveChars % 4 != 0))
            {
                return false;
            }

            long length = effectiveChars / 4L * 3L;
            if (padding > 0)
            {
                length -= padding;
            }
            else if (effectiveChars % 4L == 2)
            {
                length += 1L;
            }
            else if (effectiveChars % 4L == 3)
            {
                length += 2L;
            }

            if (length <= 0 || length > maxBytes)
            {
                return false;
            }

            decodedLength = (int)length;
            return true;
        }

        private static bool TryValidatePercentPayloadLength(
            string url,
            int payloadStart,
            int maxBytes)
        {
            if (maxBytes <= 0 || payloadStart < 0 || payloadStart > url.Length)
            {
                return false;
            }

            long decodedLength = 0;
            for (int i = payloadStart; i < url.Length;)
            {
                if (url[i] == '%')
                {
                    if (i + 2 >= url.Length ||
                        !TryHex(url[i + 1], out int high) ||
                        !TryHex(url[i + 2], out int low))
                    {
                        return false;
                    }
                    decodedLength++;
                    i += 3;
                }
                else if (url[i] <= 0x7f)
                {
                    decodedLength++;
                    i++;
                }
                else
                {
                    decodedLength += 4;
                    i++;
                }

                if (decodedLength > maxBytes)
                {
                    return false;
                }
            }

            return decodedLength > 0;
        }

        private static bool TryReadDataUriCharacter(
            string url,
            ref int index,
            out char value)
        {
            value = '\0';
            if (index < 0 || index >= url.Length)
            {
                return false;
            }

            if (url[index] != '%')
            {
                if (url[index] > 0x7f)
                {
                    return false;
                }
                value = url[index++];
                return true;
            }

            if (index + 2 >= url.Length ||
                !TryHex(url[index + 1], out int high) ||
                !TryHex(url[index + 2], out int low))
            {
                return false;
            }

            int decoded = (high << 4) | low;
            if (decoded > 0x7f)
            {
                return false;
            }

            value = (char)decoded;
            index += 3;
            return true;
        }

        private static bool IsBase64Character(char value) =>
            value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '+' or '/' or '=';

        private static bool TryClassifyDataUri(
            string url,
            SvgRenderLimits limits,
            out bool isSvg,
            out DataUriInfo info)
        {
            isSvg = false;
            if (!TryParseDataUri(url, out info))
            {
                return false;
            }

            if (IsSvgContentType(info.MimeType))
            {
                isSvg = true;
                return TryAdmitSvgDataUriPayload(url, info, limits);
            }

            if (!IsImageMimeType(info.MimeType))
            {
                return false;
            }

            if (TryDataUriPayloadLooksLikeSvg(url, info))
            {
                return false;
            }

            return TryAdmitDataUriPayload(
                url,
                info,
                limits,
                GetMaxDataUriBytes(limits));
        }

        private static bool TryDataUriPayloadLooksLikeSvg(string url, DataUriInfo info)
        {
            if (info.PayloadLength <= 0)
            {
                return false;
            }

            byte[] preview;
            bool decoded = info.IsBase64
                ? TryDecodeBase64Prefix(url, info.PayloadStart, MaxDataUriPreviewBytes, out preview)
                : TryDecodePercentPayload(
                    url,
                    info.PayloadStart,
                    MaxDataUriPreviewBytes,
                    out preview,
                    stopAtLimit: true);
            return decoded && LooksLikeSvgPayload(preview);
        }

        private static int GetMaxDataUriBytes(SvgRenderLimits limits) =>
            Math.Max(1, Math.Min(limits.MaxDecodedImageBytes, limits.MaxSourceChars));

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
                    if (IsContextUnavailable(context))
                    {
                        continue;
                    }

                    var ownerId = string.IsNullOrWhiteSpace(context.OwnerId) ? "_default" : context.OwnerId;
                    contexts[ownerId] = context;
                }
            }

            if (fallbackContext != null && !IsContextUnavailable(fallbackContext))
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

                    if (IsContextUnavailable(context))
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

                    if (IsContextUnavailable(context))
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
            url = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(url)) return null;
            var loadContext = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            if (IsContextUnavailable(loadContext))
            {
                return null;
            }

            var svgBaseUri = ResolveSvgRequestBaseUri(url, ownerDocument);
            bool isDataUri = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            bool isSvgRequest = IsSvgRequest(url) || IsKnownSvgRequest(url, loadContext);
            var activeSvgLimits = GetActiveSvgRenderLimits();
            if (isDataUri)
            {
                if (!TryClassifyDataUri(
                        url,
                        activeSvgLimits,
                        out bool dataIsSvg,
                        out _))
                {
                    return null;
                }
                isSvgRequest = dataIsSvg;
            }
            if (isSvgRequest && !TryAdmitSvgTargetSize(targetWidth, targetHeight, activeSvgLimits))
            {
                return null;
            }

            var cacheKey = CreateRequestCacheKey(
                url, loadContext, targetWidth, targetHeight, svgBaseUri);

            if (IsCssImageFunction(url))
            {
                EngineLogCompat.Debug($"[ImageLoader] Ignoring non-fetchable CSS image function: {LogUrl.Describe(url)}", LogCategory.Rendering);
                return null;
            }

            RegisterRequestOwner(cacheKey, loadContext, ownerDocument);
            if (TryGetCachedBitmap(cacheKey, out var cachedBitmap))
            {
                RegisterCacheHit();
                if (_animatedGifs.ContainsKey(cacheKey))
                {
                    NoteAnimatedImageOwner(loadContext);
                }
                return cachedBitmap;
            }

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                _failedDataUriCache.ContainsKey(cacheKey))
            {
                return null;
            }

            RegisterCacheMiss();

            // For lazy images, check if we should defer loading
            if (isLazy && elementBounds.HasValue && NetworkConfiguration.Instance.EnableLazyLoading)
            {
                // Check if element is currently in viewport
                var config = NetworkConfiguration.Instance;
                var threshold = config.LazyLoadThresholdPx;
                var viewport = ResolveViewport(loadContext);
                var expandedViewport = new SKRect(
                    viewport.Left - threshold,
                    viewport.Top - threshold,
                    viewport.Right + threshold,
                    viewport.Bottom + threshold
                );

                if (!expandedViewport.IsEmpty && !expandedViewport.IntersectsWith(elementBounds.Value))
                {
                    EngineLogCompat.Debug($"[ImageLoader] Lazy defer: {LogUrl.Describe(url)}", LogCategory.Rendering);
                    // Not in viewport - register for lazy loading
                    CapturePendingLoadContext(cacheKey, loadContext);
                    RegisterLazyImage(
                        url,
                        elementBounds.Value,
                        cacheKey,
                        loadContext?.OwnerId,
                        targetWidth,
                        targetHeight,
                        svgBaseUri);
                    return null; // Renderer should show placeholder
                }
            }

            // Handle Data URIs synchronously to prevent recursion
            if (isDataUri)
            {
                 EngineLogCompat.Debug($"[ImageLoader] Decoding Data URI: {LogUrl.Describe(url)}", LogCategory.Rendering);
                var dataBitmap = DecodeDataUri(
                    url,
                    targetWidth,
                    targetHeight,
                    ownerDocument,
                    svgBaseUri,
                    out bool dataIsSvg);
                if (dataBitmap != null)
                {
                    string dataCacheKey = CreateCacheKey(
                        url,
                        loadContext,
                        targetWidth,
                        targetHeight,
                        svgBaseUri,
                        dataIsSvg);
                    RegisterRequestOwner(dataCacheKey, loadContext, ownerDocument);
                    RecordSvgRequestMetadata(
                        url,
                        loadContext,
                        dataIsSvg,
                        null,
                        svgBaseUri);
                    string previousDataCacheKey = string.Equals(dataCacheKey, cacheKey, StringComparison.Ordinal)
                        ? null
                        : cacheKey;
                    if (TryStoreDecodedBitmap(
                        dataCacheKey,
                        url,
                        dataBitmap,
                        isLazy,
                        previousDataCacheKey))
                    {
                        return dataBitmap;
                    }

                    if (TryGetCachedBitmap(dataCacheKey, out var existingBitmap))
                    {
                        RegisterCacheHit();
                        DisposeBitmap(dataBitmap);
                        return existingBitmap;
                    }

                    DisposeBitmap(dataBitmap);
                }
                RememberFailedDataUri(cacheKey);
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
                loadContext ?? GetPendingLoadContext(cacheKey),
                svgBaseUri);
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
            if (IsContextUnavailable(context))
            {
                return false;
            }

            url = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            var svgBaseUri = ResolveSvgRequestBaseUri(url, ownerDocument);
            bool isSvgRequest = IsSvgRequest(url) || IsKnownSvgRequest(url, context);
            var activeSvgLimits = GetActiveSvgRenderLimits();
            if (isSvgRequest && !TryAdmitSvgTargetSize(targetWidth, targetHeight, activeSvgLimits))
            {
                return false;
            }
            if (isSvgRequest && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !TryAdmitSvgDataUriEnvelope(url, activeSvgLimits))
            {
                return false;
            }

            var cacheKey = CreateRequestCacheKey(
                url, context, targetWidth, targetHeight, svgBaseUri);
            RegisterRequestOwner(cacheKey, context, ownerDocument);
            string requestCacheKey = cacheKey;
            if (TryGetCachedBitmap(cacheKey, out _))
            {
                return true;
            }

            byte[] data;
            if (isSvgRequest)
            {
                data = await ReadBoundedSvgStreamAsync(
                    stream, GetMaxSvgDataBytes(activeSvgLimits)).ConfigureAwait(false);
                if (data == null)
                {
                    return false;
                }
            }
            else
            {
                int maxBytes = GetMaxDataUriBytes(activeSvgLimits);
                byte[] prefix = await ReadBoundedPrefixAsync(
                    stream,
                    Math.Min(maxBytes, MaxDataUriPreviewBytes)).ConfigureAwait(false);
                if (prefix == null)
                {
                    return false;
                }

                if (LooksLikeSvgPayload(prefix))
                {
                    isSvgRequest = true;
                    data = await ReadBoundedStreamAsync(
                        stream,
                        GetMaxSvgDataBytes(activeSvgLimits),
                        prefix).ConfigureAwait(false);
                }
                else
                {
                    data = await ReadBoundedStreamAsync(stream, maxBytes, prefix)
                        .ConfigureAwait(false);
                }
            }

            if (data == null)
            {
                return false;
            }

            if (data == null || data.Length == 0)
            {
                return false;
            }

            isSvgRequest = isSvgRequest || IsSvgRequest(url, null, data, svgBaseUri);
            if (isSvgRequest &&
                (!TryAdmitSvgTargetSize(targetWidth, targetHeight, activeSvgLimits) ||
                 !IsSvgPayloadAdmitted(data, activeSvgLimits)))
            {
                return false;
            }

            cacheKey = CreateCacheKey(
                url,
                context,
                targetWidth,
                targetHeight,
                svgBaseUri,
                isSvgRequest);
            RegisterRequestOwner(cacheKey, context, ownerDocument);
            RecordSvgRequestMetadata(
                url,
                context,
                isSvgRequest,
                isSvgRequest ? "image/svg+xml" : null,
                svgBaseUri);
            SvgResourceSnapshot svgResources = null;
            if (isSvgRequest && svgBaseUri != null)
            {
                svgResources = await BuildSvgResourceSnapshotAsync(
                    System.Text.Encoding.UTF8.GetString(data),
                    svgBaseUri,
                    context?.FetchDetailedAsync ?? FetchDetailedAsync,
                    context?.FetchBytesAsync ?? FetchBytesAsync,
                    context).ConfigureAwait(false);
            }
            string previousCacheKey = string.Equals(cacheKey, requestCacheKey, StringComparison.Ordinal)
                ? null
                : requestCacheKey;
            var bitmap = DecodeBitmapFromBytes(
                url,
                data,
                targetWidth,
                targetHeight,
                out bool isCached,
                cacheKey,
                svgBaseUri,
                svgResources,
                isSvgRequest,
                previousCacheKey: previousCacheKey);
            if (bitmap == null)
            {
                return false;
            }

            if (!isCached &&
                !TryStoreDecodedBitmap(cacheKey, url, bitmap, isLazy, previousCacheKey))
            {
                bool cached = TryGetCachedBitmap(cacheKey, out _);
                DisposeBitmap(bitmap);
                return cached;
            }

            RequestDebouncedRepaint();
            RequestDebouncedRelayout();
            return true;
        }

        private static SKBitmap DecodeDataUri(
            string url,
            int? targetWidth,
            int? targetHeight,
            Document ownerDocument,
            out bool isSvg) =>
            DecodeDataUri(url, targetWidth, targetHeight, ownerDocument, null, out isSvg);

        private static SKBitmap DecodeDataUri(
            string url,
            int? targetWidth,
            int? targetHeight,
            Document ownerDocument,
            Uri svgBaseUri,
            out bool isSvg)
        {
            isSvg = false;
            try
            {
                var limits = GetActiveSvgRenderLimits();
                if (!TryClassifyDataUri(url, limits, out isSvg, out DataUriInfo info))
                {
                    return null;
                }

                if (isSvg && !TryAdmitSvgTargetSize(targetWidth, targetHeight, limits))
                {
                    return null;
                }

                int maxBytes = isSvg
                    ? GetMaxSvgDataBytes(limits)
                    : GetMaxDataUriBytes(limits);
                if (!TryAdmitDataUriPayload(url, info, limits, maxBytes))
                {
                    return null;
                }

                byte[] bytes;
                if (info.IsBase64)
                {
                    if (!TryDecodeBase64Payload(url, info.PayloadStart, maxBytes, out bytes))
                    {
                        return null;
                    }
                }
                else if (!TryDecodePercentPayload(
                             url,
                             info.PayloadStart,
                             maxBytes,
                             out bytes,
                             stopAtLimit: false))
                {
                    return null;
                }

                if (bytes == null || bytes.Length == 0)
                {
                    return null;
                }

                if (isSvg)
                {
                    if (!IsSvgPayloadAdmitted(bytes, limits))
                    {
                        return null;
                    }

                    string svgContent;
                    try
                    {
                        svgContent = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                    }
                    catch (System.Text.DecoderFallbackException)
                    {
                        return null;
                    }

                    if (svgContent.Length > limits.MaxSourceChars)
                    {
                        return null;
                    }

                    return RenderSvgToBitmap(
                        svgContent,
                        targetWidth,
                        targetHeight,
                        svgBaseUri ?? ResolveSvgRequestBaseUri(url, ownerDocument),
                        null,
                        limits,
                        "data-uri-image");
                }

                if (LooksLikeSvgPayload(bytes))
                {
                    return null;
                }

                return DecodeBoundedRasterDataUri(bytes, targetWidth, targetHeight, limits);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[ImageLoader] Data URI Decode Error: {ex.Message}", LogCategory.Rendering);
                return null;
            }
        }

        private static SKBitmap DecodeBoundedRasterDataUri(
            byte[] bytes,
            int? targetWidth,
            int? targetHeight,
            SvgRenderLimits limits) =>
            DecodeBoundedRasterPayload(
                bytes,
                targetWidth,
                targetHeight,
                limits,
                cacheKey: null,
                previousCacheKey: null,
                out _);

        private static SKBitmap DecodeBoundedRasterPayload(
            byte[] data,
            int? targetWidth,
            int? targetHeight,
            SvgRenderLimits limits,
            string cacheKey,
            string previousCacheKey,
            out bool isCached)
        {
            isCached = false;
            if (data == null || data.Length == 0 ||
                data.Length > limits.MaxDecodedImageBytes ||
                data.Length > limits.MaxCumulativeResourceBytes)
            {
                return null;
            }

            try
            {
                using var skData = SKData.CreateCopy(data);
                using var codec = SKCodec.Create(skData);
                if (codec == null ||
                    !TryResolveRasterDecodeInfo(
                        codec,
                        targetWidth,
                        targetHeight,
                        limits,
                        out SKImageInfo imageInfo,
                        out bool canDecodeIntrinsic))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(cacheKey) && codec.FrameCount > 1)
                {
                    AnimatedImage animated = DecodeAnimatedGif(
                        codec,
                        imageInfo,
                        canDecodeIntrinsic,
                        limits);
                    if (animated == null)
                    {
                        return null;
                    }

                    if (TryStoreAnimatedImage(cacheKey, animated, previousCacheKey))
                    {
                        isCached = true;
                        return animated.Frames[0];
                    }

                    DisposeAnimatedImage(animated);
                    if (TryGetCachedBitmap(cacheKey, out SKBitmap cachedBitmap))
                    {
                        isCached = true;
                        return cachedBitmap;
                    }
                    return null;
                }

                int requiredFrame = -1;
                if (codec.FrameCount > 1 &&
                    codec.GetFrameInfo(0, out var firstFrameInfo))
                {
                    requiredFrame = firstFrameInfo.RequiredFrame;
                }
                return DecodeCodecFrame(
                    codec,
                    0,
                    requiredFrame,
                    imageInfo,
                    canDecodeIntrinsic,
                    useCodecOptions: false) ??
                    DecodeEncodedImageAtIntrinsicSize(skData, imageInfo);
            }
            catch
            {
                return null;
            }
        }

        private static SKBitmap DecodeEncodedImageAtIntrinsicSize(
            SKData encoded,
            SKImageInfo imageInfo)
        {
            if (encoded == null || encoded.IsEmpty ||
                imageInfo.Width <= 0 || imageInfo.Height <= 0)
            {
                return null;
            }

            try
            {
                using var image = SKImage.FromEncodedData(encoded);
                if (image == null ||
                    image.Width != imageInfo.Width ||
                    image.Height != imageInfo.Height)
                {
                    return null;
                }

                var decoded = SKBitmap.FromImage(image);
                if (decoded == null ||
                    decoded.Width != imageInfo.Width ||
                    decoded.Height != imageInfo.Height)
                {
                    DisposeBitmap(decoded);
                    return null;
                }

                return decoded;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryResolveRasterDecodeInfo(
            SKCodec codec,
            int? requestedWidth,
            int? requestedHeight,
            SvgRenderLimits limits,
            out SKImageInfo imageInfo,
            out bool canDecodeIntrinsic)
        {
            imageInfo = default;
            canDecodeIntrinsic = false;
            if (codec == null)
            {
                return false;
            }

            SKImageInfo intrinsicInfo = codec.Info;
            if (intrinsicInfo.Width <= 0 || intrinsicInfo.Height <= 0)
            {
                return false;
            }

            double width;
            double height;
            if (requestedWidth.HasValue && requestedHeight.HasValue)
            {
                width = requestedWidth.Value;
                height = requestedHeight.Value;
            }
            else if (requestedWidth.HasValue)
            {
                width = requestedWidth.Value;
                height = Math.Ceiling(
                    width * intrinsicInfo.Height / (double)intrinsicInfo.Width);
            }
            else if (requestedHeight.HasValue)
            {
                height = requestedHeight.Value;
                width = Math.Ceiling(
                    height * intrinsicInfo.Width / (double)intrinsicInfo.Height);
            }
            else
            {
                width = intrinsicInfo.Width;
                height = intrinsicInfo.Height;
            }

            if (!TryAdmitSvgRasterDimensions(width, height, limits))
            {
                return false;
            }

            canDecodeIntrinsic = TryAdmitSvgRasterDimensions(
                intrinsicInfo.Width,
                intrinsicInfo.Height,
                limits);
            imageInfo = new SKImageInfo(
                (int)Math.Ceiling(width),
                (int)Math.Ceiling(height),
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            return true;
        }

        private static SKBitmap DecodeCodecFrame(
            SKCodec codec,
            int frameIndex,
            int requiredFrame,
            SKImageInfo imageInfo,
            bool canDecodeIntrinsic,
            bool useCodecOptions = true)
        {
            if (useCodecOptions &&
                (imageInfo.Width != codec.Info.Width || imageInfo.Height != codec.Info.Height))
            {
                if (!canDecodeIntrinsic)
                {
                    return null;
                }

                SKImageInfo intrinsicInput = codec.Info;
                SKImageInfo intrinsicOutput = new(
                    intrinsicInput.Width,
                    intrinsicInput.Height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul);
                SKBitmap intrinsicBitmap = DecodeCodecFrame(
                    codec,
                    frameIndex,
                    requiredFrame,
                    intrinsicOutput,
                    canDecodeIntrinsic: false,
                    useCodecOptions: true);
                if (intrinsicBitmap == null)
                {
                    return null;
                }

                try
                {
                    SKBitmap resizedBitmap = intrinsicBitmap.Resize(
                        new SKSizeI(imageInfo.Width, imageInfo.Height),
                        SKSamplingOptions.Default);
                    DisposeBitmap(intrinsicBitmap);
                    return resizedBitmap;
                }
                catch
                {
                    DisposeBitmap(intrinsicBitmap);
                    return null;
                }
            }

            var bitmap = new SKBitmap();
            if (!bitmap.TryAllocPixels(imageInfo))
            {
                bitmap.Dispose();
                return null;
            }

            try
            {
                SKCodecResult result = useCodecOptions
                    ? codec.GetPixels(
                        imageInfo,
                        bitmap.GetPixels(),
                        new SKCodecOptions(frameIndex, requiredFrame))
                    : codec.GetPixels(imageInfo, bitmap.GetPixels());
                if (result == SKCodecResult.InvalidScale && canDecodeIntrinsic)
                {
                    bitmap.Dispose();
                    SKImageInfo intrinsicInfo = codec.Info;
                    SKImageInfo intrinsicOutput = new(
                        intrinsicInfo.Width,
                        intrinsicInfo.Height,
                        SKColorType.Bgra8888,
                        SKAlphaType.Premul);
                    bitmap = DecodeCodecFrame(
                        codec,
                        frameIndex,
                        requiredFrame,
                        intrinsicOutput,
                        canDecodeIntrinsic: false,
                        useCodecOptions: useCodecOptions);
                    if (bitmap == null)
                    {
                        return null;
                    }

                    if (bitmap.Width == imageInfo.Width && bitmap.Height == imageInfo.Height)
                    {
                        return bitmap;
                    }

                    SKBitmap resized = bitmap.Resize(
                        new SKSizeI(imageInfo.Width, imageInfo.Height),
                        SKSamplingOptions.Default);
                    DisposeBitmap(bitmap);
                    return resized;
                }

                if (result != SKCodecResult.Success &&
                    result != SKCodecResult.IncompleteInput)
                {
                    bitmap.Dispose();
                    return null;
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                return null;
            }
        }

        internal static SKBitmap GetInlineSvgImage(
            string svgContent,
            int targetWidth,
            int targetHeight,
            Uri baseUri = null,
            Document ownerDocument = null)
        {
            var limits = GetActiveSvgRenderLimits();
            if (string.IsNullOrWhiteSpace(svgContent) ||
                svgContent.Length > limits.MaxSourceChars ||
                !TryAdmitSvgTargetSize(targetWidth, targetHeight, limits))
            {
                return null;
            }

            var context = CreateDocumentRequestContext(_ambientContext.Value, ownerDocument);
            if (IsContextUnavailable(context))
            {
                return null;
            }

            baseUri ??= ResolveDocumentBaseUri(ownerDocument);
            string cacheKey = CreateInlineSvgCacheKey(
                svgContent, context, baseUri, targetWidth, targetHeight, limits);
            if (cacheKey == null)
            {
                return null;
            }

            RegisterRequestOwner(cacheKey, context, ownerDocument);
            if (TryGetCachedBitmap(cacheKey, out var cachedBitmap))
            {
                RegisterCacheHit();
                return cachedBitmap;
            }

            RegisterCacheMiss();
            var bitmap = RenderSvgToBitmap(
                svgContent,
                targetWidth,
                targetHeight,
                baseUri,
                null,
                limits,
                "inline-svg");
            if (bitmap == null)
            {
                return null;
            }

            if (TryStoreDecodedBitmap(cacheKey, "inline SVG", bitmap, isLazy: false))
            {
                return bitmap;
            }

            if (TryGetCachedBitmap(cacheKey, out cachedBitmap))
            {
                RegisterCacheHit();
                DisposeBitmap(bitmap);
                return cachedBitmap;
            }

            DisposeBitmap(bitmap);
            return null;
        }

        private static string CreateInlineSvgCacheKey(
            string svgContent,
            ImageLoaderRequestContext context,
            Uri baseUri,
            int targetWidth,
            int targetHeight,
            SvgRenderLimits limits)
        {
            int byteCount;
            try
            {
                byteCount = System.Text.Encoding.UTF8.GetByteCount(svgContent);
            }
            catch
            {
                return null;
            }

            if (byteCount > limits.MaxDecodedImageBytes)
            {
                return null;
            }

            byte[] contentBytes;
            byte[] hash;
            try
            {
                contentBytes = System.Text.Encoding.UTF8.GetBytes(svgContent);
                using var sha256 = SHA256.Create();
                hash = sha256.ComputeHash(contentBytes);
            }
            catch
            {
                return null;
            }

            string identity = "inline-svg:" + Convert.ToHexString(hash);
            return CreateCacheKey(
                identity,
                context,
                targetWidth,
                targetHeight,
                baseUri,
                isSvgRequest: true);
        }

        private static Task<byte[]> ReadBoundedSvgStreamAsync(Stream stream, int maxBytes) =>
            ReadBoundedStreamAsync(stream, maxBytes);

        private static async Task<byte[]> ReadBoundedPrefixAsync(Stream stream, int maxBytes)
        {
            if (stream == null || maxBytes <= 0)
            {
                return null;
            }

            int capacity = Math.Min(maxBytes, MaxDataUriPreviewBytes);
            var prefix = new byte[capacity];
            int count = 0;
            while (count < capacity)
            {
                int read = await stream.ReadAsync(
                    prefix,
                    count,
                    capacity - count).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                count += read;
            }

            if (count == 0)
            {
                return null;
            }

            if (count != prefix.Length)
            {
                Array.Resize(ref prefix, count);
            }

            return prefix;
        }

        private static async Task<byte[]> ReadBoundedStreamAsync(
            Stream stream,
            int maxBytes,
            byte[] prefix = null)
        {
            if (stream == null || maxBytes <= 0)
            {
                return null;
            }

            using var output = new MemoryStream(Math.Min(maxBytes, 81920));
            if (prefix != null)
            {
                if (prefix.Length > maxBytes)
                {
                    return null;
                }

                output.Write(prefix, 0, prefix.Length);
            }

            var buffer = new byte[81920];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (output.Length > maxBytes - read)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }

        private static bool TryAdmitSvgDataUriEnvelope(string url, SvgRenderLimits limits)
        {
            return TryClassifyDataUri(url, limits, out bool isSvg, out _) && isSvg;
        }

        private static int GetMaxSvgDataBytes(SvgRenderLimits limits) =>
            Math.Max(1, Math.Min(limits.MaxSourceChars, limits.MaxDecodedImageBytes));

        private static bool TryDecodeBase64Payload(
            string url,
            int payloadStart,
            int maxBytes,
            out byte[] bytes)
        {
            bytes = null;
            if (!TryValidateBase64PayloadLength(url, payloadStart, maxBytes, out int decodedLength))
            {
                return false;
            }

            long maxEncodedChars = ((long)maxBytes + 2L) / 3L * 4L;
            int capacity = (int)Math.Min(
                maxEncodedChars,
                Math.Max(4L, (long)url.Length - payloadStart + 4L));
            var clean = new char[capacity];
            int effectiveChars = 0;
            int existingPadding = 0;
            for (int i = payloadStart; i < url.Length && effectiveChars < clean.Length;)
            {
                if (!TryReadDataUriCharacter(url, ref i, out char value) ||
                    IsSvgBase64Whitespace(value))
                {
                    if (value == '\0')
                    {
                        return false;
                    }
                    continue;
                }

                if (value == '=')
                {
                    existingPadding++;
                }
                clean[effectiveChars++] = value;
            }

            if (effectiveChars == 0)
            {
                return false;
            }

            int totalChars = effectiveChars;
            if (existingPadding == 0)
            {
                int remainder = effectiveChars % 4;
                if (remainder == 1)
                {
                    return false;
                }
                if (remainder != 0)
                {
                    if (totalChars + (4 - remainder) > clean.Length)
                    {
                        return false;
                    }
                    while (totalChars % 4 != 0)
                    {
                        clean[totalChars++] = '=';
                    }
                }
            }

            try
            {
                bytes = Convert.FromBase64CharArray(clean, 0, totalChars);
            }
            catch (FormatException)
            {
                return false;
            }

            return bytes != null && bytes.Length == decodedLength && bytes.Length <= maxBytes;
        }

        private static bool TryDecodeBase64Prefix(
            string url,
            int payloadStart,
            int maxBytes,
            out byte[] bytes)
        {
            bytes = null;
            if (maxBytes <= 0 || payloadStart < 0 || payloadStart > url.Length)
            {
                return false;
            }

            int capacity = ((maxBytes + 2) / 3) * 4;
            var clean = new char[capacity];
            int effectiveChars = 0;
            for (int i = payloadStart; i < url.Length && effectiveChars < clean.Length;)
            {
                if (!TryReadDataUriCharacter(url, ref i, out char value))
                {
                    return false;
                }
                if (IsSvgBase64Whitespace(value))
                {
                    continue;
                }
                if (value == '=')
                {
                    clean[effectiveChars++] = value;
                    break;
                }
                if (!IsBase64Character(value) || value == '=')
                {
                    return false;
                }
                clean[effectiveChars++] = value;
            }

            if (effectiveChars == 0)
            {
                return false;
            }

            int remainder = effectiveChars % 4;
            if (remainder == 1)
            {
                return false;
            }
            if (remainder != 0)
            {
                while (effectiveChars % 4 != 0)
                {
                    clean[effectiveChars++] = '=';
                }
            }

            try
            {
                bytes = Convert.FromBase64CharArray(clean, 0, effectiveChars);
            }
            catch (FormatException)
            {
                return false;
            }

            return bytes != null && bytes.Length > 0;
        }

        private static bool TryDecodePercentPayload(
            string url,
            int payloadStart,
            int maxBytes,
            out byte[] bytes,
            bool stopAtLimit)
        {
            bytes = null;
            if (maxBytes <= 0 || payloadStart < 0 || payloadStart > url.Length)
            {
                return false;
            }

            long upperBound = Math.Min(
                (long)maxBytes,
                Math.Max(1L, (long)(url.Length - payloadStart) * 4L));
            var output = new byte[(int)upperBound];
            int count = 0;
            for (int i = payloadStart; i < url.Length;)
            {
                if (url[i] == '%')
                {
                    if (i + 2 >= url.Length ||
                        !TryHex(url[i + 1], out int high) ||
                        !TryHex(url[i + 2], out int low))
                    {
                        return false;
                    }
                    if (count >= output.Length)
                    {
                        return false;
                    }
                    output[count++] = (byte)((high << 4) | low);
                    i += 3;
                }
                else if (url[i] <= 0x7f)
                {
                    if (count >= output.Length)
                    {
                        return false;
                    }
                    output[count++] = (byte)url[i++];
                }
                else
                {
                    int scalar;
                    if (char.IsHighSurrogate(url[i]))
                    {
                        if (i + 1 >= url.Length || !char.IsLowSurrogate(url[i + 1]))
                        {
                            return false;
                        }
                        scalar = char.ConvertToUtf32(url[i], url[i + 1]);
                        i += 2;
                    }
                    else if (char.IsLowSurrogate(url[i]))
                    {
                        return false;
                    }
                    else
                    {
                        scalar = url[i++];
                    }

                    int encodedLength = scalar <= 0x7f ? 1 :
                        scalar <= 0x7ff ? 2 :
                        scalar <= 0xffff ? 3 : 4;
                    if (count > output.Length - encodedLength)
                    {
                        return false;
                    }
                    if (encodedLength == 2)
                    {
                        output[count++] = (byte)(0xc0 | (scalar >> 6));
                        output[count++] = (byte)(0x80 | (scalar & 0x3f));
                    }
                    else if (encodedLength == 3)
                    {
                        output[count++] = (byte)(0xe0 | (scalar >> 12));
                        output[count++] = (byte)(0x80 | ((scalar >> 6) & 0x3f));
                        output[count++] = (byte)(0x80 | (scalar & 0x3f));
                    }
                    else
                    {
                        output[count++] = (byte)(0xf0 | (scalar >> 18));
                        output[count++] = (byte)(0x80 | ((scalar >> 12) & 0x3f));
                        output[count++] = (byte)(0x80 | ((scalar >> 6) & 0x3f));
                        output[count++] = (byte)(0x80 | (scalar & 0x3f));
                    }
                }

                if (stopAtLimit && count >= maxBytes)
                {
                    break;
                }
            }

            if (count == 0)
            {
                return false;
            }
            if (count != output.Length)
            {
                Array.Resize(ref output, count);
            }
            bytes = output;
            return true;
        }

        private static bool IsSvgBase64Whitespace(char value) =>
            value is '\r' or '\n' or ' ' or '\t' or '\f' or '\v';

        private static bool TryHex(char value, out int result)
        {
            if (value is >= '0' and <= '9')
            {
                result = value - '0';
                return true;
            }
            if (value is >= 'a' and <= 'f')
            {
                result = value - 'a' + 10;
                return true;
            }
            if (value is >= 'A' and <= 'F')
            {
                result = value - 'A' + 10;
                return true;
            }
            result = 0;
            return false;
        }


        private static void RememberFailedDataUri(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || !_failedDataUriCache.TryAdd(cacheKey, 0))
            {
                return;
            }

            _failedDataUriOrder.Enqueue(cacheKey);
            while (_failedDataUriCache.Count > MAX_FAILED_DATA_URI_CACHE_ENTRIES)
            {
                if (_failedDataUriOrder.TryDequeue(out var oldestKey))
                {
                    _failedDataUriCache.TryRemove(oldestKey, out _);
                    continue;
                }

                foreach (var staleKey in _failedDataUriCache.Keys)
                {
                    if (_failedDataUriCache.TryRemove(staleKey, out _))
                    {
                        break;
                    }
                }
            }
        }

        private static void DrainFailedDataUriOrder()
        {
            while (_failedDataUriOrder.TryDequeue(out _))
            {
            }

            foreach (var key in _failedDataUriCache.Keys)
            {
                _failedDataUriOrder.Enqueue(key);
            }
        }

        private readonly record struct SvgRequestMetadata(string ContentType, Uri ResponseUri);
        private readonly record struct PendingSvgResource(Uri Uri, int Depth);
        private readonly record struct FetchedSvgResource(
            PendingSvgResource Pending,
            BinaryFetchResult Result);

        private static async Task<SvgResourceSnapshot> BuildSvgResourceSnapshotAsync(
            string source,
            Uri documentUri,
            Func<Uri, Task<BinaryFetchResult>> detailedFetcher,
            Func<Uri, Task<byte[]>> byteFetcher,
            ImageLoaderRequestContext context)
        {
            var snapshot = new SvgResourceSnapshot();
            if (documentUri == null || !documentUri.IsAbsoluteUri)
                return snapshot;

            var limits = SvgRenderLimits.Normalize(SvgRenderLimits.Default);
            if (string.IsNullOrWhiteSpace(source) || source.Length > limits.MaxSourceChars)
                return snapshot;

            int maxDepth = Math.Min(16, limits.MaxReferenceDepth);
            int maxCount = limits.MaxResourceCount;
            long remainingBytes = limits.MaxCumulativeResourceBytes;
            var pending = new Queue<PendingSvgResource>();
            var seen = new HashSet<Uri>();
            var preloadClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (Uri uri in SvgResourceDiscovery.DiscoverImages(source, documentUri, limits))
            {
                if (seen.Add(uri)) pending.Enqueue(new PendingSvgResource(uri, 1));
            }

            while (pending.Count > 0 && snapshot.Count < maxCount &&
                    !IsContextUnavailable(context) &&

                   preloadClock.ElapsedMilliseconds < MaxSvgResourcePreloadMilliseconds)
            {
                var batch = new List<PendingSvgResource>(MaxSvgResourcePreloadConcurrency);
                while (pending.Count > 0 && batch.Count < MaxSvgResourcePreloadConcurrency &&
                       snapshot.Count + batch.Count < maxCount)
                    batch.Add(pending.Dequeue());

                Task<FetchedSvgResource[]> fetchBatch = Task.WhenAll(batch.Select(async item =>
                {
                    try
                    {
                        BinaryFetchResult result;
                        if (detailedFetcher != null)
                        {
                            result = await detailedFetcher(item.Uri).ConfigureAwait(false);
                        }
                        else if (byteFetcher != null)
                        {
                            byte[] body = await byteFetcher(item.Uri).ConfigureAwait(false);
                            result = new BinaryFetchResult
                            {
                                Body = body,
                                FinalUri = item.Uri,
                                FailureReason = body == null || body.Length == 0
                                    ? BinaryFetchFailureReason.BodyReadFailed
                                    : BinaryFetchFailureReason.None
                            };
                        }
                        else
                        {
                            result = null;
                        }
                        return new FetchedSvgResource(item, result);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug(
                            $"[ImageLoader] Nested SVG resource fetch failed: {ex.GetType().Name}",
                            LogCategory.Rendering);
                        return new FetchedSvgResource(item, null);
                    }
                }));
                FetchedSvgResource[] fetched;
                int remainingMilliseconds = Math.Max(
                    1, MaxSvgResourcePreloadMilliseconds - (int)preloadClock.ElapsedMilliseconds);
                try
                {
                    fetched = await fetchBatch.WaitAsync(
                        TimeSpan.FromMilliseconds(remainingMilliseconds)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    EngineLogCompat.Warn(
                        $"[ImageLoader] SVG nested-resource preload timed out after " +
                        $"{MaxSvgResourcePreloadMilliseconds}ms; admitted={snapshot.Count}",
                        LogCategory.Rendering);
                    break;
                }

                foreach (var item in fetched)
                {
                    BinaryFetchResult result = item.Result;
                    byte[] body = result?.Body;
                    Uri finalUri = result?.FinalUri ?? item.Pending.Uri;
                    if (result?.Succeeded != true || body == null || body.Length == 0 ||
                        body.Length > limits.MaxDecodedImageBytes || body.Length > remainingBytes ||
                        !SvgResourceDiscovery.IsSameOrigin(documentUri, finalUri))
                        continue;

                    string contentType = result.ContentType ?? string.Empty;
                    snapshot.Add(item.Pending.Uri, contentType, body);
                    remainingBytes -= body.Length;

                    if (item.Pending.Depth >= maxDepth ||
                        !LooksLikeSvgResource(item.Pending.Uri, contentType, body))
                        continue;
                    try
                    {
                        if (body.Length > limits.MaxSourceChars)
                            continue;
                        string nestedSource = new System.Text.UTF8Encoding(false, true).GetString(body);
                        foreach (Uri nestedUri in SvgResourceDiscovery.DiscoverImages(
                                     nestedSource, item.Pending.Uri, limits))
                        {
                            if (seen.Count >= maxCount || !seen.Add(nestedUri)) continue;
                            pending.Enqueue(new PendingSvgResource(
                                nestedUri, item.Pending.Depth + 1));
                        }
                    }
                    catch (System.Text.DecoderFallbackException)
                    {
                        // The renderer will reject the malformed SVG snapshot entry.
                    }
                }
            }
            return snapshot;
        }

        private static bool IsSvgPayloadAdmitted(byte[] data, SvgRenderLimits limits)
        {
            return data != null && data.Length > 0 &&
                data.Length <= limits.MaxDecodedImageBytes &&
                data.Length <= limits.MaxSourceChars &&
                data.Length <= limits.MaxCumulativeResourceBytes;
        }

        private static bool LooksLikeSvgResource(Uri uri, string contentType, byte[] body)
        {
            return IsSvgRequest(
                uri != null && uri.IsAbsoluteUri ? uri.AbsoluteUri : null,
                contentType,
                body,
                uri);
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
            ImageLoaderRequestContext context = null,
            Uri svgBaseUri = null)
        {
            string requestKey = cacheKey;
            string storageKey = null;
            bool semaphoreHeld = false;
            try
            {
                url = NormalizeImageUrl(url);
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(requestKey))
                {
                    return;
                }

                if (TryGetCachedBitmap(requestKey, out _))
                {
                    return;
                }

                var effectiveContext = context ?? _ambientContext.Value;
                if (IsContextUnavailable(effectiveContext))
                {
                    return;
                }

                if (!await WaitForLoadSlotAsync(GetLoadAdmissionTimeout()).ConfigureAwait(false))
                {
                    EngineLogCompat.Warn(
                        $"[ImageLoader] Load admission timed out; dropping {(url.Length > 80 ? url.Substring(0, 80) + "..." : url)}",
                        LogCategory.Rendering);
                    return;
                }

                semaphoreHeld = true;
                if (TryGetCachedBitmap(requestKey, out _))
                {
                    return;
                }

                if (IsContextUnavailable(effectiveContext))
                {
                    return;
                }

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var limits = GetActiveSvgRenderLimits();
                    if (!TryClassifyDataUri(
                            url,
                            limits,
                            out bool dataIsSvg,
                            out DataUriInfo dataUri))
                    {
                        RememberFailedDataUri(requestKey);
                        return;
                    }

                    if (dataIsSvg &&
                        !TryAdmitSvgTargetSize(targetWidth, targetHeight, limits))
                    {
                        RememberFailedDataUri(requestKey);
                        return;
                    }

                    Uri dataBaseUri = svgBaseUri ?? ResolveSvgRequestBaseUri(url, null);
                    storageKey = CreateCacheKey(
                        url,
                        effectiveContext,
                        targetWidth,
                        targetHeight,
                        dataBaseUri,
                        dataIsSvg);
                    RegisterRequestOwner(storageKey, effectiveContext);
                    RekeyRequestState(requestKey, storageKey);
                    var dataResult = new BinaryFetchResult
                    {
                        Body = null,
                        ContentType = dataUri.MimeType,
                        FinalUri = dataBaseUri,
                        FailureReason = BinaryFetchFailureReason.None,
                        DecodeFormat = dataIsSvg ? "image/svg+xml" : dataUri.MimeType
                    };
                    RecordLoadResult(url, storageKey, dataResult, effectiveContext, dataBaseUri);
                    SKBitmap dataBitmap = DecodeDataUri(
                        url,
                        targetWidth,
                        targetHeight,
                        null,
                        dataBaseUri,
                        out dataIsSvg);
                    if (dataBitmap == null)
                    {
                        RecordLoadResult(
                            url,
                            storageKey,
                            dataResult with { DecodeFailureReason = "Data URI decode failed" },
                            effectiveContext,
                            dataBaseUri);
                        RememberFailedDataUri(storageKey);
                        return;
                    }

                    if (IsContextUnavailable(effectiveContext))
                    {
                        DisposeBitmap(dataBitmap);
                        return;
                    }

                    if (TryStoreDecodedBitmap(
                            storageKey,
                            url,
                            dataBitmap,
                            isLazy,
                            string.Equals(storageKey, requestKey, StringComparison.Ordinal)
                                ? null
                                : requestKey))
                    {
                        RequestDebouncedRepaint(storageKey, effectiveContext);
                        RequestDebouncedRelayout(storageKey, effectiveContext);
                    }
                    else
                    {
                        DisposeBitmap(dataBitmap);
                    }
                    return;
                }

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    EngineLogCompat.Warn($"[ImageLoader] Skipped non-HTTP URL: {LogUrl.Describe(url)}", LogCategory.Rendering);
                    return;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
                {
                    EngineLogCompat.Warn($"[ImageLoader] Invalid absolute URI, skipping: {LogUrl.Describe(url)}", LogCategory.Rendering);
                    return;
                }

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

                if (IsContextUnavailable(effectiveContext))
                {
                    return;
                }

                RecordLoadResult(
                    url,
                    requestKey,
                    fetchResult,
                    effectiveContext,
                    svgBaseUri ?? ResolveSvgRequestBaseUri(url, null));

                if (!fetchResult.Succeeded)
                {
                    EngineLogCompat.Warn($"[ImageLoader] Fetch failed: url={LogUrl.Describe(url)} reason={fetchResult.FailureReason} status={fetchResult.StatusCode}", LogCategory.Rendering);
                    return;
                }

                var data = fetchResult.Body;
                if (data == null || data.Length == 0)
                {
                    EngineLogCompat.Warn($"[ImageLoader] Empty image response for: {LogUrl.Describe(url)}", LogCategory.Rendering);
                    return;
                }

                var activeSvgLimits = GetActiveSvgRenderLimits();
                var requestedSvgUri = fetchResult.FinalUri != null && fetchResult.FinalUri.IsAbsoluteUri
                    ? fetchResult.FinalUri
                    : absoluteUri;
                bool isSvgRequest = IsSvgRequest(
                    url,
                    fetchResult.ContentType,
                    data,
                    requestedSvgUri);
                var cacheBaseUri = svgBaseUri ?? ResolveSvgRequestBaseUri(url, null);
                storageKey = CreateCacheKey(
                    url,
                    effectiveContext,
                    targetWidth,
                    targetHeight,
                    cacheBaseUri,
                    isSvgRequest);
                RegisterRequestOwner(storageKey, effectiveContext);
                RecordSvgRequestMetadata(
                    url,
                    effectiveContext,
                    isSvgRequest,
                    fetchResult.ContentType,
                    requestedSvgUri,
                    cacheBaseUri);
                RekeyRequestState(requestKey, storageKey);
                if (!string.Equals(requestKey, storageKey, StringComparison.Ordinal))
                {
                    RecordLoadResult(
                        url,
                        storageKey,
                        fetchResult,
                        effectiveContext,
                        cacheBaseUri);
                    RemoveStaleLoadResultKey(
                        requestKey,
                        url,
                        effectiveContext,
                        cacheBaseUri);
                }

                if (isSvgRequest &&
                    (!TryAdmitSvgTargetSize(targetWidth, targetHeight, activeSvgLimits) ||
                     !IsSvgPayloadAdmitted(data, activeSvgLimits)))
                {
                    RecordLoadResult(url, storageKey, fetchResult with
                    {
                        DecodeFailureReason = "SVG payload exceeds admission budget"
                    }, effectiveContext, cacheBaseUri);
                    return;
                }

                var decodeFormat = DetectDecodeFormat(
                    url,
                    data,
                    fetchResult.ContentType,
                    requestedSvgUri);
                SKBitmap bitmap;
                bool bitmapIsCached = false;
                try
                {
                    SvgResourceSnapshot svgResources = null;
                    if (isSvgRequest)
                    {
                        svgResources = await BuildSvgResourceSnapshotAsync(
                            System.Text.Encoding.UTF8.GetString(data),
                            requestedSvgUri,
                            detailedFetcher,
                            fetcher,
                            effectiveContext).ConfigureAwait(false);
                    }
                    bitmap = DecodeBitmapFromBytes(
                        url,
                        data,
                        targetWidth,
                        targetHeight,
                        out bitmapIsCached,
                        storageKey,
                        requestedSvgUri,
                        svgResources,
                        isSvgRequest,
                        fetchResult.ContentType,
                        requestedSvgUri,
                        previousCacheKey: requestKey);
                }
                catch (Exception ex)
                {
                    RecordLoadResult(url, storageKey, fetchResult with
                    {
                        DecodeFormat = decodeFormat,
                        DecodeFailureReason = ex.Message
                    }, effectiveContext, cacheBaseUri);
                    EngineLogCompat.Warn($"[ImageLoader] Decode failed: url={LogUrl.Describe(url)} format={decodeFormat ?? "unknown"}", LogCategory.Rendering);
                    return;
                }

                if (bitmap != null)
                {
                    if (IsContextUnavailable(effectiveContext))
                    {
                        DisposeBitmap(bitmap);
                        return;
                    }

                    if (bitmapIsCached ||
                        TryStoreDecodedBitmap(storageKey, url, bitmap, isLazy, requestKey))
                    {
                        RecordLoadResult(
                            url,
                            storageKey,
                            fetchResult with { DecodeFormat = decodeFormat },
                            effectiveContext,
                            cacheBaseUri);
                        RequestDebouncedRepaint(storageKey ?? requestKey, effectiveContext);
                        RequestDebouncedRelayout(storageKey ?? requestKey, effectiveContext);
                    }
                    else
                    {
                        DisposeBitmap(bitmap);
                    }
                }
                else
                {
                    RecordLoadResult(url, storageKey, fetchResult with
                    {
                        DecodeFormat = decodeFormat,
                        DecodeFailureReason = "Decoder returned no bitmap"
                    }, effectiveContext, cacheBaseUri);
                    EngineLogCompat.Warn($"[ImageLoader] Decode Failed: {LogUrl.Describe(url)}", LogCategory.Rendering);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[ImageLoader] Error loading {LogUrl.Describe(url)}: {ex.Message}", LogCategory.Rendering);
            }
            finally
            {
                if (semaphoreHeld)
                {
                    _loadSemaphore.Release();
                }

                CompletePendingLoad(requestKey);
                if (!string.Equals(storageKey, requestKey, StringComparison.Ordinal))
                {
                    CompletePendingLoad(storageKey);
                }
            }
        }

        private static TimeSpan GetLoadAdmissionTimeout()
        {
            int resourceTimeoutSeconds =
                Math.Max(1, NetworkConfiguration.Instance.ResourceTimeoutSeconds);
            return TimeSpan.FromSeconds(Math.Min(resourceTimeoutSeconds, 60) * 4);
        }

        private static async Task<bool> WaitForLoadSlotAsync(TimeSpan timeout)
        {
            if (_loadSemaphore.CurrentCount > 0 &&
                await _loadSemaphore.WaitAsync(0).ConfigureAwait(false))
            {
                return true;
            }

            var deadlineTicks = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                if (await _loadSemaphore
                        .WaitAsync(LoadAdmissionPollMilliseconds)
                        .ConfigureAwait(false))
                {
                    return true;
                }

                if (Environment.TickCount64 >= deadlineTicks)
                {
                    return false;
                }
            }
        }

        private static string DetectDecodeFormat(
            string url,
            byte[] data,
            string contentType,
            Uri responseUri = null)
        {
            if (IsSvgRequest(url, contentType, data, responseUri))
            {
                return string.IsNullOrWhiteSpace(contentType) ? "image/svg+xml" : contentType;
            }
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                return contentType;
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
            out bool isCached,
            string cacheKey = null,
            Uri svgBaseUri = null,
            ISvgResourceResolver svgResourceResolver = null,
            bool? isSvgRequest = null,
            string contentType = null,
            Uri responseUri = null,
            string previousCacheKey = null)
        {
            SKBitmap bitmap = null;
            isCached = false;
            cacheKey ??= url;
            var activeSvgLimits = GetActiveSvgRenderLimits();
            bool isSvg = isSvgRequest ?? IsSvgRequest(url, contentType, data, responseUri);

            if (isSvg)
            {
                if (!IsSvgPayloadAdmitted(data, activeSvgLimits))
                {
                    return null;
                }

                string svgContent = System.Text.Encoding.UTF8.GetString(data);
                if (svgContent.Length > activeSvgLimits.MaxSourceChars)
                {
                    return null;
                }

                bitmap = RenderSvgToBitmap(
                    svgContent, targetWidth, targetHeight,
                    svgBaseUri, svgResourceResolver, activeSvgLimits, "image");

                if (bitmap == null)
                {
                    EngineLogCompat.Warn($"[ImageLoader] SVG Render Failed for: {LogUrl.Describe(url)}", LogCategory.Rendering);
                }

                return bitmap;
            }

            return DecodeBoundedRasterPayload(
                data,
                targetWidth,
                targetHeight,
                activeSvgLimits,
                cacheKey,
                previousCacheKey,
                out isCached);
        }

        private static bool TryStoreAnimatedImage(
            string cacheKey,
            AnimatedImage animated,
            string previousCacheKey = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) ||
                animated?.Frames == null ||
                animated.Frames.Length == 0)
            {
                return false;
            }

            animated.LastAccessed = DateTime.UtcNow;
            lock (_cacheLock)
            {
                if (_memoryCache.ContainsKey(cacheKey) ||
                    _legacyCache.ContainsKey(cacheKey) ||
                    _animatedGifs.ContainsKey(cacheKey) ||
                    !_animatedGifs.TryAdd(cacheKey, animated))
                {
                    return false;
                }

                _currentCacheBytes += animated.ByteSize;
                RemovePreviousCacheEntryLocked(previousCacheKey, cacheKey);
            }

            EvictIfNeeded();
            _lazyRegistry.TryRemove(cacheKey, out _);
            Interlocked.Increment(ref _cacheVersion);
            BumpOwnerGenerations(cacheKey);
            EnsureGifAnimationTimer();
            return true;
        }

        private static bool TryStoreDecodedBitmap(
            string cacheKey,
            string url,
            SKBitmap bitmap,
            bool isLazy,
            string previousCacheKey = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) ||
                bitmap == null ||
                bitmap.IsNull ||
                bitmap.Width <= 0 ||
                bitmap.Height <= 0)
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

            lock (_cacheLock)
            {
                if (_memoryCache.ContainsKey(cacheKey) ||
                    _legacyCache.ContainsKey(cacheKey) ||
                    _animatedGifs.ContainsKey(cacheKey))
                {
                    return false;
                }

                if (!_memoryCache.TryAdd(cacheKey, entry) ||
                    !_legacyCache.TryAdd(cacheKey, bitmap))
                {
                    _memoryCache.TryRemove(cacheKey, out _);
                    _legacyCache.TryRemove(cacheKey, out _);
                    return false;
                }

                _currentCacheBytes += bitmap.ByteCount;
                RemovePreviousCacheEntryLocked(previousCacheKey, cacheKey);
            }

            EvictIfNeeded();
            _lazyRegistry.TryRemove(cacheKey, out _);
            Interlocked.Increment(ref _cacheVersion);
            BumpOwnerGenerations(cacheKey);
            EngineLogCompat.Log(
                LogCategory.Rendering,
                LogLevel.Debug,
                $"[ImageLoader] SUCCESS: {url} ({bitmap.Width}x{bitmap.Height})");
            return true;
        }

        private static void RemovePreviousCacheEntryLocked(
            string previousCacheKey,
            string currentCacheKey)
        {
            if (string.IsNullOrEmpty(previousCacheKey) ||
                string.Equals(previousCacheKey, currentCacheKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_memoryCache.TryRemove(previousCacheKey, out var previousEntry))
            {
                _legacyCache.TryRemove(previousCacheKey, out _);
                _currentCacheBytes -= previousEntry?.ByteSize ?? 0;
            }
            if (_animatedGifs.TryRemove(previousCacheKey, out var previousAnimated))
            {
                _currentCacheBytes -= previousAnimated?.ByteSize ?? 0;
            }
            _lazyRegistry.TryRemove(previousCacheKey, out _);
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

        private static SKRect ResolveViewport(ImageLoaderRequestContext context)
        {
            var ownerId = context?.OwnerId ?? string.Empty;
            return _ownerViewports.TryGetValue(ownerId, out var viewport) ? viewport : SKRect.Empty;
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
                OwnerRootId = context.OwnerRootId ?? context.OwnerId,
                OwnerLifetimeState = context.OwnerLifetimeState,
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
            BinaryFetchResult result,
            ImageLoaderRequestContext context = null,
            Uri svgBaseUri = null)
        {
            if (result == null)
            {
                return;
            }

            string normalizedUrl = NormalizeImageUrl(url);
            bool isSvgRequest = IsSvgRequest(
                normalizedUrl,
                result.ContentType,
                result.Body,
                result.FinalUri);
            svgBaseUri ??= ResolveSvgRequestBaseUri(normalizedUrl, null);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                _lastLoadResults[cacheKey] = result;
            }

            string resultKey = CreateLoadResultKey(
                normalizedUrl,
                context,
                svgBaseUri,
                isSvgRequest);
            if (!string.IsNullOrEmpty(resultKey))
            {
                _lastLoadResults[resultKey] = result;
            }

            if (!string.IsNullOrEmpty(normalizedUrl) &&
                !string.Equals(resultKey, normalizedUrl, StringComparison.Ordinal))
            {
                _lastLoadResults[normalizedUrl] = result;
            }

            RecordSvgRequestMetadata(
                normalizedUrl,
                context,
                isSvgRequest,
                result.ContentType,
                result.FinalUri,
                svgBaseUri);
        }

        private static string CreateRequestCacheKey(
            string url,
            ImageLoaderRequestContext context,
            int? targetWidth = null,
            int? targetHeight = null,
            Uri svgBaseUri = null,
            Document ownerDocument = null,
            bool? isSvgRequestOverride = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return string.Empty;
            }

            bool isSvgRequest = isSvgRequestOverride ??
                (IsSvgRequest(normalizedUrl) || IsKnownSvgRequest(normalizedUrl, context));
            svgBaseUri ??= ResolveSvgRequestBaseUri(normalizedUrl, ownerDocument);
            return CreateCacheKey(
                normalizedUrl,
                context,
                targetWidth,
                targetHeight,
                svgBaseUri,
                isSvgRequest);
        }

        private static string CreateCacheKey(
            string url,
            ImageLoaderRequestContext context,
            int? targetWidth = null,
            int? targetHeight = null,
            Uri svgBaseUri = null,
            bool? isSvgRequest = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return string.Empty;
            }

            bool svg = isSvgRequest ?? IsSvgRequest(normalizedUrl);
            return CreateCanonicalKey(
                normalizedUrl,
                context,
                targetWidth,
                targetHeight,
                svgBaseUri,
                svg,
                includeSize: true);
        }

        private static string CreateCanonicalKey(
            string url,
            ImageLoaderRequestContext context,
            int? targetWidth,
            int? targetHeight,
            Uri svgBaseUri,
            bool isSvgRequest,
            bool includeSize)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            if (string.IsNullOrEmpty(normalizedUrl))
            {
                return string.Empty;
            }

            string key = string.IsNullOrWhiteSpace(context?.OwnerId)
                ? normalizedUrl
                : $"{context.OwnerId}\n{normalizedUrl}";
            if (isSvgRequest && svgBaseUri != null && svgBaseUri.IsAbsoluteUri)
            {
                key += $"\nbase:{svgBaseUri.AbsoluteUri}";
            }

            if (includeSize && (targetWidth.HasValue || targetHeight.HasValue))
            {
                string width = targetWidth.HasValue
                    ? targetWidth.Value.ToString(CultureInfo.InvariantCulture)
                    : "-";
                string height = targetHeight.HasValue
                    ? targetHeight.Value.ToString(CultureInfo.InvariantCulture)
                    : "-";
                key += $"\nsize:{width}:{height}";
            }

            return key;
        }

        private static string CreateRequestKeyPrefix(
            string url,
            ImageLoaderRequestContext context,
            bool? isSvgRequest = null,
            Uri svgBaseUri = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            bool svg = isSvgRequest ??
                (IsSvgRequest(normalizedUrl) || IsKnownSvgRequest(normalizedUrl, context));
            svgBaseUri ??= ResolveSvgRequestBaseUri(normalizedUrl, null);
            return CreateCanonicalKey(
                normalizedUrl,
                context,
                null,
                null,
                svgBaseUri,
                svg,
                includeSize: false);
        }

        private static string CreateLoadResultKey(
            string url,
            ImageLoaderRequestContext context,
            Uri svgBaseUri = null,
            bool? isSvgRequest = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            bool svg = isSvgRequest ??
                (IsSvgRequest(normalizedUrl) || IsKnownSvgRequest(normalizedUrl, context));
            svgBaseUri ??= ResolveSvgRequestBaseUri(normalizedUrl, null);
            return CreateCanonicalKey(
                normalizedUrl,
                context,
                null,
                null,
                svgBaseUri,
                svg,
                includeSize: false);
        }

        private static bool IsKnownSvgRequest(
            string url,
            ImageLoaderRequestContext context)
        {
            return TryGetKnownSvgRequestMetadata(
                       url,
                       context,
                       out string contentType,
                       out Uri responseUri) &&
                   IsSvgRequest(url, contentType, null, responseUri);
        }

        private static void RecordSvgRequestMetadata(
            string url,
            ImageLoaderRequestContext context,
            bool isSvgRequest,
            string contentType,
            Uri responseUri,
            Uri svgBaseUri = null)
        {
            svgBaseUri ??= ResolveSvgRequestBaseUri(url, null);
            string key = CreateLoadResultKey(url, context, svgBaseUri, isSvgRequest);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (isSvgRequest)
            {
                _svgRequestMetadata[key] = new SvgRequestMetadata(contentType, responseUri);
            }
            else
            {
                _svgRequestMetadata.TryRemove(key, out _);
            }
        }

        private static bool TryGetKnownSvgRequestMetadata(
            string url,
            ImageLoaderRequestContext context,
            out string contentType,
            out Uri responseUri,
            Uri svgBaseUri = null,
            bool? isSvgRequest = null)
        {
            contentType = null;
            responseUri = null;
            string normalizedUrl = NormalizeImageUrl(url);
            bool svg = isSvgRequest ?? IsSvgRequest(normalizedUrl);
            string key = CreateLoadResultKey(normalizedUrl, context, svgBaseUri, svg);
            if (!_svgRequestMetadata.TryGetValue(key, out var metadata))
            {
                if (svg)
                {
                    return false;
                }
                key = CreateLoadResultKey(normalizedUrl, context, svgBaseUri, true);
                if (!_svgRequestMetadata.TryGetValue(key, out metadata))
                {
                    return false;
                }
            }

            contentType = metadata.ContentType;
            responseUri = metadata.ResponseUri;
            return true;
        }

        private static bool HasExactCacheKey(string cacheKey)
        {
            return !string.IsNullOrEmpty(cacheKey) &&
                (_memoryCache.ContainsKey(cacheKey) ||
                 _legacyCache.ContainsKey(cacheKey) ||
                 _animatedGifs.ContainsKey(cacheKey));
        }

        private static bool HasCacheEntryForRequest(
            string url,
            ImageLoaderRequestContext context,
            Uri svgBaseUri = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            string requestPrefix = CreateRequestKeyPrefix(
                normalizedUrl,
                context,
                IsSvgRequest(normalizedUrl) || IsKnownSvgRequest(normalizedUrl, context),
                svgBaseUri ?? ResolveSvgRequestBaseUri(normalizedUrl, null));
            if (HasExactCacheKey(requestPrefix))
            {
                return true;
            }

            string variantPrefix = requestPrefix + "\nsize:";
            return _memoryCache.Keys.Any(key => key.StartsWith(variantPrefix, StringComparison.Ordinal)) ||
                _legacyCache.Keys.Any(key => key.StartsWith(variantPrefix, StringComparison.Ordinal)) ||
                _animatedGifs.Keys.Any(key => key.StartsWith(variantPrefix, StringComparison.Ordinal));
        }

        private static void RekeyRequestState(string previousKey, string currentKey)
        {
            if (string.IsNullOrEmpty(previousKey) ||
                string.IsNullOrEmpty(currentKey) ||
                string.Equals(previousKey, currentKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_lazyRegistry.TryRemove(previousKey, out var info))
            {
                _lazyRegistry[currentKey] = info;
            }

            bool pendingMoved = false;
            lock (_pendingLock)
            {
                if (_pendingLoads.Contains(previousKey) &&
                    !_pendingLoads.Contains(currentKey))
                {
                    _pendingLoads.Remove(previousKey);
                    _pendingLoads.Add(currentKey);
                    pendingMoved = true;
                }
            }

            if (pendingMoved &&
                _pendingLoadContexts.TryRemove(previousKey, out var contexts))
            {
                if (_pendingLoadContexts.TryGetValue(currentKey, out var existing))
                {
                    foreach (var entry in contexts)
                    {
                        existing[entry.Key] = entry.Value;
                    }
                }
                else
                {
                    _pendingLoadContexts[currentKey] = contexts;
                }
            }
        }

        private static void RemoveStaleLoadResultKey(
            string cacheKey,
            string url,
            ImageLoaderRequestContext context,
            Uri svgBaseUri = null)
        {
            string normalizedUrl = NormalizeImageUrl(url);
            string stableKey = CreateLoadResultKey(
                normalizedUrl,
                context,
                svgBaseUri ?? ResolveSvgRequestBaseUri(normalizedUrl, null),
                IsSvgRequest(normalizedUrl));
            string svgStableKey = CreateLoadResultKey(
                normalizedUrl,
                context,
                svgBaseUri ?? ResolveSvgRequestBaseUri(normalizedUrl, null),
                true);
            if (!string.IsNullOrEmpty(cacheKey) &&
                !string.Equals(cacheKey, stableKey, StringComparison.Ordinal) &&
                !string.Equals(cacheKey, svgStableKey, StringComparison.Ordinal) &&
                !string.Equals(cacheKey, normalizedUrl, StringComparison.Ordinal))
            {
                _lastLoadResults.TryRemove(cacheKey, out _);
            }
        }

        private static Uri ResolveSvgRequestBaseUri(string url, Document ownerDocument)
        {
            if (url != null && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDocumentBaseUri(ownerDocument);
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
        }

        private static Uri ResolveDocumentBaseUri(Document ownerDocument)
        {
            string value = ownerDocument?.BaseURI;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = ownerDocument?.DocumentURI;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                value = ownerDocument?.URL;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
        }

        private static bool TryGetCachedBitmap(string cacheKey, out SKBitmap bitmap)
        {
            lock (_cacheLock)
            {
                if (_memoryCache.TryGetValue(cacheKey, out var entry) && entry?.Bitmap != null)
                {
                    entry.LastAccessed = DateTime.UtcNow;
                    bitmap = entry.Bitmap;
                    return true;
                }

                if (_legacyCache.TryGetValue(cacheKey, out bitmap) && bitmap != null)
                {
                    return true;
                }
            }

            if (_animatedGifs.TryGetValue(cacheKey, out var animated))
            {
                bitmap = animated.GetCurrentFrame();
                return bitmap != null;
            }

            bitmap = null;
            return false;
        }

        private static void DisposeBitmap(SKBitmap bitmap)
        {
            try
            {
                if (bitmap != null && !bitmap.IsNull)
                {
                    bitmap.Dispose();
                }
            }
            catch
            {
            }
        }

        private static void DisposeAnimatedImage(AnimatedImage animated)
        {
            if (animated?.Frames == null)
            {
                return;
            }

            foreach (SKBitmap frame in animated.Frames)
            {
                DisposeBitmap(frame);
            }
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

        private static bool TryAdmitAnimatedGif(
            int frameCount,
            SKImageInfo imageInfo,
            SvgRenderLimits limits)
        {
            if (IsInvalidCodecFrameCount(frameCount) ||
                imageInfo.Width <= 0 || imageInfo.Height <= 0)
            {
                return false;
            }

            long pixelsPerFrame = (long)imageInfo.Width * imageInfo.Height;
            long maxAggregatePixels = Math.Min(
                limits.MaxRasterPixels,
                limits.MaxCumulativeResourceBytes / 4L);
            if (maxAggregatePixels <= 0 ||
                pixelsPerFrame > maxAggregatePixels / frameCount)
            {
                return false;
            }

            long bytesPerPixel = Math.Max(1L, imageInfo.BytesPerPixel);
            long bytesPerFrame;
            try
            {
                bytesPerFrame = checked(pixelsPerFrame * bytesPerPixel);
            }
            catch (OverflowException)
            {
                return false;
            }

            return bytesPerFrame <= limits.MaxCumulativeResourceBytes / frameCount;
        }

        private static bool IsInvalidCodecFrameCount(int frameCount) =>
            frameCount <= 1 || frameCount > MaxAnimatedGifFrameCount;

        private static AnimatedImage DecodeAnimatedGif(
            SKCodec codec,
            SKImageInfo imageInfo,
            bool canDecodeIntrinsic,
            SvgRenderLimits limits)
        {
            int frameCount = codec.FrameCount;
            if (!TryAdmitAnimatedGif(frameCount, imageInfo, limits))
            {
                return null;
            }

            var frames = new SKBitmap[frameCount];
            var durations = new int[frameCount];
            int decodedCount = 0;
            long byteSize = 0;

            for (int i = 0; i < frameCount; i++)
            {
                if (!codec.GetFrameInfo(i, out SKCodecFrameInfo frameInfo))
                {
                    continue;
                }

                SKBitmap bitmap = DecodeCodecFrame(
                    codec,
                    i,
                    frameInfo.RequiredFrame,
                    imageInfo,
                    canDecodeIntrinsic);
                if (bitmap == null)
                {
                    EngineLogCompat.Warn($"[ImageLoader] GIF frame {i} decode failed", LogCategory.Rendering);
                    continue;
                }

                if (byteSize > limits.MaxCumulativeResourceBytes - bitmap.ByteCount)
                {
                    DisposeBitmap(bitmap);
                    for (int frameIndex = 0; frameIndex < decodedCount; frameIndex++)
                    {
                        DisposeBitmap(frames[frameIndex]);
                    }
                    return null;
                }

                frames[decodedCount] = bitmap;
                durations[decodedCount] = Math.Max(frameInfo.Duration, 50);
                decodedCount++;
                byteSize += bitmap.ByteCount;
            }

            if (decodedCount == 0)
            {
                return null;
            }

            if (decodedCount != frames.Length)
            {
                Array.Resize(ref frames, decodedCount);
                Array.Resize(ref durations, decodedCount);
            }

            long totalDuration = 0;
            for (int i = 0; i < durations.Length; i++)
            {
                totalDuration += durations[i];
            }
            if (totalDuration <= 0)
            {
                totalDuration = frames.Length * 100L;
            }

            int boundedDuration = (int)Math.Min(int.MaxValue, totalDuration);
            var frameEndOffsets = new int[durations.Length];
            long frameEnd = 0;
            for (int i = 0; i < durations.Length; i++)
            {
                frameEnd = Math.Min(boundedDuration, frameEnd + durations[i]);
                frameEndOffsets[i] = (int)frameEnd;
            }

            return new AnimatedImage
            {
                Frames = frames,
                Durations = durations,
                FrameEndOffsets = frameEndOffsets,
                TotalDuration = boundedDuration,
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
                        int newFrameIndex = anim.ResolveFrameIndex((int)posInCycle);
                        double accumulated = anim.FrameEndOffsets != null && newFrameIndex < anim.FrameEndOffsets.Length
                            ? anim.FrameEndOffsets[newFrameIndex]
                            : posInCycle + 50;

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

