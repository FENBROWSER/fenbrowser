using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network
{
    public enum ResourceHint
    {
        Prefetch,
        Preload,
        Preconnect,
        DnsPrefetch,
        Prerender
    }

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

    public sealed class PrefetchRequest
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
        internal string OperationKey { get; set; }
        internal bool Started { get; set; }
        internal FetchContext Context { get; set; }
    }

    /// <summary>
    /// Schedules speculative network hints with bounded concurrency and priority.
    /// The queue is broker-owned under one lock; execution is asynchronous and never
    /// consumes a worker thread merely to wait on network I/O.
    /// </summary>
    public sealed class ResourcePrefetcher : IDisposable
    {
        private const int DefaultMaxConcurrentPrefetches = 4;
        private const int DefaultMaxQueueSize = 100;

        private readonly ResourceManager _resourceManager;
        private readonly PriorityQueue<PrefetchRequest, (int negativePriority, long sequence)> _queue = new();
        private readonly Dictionary<string, PrefetchRequest> _pending = new(StringComparer.Ordinal);
        private readonly HashSet<string> _completedOperations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _preconnectedHosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly List<CancellationTokenSource> _retiredCancellationSources = new();
        private SemaphoreSlim _throttle;
        private CancellationTokenSource _cts;
        private long _enqueueSequence;
        private int _generation;
        private int _activeOperations;
        private int _resourcesDisposed;
        private int _processingFlag;
        private int _maxConcurrentPrefetches = DefaultMaxConcurrentPrefetches;
        private int _maxQueueSize = DefaultMaxQueueSize;
        private TimeSpan _prefetchTimeout = TimeSpan.FromSeconds(30);
        private bool _disposed;

        public int MaxConcurrentPrefetches
        {
            get
            {
                lock (_lock) return _maxConcurrentPrefetches;
            }
            set
            {
                if (value < 1 || value > 64)
                    throw new ArgumentOutOfRangeException(nameof(value), "Prefetch concurrency must be between 1 and 64.");

                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    if (_activeOperations != 0 || _processingFlag != 0 || _pending.Count != 0 || _queue.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Prefetch concurrency can only be changed while the scheduler is idle.");
                    }

                    if (value == _maxConcurrentPrefetches) return;
                    var oldThrottle = _throttle;
                    _throttle = new SemaphoreSlim(value, value);
                    _maxConcurrentPrefetches = value;
                    oldThrottle.Dispose();
                }
            }
        }

        public int MaxQueueSize
        {
            get
            {
                lock (_lock) return _maxQueueSize;
            }
            set
            {
                if (value < 1 || value > 10_000)
                    throw new ArgumentOutOfRangeException(nameof(value), "Prefetch queue size must be between 1 and 10000.");
                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    _maxQueueSize = value;
                }
            }
        }

        public TimeSpan PrefetchTimeout
        {
            get
            {
                lock (_lock) return _prefetchTimeout;
            }
            set
            {
                if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
                    throw new ArgumentOutOfRangeException(nameof(value), "Prefetch timeout must be between zero and five minutes.");
                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    _prefetchTimeout = value;
                }
            }
        }

        public bool EnablePrefetch { get; set; } = true;
        public bool EnablePreload { get; set; } = true;
        public bool EnablePreconnect { get; set; } = true;
        public bool EnableDnsPrefetch { get; set; } = true;

        public event Action<Uri, bool> OnPrefetchComplete;

        public ResourcePrefetcher(ResourceManager resourceManager)
        {
            _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
            _throttle = new SemaphoreSlim(DefaultMaxConcurrentPrefetches, DefaultMaxConcurrentPrefetches);
            _cts = new CancellationTokenSource();
        }

        public async Task PrefetchFromDomAsync(Element document, Uri baseUri)
        {
            if (document == null || baseUri == null) return;

            try
            {
                var linkElements = CollectLinkElements(document);
                var tasks = new List<Task>(linkElements.Count);
                foreach (var link in linkElements)
                {
                    var relTokens = TokenizeRel(link.GetAttribute("rel"));
                    var href = link.GetAttribute("href");
                    if (relTokens.Count == 0 || string.IsNullOrWhiteSpace(href) ||
                        !Uri.TryCreate(baseUri, href, out var url))
                    {
                        continue;
                    }

                    if (!TryResolveHint(relTokens, link.GetAttribute("as"), out var hint, out var asType))
                    {
                        continue;
                    }

                    tasks.Add(QueueHintAsync(
                        url,
                        hint,
                        asType,
                        link.GetAttribute("crossorigin"),
                        link.GetAttribute("type")));
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] DOM hint scan failed (type={ex.GetType().Name}).",
                    LogCategory.Network);
            }
        }

        public async Task ProcessLinkHeadersAsync(HttpResponseHeaders headers, Uri baseUri)
        {
            if (headers == null || baseUri == null) return;

            try
            {
                if (!headers.TryGetValues("Link", out var linkValues)) return;

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
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Link header processing failed (type={ex.GetType().Name}).",
                    LogCategory.Network);
            }
        }

        public Task QueueHintAsync(
            Uri url,
            ResourceHint hint,
            PreloadAs asType = PreloadAs.Unknown,
            string crossOrigin = null,
            string mimeType = null,
            FetchContext context = null)
        {
            if (url == null || !url.IsAbsoluteUri || !IsFetchableHintUrl(url) || !IsHintEnabled(hint))
            {
                return Task.CompletedTask;
            }

            // Prerender is deliberately not accepted until a separate browsing-context
            // implementation exists. Queueing a hint with no execution path made the
            // public scheduler claim work it could never perform.
            if (hint == ResourceHint.Prerender)
            {
                return Task.CompletedTask;
            }

            var operationKey = BuildOperationKey(url, hint, asType, crossOrigin) + "|" + (context?.NetworkPartitionKey.ToString() ?? "global");
            var priority = GetPriority(hint, asType);
            var queued = false;

            lock (_lock)
            {
                if (_disposed) return Task.CompletedTask;
                if (_completedOperations.Contains(operationKey)) return Task.CompletedTask;

                if (_pending.TryGetValue(operationKey, out var existing))
                {
                    // Preload supersedes a weaker prefetch for the same fetch identity.
                    // Re-enqueue the same object at a better priority; stale queue entries
                    // are ignored once the object is marked Started.
                    if (!existing.Started && priority > existing.Priority)
                    {
                        existing.Priority = priority;
                        existing.Hint = hint;
                        existing.QueuedAt = DateTimeOffset.UtcNow;
                        _queue.Enqueue(existing, (-priority, ++_enqueueSequence));
                        queued = true;
                    }
                }
                else
                {
                    if (CountQueuedUniqueLocked() >= _maxQueueSize)
                    {
                        return Task.CompletedTask;
                    }

                    var request = new PrefetchRequest
                    {
                        Url = url,
                        Hint = hint,
                        AsType = asType,
                        CrossOrigin = crossOrigin,
                        MimeType = mimeType,
                        Priority = priority,
                        QueuedAt = DateTimeOffset.UtcNow,
                        Generation = _generation,
                        OperationKey = operationKey,
                        Context = context
                    };

                    _pending.Add(operationKey, request);
                    _queue.Enqueue(request, (-priority, ++_enqueueSequence));
                    queued = true;
                }
            }

            if (queued)
            {
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Queued {hint} priority={priority} target={GetSafeUrlForLog(url)}",
                    LogCategory.Network);
                EnsureProcessing();
            }

            return Task.CompletedTask;
        }

        private void EnsureProcessing()
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _processingFlag, 1, 0) != 0) return;

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

            _ = ProcessQueueWorkerAsync(token, generation);
        }

        private async Task ProcessQueueWorkerAsync(CancellationToken ct, int generation)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _throttle.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    PrefetchRequest request;
                    lock (_lock)
                    {
                        if (!TryDequeueNextLocked(generation, out request))
                        {
                            _throttle.Release();
                            break;
                        }
                        request.Started = true;
                    }

                    Interlocked.Increment(ref _activeOperations);
                    _ = RunPrefetchOperationAsync(request, ct);
                }
            }
            finally
            {
                Volatile.Write(ref _processingFlag, 0);
                EndOperation();

                lock (_lock)
                {
                    if (!_disposed && HasRunnableQueuedRequestLocked(_generation))
                    {
                        // Kick outside the lock below.
                        ThreadPool.QueueUserWorkItem(static state => ((ResourcePrefetcher)state).EnsureProcessing(), this);
                    }
                }
            }
        }

        private async Task RunPrefetchOperationAsync(PrefetchRequest request, CancellationToken generationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
                TimeSpan timeoutValue;
                lock (_lock) timeoutValue = _prefetchTimeout;
                timeout.CancelAfter(timeoutValue);
                await ExecutePrefetchAsync(request, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _throttle.Release();
                EndOperation();
            }
        }

        public async Task PreconnectAsync(Uri url)
        {
            _ = await TryPreconnectAsync(url, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<bool> TryPreconnectAsync(Uri url, CancellationToken ct)
        {
            if (url == null || !url.IsAbsoluteUri ||
                (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var originKey = GetOriginKey(url);
            lock (_lock)
            {
                if (_preconnectedHosts.Contains(originKey)) return true;
                _preconnectedHosts.Add(originKey);
            }

            var success = false;
            try
            {
                var originRoot = new Uri(originKey + "/", UriKind.Absolute);
                using var request = new HttpRequestMessage(HttpMethod.Head, originRoot);
                request.Headers.ConnectionClose = false;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                TimeSpan configuredTimeout;
                lock (_lock) configuredTimeout = _prefetchTimeout;
                timeout.CancelAfter(configuredTimeout < TimeSpan.FromSeconds(5)
                    ? configuredTimeout
                    : TimeSpan.FromSeconds(5));

                using var response = await HttpClientFactory.GetSharedClient().SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                success = true;
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Preconnect warmed: {GetSafeUrlForLog(url)}",
                    LogCategory.Network);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Preconnect failed (type={ex.GetType().Name}).",
                    LogCategory.Network);
                return false;
            }
            finally
            {
                if (!success)
                {
                    lock (_lock) _preconnectedHosts.Remove(originKey);
                }
            }
        }

        private static async Task<bool> PrefetchDnsAsync(Uri url, CancellationToken ct)
        {
            if (url == null || !url.IsAbsoluteUri || string.IsNullOrWhiteSpace(url.DnsSafeHost)) return false;
            if (IPAddress.TryParse(url.DnsSafeHost, out _)) return true;

            if (BrowserSettings.Instance.UseSecureDNS)
            {
                var secureAddresses = await SecureDnsResolver.ResolveAllAsync(url.DnsSafeHost, ct).ConfigureAwait(false);
                return secureAddresses.Count > 0;
            }

            var systemAddresses = await Dns.GetHostAddressesAsync(url.DnsSafeHost, ct).ConfigureAwait(false);
            return systemAddresses.Length > 0;
        }

        private async Task ExecutePrefetchAsync(PrefetchRequest request, CancellationToken ct)
        {
            var success = false;
            try
            {
                if (!IsHintEnabled(request.Hint)) return;

                switch (request.Hint)
                {
                    case ResourceHint.Preload:
                    case ResourceHint.Prefetch:
                        switch (request.AsType)
                        {
                            case PreloadAs.Image:
                                await _resourceManager.FetchImageAsync(request.Context ?? new FetchContext
                                {
                                    RequestUri = request.Url,
                                    Destination = "image",
                                    Mode = "no-cors",
                                    CredentialsMode = "include",
                                    Method = "GET"
                                }).ConfigureAwait(false);
                                success = true;
                                break;
                            case PreloadAs.Style:
                            case PreloadAs.Script:
                            case PreloadAs.Fetch:
                            case PreloadAs.Document:
                            case PreloadAs.Worker:
                            case PreloadAs.Unknown:
                                await _resourceManager.FetchTextDetailedAsync(request.Context ?? new FetchContext
                                {
                                    RequestUri = request.Url,
                                    Destination = request.AsType switch
                                    {
                                        PreloadAs.Style => "style",
                                        PreloadAs.Script => "script",
                                        PreloadAs.Worker => "worker",
                                        PreloadAs.Document => "iframe",
                                        _ => "empty"
                                    },
                                    Mode = "no-cors",
                                    CredentialsMode = "include",
                                    Method = "GET"
                                }).ConfigureAwait(false);
                                success = true;
                                break;
                            default:
                                // No binary cache API is currently shared by font/media
                                // consumers. Do not waste bandwidth by decoding those
                                // resources through the text cache and calling it a hit.
                                success = false;
                                break;
                        }
                        break;
                    case ResourceHint.Preconnect:
                        success = await TryPreconnectAsync(request.Url, ct).ConfigureAwait(false);
                        break;
                    case ResourceHint.DnsPrefetch:
                        success = await PrefetchDnsAsync(request.Url, ct).ConfigureAwait(false);
                        break;
                    case ResourceHint.Prerender:
                        success = false;
                        break;
                }

                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] {(success ? "Completed" : "Skipped/failed")} {request.Hint} target={GetSafeUrlForLog(request.Url)}",
                    LogCategory.Network);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Cancelled {request.Hint}.",
                    LogCategory.Network);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[ResourcePrefetcher] Failed {request.Hint} (type={ex.GetType().Name}).",
                    LogCategory.Network);
            }
            finally
            {
                var notify = false;
                lock (_lock)
                {
                    if (!_disposed && request.Generation == _generation)
                    {
                        RemovePendingIfOwnedLocked(request.OperationKey, request);
                        if (success) _completedOperations.Add(request.OperationKey);
                        notify = true;
                    }
                }

                request.Completed = success;
                if (notify)
                {
                    try
                    {
                        OnPrefetchComplete?.Invoke(request.Url, success);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug(
                            $"[ResourcePrefetcher] Completion subscriber failed (type={ex.GetType().Name}).",
                            LogCategory.Network);
                    }
                }
            }
        }

        private bool TryDequeueNextLocked(int generation, out PrefetchRequest request)
        {
            request = null;
            while (_queue.Count > 0)
            {
                var candidate = _queue.Dequeue();
                if (candidate.Generation > generation)
                {
                    _queue.Enqueue(candidate, (-candidate.Priority, ++_enqueueSequence));
                    return false;
                }

                if (candidate.Generation < generation)
                {
                    RemovePendingIfOwnedLocked(candidate.OperationKey, candidate);
                    continue;
                }

                if (candidate.Started || candidate.Completed) continue;
                if (!_pending.TryGetValue(candidate.OperationKey, out var current) || !ReferenceEquals(current, candidate))
                {
                    continue;
                }

                request = candidate;
                return true;
            }

            return false;
        }

        private bool HasRunnableQueuedRequestLocked(int generation)
        {
            foreach (var pair in _pending)
            {
                var request = pair.Value;
                if (request.Generation == generation && !request.Started && !request.Completed)
                {
                    return true;
                }
            }
            return false;
        }

        private int CountQueuedUniqueLocked()
        {
            var count = 0;
            foreach (var request in _pending.Values)
            {
                if (!request.Started && !request.Completed) count++;
            }
            return count;
        }

        private void RemovePendingIfOwnedLocked(string key, PrefetchRequest request)
        {
            if (!string.IsNullOrEmpty(key) &&
                _pending.TryGetValue(key, out var current) &&
                ReferenceEquals(current, request))
            {
                _pending.Remove(key);
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
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;

            List<CancellationTokenSource> cancellationSources;
            SemaphoreSlim throttle;
            lock (_lock)
            {
                cancellationSources = new List<CancellationTokenSource>(_retiredCancellationSources.Count + 1);
                cancellationSources.AddRange(_retiredCancellationSources);
                cancellationSources.Add(_cts);
                _retiredCancellationSources.Clear();
                throttle = _throttle;
            }

            foreach (var source in cancellationSources)
            {
                source.Dispose();
            }
            throttle.Dispose();
        }

        private void ParseLinkHeader(string linkValue, Uri baseUri, List<Task> queueTasks)
        {
            foreach (var segment in SplitLinkHeaderValues(linkValue))
            {
                try
                {
                    var open = segment.IndexOf('<');
                    var close = open >= 0 ? segment.IndexOf('>', open + 1) : -1;
                    if (open < 0 || close <= open + 1) continue;

                    var href = segment.Substring(open + 1, close - open - 1).Trim();
                    if (!Uri.TryCreate(baseUri, href, out var url)) continue;

                    var parameters = ParseLinkParameters(segment.AsSpan(close + 1));
                    if (!parameters.TryGetValue("rel", out var relValue)) continue;

                    var relTokens = TokenizeRel(relValue);
                    parameters.TryGetValue("as", out var asValue);
                    if (!TryResolveHint(relTokens, asValue, out var hint, out var asType)) continue;

                    parameters.TryGetValue("crossorigin", out var crossOrigin);
                    parameters.TryGetValue("type", out var mimeType);
                    queueTasks.Add(QueueHintAsync(url, hint, asType, crossOrigin, mimeType));
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug(
                        $"[ResourcePrefetcher] Link header segment rejected (type={ex.GetType().Name}).",
                        LogCategory.Network);
                }
            }
        }

        private static List<string> SplitLinkHeaderValues(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return result;

            var start = 0;
            var quote = '\0';
            var inAngle = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < value.Length)
                    {
                        i++;
                        continue;
                    }
                    if (c == quote) quote = '\0';
                    continue;
                }

                if (c is '\'' or '"') quote = c;
                else if (c == '<') inAngle = true;
                else if (c == '>') inAngle = false;
                else if (c == ',' && !inAngle)
                {
                    var segment = value.Substring(start, i - start).Trim();
                    if (segment.Length > 0) result.Add(segment);
                    start = i + 1;
                }
            }

            var finalSegment = value.Substring(start).Trim();
            if (finalSegment.Length > 0) result.Add(finalSegment);
            return result;
        }

        private static Dictionary<string, string> ParseLinkParameters(ReadOnlySpan<char> value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            while (i < value.Length)
            {
                while (i < value.Length && (value[i] == ';' || char.IsWhiteSpace(value[i]))) i++;
                if (i >= value.Length) break;

                var nameStart = i;
                while (i < value.Length && value[i] != '=' && value[i] != ';') i++;
                var name = value.Slice(nameStart, i - nameStart).Trim().ToString();
                var parameterValue = string.Empty;

                if (i < value.Length && value[i] == '=')
                {
                    i++;
                    while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
                    if (i < value.Length && value[i] is '\'' or '"')
                    {
                        var quote = value[i++];
                        var builder = new System.Text.StringBuilder();
                        while (i < value.Length && value[i] != quote)
                        {
                            if (value[i] == '\\' && i + 1 < value.Length)
                            {
                                i++;
                            }
                            builder.Append(value[i++]);
                        }
                        if (i < value.Length && value[i] == quote) i++;
                        parameterValue = builder.ToString();
                    }
                    else
                    {
                        var valueStart = i;
                        while (i < value.Length && value[i] != ';') i++;
                        parameterValue = value.Slice(valueStart, i - valueStart).Trim().ToString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(name) && !result.ContainsKey(name))
                {
                    result[name] = parameterValue;
                }

                while (i < value.Length && value[i] != ';') i++;
            }
            return result;
        }

        private static List<Element> CollectLinkElements(Element root)
        {
            var result = new List<Element>();
            var stack = new Stack<Element>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (element.TagName?.Equals("link", StringComparison.OrdinalIgnoreCase) == true)
                {
                    result.Add(element);
                }

                var children = new List<Element>();
                foreach (var child in element.Children)
                {
                    if (child is Element el) children.Add(el);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }

            return result;
        }

        private static HashSet<string> TokenizeRel(string rel)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rel)) return tokens;
            foreach (var token in rel.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(token)) tokens.Add(token.Trim());
            }
            return tokens;
        }

        private static bool TryResolveHint(
            HashSet<string> relTokens,
            string asValue,
            out ResourceHint hint,
            out PreloadAs asType)
        {
            asType = ParseAsType(asValue);
            if (relTokens.Contains("preload") || relTokens.Contains("modulepreload"))
            {
                hint = ResourceHint.Preload;
                if (relTokens.Contains("modulepreload") && asType == PreloadAs.Unknown) asType = PreloadAs.Script;
                return true;
            }
            if (relTokens.Contains("stylesheet"))
            {
                hint = ResourceHint.Preload;
                asType = PreloadAs.Style;
                return true;
            }
            if (relTokens.Contains("prefetch")) { hint = ResourceHint.Prefetch; return true; }
            if (relTokens.Contains("preconnect")) { hint = ResourceHint.Preconnect; return true; }
            if (relTokens.Contains("dns-prefetch")) { hint = ResourceHint.DnsPrefetch; return true; }
            if (relTokens.Contains("prerender")) { hint = ResourceHint.Prerender; return true; }

            hint = default;
            return false;
        }

        private static PreloadAs ParseAsType(string asValue)
        {
            if (string.IsNullOrWhiteSpace(asValue)) return PreloadAs.Unknown;
            return asValue.Trim().ToLowerInvariant() switch
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

        private static int GetPriority(ResourceHint hint, PreloadAs asType)
        {
            var basePriority = hint switch
            {
                ResourceHint.Preload => 100,
                ResourceHint.DnsPrefetch => 90,
                ResourceHint.Preconnect => 80,
                ResourceHint.Prefetch => 50,
                ResourceHint.Prerender => 30,
                _ => 10
            };

            var typePriority = asType switch
            {
                PreloadAs.Document => 25,
                PreloadAs.Style => 20,
                PreloadAs.Script => 15,
                PreloadAs.Worker => 15,
                PreloadAs.Font => 10,
                _ => 0
            };
            return basePriority + typePriority;
        }

        private bool IsHintEnabled(ResourceHint hint)
        {
            return hint switch
            {
                ResourceHint.Prefetch => EnablePrefetch,
                ResourceHint.Preload => EnablePreload,
                ResourceHint.Preconnect => EnablePreconnect,
                ResourceHint.DnsPrefetch => EnableDnsPrefetch,
                ResourceHint.Prerender => false,
                _ => false
            };
        }

        private static bool IsFetchableHintUrl(Uri url)
        {
            return string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildOperationKey(
            Uri url,
            ResourceHint hint,
            PreloadAs asType,
            string crossOrigin)
        {
            if (hint is ResourceHint.Preload or ResourceHint.Prefetch)
            {
                return $"fetch|{asType}|{NormalizeCrossOrigin(crossOrigin)}|{url.AbsoluteUri}";
            }
            if (hint == ResourceHint.Preconnect)
            {
                return "preconnect|" + GetOriginKey(url);
            }
            if (hint == ResourceHint.DnsPrefetch)
            {
                return "dns|" + (url.IdnHost ?? string.Empty).TrimEnd('.').ToLowerInvariant();
            }
            return hint + "|" + url.AbsoluteUri;
        }

        private static string NormalizeCrossOrigin(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "none";
            var normalized = value.Trim().ToLowerInvariant();
            return normalized == "use-credentials" ? "use-credentials" : "anonymous";
        }

        private static string GetOriginKey(Uri url)
        {
            var defaultPort = (url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && url.Port == 443) ||
                              (url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && url.Port == 80);
            return defaultPort
                ? $"{url.Scheme.ToLowerInvariant()}://{url.IdnHost.ToLowerInvariant()}"
                : $"{url.Scheme.ToLowerInvariant()}://{url.IdnHost.ToLowerInvariant()}:{url.Port}";
        }

        private static string GetSafeUrlForLog(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri) return string.Empty;
            try { return GetOriginKey(url); }
            catch { return url.Scheme + ":"; }
        }

        public (int pending, int completed, int queued) GetStats()
        {
            lock (_lock)
            {
                var queued = CountQueuedUniqueLocked();
                var pendingActive = _pending.Count - queued;
                if (pendingActive < 0) pendingActive = 0;
                return (pendingActive, _completedOperations.Count, queued);
            }
        }

        public void Clear()
        {
            CancellationTokenSource previous;
            lock (_lock)
            {
                if (_disposed) return;
                previous = _cts;
                _retiredCancellationSources.Add(previous);
                _cts = new CancellationTokenSource();
                _generation++;
                _completedOperations.Clear();
                _preconnectedHosts.Clear();
                _pending.Clear();
                _queue.Clear();
            }

            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            List<CancellationTokenSource> cancellationSources;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                cancellationSources = new List<CancellationTokenSource>(_retiredCancellationSources.Count + 1);
                cancellationSources.AddRange(_retiredCancellationSources);
                cancellationSources.Add(_cts);
                _pending.Clear();
                _queue.Clear();
            }

            foreach (var source in cancellationSources)
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            if (Volatile.Read(ref _activeOperations) == 0)
            {
                DisposeSynchronizationResources();
            }
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResourcePrefetcher));
        }
    }
}
