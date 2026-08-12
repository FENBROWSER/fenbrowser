using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Resource hint types per W3C Resource Hints spec.
    /// </summary>
    public enum ResourceHint
    {
        /// <summary>Fetch resource in background for future navigation</summary>
        Prefetch,
        /// <summary>Fetch resource with high priority for current page</summary>
        Preload,
        /// <summary>Establish early connection (TCP/TLS handshake)</summary>
        Preconnect,
        /// <summary>Perform early DNS lookup</summary>
        DnsPrefetch,
        /// <summary>Speculatively render page in background</summary>
        Prerender
    }

    /// <summary>
    /// Resource type hints for preload as attribute
    /// </summary>
    public enum PreloadAs
    {
        Unknown,
        Script,
        Style,
        Image,
        Font,
        Fetch,
        Document,
        Audio,
        Video,
        Track,
        Worker
    }

    /// <summary>
    /// Represents a prefetch/preload request
    /// </summary>
    public class PrefetchRequest
    {
        public Uri Url { get; set; }
        public ResourceHint Hint { get; set; }
        public PreloadAs AsType { get; set; }
        public string CrossOrigin { get; set; }
        public string MimeType { get; set; }
        public int Priority { get; set; }
        public DateTimeOffset QueuedAt { get; set; }
        public bool Completed { get; set; }
        internal int Generation { get; set; }
    }

    /// <summary>
    /// Handles resource prefetching, preloading, and connection hints.
    /// Implements W3C Resource Hints and Preload specifications.
    /// </summary>
    public class ResourcePrefetcher : IDisposable
    {
        private readonly ResourceManager _resourceManager;
        private readonly ConcurrentQueue<PrefetchRequest> _queue;
        private readonly ConcurrentDictionary<string, PrefetchRequest> _pending;
        private readonly HashSet<string> _completedUrls;
        private readonly HashSet<string> _preconnectedHosts;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _throttle;
        private readonly List<CancellationTokenSource> _retiredCancellationSources = new();
        private CancellationTokenSource _cts;
        private int _generation;
        private int _activeOperations;
        private int _resourcesDisposed;
        private bool _disposed;

        // Configuration
        public int MaxConcurrentPrefetches { get; set; } = 4;
        public int MaxQueueSize { get; set; } = 100;
        public TimeSpan PrefetchTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool EnablePrefetch { get; set; } = true;
        public bool EnablePreload { get; set; } = true;
        public bool EnablePreconnect { get; set; } = true;
        public bool EnableDnsPrefetch { get; set; } = true;

        /// <summary>
        /// Event fired when a prefetch completes
        /// </summary>
        public event Action<Uri, bool> OnPrefetchComplete;

        public ResourcePrefetcher(ResourceManager resourceManager)
        {
            _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
            _queue = new ConcurrentQueue<PrefetchRequest>();
            _pending = new ConcurrentDictionary<string, PrefetchRequest>();
            _completedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _preconnectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _throttle = new SemaphoreSlim(MaxConcurrentPrefetches);
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Parse and queue prefetch hints from HTML document.
        /// Looks for: <link rel="prefetch|preload|preconnect|dns-prefetch">
        /// </summary>
        public async Task PrefetchFromDomAsync(Element document, Uri baseUri)
        {
            if (document == null || baseUri == null) return;

            try
            {
                var linkElements = new List<Element>();
                CollectLinkElements(document, linkElements);

                foreach (var link in linkElements)
                {
                    var rel = link.GetAttribute("rel")?.ToLowerInvariant();
                    var href = link.GetAttribute("href");

                    if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(href))
                        continue;

                    Uri url;
                    if (!Uri.TryCreate(baseUri, href, out url))
                        continue;

                    var hint = ParseRelToHint(rel);
                    if (hint == null) continue;

                    var asType = ParseAsType(link.GetAttribute("as"));
                    var crossOrigin = link.GetAttribute("crossorigin");
                    var mimeType = link.GetAttribute("type");

                    await QueueHintAsync(url, hint.Value, asType, crossOrigin, mimeType);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[ResourcePrefetcher] Error parsing DOM hints: {ex.Message}", LogCategory.Network);
            }
        }

        /// <summary>
        /// Parse Link headers from HTTP response for preload hints.
        /// Format: Link: </style.css>; rel=preload; as=style
        /// </summary>
        public async Task ProcessLinkHeadersAsync(HttpResponseHeaders headers, Uri baseUri)
        {
            if (headers == null || baseUri == null) return;

            try
            {
                IEnumerable<string> linkValues;
                if (!headers.TryGetValues("Link", out linkValues))
                    return;

                var queueTasks = new List<Task>();
                foreach (var linkValue in linkValues)
                {
                    ParseLinkHeader(linkValue, baseUri, queueTasks);
                }

                if (queueTasks.Count > 0)
                {
                    await Task.WhenAll(queueTasks).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[ResourcePrefetcher] Error parsing Link headers: {ex.Message}", LogCategory.Network);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Queue a prefetch/preload request
        /// </summary>
        public async Task QueueHintAsync(Uri url, ResourceHint hint, PreloadAs asType = PreloadAs.Unknown,
            string crossOrigin = null, string mimeType = null)
        {
            if (url == null) return;

            var key = url.AbsoluteUri;
            PrefetchRequest request;

            lock (_lock)
            {
                if (_disposed)
                    return;

                // Skip if already processed or pending
                if (_completedUrls.Contains(key) || _pending.ContainsKey(key))
                    return;

                // Respect queue limit
                if (_queue.Count >= MaxQueueSize)
                    return;

                request = new PrefetchRequest
                {
                    Url = url,
                    Hint = hint,
                    AsType = asType,
                    CrossOrigin = crossOrigin,
                    MimeType = mimeType,
                    Priority = GetPriority(hint, asType),
                    QueuedAt = DateTimeOffset.UtcNow,
                    Generation = _generation
                };

                if (!_pending.TryAdd(key, request))
                    return;

                _queue.Enqueue(request);
            }

            EngineLogCompat.Debug($"[ResourcePrefetcher] Queued {hint}: {url}", LogCategory.Network);

            // PreloadScanner calls this directly during HTML parse and never
            // touches PrefetchFromDomAsync, so every enqueue owns the processing kick.
            EnsureProcessing();

            await Task.CompletedTask;
        }

        private int _processingFlag;

        private void EnsureProcessing()
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _processingFlag, 1, 0) != 0)
            {
                return;
            }

            CancellationToken token;
            int generation;
            lock (_lock)
            {
                if (_disposed)
                {
                    Volatile.Write(ref _processingFlag, 0);
                    return;
                }

                token = _cts.Token;
                generation = _generation;
                Interlocked.Increment(ref _activeOperations);
            }

            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueueAsync(token, generation).ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _processingFlag, 0);
                        EndOperation();

                        // If new-generation items raced in while an old worker was
                        // cancelling, kick one worker for the current generation.
                        if (!_disposed && !_queue.IsEmpty)
                        {
                            EnsureProcessing();
                        }
                    }
                });
            }
            catch
            {
                Volatile.Write(ref _processingFlag, 0);
                EndOperation();
                throw;
            }
        }

        /// <summary>
        /// Preconnect to a host (TCP/TLS handshake)
        /// </summary>
        public async Task PreconnectAsync(Uri url)
        {
            if (url == null) return;

            var host = url.Host;
            lock (_lock)
            {
                if (_preconnectedHosts.Contains(host))
                    return;
                _preconnectedHosts.Add(host);
            }

            try
            {
                // Trigger connection by making a HEAD request
                var headUri = new Uri($"{url.Scheme}://{url.Host}");
                // The actual connection warming happens in HttpClient's connection pool
                EngineLogCompat.Debug($"[ResourcePrefetcher] Preconnect: {host}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[ResourcePrefetcher] Preconnect failed: {host} - {ex.Message}", LogCategory.Network);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Process the prefetch queue
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken ct, int generation)
        {
            while (!ct.IsCancellationRequested && _queue.TryDequeue(out var request))
            {
                if (request.Generation != generation)
                {
                    if (request.Generation < generation)
                    {
                        RemovePendingIfOwned(request);
                        continue;
                    }

                    // A newer generation was queued while this worker was winding
                    // down. Put it back for the worker that owns that generation.
                    _queue.Enqueue(request);
                    break;
                }

                if (request.Completed) continue;

                try
                {
                    await _throttle.WaitAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _activeOperations);

                    try
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ExecutePrefetchAsync(request, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                _throttle.Release();
                                EndOperation();
                            }
                        });
                    }
                    catch
                    {
                        _throttle.Release();
                        EndOperation();
                        throw;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Execute a single prefetch request
        /// </summary>
        private async Task ExecutePrefetchAsync(PrefetchRequest request, CancellationToken ct)
        {
            var key = request.Url.AbsoluteUri;
            bool success = false;

            try
            {
                switch (request.Hint)
                {
                    case ResourceHint.Preload:
                    case ResourceHint.Prefetch:
                        // Fetch the resource (it will be cached)
                        switch (request.AsType)
                        {
                            case PreloadAs.Image:
                                await _resourceManager.FetchImageAsync(request.Url);
                                break;
                            case PreloadAs.Style:
                            case PreloadAs.Script:
                            case PreloadAs.Fetch:
                            case PreloadAs.Document:
                            default:
                                await _resourceManager.FetchTextAsync(request.Url);
                                break;
                        }
                        success = true;
                        break;

                    case ResourceHint.Preconnect:
                        await PreconnectAsync(request.Url);
                        success = true;
                        break;

                    case ResourceHint.DnsPrefetch:
                        // DNS is resolved automatically by HttpClient
                        success = true;
                        break;
                }

                EngineLogCompat.Debug($"[ResourcePrefetcher] Completed {request.Hint}: {request.Url}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[ResourcePrefetcher] Failed {request.Hint}: {request.Url} - {ex.Message}", LogCategory.Network);
            }
            finally
            {
                bool notify;
                lock (_lock)
                {
                    notify = !_disposed && request.Generation == _generation;
                    if (notify)
                    {
                        RemovePendingIfOwnedLocked(key, request);
                        if (success)
                        {
                            _completedUrls.Add(key);
                        }
                    }
                }

                request.Completed = success;
                if (notify)
                {
                    OnPrefetchComplete?.Invoke(request.Url, success);
                }
            }
        }

        private void RemovePendingIfOwned(PrefetchRequest request)
        {
            if (request?.Url == null)
                return;

            lock (_lock)
            {
                RemovePendingIfOwnedLocked(request.Url.AbsoluteUri, request);
            }
        }

        private void RemovePendingIfOwnedLocked(string key, PrefetchRequest request)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, request))
            {
                _pending.TryRemove(key, out _);
            }
        }

        private void EndOperation()
        {
            if (Interlocked.Decrement(ref _activeOperations) == 0 && _disposed)
            {
                DisposeSynchronizationResources();
            }
        }

        private void DisposeSynchronizationResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            {
                return;
            }

            List<CancellationTokenSource> cancellationSources;
            lock (_lock)
            {
                cancellationSources = new List<CancellationTokenSource>(_retiredCancellationSources.Count + 1);
                cancellationSources.AddRange(_retiredCancellationSources);
                cancellationSources.Add(_cts);
                _retiredCancellationSources.Clear();
            }

            foreach (var source in cancellationSources)
            {
                source.Dispose();
            }

            _throttle.Dispose();
        }

        /// <summary>
        /// Parse Link header value
        /// </summary>
        private void ParseLinkHeader(string linkValue, Uri baseUri, List<Task> queueTasks)
        {
            // Format: </path>; rel=preload; as=style, </other>; rel=prefetch
            var parts = linkValue.Split(',');

            foreach (var part in parts)
            {
                try
                {
                    var urlMatch = Regex.Match(part, @"<([^>]+)>");
                    if (!urlMatch.Success) continue;

                    var href = urlMatch.Groups[1].Value;
                    Uri url;
                    if (!Uri.TryCreate(baseUri, href, out url))
                        continue;

                    var relMatch = Regex.Match(part, @"rel\s*=\s*[""']?(\w+)[""']?", RegexOptions.IgnoreCase);
                    var rel = relMatch.Success ? relMatch.Groups[1].Value.ToLowerInvariant() : null;

                    var hint = ParseRelToHint(rel);
                    if (hint == null) continue;

                    var asMatch = Regex.Match(part, @"\bas\s*=\s*[""']?(\w+)[""']?", RegexOptions.IgnoreCase);
                    var asType = asMatch.Success ? ParseAsType(asMatch.Groups[1].Value) : PreloadAs.Unknown;

                    queueTasks.Add(QueueHintAsync(url, hint.Value, asType));
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[ResourcePrefetcher] Error parsing Link header segment: {ex.Message}", LogCategory.Network);
                }
            }
        }

        /// <summary>
        /// Collect all link elements from document
        /// </summary>
        private void CollectLinkElements(Element element, List<Element> links)
        {
            if (element.TagName?.Equals("link", StringComparison.OrdinalIgnoreCase) == true)
            {
                links.Add(element);
            }

            foreach (var child in element.Children)
            {
                if (child is Element el) CollectLinkElements(el, links);
            }
        }

        /// <summary>
        /// Parse rel attribute to ResourceHint
        /// </summary>
        private static ResourceHint? ParseRelToHint(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;

            return rel switch
            {
                "prefetch" => ResourceHint.Prefetch,
                "preload" => ResourceHint.Preload,
                "preconnect" => ResourceHint.Preconnect,
                "dns-prefetch" => ResourceHint.DnsPrefetch,
                "prerender" => ResourceHint.Prerender,
                _ => null
            };
        }

        /// <summary>
        /// Parse as attribute to PreloadAs
        /// </summary>
        private static PreloadAs ParseAsType(string asValue)
        {
            if (string.IsNullOrEmpty(asValue)) return PreloadAs.Unknown;

            return asValue.ToLowerInvariant() switch
            {
                "script" => PreloadAs.Script,
                "style" => PreloadAs.Style,
                "image" => PreloadAs.Image,
                "font" => PreloadAs.Font,
                "fetch" => PreloadAs.Fetch,
                "document" => PreloadAs.Document,
                "audio" => PreloadAs.Audio,
                "video" => PreloadAs.Video,
                "track" => PreloadAs.Track,
                "worker" => PreloadAs.Worker,
                _ => PreloadAs.Unknown
            };
        }

        /// <summary>
        /// Get priority for request ordering
        /// </summary>
        private static int GetPriority(ResourceHint hint, PreloadAs asType)
        {
            // Higher = more urgent
            int basePriority = hint switch
            {
                ResourceHint.Preload => 100,
                ResourceHint.Prefetch => 50,
                ResourceHint.Preconnect => 80,
                ResourceHint.DnsPrefetch => 90,
                ResourceHint.Prerender => 30,
                _ => 10
            };

            int typePriority = asType switch
            {
                PreloadAs.Style => 20,
                PreloadAs.Script => 15,
                PreloadAs.Font => 10,
                PreloadAs.Document => 25,
                _ => 0
            };

            return basePriority + typePriority;
        }

        /// <summary>
        /// Get statistics about prefetching
        /// </summary>
        public (int pending, int completed, int queued) GetStats()
        {
            lock (_lock)
            {
                int queued = _queue.Count;
                int pendingActive = _pending.Count - queued;
                if (pendingActive < 0) pendingActive = 0;
                return (pendingActive, _completedUrls.Count, queued);
            }
        }

        /// <summary>
        /// Clear current hint state and start a fresh cancellation generation.
        /// </summary>
        public void Clear()
        {
            CancellationTokenSource previous;
            lock (_lock)
            {
                if (_disposed)
                    return;

                previous = _cts;
                _retiredCancellationSources.Add(previous);
                _cts = new CancellationTokenSource();
                _generation++;

                _completedUrls.Clear();
                _preconnectedHosts.Clear();
                _pending.Clear();
                while (_queue.TryDequeue(out _)) { }
            }

            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            List<CancellationTokenSource> cancellationSources;
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                cancellationSources = new List<CancellationTokenSource>(_retiredCancellationSources.Count + 1);
                cancellationSources.AddRange(_retiredCancellationSources);
                cancellationSources.Add(_cts);

                _pending.Clear();
                while (_queue.TryDequeue(out _)) { }
            }

            foreach (var source in cancellationSources)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (Volatile.Read(ref _activeOperations) == 0)
            {
                DisposeSynchronizationResources();
            }
        }
    }
}


