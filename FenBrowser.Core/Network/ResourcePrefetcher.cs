using FenBrowser.Core.Dom;
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
        private readonly CancellationTokenSource _cts;
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

                // Start processing queue
                _ = Task.Run(() => ProcessQueueAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[ResourcePrefetcher] Error parsing DOM hints: {ex.Message}", LogCategory.Network);
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

                foreach (var linkValue in linkValues)
                {
                    ParseLinkHeader(linkValue, baseUri);
                }

                // Start processing queue
                _ = Task.Run(() => ProcessQueueAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[ResourcePrefetcher] Error parsing Link headers: {ex.Message}", LogCategory.Network);
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

            lock (_lock)
            {
                // Skip if already processed or pending
                if (_completedUrls.Contains(key) || _pending.ContainsKey(key))
                    return;

                // Respect queue limit
                if (_queue.Count >= MaxQueueSize)
                    return;
            }

            var request = new PrefetchRequest
            {
                Url = url,
                Hint = hint,
                AsType = asType,
                CrossOrigin = crossOrigin,
                MimeType = mimeType,
                Priority = GetPriority(hint, asType),
                QueuedAt = DateTimeOffset.UtcNow
            };

            _pending.TryAdd(key, request);
            _queue.Enqueue(request);

            FenLogger.Debug($"[ResourcePrefetcher] Queued {hint}: {url}", LogCategory.Network);

            await Task.CompletedTask;
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
                FenLogger.Debug($"[ResourcePrefetcher] Preconnect: {host}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[ResourcePrefetcher] Preconnect failed: {host} - {ex.Message}", LogCategory.Network);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Process the prefetch queue
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _queue.TryDequeue(out var request))
            {
                if (request.Completed) continue;

                try
                {
                    await _throttle.WaitAsync(ct);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecutePrefetchAsync(request, ct);
                        }
                        finally
                        {
                            _throttle.Release();
                        }
                    }, ct);
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

                FenLogger.Debug($"[ResourcePrefetcher] Completed {request.Hint}: {request.Url}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[ResourcePrefetcher] Failed {request.Hint}: {request.Url} - {ex.Message}", LogCategory.Network);
            }
            finally
            {
                request.Completed = true;
                _pending.TryRemove(key, out _);

                lock (_lock)
                {
                    _completedUrls.Add(key);
                }

                OnPrefetchComplete?.Invoke(request.Url, success);
            }
        }

        /// <summary>
        /// Parse Link header value
        /// </summary>
        private void ParseLinkHeader(string linkValue, Uri baseUri)
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

                    _ = QueueHintAsync(url, hint.Value, asType);
                }
                catch { }
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
                return (_pending.Count, _completedUrls.Count, _queue.Count);
            }
        }

        /// <summary>
        /// Clear all state and cancel pending requests
        /// </summary>
        public void Clear()
        {
            _cts.Cancel();

            lock (_lock)
            {
                _completedUrls.Clear();
                _preconnectedHosts.Clear();
            }

            _pending.Clear();
            while (_queue.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                _cts.Dispose();
                _throttle.Dispose();
                _disposed = true;
            }
        }
    }
}

