using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Enhanced network client with connection pooling, statistics, and keep-alive optimization.
    /// Implements handler pipeline pattern for extensibility.
    /// </summary>
    public class NetworkClient : INetworkClient
    {
        private readonly List<INetworkHandler> _handlers;
        private readonly ConnectionPoolStats _stats;
        private readonly ConcurrentDictionary<string, ConnectionInfo> _activeConnections;
        private readonly Dictionary<string, HostRequestGate> _hostRequestGates;
        private readonly object _hostRequestGatesLock = new();
        private readonly SemaphoreSlim _connectionSemaphore;
        private long _defaultTimeoutTicks = TimeSpan.FromSeconds(30).Ticks;

        // Concurrency limits are construction-time invariants. The previous mutable
        // properties did not resize their semaphore and therefore advertised settings
        // that had no effect after construction.
        public int MaxConcurrentRequests { get; }
        public int MaxConnectionsPerHost { get; }
        public TimeSpan DefaultTimeout
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _defaultTimeoutTicks));
            set
            {
                if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive or Timeout.InfiniteTimeSpan.");
                Interlocked.Exchange(ref _defaultTimeoutTicks, value.Ticks);
            }
        }
        public bool EnableConnectionReuse { get; set; } = true;
        public bool EnableKeepAlive { get; set; } = true;
        public bool LogConnectionStats { get; set; } = false;

        // FEN_LOG_RESPONSE_BODY_URLS=<substring>[;<substring>...]: log the first
        // 2 KB of the response body for matching URLs. Opt-in because it buffers
        // the body; meant for reading what a service such as reCAPTCHA's
        // /userverify actually answered, which the status line cannot tell.
        private static readonly string[] ResponseBodyLogFilters =
            (Environment.GetEnvironmentVariable("FEN_LOG_RESPONSE_BODY_URLS") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static async Task LogResponseBodyIfRequestedAsync(
            HttpRequestMessage request,
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (ResponseBodyLogFilters.Length == 0 || response?.Content == null)
            {
                return;
            }

            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            var matches = false;
            foreach (var filter in ResponseBodyLogFilters)
            {
                if (url.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    break;
                }
            }

            if (!matches)
            {
                return;
            }

            try
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                const int maxChars = 32768;
                var shown = body.Length > maxChars ? body.Substring(0, maxChars) + "..." : body;
                LogManager.Log(LogCategory.Network, LogLevel.Info,
                    $"[NetworkClient] Body {request.Method} {url} ({body.Length} chars): {shown}");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogCategory.Network, LogLevel.Debug,
                    $"[NetworkClient] Body capture failed for {url}: {ex.Message}");
            }
        }


        private sealed class HostRequestGate : IDisposable
        {
            public HostRequestGate(int limit)
            {
                Semaphore = new SemaphoreSlim(limit, limit);
            }

            public SemaphoreSlim Semaphore { get; }
            public int LeaseCount { get; set; }

            public void Dispose() => Semaphore.Dispose();
        }

        public NetworkClient(
            IEnumerable<INetworkHandler> handlers,
            int maxConcurrentRequests = 100,
            int maxConnectionsPerHost = 10)
        {
            if (handlers == null) throw new ArgumentNullException(nameof(handlers));
            if (maxConcurrentRequests <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
            if (maxConnectionsPerHost <= 0) throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerHost));

            _handlers = handlers.ToList();
            _stats = new ConnectionPoolStats();
            _activeConnections = new ConcurrentDictionary<string, ConnectionInfo>();
            _hostRequestGates = new Dictionary<string, HostRequestGate>(StringComparer.OrdinalIgnoreCase);
            MaxConcurrentRequests = maxConcurrentRequests;
            MaxConnectionsPerHost = maxConnectionsPerHost;
            _connectionSemaphore = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var sw = Stopwatch.StartNew();
            var hostKey = GetHostKey(request.RequestUri);
            var globalSemaphoreAcquired = false;
            var hostSemaphoreAcquired = false;
            var requestCounted = false;
            HostRequestGate hostGate = null;
            ConnectionInfo connInfo = null;

            var timeout = DefaultTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan)
                timeoutCts.CancelAfter(timeout);
            var effectiveToken = timeoutCts.Token;

            try
            {
                // Lease the host gate before awaiting it. The lease count includes
                // both waiters and holders, so a gate cannot be removed/disposed while
                // another request is about to wait on it.
                hostGate = LeaseHostRequestGate(hostKey);
                await hostGate.Semaphore.WaitAsync(effectiveToken).ConfigureAwait(false);
                hostSemaphoreAcquired = true;

                // Acquire the per-host gate before the global gate. This prevents one
                // hot origin from occupying every global slot while most of its own
                // requests are merely waiting for the per-host limit.
                await _connectionSemaphore.WaitAsync(effectiveToken).ConfigureAwait(false);
                globalSemaphoreAcquired = true;

                // Track connection only after this request owns both concurrency slots.
                connInfo = _activeConnections.GetOrAdd(hostKey, _ => new ConnectionInfo(hostKey));
                Interlocked.Increment(ref connInfo.ActiveRequests);
                _stats.IncrementTotalRequests();
                requestCounted = true;

                if (EnableKeepAlive && !request.Headers.Connection.Contains("close"))
                    request.Headers.ConnectionClose = false;

                var context = new NetworkContext(request);
                await ExecutePipelineAsync(context, 0, effectiveToken).ConfigureAwait(false);

                if (context.Response == null)
                {
                    throw new InvalidOperationException(
                        "Network handler pipeline completed without producing an HTTP response. " +
                        "This is an engine pipeline configuration error, not an HTTP 500 response from the origin.");
                }

                sw.Stop();
                // Every request the engine makes, in the engine's own log. Without
                // this a run could not answer "did the page ask for that?" from its
                // logs at all - diagnosing why reCAPTCHA never issues its /reload
                // meant re-running under separate tooling that records requests,
                // which is not available for a GUI session.
                LogManager.Log(LogCategory.Network, LogLevel.Info,
                    $"[NetworkClient] {request.Method} {request.RequestUri} -> " +
                    $"{(int)context.Response.StatusCode} {sw.ElapsedMilliseconds}ms");
                FenBrowser.Core.Memory.EngineMetrics.Instance.Increment(
                    FenBrowser.Core.Memory.MetricCounter.FetchRequestCount);
                _stats.RecordRequest(hostKey, sw.ElapsedMilliseconds, context.Response.IsSuccessStatusCode);
                requestCounted = false;
                connInfo.LastUsed = DateTime.UtcNow;
                Interlocked.Increment(ref connInfo.TotalRequests);

                if (LogConnectionStats && sw.ElapsedMilliseconds > 1000)
                {
                    LogManager.Log(LogCategory.Network, LogLevel.Debug,
                        $"[NetworkClient] Slow request: {request.RequestUri} took {sw.ElapsedMilliseconds}ms");
                }

                await LogResponseBodyIfRequestedAsync(request, context.Response, effectiveToken).ConfigureAwait(false);

                return context.Response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                // A request that never completes is exactly the interesting case, so
                // say so rather than leaving a gap in the log.
                LogManager.Log(LogCategory.Network, LogLevel.Warn,
                    $"[NetworkClient] {request.Method} {request.RequestUri} -> " +
                    $"{ex.GetType().Name}: {ex.Message} {sw.ElapsedMilliseconds}ms");
                if (requestCounted)
                {
                    _stats.RecordRequest(hostKey, sw.ElapsedMilliseconds, success: false);
                    requestCounted = false;
                    if (connInfo != null)
                    {
                        connInfo.LastUsed = DateTime.UtcNow;
                        Interlocked.Increment(ref connInfo.TotalRequests);
                    }
                }
                throw;
            }
            finally
            {
                if (connInfo != null)
                    Interlocked.Decrement(ref connInfo.ActiveRequests);

                if (globalSemaphoreAcquired)
                    _connectionSemaphore.Release();

                if (hostSemaphoreAcquired)
                    hostGate.Semaphore.Release();

                if (hostGate != null)
                    ReleaseHostRequestGate(hostKey, hostGate);
            }
        }

        private HostRequestGate LeaseHostRequestGate(string hostKey)
        {
            lock (_hostRequestGatesLock)
            {
                if (!_hostRequestGates.TryGetValue(hostKey, out var gate))
                {
                    gate = new HostRequestGate(MaxConnectionsPerHost);
                    _hostRequestGates.Add(hostKey, gate);
                }

                checked { gate.LeaseCount++; }
                return gate;
            }
        }

        private void ReleaseHostRequestGate(string hostKey, HostRequestGate gate)
        {
            var dispose = false;
            lock (_hostRequestGatesLock)
            {
                if (gate.LeaseCount <= 0)
                    throw new InvalidOperationException("Host request gate lease count underflow.");

                gate.LeaseCount--;
                if (gate.LeaseCount == 0 &&
                    _hostRequestGates.TryGetValue(hostKey, out var current) &&
                    ReferenceEquals(current, gate))
                {
                    _hostRequestGates.Remove(hostKey);
                    _activeConnections.TryRemove(hostKey, out _);
                    dispose = true;
                }
            }

            if (dispose)
                gate.Dispose();
        }

        /// <summary>Preconnect to a host to warm up connection pool.</summary>
        public async Task PreconnectAsync(Uri uri, CancellationToken ct = default)
        {
            if (uri == null) return;
            var hostKey = GetHostKey(uri);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(uri, "/"));
                request.Headers.ConnectionClose = false;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await SendAsync(request, cts.Token).ConfigureAwait(false);
                LogManager.Log(LogCategory.Network, LogLevel.Debug,
                    $"[NetworkClient] Preconnected to {hostKey}");
            }
            catch
            {
                // Preconnect failure is not critical.
            }
        }

        public ConnectionPoolStats GetStats() => _stats;

        public ConnectionInfo GetConnectionInfo(string host)
        {
            _activeConnections.TryGetValue(host?.ToLowerInvariant() ?? "", out var info);
            return info;
        }

        public IReadOnlyDictionary<string, ConnectionInfo> GetActiveConnections() =>
            new Dictionary<string, ConnectionInfo>(_activeConnections);

        public void ResetStats() => _stats.Reset();

        private async Task ExecutePipelineAsync(NetworkContext context, int index, CancellationToken ct)
        {
            if (index >= _handlers.Count)
                return;

            var handler = _handlers[index];
            await handler.HandleAsync(context, () => ExecutePipelineAsync(context, index + 1, ct), ct).ConfigureAwait(false);
        }

        private static string GetHostKey(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
                return "unknown";

            return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
        }

        public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            var configuration = NetworkConfiguration.Instance;
            configuration.ValidateOrThrow();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await BufferContentBoundedAsync(resp.Content, configuration.MaxTextResourceBytes, ct).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        public async Task<Stream> GetStreamAsync(string url, CancellationToken ct = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            HttpResponseMessage response = null;
            CancellationTokenSource lifetimeTimeout = null;

            try
            {
                lifetimeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeout = DefaultTimeout;
                if (timeout != Timeout.InfiniteTimeSpan)
                    lifetimeTimeout.CancelAfter(timeout);

                response = await SendAsync(request, lifetimeTimeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var stream = await response.Content.ReadAsStreamAsync(lifetimeTimeout.Token).ConfigureAwait(false);
                return new ResponseOwnedStream(stream, response, request, lifetimeTimeout);
            }
            catch
            {
                response?.Dispose();
                request.Dispose();
                lifetimeTimeout?.Dispose();
                throw;
            }
        }

        public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
        {
            var configuration = NetworkConfiguration.Instance;
            configuration.ValidateOrThrow();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await BufferContentBoundedAsync(resp.Content, configuration.MaxMaterializedBinaryResourceBytes, ct).ConfigureAwait(false);
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        private static async Task BufferContentBoundedAsync(
            HttpContent content,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (maxBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            var declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > maxBytes)
            {
                throw new InvalidDataException(
                    $"Network response exceeds the configured {maxBytes}-byte materialization limit.");
            }

            await content.LoadIntoBufferAsync(maxBytes, cancellationToken).ConfigureAwait(false);
        }

        private sealed class ResponseOwnedStream : Stream
        {
            private readonly Stream _inner;
            private readonly HttpResponseMessage _response;
            private readonly HttpRequestMessage _request;
            private readonly CancellationTokenSource _lifetimeTimeout;
            private int _disposed;

            public ResponseOwnedStream(
                Stream inner,
                HttpResponseMessage response,
                HttpRequestMessage request,
                CancellationTokenSource lifetimeTimeout)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _response = response ?? throw new ArgumentNullException(nameof(response));
                _request = request ?? throw new ArgumentNullException(nameof(request));
                _lifetimeTimeout = lifetimeTimeout ?? throw new ArgumentNullException(nameof(lifetimeTimeout));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => throw SyncIoNotSupported();
            public override Task FlushAsync(CancellationToken cancellationToken) =>
                FlushWithLifetimeAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
            public override int Read(Span<byte> buffer) => throw SyncIoNotSupported();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                CancellationTokenSource linked = null;
                try
                {
                    var token = GetOperationCancellation(cancellationToken, ref linked);
                    return await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
                }
                finally
                {
                    linked?.Dispose();
                }
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                CancellationTokenSource linked = null;
                try
                {
                    var token = GetOperationCancellation(cancellationToken, ref linked);
                    return await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                finally
                {
                    linked?.Dispose();
                }
            }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => throw SyncIoNotSupported();
            public override void Write(ReadOnlySpan<byte> buffer) => throw SyncIoNotSupported();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteWithLifetimeAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                WriteMemoryWithLifetimeAsync(buffer, cancellationToken);

            private async Task FlushWithLifetimeAsync(CancellationToken cancellationToken)
            {
                CancellationTokenSource linked = null;
                try
                {
                    var token = GetOperationCancellation(cancellationToken, ref linked);
                    await _inner.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    linked?.Dispose();
                }
            }

            private async Task WriteWithLifetimeAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                CancellationTokenSource linked = null;
                try
                {
                    var token = GetOperationCancellation(cancellationToken, ref linked);
                    await _inner.WriteAsync(buffer, offset, count, token).ConfigureAwait(false);
                }
                finally
                {
                    linked?.Dispose();
                }
            }

            private async ValueTask WriteMemoryWithLifetimeAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken)
            {
                CancellationTokenSource linked = null;
                try
                {
                    var token = GetOperationCancellation(cancellationToken, ref linked);
                    await _inner.WriteAsync(buffer, token).ConfigureAwait(false);
                }
                finally
                {
                    linked?.Dispose();
                }
            }

            private CancellationToken GetOperationCancellation(
                CancellationToken cancellationToken,
                ref CancellationTokenSource linked)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(ResponseOwnedStream));

                var lifetimeToken = _lifetimeTimeout.Token;
                if (!cancellationToken.CanBeCanceled || cancellationToken == lifetimeToken)
                    return lifetimeToken;
                if (!lifetimeToken.CanBeCanceled)
                    return cancellationToken;

                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
                return linked.Token;
            }

            private static NotSupportedException SyncIoNotSupported() =>
                new("Browser response streams require asynchronous I/O.");

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try { _inner.Dispose(); }
                    finally
                    {
                        _response.Dispose();
                        _request.Dispose();
                        _lifetimeTimeout.Dispose();
                    }
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                try { await _inner.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    _response.Dispose();
                    _request.Dispose();
                    _lifetimeTimeout.Dispose();
                }
                GC.SuppressFinalize(this);
            }
        }
    }

    public class ConnectionPoolStats
    {
        private long _totalRequests;
        private long _successfulRequests;
        private const int MaxTrackedHostStats = 4096;
        private long _failedRequests;
        private long _totalLatencyMs;
        private readonly ConcurrentDictionary<string, HostStats> _hostStats = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _hostStatsAdmissionLock = new();

        public long TotalRequests => Interlocked.Read(ref _totalRequests);
        public long SuccessfulRequests => Interlocked.Read(ref _successfulRequests);
        public long FailedRequests => Interlocked.Read(ref _failedRequests);
        public long CompletedRequests => SuccessfulRequests + FailedRequests;
        public double AverageLatencyMs
        {
            get
            {
                var completed = CompletedRequests;
                return completed > 0 ? (double)Interlocked.Read(ref _totalLatencyMs) / completed : 0;
            }
        }
        public double SuccessRate
        {
            get
            {
                var successes = SuccessfulRequests;
                var completed = successes + FailedRequests;
                return completed > 0 ? (double)successes / completed * 100 : 0;
            }
        }

        public void IncrementTotalRequests() => Interlocked.Increment(ref _totalRequests);

        public void RecordRequest(string host, long latencyMs, bool success)
        {
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            if (success) Interlocked.Increment(ref _successfulRequests);
            else Interlocked.Increment(ref _failedRequests);

            var hostStats = GetOrAddHostStatsBounded(host);
            hostStats?.RecordRequest(latencyMs, success);
        }

        private HostStats GetOrAddHostStatsBounded(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            if (_hostStats.TryGetValue(host, out var existing))
                return existing;

            lock (_hostStatsAdmissionLock)
            {
                if (_hostStats.TryGetValue(host, out existing))
                    return existing;

                if (_hostStats.Count >= MaxTrackedHostStats)
                    return null;

                var created = new HostStats();
                _hostStats[host] = created;
                return created;
            }
        }

        public HostStats GetHostStats(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            _hostStats.TryGetValue(host, out var stats);
            return stats;
        }

        public IReadOnlyDictionary<string, HostStats> GetAllHostStats() =>
            new Dictionary<string, HostStats>(_hostStats);

        public void Reset()
        {
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _successfulRequests, 0);
            Interlocked.Exchange(ref _failedRequests, 0);
            Interlocked.Exchange(ref _totalLatencyMs, 0);
            lock (_hostStatsAdmissionLock)
            {
                _hostStats.Clear();
            }
        }

        public string GetSummary() =>
            $"Requests: {TotalRequests} | Success: {SuccessRate:F1}% | Avg Latency: {AverageLatencyMs:F1}ms";
    }

    public class HostStats
    {
        private long _requests;
        private long _successes;
        private long _totalLatencyMs;
        private long _minLatencyMs = long.MaxValue;
        private long _maxLatencyMs;

        public long Requests => Interlocked.Read(ref _requests);
        public long Successes => Interlocked.Read(ref _successes);
        public double AverageLatencyMs
        {
            get
            {
                var requests = Interlocked.Read(ref _requests);
                return requests > 0 ? (double)Interlocked.Read(ref _totalLatencyMs) / requests : 0;
            }
        }
        public long MinLatencyMs
        {
            get
            {
                var min = Interlocked.Read(ref _minLatencyMs);
                return min == long.MaxValue ? 0 : min;
            }
        }
        public long MaxLatencyMs => Interlocked.Read(ref _maxLatencyMs);

        public void RecordRequest(long latencyMs, bool success)
        {
            Interlocked.Increment(ref _requests);
            if (success) Interlocked.Increment(ref _successes);
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            UpdateMinimum(ref _minLatencyMs, latencyMs);
            UpdateMaximum(ref _maxLatencyMs, latencyMs);
        }

        private static void UpdateMinimum(ref long target, long candidate)
        {
            var observed = Interlocked.Read(ref target);
            while (candidate < observed)
            {
                var original = Interlocked.CompareExchange(ref target, candidate, observed);
                if (original == observed) return;
                observed = original;
            }
        }

        private static void UpdateMaximum(ref long target, long candidate)
        {
            var observed = Interlocked.Read(ref target);
            while (candidate > observed)
            {
                var original = Interlocked.CompareExchange(ref target, candidate, observed);
                if (original == observed) return;
                observed = original;
            }
        }
    }

    public class ConnectionInfo
    {
        public string Host { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastUsed { get; set; }
        public long TotalRequests;
        public int ActiveRequests;

        public ConnectionInfo(string host)
        {
            Host = host;
            CreatedAt = DateTime.UtcNow;
            LastUsed = DateTime.UtcNow;
        }

        public TimeSpan Age => DateTime.UtcNow - CreatedAt;
        public TimeSpan IdleTime => DateTime.UtcNow - LastUsed;
    }
}
