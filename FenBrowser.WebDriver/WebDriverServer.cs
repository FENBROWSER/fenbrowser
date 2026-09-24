// =============================================================================
// WebDriverServer.cs
// W3C WebDriver HTTP Server (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §5 - Nodes
//                 https://www.w3.org/TR/webdriver2/#nodes
// 
// PURPOSE: Production-grade HTTP server for WebDriver commands.
// SECURITY: Origin validation, capability guards, bounded request admission.
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.WebDriver.BiDi;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Security;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// W3C WebDriver HTTP server.
    /// </summary>
    public class WebDriverServer : IDisposable, IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly SessionManager _sessionManager;
        private readonly CommandRouter _router;
        private readonly CommandHandler _handler;
        private readonly WebDriverCommandQueue _commandQueue;
        private readonly ConcurrentDictionary<string, WebDriverCommandQueue> _sessionCommandQueues =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, Task> _activeRequests = new();
        private readonly OriginValidator _originValidator;
        private readonly CancellationTokenSource _cts;
        private readonly IBiDiTransportBootstrap _biDiBootstrap;
        private readonly SemaphoreSlim _requestAdmission;
        private readonly int _port;
        private readonly object _lifecycleGate = new();
        private static readonly string[] AllowedCorsMethods = { "GET", "POST", "DELETE", "OPTIONS" };
        private static readonly string[] AllowedCorsHeaders = { "content-type" };
        private const int MaxRequestBodyBytes = 16 * 1024 * 1024;
        private const int MaxConcurrentRequests = 32;
        private Task _listenerTask = Task.CompletedTask;
        private Task _stopTask;
        private long _nextCommandId;
        private long _nextRequestId;
        private int _started;
        private int _disposeStarted;
        private int _disposed;
        
        public event Action<string> OnLog;
        
        public WebDriverServer(int port = 4444, IBiDiTransportBootstrap biDiBootstrap = null, int maxSessions = 10)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            
            if (maxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSessions), "maxSessions must be at least 1.");
            }

            _sessionManager = new SessionManager(maxSessions: maxSessions);
            _router = new CommandRouter();
            _handler = new CommandHandler(_sessionManager);
            _commandQueue = new WebDriverCommandQueue();
            _originValidator = new OriginValidator(allowLocalhostOnly: true);
            _cts = new CancellationTokenSource();
            _requestAdmission = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
            _biDiBootstrap = biDiBootstrap ?? new NoOpBiDiTransportBootstrap();
        }
        
        /// <summary>
        /// Set the browser driver implementation.
        /// </summary>
        public void SetDriver(IBrowserDriver driver)
        {
            _handler.Browser = driver;
        }

        /// <summary>
        /// Start the WebDriver server.
        /// </summary>
        public void Start()
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return;
            }

            try
            {
                ValidateCommandCoverage();
                _listener.Start();
                Log($"WebDriver server started on port {_port}");
                _biDiBootstrap.Register(new BiDiBootstrapContext(_port)
                {
                    SessionManager = _sessionManager,
                    Browser = _handler.Browser,
                    CommandHandler = _handler
                });
                _listenerTask = Task.Run(ListenAsync);
            }
            catch
            {
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }
        
        /// <summary>
        /// Stop the WebDriver server.
        /// </summary>
        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public Task StopAsync()
        {
            lock (_lifecycleGate)
            {
                return _stopTask ??= StopCoreAsync();
            }
        }

        private async Task StopCoreAsync()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }

            var requests = _activeRequests.Values.ToArray();
            if (requests.Length > 0)
            {
                try
                {
                    await Task.WhenAll(requests).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await _biDiBootstrap.StopAsync().ConfigureAwait(false);
            Log("WebDriver server stopped");
        }
        
        private async Task ListenAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (!_requestAdmission.Wait(0))
                    {
                        await SendErrorAsync(
                            context.Response,
                            ErrorCodes.UnknownError,
                            "WebDriver server is busy.",
                            503).ConfigureAwait(false);
                        continue;
                    }

                    var requestId = Interlocked.Increment(ref _nextRequestId);
                    var requestTask = HandleAdmittedRequestAsync(context);
                    _activeRequests[requestId] = requestTask;
                    _ = ObserveRequestAsync(requestId, requestTask);
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listener error: {ex}");
                }
            }
        }

        private async Task ObserveRequestAsync(long requestId, Task requestTask)
        {
            try
            {
                await requestTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Request task failed: {ex}");
            }
            finally
            {
                _activeRequests.TryRemove(requestId, out _);
            }
        }

        private async Task HandleAdmittedRequestAsync(HttpListenerContext context)
        {
            try
            {
                await HandleRequestAsync(context).ConfigureAwait(false);
            }
            finally
            {
                _requestAdmission.Release();
            }
        }
        
        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            using var logScope = EngineLogCompat.BeginScope(
                component: "WebDriver",
                data: new System.Collections.Generic.Dictionary<string, object>
                {
                    ["remoteEndPoint"] = request.RemoteEndPoint?.ToString() ?? string.Empty,
                    ["method"] = request.HttpMethod ?? string.Empty,
                    ["path"] = request.Url?.AbsolutePath ?? string.Empty
                });
            
            try
            {
                var origin = request.Headers["Origin"];
                if (!_originValidator.ValidateOrigin(request.RemoteEndPoint) || !_originValidator.ValidateOriginHeader(origin))
                {
                    const string reasonCode = SecurityBlockReasons.OriginNotAllowed;
                    const string detail = "Request endpoint or Origin header is not authorized";
                    SecurityAudit.LogBlocked(reasonCode, detail);
                    EngineLogCompat.Warn(
                        $"[WebDriver] Unauthorized request blocked. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}",
                        LogCategory.Security);
                    Log($"Security Alert: Blocked request from unauthorized origin or endpoint. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}");
                    await SendErrorAsync(
                        response,
                        ErrorCodes.UnknownCommand,
                        SecurityAudit.BuildBlockedMessage(reasonCode),
                        403,
                        SecurityAudit.CreateFailureData(reasonCode, detail));
                    return;
                }

                if (!string.IsNullOrEmpty(origin))
                {
                    response.Headers["Access-Control-Allow-Origin"] = origin;
                    response.Headers["Access-Control-Allow-Methods"] = string.Join(", ", AllowedCorsMethods);
                    response.Headers["Vary"] = "Origin";
                }

                if (request.HttpMethod == "OPTIONS")
                {
                    if (!ValidatePreflightRequest(request, response))
                    {
                        const string reasonCode = SecurityBlockReasons.PreflightRejected;
                        const string detail = "Preflight method or headers are not allowed";
                        SecurityAudit.LogBlocked(reasonCode, detail);
                        EngineLogCompat.Warn(
                            $"[WebDriver] Invalid preflight blocked. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}",
                            LogCategory.Security);
                        Log($"Security Alert: Blocked invalid WebDriver preflight. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}");
                        await SendErrorAsync(
                            response,
                            ErrorCodes.UnknownCommand,
                            SecurityAudit.BuildBlockedMessage(reasonCode),
                            403,
                            SecurityAudit.CreateFailureData(reasonCode, detail));
                        return;
                    }

                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                var path = request.Url?.AbsolutePath ?? "/";
                var method = request.HttpMethod;
                
                Log($"{method} {path}");
                
                var routeMatch = _router.Match(method, path);
                
                if (routeMatch == null)
                {
                    await SendErrorAsync(response, ErrorCodes.UnknownCommand,
                        $"Unknown command: {method} {path}", 404);
                    return;
                }
                
                JsonDocument bodyDocument = null;
                JsonElement? body = null;
                if (request.HasEntityBody)
                {
                    bodyDocument = await ReadRequestBodyAsync(request, _cts.Token).ConfigureAwait(false);
                    body = bodyDocument.RootElement;
                }

                using (bodyDocument)
                {

                    var sessionId = routeMatch.GetSessionId();
                    var commandQueue = string.IsNullOrEmpty(sessionId)
                        ? _commandQueue
                        : _sessionCommandQueues.GetOrAdd(
                            sessionId,
                            static _ => new WebDriverCommandQueue());

                    var commandId = Interlocked.Increment(ref _nextCommandId);
                    var queuedAt = Stopwatch.StartNew();
                    var result = await commandQueue.ExecuteWithSynchronousAdmissionAsync(async () =>
                    {
                        var queueWaitMs = queuedAt.ElapsedMilliseconds;
                        var execution = Stopwatch.StartNew();
                        Log($"Command {commandId} started: {routeMatch.Command} (queueWaitMs={queueWaitMs})");
                        try
                        {
                            var executionTask = _handler.ExecuteParsedAsync(routeMatch, body);
                            var deadline = _handler.GetProtocolCommandDeadline(routeMatch);
                            if (deadline.HasValue)
                            {
                                try
                                {
                                    return await WebDriverCommandDeadline.WaitAsync(
                                        executionTask,
                                        deadline.Value.TimeoutMs).ConfigureAwait(false);
                                }
                                catch (TimeoutException)
                                {
                                    // The browser is still busy with the command, so later
                                    // commands for this session must not queue behind it.
                                    _handler.MarkSessionUnresponsive(sessionId);
                                    throw new WebDriverException(
                                        deadline.Value.ErrorCode,
                                        $"{routeMatch.Command} did not finish within {deadline.Value.TimeoutMs} ms");
                                }
                            }

                            return await executionTask.ConfigureAwait(false);
                        }
                        catch (WebDriverException ex) when (
                            string.Equals(ex.ErrorCode, ErrorCodes.ScriptTimeout, StringComparison.Ordinal))
                        {
                            _handler.MarkSessionUnresponsive(sessionId);
                            throw;
                        }
                        finally
                        {
                            Log($"Command {commandId} finished: {routeMatch.Command} (durationMs={execution.ElapsedMilliseconds})");
                        }
                    }, _cts.Token).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(sessionId) &&
                        string.Equals(routeMatch.Command, "DeleteSession", StringComparison.Ordinal))
                    {
                        _sessionCommandQueues.TryRemove(sessionId, out _);
                    }

                    await SendResponseAsync(response, result, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (RequestBodyTooLargeException ex)
            {
                await SendErrorAsync(response, ErrorCodes.InvalidArgument, ex.Message, 413);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                response.Abort();
            }
            catch (WebDriverException wdEx)
            {
                await SendErrorAsync(response, wdEx.ErrorCode, wdEx.Message, wdEx.HttpStatus, wdEx.ErrorData);
            }
            catch (JsonException)
            {
                await SendErrorAsync(response, ErrorCodes.InvalidArgument,
                    "Invalid JSON in request body", 400);
            }
            catch (Exception ex)
            {
                // Preserve detailed diagnostics internally, but do not expose file
                // paths, stack-adjacent state, socket details, or implementation
                // messages through the remote WebDriver protocol.
                Log($"Request error: {ex}");
                await SendErrorAsync(
                    response,
                    ErrorCodes.UnknownError,
                    "Internal WebDriver error.",
                    500);
            }
        }

        private static async Task<JsonDocument> ReadRequestBodyAsync(
            HttpListenerRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ContentLength64 > MaxRequestBodyBytes)
            {
                throw new RequestBodyTooLargeException(
                    $"WebDriver request body exceeds the {MaxRequestBodyBytes}-byte limit.");
            }

            using var bounded = new BoundedReadStream(request.InputStream, MaxRequestBodyBytes);
            return await JsonDocument.ParseAsync(
                bounded,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                },
                cancellationToken).ConfigureAwait(false);
        }
        
        private static async Task SendResponseAsync(
            HttpListenerResponse response,
            WebDriverResponse result,
            CancellationToken cancellationToken = default)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = 200;
            response.Headers["Cache-Control"] = "no-cache";
            await WriteJsonAsync(response, result, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(
            HttpListenerResponse response,
            WebDriverResponse result,
            CancellationToken cancellationToken = default)
        {
            response.SendChunked = true;
            await JsonSerializer.SerializeAsync(
                response.OutputStream,
                result,
                WebDriverResponse.SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            response.Close();
        }
        
        private async Task SendErrorAsync(HttpListenerResponse response, string error, string message, int status, object data = null)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = status;
            response.Headers["Cache-Control"] = "no-cache";
            
            await WriteJsonAsync(
                response,
                WebDriverResponse.Error(error, message, data: data)).ConfigureAwait(false);
        }
        
        private void Log(string message)
        {
            EngineLogCompat.Info(message, LogCategory.WebDriver);
            OnLog?.Invoke($"[WebDriver] {message}");
        }

        private static bool ValidatePreflightRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            var requestedMethod = request.Headers["Access-Control-Request-Method"];
            if (string.IsNullOrWhiteSpace(requestedMethod) ||
                !AllowedCorsMethods.Contains(requestedMethod, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var requestedHeaderValue = request.Headers["Access-Control-Request-Headers"];
            if (string.IsNullOrWhiteSpace(requestedHeaderValue))
            {
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                return true;
            }

            var requestedHeaders = requestedHeaderValue
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(header => header.Trim())
                .Where(header => !string.IsNullOrEmpty(header))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (requestedHeaders.Length == 0)
            {
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                return true;
            }

            if (requestedHeaders.Any(header => !AllowedCorsHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            response.Headers["Access-Control-Allow-Headers"] = string.Join(", ", requestedHeaders);
            return true;
        }

        private void ValidateCommandCoverage()
        {
            var registered = _router.GetRegisteredCommands()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var implemented = CommandHandler.GetImplementedCommands()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var missing = registered.Except(implemented, StringComparer.Ordinal).ToArray();
            var extra = implemented.Except(registered, StringComparer.Ordinal).ToArray();

            Log($"Command coverage: routes={_router.GetRegisteredRouteCount()}, uniqueCommands={registered.Length}, implemented={implemented.Length}, missing={missing.Length}, extra={extra.Length}");
            if (missing.Length > 0)
            {
                Log($"Missing command handlers: {string.Join(", ", missing)}");
            }
            if (extra.Length > 0)
            {
                Log($"Implemented-but-unrouted commands: {string.Join(", ", extra)}");
            }

            if (missing.Length == 0 && extra.Length == 0)
            {
                return;
            }

#if DEBUG
            var allowPartialRaw = Environment.GetEnvironmentVariable("FEN_WEBDRIVER_ALLOW_PARTIAL_COMMAND_COVERAGE");
            var allowPartial = string.Equals(allowPartialRaw, "1", StringComparison.Ordinal) ||
                               string.Equals(allowPartialRaw, "true", StringComparison.OrdinalIgnoreCase);
            if (allowPartial)
            {
                Log("WARNING: Starting WebDriver with incomplete command coverage because FEN_WEBDRIVER_ALLOW_PARTIAL_COMMAND_COVERAGE is enabled in a Debug build.");
                return;
            }
#endif

            throw new InvalidOperationException(
                "WebDriver command coverage is incomplete. The remote end will not start while registered routes and implemented handlers differ.");
        }
        
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(WebDriverServer));
        }

        private sealed class RequestBodyTooLargeException : Exception
        {
            public RequestBodyTooLargeException(string message)
                : base(message)
            {
            }
        }

        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _limit;
            private long _consumed;

            public BoundedReadStream(Stream inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _consumed; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
                => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var read = _inner.Read(Limit(buffer));
                Count(read);
                return read;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(Limit(buffer), cancellationToken).ConfigureAwait(false);
                Count(read);
                return read;
            }

            private Span<byte> Limit(Span<byte> buffer)
            {
                var remainingWithProbe = Math.Max(1, _limit - _consumed + 1);
                return buffer[..(int)Math.Min(buffer.Length, remainingWithProbe)];
            }

            private Memory<byte> Limit(Memory<byte> buffer)
            {
                var remainingWithProbe = Math.Max(1, _limit - _consumed + 1);
                return buffer[..(int)Math.Min(buffer.Length, remainingWithProbe)];
            }

            private void Count(int read)
            {
                _consumed += read;
                if (_consumed > _limit)
                {
                    throw new RequestBodyTooLargeException(
                        $"WebDriver request body exceeds the {_limit}-byte limit.");
                }
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        
        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            _sessionManager.Dispose();
            _requestAdmission.Dispose();
            _cts.Dispose();
            _listener.Close();
            Volatile.Write(ref _disposed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
