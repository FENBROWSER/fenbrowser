using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;

namespace FenBrowser.WebDriver.BiDi;

/// <summary>
/// W3C WebDriver BiDi WebSocket transport.
///
/// The transport is loopback-only and requires an already-created WebDriver
/// session. The session id is treated as a capability and every upgrade is
/// subjected to the same remote-endpoint and browser-Origin checks as the HTTP
/// WebDriver endpoint.
/// </summary>
public sealed class BiDiWebSocketServer : IDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int MaxConcurrentConnections = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex SessionPathRegex = new(
        @"^/session/(?<sid>[0-9a-fA-F]{32})/bidi$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpListener _listener;
    private readonly SessionManager _sessionManager;
    private readonly OriginValidator _originValidator;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectionAdmission = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sessionReservations = new(StringComparer.Ordinal);
    private readonly int _port;
    private Task? _listenTask;
    private int _started;
    private int _disposed;

    public BiDiWebSocketServer(SessionManager sessionManager, int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _port = port;
        _originValidator = new OriginValidator(allowLocalhostOnly: true);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/session/");
    }

    public int ConnectedClientCount => _clients.Count;

    public void Start()
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _listener.Start();
            _listenTask = Task.Run(ListenLoopAsync);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (!_listener.IsListening)
                {
                    break;
                }
                continue;
            }

            if (!_connectionAdmission.Wait(0))
            {
                TryReject(context, 503);
                continue;
            }

            _ = HandleAdmittedContextAsync(context);
        }
    }

    private async Task HandleAdmittedContextAsync(HttpListenerContext context)
    {
        try
        {
            await HandleContextAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _connectionAdmission.Release();
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        string? reservedSessionId = null;
        WebSocket? webSocket = null;

        try
        {
            var request = context.Request;
            if (!_originValidator.ValidateOrigin(request.RemoteEndPoint) ||
                !_originValidator.ValidateOriginHeader(request.Headers["Origin"]) ||
                !ValidateLoopbackHostHeader(request.UserHostName))
            {
                TryReject(context, 403);
                return;
            }

            if (!request.IsWebSocketRequest)
            {
                TryReject(context, 400);
                return;
            }

            var path = request.Url?.AbsolutePath ?? string.Empty;
            var match = SessionPathRegex.Match(path);
            if (!match.Success)
            {
                TryReject(context, 404);
                return;
            }

            var sessionId = match.Groups["sid"].Value;
            if (!_sessionManager.HasSession(sessionId))
            {
                TryReject(context, 401);
                return;
            }

            // Exactly one controller socket is permitted per session. The previous
            // implementation overwrote the dictionary entry while leaving the old
            // socket alive, allowing two independent controllers to issue commands.
            if (!_sessionReservations.TryAdd(sessionId, 0))
            {
                TryReject(context, 409);
                return;
            }
            reservedSessionId = sessionId;

            var socketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            webSocket = socketContext.WebSocket;
            if (!_clients.TryAdd(sessionId, webSocket))
            {
                await CloseBestEffortAsync(
                    webSocket,
                    WebSocketCloseStatus.PolicyViolation,
                    "session already connected",
                    _cts.Token).ConfigureAwait(false);
                return;
            }

            await RunClientAsync(sessionId, webSocket, _cts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            TryReject(context, 400);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
        finally
        {
            if (reservedSessionId != null)
            {
                _clients.TryRemove(reservedSessionId, out _);
                _sessionReservations.TryRemove(reservedSessionId, out _);
            }

            if (webSocket != null)
            {
                try
                {
                    webSocket.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private async Task RunClientAsync(string sessionId, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!_sessionManager.HasSession(sessionId))
            {
                await CloseBestEffortAsync(
                    socket,
                    WebSocketCloseStatus.PolicyViolation,
                    "session no longer exists",
                    ct).ConfigureAwait(false);
                return;
            }

            WebSocketReceiveResult result;
            using var ms = new System.IO.MemoryStream();
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseBestEffortAsync(
                        socket,
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        ct).ConfigureAwait(false);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseBestEffortAsync(
                        socket,
                        WebSocketCloseStatus.InvalidMessageType,
                        "text messages required",
                        ct).ConfigureAwait(false);
                    return;
                }

                if (ms.Length > MaxMessageBytes - result.Count)
                {
                    await CloseBestEffortAsync(
                        socket,
                        WebSocketCloseStatus.MessageTooBig,
                        "message too large",
                        ct).ConfigureAwait(false);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string json;
            try
            {
                if (!ms.TryGetBuffer(out var segment) || segment.Array == null)
                {
                    json = StrictUtf8.GetString(ms.ToArray());
                }
                else
                {
                    json = StrictUtf8.GetString(segment.Array, segment.Offset, segment.Count);
                }
            }
            catch (DecoderFallbackException)
            {
                await CloseBestEffortAsync(
                    socket,
                    WebSocketCloseStatus.InvalidPayloadData,
                    "invalid UTF-8",
                    ct).ConfigureAwait(false);
                return;
            }

            var processed = ProcessMessage(json, sessionId);
            var bytes = Encoding.UTF8.GetBytes(processed.Response);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                ct).ConfigureAwait(false);

            if (processed.CloseAfterResponse)
            {
                await CloseBestEffortAsync(
                    socket,
                    WebSocketCloseStatus.NormalClosure,
                    "session ended",
                    ct).ConfigureAwait(false);
                return;
            }
        }
    }

    private BiDiMessageResult ProcessMessage(string json, string sessionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(-1, "invalid argument", "BiDi message must be a JSON object.");
            }

            if (!root.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var id) ||
                id < 0 ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(-1, "invalid argument", "BiDi messages require a non-negative integer 'id' and string 'method'.");
            }

            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method))
            {
                return Error(id, "invalid argument", "BiDi method must not be empty.");
            }

            switch (method)
            {
                case "session.status":
                    return Success(id, new Dictionary<string, object>
                    {
                        ["ready"] = true,
                        ["message"] = "FenBrowser WebDriver BiDi ready"
                    });

                case "session.end":
                    _sessionManager.DeleteSession(sessionId);
                    return Success(id, new Dictionary<string, object>(), closeAfterResponse: true);

                case "session.new":
                    return Error(
                        id,
                        "unsupported operation",
                        "session.new is not supported on an already-bound WebDriver session socket.");

                case "session.subscribe":
                case "session.unsubscribe":
                    return Error(
                        id,
                        "unsupported operation",
                        "BiDi event subscription delivery is not implemented yet.");

                case "ping":
                    return Success(id, new Dictionary<string, object> { ["pong"] = true });

                default:
                    return Error(id, "unknown command", $"Unknown BiDi method: {method}");
            }
        }
        catch (JsonException)
        {
            return Error(-1, "invalid argument", "Malformed BiDi message JSON.");
        }
        catch (InvalidOperationException)
        {
            return Error(-1, "invalid argument", "Invalid BiDi message shape.");
        }
    }

    private static BiDiMessageResult Success(long id, object result, bool closeAfterResponse = false)
        => new(BuildResultResponse(id, result), closeAfterResponse);

    private static BiDiMessageResult Error(long id, string error, string message)
        => new(BuildErrorResponse(id, error, message), false);

    private static string BuildResultResponse(long id, object result)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "success",
            ["id"] = id,
            ["result"] = result
        });
    }

    private static string BuildErrorResponse(long id, string error, string message)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["error"] = error,
                ["message"] = message,
                ["stacktrace"] = string.Empty
            }
        });
    }

    private bool ValidateLoopbackHostHeader(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader) ||
            !Uri.TryCreate("http://" + hostHeader.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Port != _port)
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static void TryReject(HttpListenerContext context, int statusCode)
    {
        try
        {
            context.Response.StatusCode = statusCode;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Close();
        }
        catch
        {
        }
    }

    private static async Task CloseBestEffortAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseAsync(status, description, ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BiDiWebSocketServer));
        }
    }

    private readonly record struct BiDiMessageResult(string Response, bool CloseAfterResponse);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }

        _clients.Clear();
        _sessionReservations.Clear();
        _listener.Close();
        _connectionAdmission.Dispose();
        _cts.Dispose();
    }
}
