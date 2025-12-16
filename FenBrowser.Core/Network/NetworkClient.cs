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
        private readonly SemaphoreSlim _connectionSemaphore;
        
        // Configuration
        public int MaxConcurrentRequests { get; set; } = 100;
        public int MaxConnectionsPerHost { get; set; } = 10;
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool EnableConnectionReuse { get; set; } = true;
        public bool EnableKeepAlive { get; set; } = true;
        public bool LogConnectionStats { get; set; } = false;

        public NetworkClient(IEnumerable<INetworkHandler> handlers)
        {
            _handlers = handlers.ToList();
            _stats = new ConnectionPoolStats();
            _activeConnections = new ConcurrentDictionary<string, ConnectionInfo>();
            _connectionSemaphore = new SemaphoreSlim(MaxConcurrentRequests);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var hostKey = GetHostKey(request.RequestUri);
            
            try
            {
                // Throttle concurrent requests
                await _connectionSemaphore.WaitAsync(ct).ConfigureAwait(false);
                
                // Track connection
                var connInfo = _activeConnections.GetOrAdd(hostKey, _ => new ConnectionInfo(hostKey));
                Interlocked.Increment(ref connInfo.ActiveRequests);
                _stats.IncrementTotalRequests();
                
                // Add keep-alive headers for connection reuse
                if (EnableKeepAlive && !request.Headers.Connection.Contains("close"))
                {
                    request.Headers.ConnectionClose = false;
                }
                
                var context = new NetworkContext(request);
                await ExecutePipelineAsync(context, 0, ct).ConfigureAwait(false);
                
                sw.Stop();
                
                // Update stats
                _stats.RecordRequest(hostKey, sw.ElapsedMilliseconds, context.Response?.IsSuccessStatusCode == true);
                connInfo.LastUsed = DateTime.UtcNow;
                connInfo.TotalRequests++;
                
                if (LogConnectionStats && sw.ElapsedMilliseconds > 1000)
                {
                    LogManager.Log(LogCategory.Network, LogLevel.Debug,
                        $"[NetworkClient] Slow request: {request.RequestUri} took {sw.ElapsedMilliseconds}ms");
                }
                
                return context.Response ?? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) 
                { 
                    ReasonPhrase = "No response generated" 
                };
            }
            finally
            {
                _connectionSemaphore.Release();
                
                if (_activeConnections.TryGetValue(hostKey, out var info))
                {
                    Interlocked.Decrement(ref info.ActiveRequests);
                }
            }
        }

        /// <summary>
        /// Preconnect to a host to warm up connection pool
        /// </summary>
        public async Task PreconnectAsync(Uri uri, CancellationToken ct = default)
        {
            if (uri == null) return;
            
            var hostKey = GetHostKey(uri);
            if (_activeConnections.ContainsKey(hostKey)) return; // Already connected
            
            try
            {
                // Make a lightweight HEAD request to establish connection
                using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(uri, "/"));
                request.Headers.ConnectionClose = false;
                
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                
                await SendAsync(request, cts.Token).ConfigureAwait(false);
                
                LogManager.Log(LogCategory.Network, LogLevel.Debug,
                    $"[NetworkClient] Preconnected to {hostKey}");
            }
            catch
            {
                // Preconnect failure is not critical
            }
        }

        /// <summary>
        /// Get connection pool statistics
        /// </summary>
        public ConnectionPoolStats GetStats() => _stats;

        /// <summary>
        /// Get active connection info for a host
        /// </summary>
        public ConnectionInfo GetConnectionInfo(string host)
        {
            _activeConnections.TryGetValue(host?.ToLowerInvariant() ?? "", out var info);
            return info;
        }

        /// <summary>
        /// Get all active connections
        /// </summary>
        public IReadOnlyDictionary<string, ConnectionInfo> GetActiveConnections()
        {
            return new Dictionary<string, ConnectionInfo>(_activeConnections);
        }

        /// <summary>
        /// Reset statistics
        /// </summary>
        public void ResetStats()
        {
            _stats.Reset();
        }

        private async Task ExecutePipelineAsync(NetworkContext context, int index, CancellationToken ct)
        {
            if (index >= _handlers.Count)
            {
                return;
            }

            var handler = _handlers[index];
            await handler.HandleAsync(context, () => ExecutePipelineAsync(context, index + 1, ct), ct).ConfigureAwait(false);
        }

        private static string GetHostKey(Uri uri)
        {
            return uri != null ? $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant() : "unknown";
        }

        public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await SendAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public async Task<Stream> GetStreamAsync(string url, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        }

        public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await SendAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Statistics for connection pool monitoring
    /// </summary>
    public class ConnectionPoolStats
    {
        private long _totalRequests;
        private long _successfulRequests;
        private long _failedRequests;
        private long _totalLatencyMs;
        private readonly ConcurrentDictionary<string, HostStats> _hostStats = new();

        public long TotalRequests => _totalRequests;
        public long SuccessfulRequests => _successfulRequests;
        public long FailedRequests => _failedRequests;
        public double AverageLatencyMs => _totalRequests > 0 ? (double)_totalLatencyMs / _totalRequests : 0;
        public double SuccessRate => _totalRequests > 0 ? (double)_successfulRequests / _totalRequests * 100 : 0;

        public void IncrementTotalRequests() => Interlocked.Increment(ref _totalRequests);

        public void RecordRequest(string host, long latencyMs, bool success)
        {
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            
            if (success)
                Interlocked.Increment(ref _successfulRequests);
            else
                Interlocked.Increment(ref _failedRequests);

            var hostStats = _hostStats.GetOrAdd(host, _ => new HostStats());
            hostStats.RecordRequest(latencyMs, success);
        }

        public HostStats GetHostStats(string host)
        {
            _hostStats.TryGetValue(host, out var stats);
            return stats;
        }

        public IReadOnlyDictionary<string, HostStats> GetAllHostStats()
        {
            return new Dictionary<string, HostStats>(_hostStats);
        }

        public void Reset()
        {
            _totalRequests = 0;
            _successfulRequests = 0;
            _failedRequests = 0;
            _totalLatencyMs = 0;
            _hostStats.Clear();
        }

        public string GetSummary()
        {
            return $"Requests: {TotalRequests} | Success: {SuccessRate:F1}% | Avg Latency: {AverageLatencyMs:F1}ms";
        }
    }

    /// <summary>
    /// Per-host statistics
    /// </summary>
    public class HostStats
    {
        private long _requests;
        private long _successes;
        private long _totalLatencyMs;
        private long _minLatencyMs = long.MaxValue;
        private long _maxLatencyMs;

        public long Requests => _requests;
        public long Successes => _successes;
        public double AverageLatencyMs => _requests > 0 ? (double)_totalLatencyMs / _requests : 0;
        public long MinLatencyMs => _minLatencyMs == long.MaxValue ? 0 : _minLatencyMs;
        public long MaxLatencyMs => _maxLatencyMs;

        public void RecordRequest(long latencyMs, bool success)
        {
            Interlocked.Increment(ref _requests);
            if (success) Interlocked.Increment(ref _successes);
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            
            // Update min/max (not perfectly thread-safe but acceptable for stats)
            if (latencyMs < _minLatencyMs) _minLatencyMs = latencyMs;
            if (latencyMs > _maxLatencyMs) _maxLatencyMs = latencyMs;
        }
    }

    /// <summary>
    /// Information about a connection to a host
    /// </summary>
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
