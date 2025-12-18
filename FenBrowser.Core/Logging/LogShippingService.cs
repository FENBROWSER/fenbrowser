using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Service for shipping logs to external endpoints (Elasticsearch, Splunk, custom APIs).
    /// Supports batching, retry, and compression.
    /// </summary>
    public sealed class LogShippingService : IDisposable
    {
        private static readonly Lazy<LogShippingService> _instance = 
            new Lazy<LogShippingService>(() => new LogShippingService());
        
        public static LogShippingService Instance => _instance.Value;

        private readonly ConcurrentQueue<LogEntry> _buffer;
        private readonly HttpClient _httpClient;
        private readonly Timer _flushTimer;
        private readonly object _lock = new object();
        private CancellationTokenSource _cts;
        private bool _disposed;

        // Configuration
        public string EndpointUrl { get; set; }
        public string ApiKey { get; set; }
        public int BatchSize { get; set; } = 100;
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxBufferSize { get; set; } = 10000;
        public int MaxRetries { get; set; } = 3;
        public bool IsEnabled { get; set; } = false;
        public bool UseCompression { get; set; } = true;
        public string ApplicationName { get; set; } = "FenBrowser";
        public string Environment { get; set; } = "development";

        public event Action<int, string> OnShipmentComplete;
        public event Action<Exception> OnShipmentError;

        private LogShippingService()
        {
            _buffer = new ConcurrentQueue<LogEntry>();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _cts = new CancellationTokenSource();
            _flushTimer = new Timer(async _ => await FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Configure the log shipping endpoint
        /// </summary>
        public void Configure(string endpointUrl, string apiKey = null, string appName = null)
        {
            EndpointUrl = endpointUrl;
            ApiKey = apiKey;
            if (!string.IsNullOrEmpty(appName))
                ApplicationName = appName;
            IsEnabled = !string.IsNullOrEmpty(endpointUrl);
        }

        /// <summary>
        /// Start the shipping service
        /// </summary>
        public void Start()
        {
            if (string.IsNullOrEmpty(EndpointUrl))
            {
                LogManager.Log(LogCategory.Errors, LogLevel.Warn, 
                    "[LogShipping] Cannot start - no endpoint configured");
                return;
            }

            _flushTimer.Change(FlushInterval, FlushInterval);
            LogManager.Log(LogCategory.General, LogLevel.Info, 
                $"[LogShipping] Started - endpoint: {EndpointUrl}, batch: {BatchSize}");
        }

        /// <summary>
        /// Stop the shipping service
        /// </summary>
        public void Stop()
        {
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            
            // Final flush
            _ = FlushAsync();
            
            LogManager.Log(LogCategory.General, LogLevel.Info, "[LogShipping] Stopped");
        }

        /// <summary>
        /// Enqueue a log entry for shipping
        /// </summary>
        public void Enqueue(LogEntry entry)
        {
            if (!IsEnabled || entry == null) return;

            // Respect buffer limit
            if (_buffer.Count >= MaxBufferSize)
            {
                // Drop oldest entries
                while (_buffer.Count >= MaxBufferSize && _buffer.TryDequeue(out _)) { }
            }

            _buffer.Enqueue(entry);

            // Immediate flush if buffer is full
            if (_buffer.Count >= BatchSize)
            {
                _ = Task.Run(() => FlushAsync());
            }
        }

        /// <summary>
        /// Flush all buffered logs to the endpoint
        /// </summary>
        public async Task FlushAsync()
        {
            if (!IsEnabled || string.IsNullOrEmpty(EndpointUrl) || _buffer.IsEmpty)
                return;

            var batch = new List<LogEntry>();
            while (batch.Count < BatchSize && _buffer.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0) return;

            try
            {
                await ShipBatchAsync(batch, _cts.Token);
                OnShipmentComplete?.Invoke(batch.Count, "success");
            }
            catch (Exception ex)
            {
                OnShipmentError?.Invoke(ex);
                
                // Re-queue failed entries (at front)
                foreach (var entry in batch)
                {
                    _buffer.Enqueue(entry); // Will be at end, but better than lost
                }
            }
        }

        private async Task ShipBatchAsync(List<LogEntry> batch, CancellationToken ct)
        {
            var payload = CreatePayload(batch);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            // Add compression header if enabled
            if (UseCompression)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
            {
                Content = content
            };

            // Add API key header if configured
            if (!string.IsNullOrEmpty(ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {ApiKey}");
            }

            request.Headers.Add("X-Application", ApplicationName);
            request.Headers.Add("X-Environment", Environment);

            // Retry logic with exponential backoff
            int retryCount = 0;
            Exception lastException = null;

            while (retryCount < MaxRetries)
            {
                try
                {
                    var response = await _httpClient.SendAsync(request, ct);
                    
                    if (response.IsSuccessStatusCode)
                        return;

                    var statusCode = (int)response.StatusCode;
                    if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                    {
                        // Client error (except rate limit) - don't retry
                        throw new HttpRequestException($"Log shipping failed: HTTP {statusCode}");
                    }
                    
                    // Server error or rate limit - retry
                    lastException = new HttpRequestException($"HTTP {statusCode}");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    lastException = ex;
                }

                retryCount++;
                if (retryCount < MaxRetries)
                {
                    // Exponential backoff: 1s, 2s, 4s, ...
                    await Task.Delay(TimeSpan.FromSeconds(System.Math.Pow(2, retryCount - 1)), ct);
                }
            }

            throw lastException ?? new Exception("Log shipping failed after retries");
        }

        private string CreatePayload(List<LogEntry> batch)
        {
            var entries = new List<object>();
            
            foreach (var entry in batch)
            {
                entries.Add(new
                {
                    timestamp = entry.Timestamp.ToString("O"),
                    level = entry.Level.ToString().ToLower(),
                    category = entry.Category.ToString(),
                    message = entry.Message,
                    threadId = entry.ThreadId,
                    application = ApplicationName,
                    environment = Environment,
                    correlationId = entry.CorrelationId,
                    durationMs = entry.DurationMs,
                    memoryBytes = entry.MemoryBytes,
                    exception = entry.Exception != null ? new
                    {
                        type = entry.Exception.GetType().FullName,
                        message = entry.Exception.Message,
                        stackTrace = entry.Exception.StackTrace
                    } : null,
                    data = entry.Data
                });
            }

            return JsonSerializer.Serialize(new { logs = entries });
        }

        /// <summary>
        /// Get current buffer status
        /// </summary>
        public (int buffered, int maxSize, bool isEnabled) GetStatus()
        {
            return (_buffer.Count, MaxBufferSize, IsEnabled);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                _flushTimer.Dispose();
                _httpClient.Dispose();
                _cts.Dispose();
                _disposed = true;
            }
        }
    }
}
