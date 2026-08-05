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

namespace FenBrowser.WebDriver.BiDi;

/// <summary>
/// W3C WebDriver BiDi WebSocket transport.
///
/// Implements the transport layer of the BiDi specification: a WebSocket
/// endpoint at /session/{sessionId}/bidi that accepts JSON-RPC-style messages
/// and delivers responses. The command surface covers the session lifecycle
/// (session.status, session.new, session.end, session.subscribe/unsubscribe)
/// plus a ping used by conformance clients to validate the transport.
///
/// The endpoint authenticates purely by session id (the session id is the
/// capability token) and binds to loopback, mirroring the HTTP listener.
/// </summary>
public sealed class BiDiWebSocketServer : IDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private readonly HttpListener _listener;
    private readonly SessionManager _sessionManager;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private Task? _listenTask;
    private bool _started;

    public BiDiWebSocketServer(SessionManager sessionManager, int port)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/session/");
    }

    public int ConnectedClientCount => _clients.Count;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;

            // Expected: /session/{sessionId}/bidi
            var match = Regex.Match(path, @"^/session/(?<sid>[^/]+)/bidi$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var sessionId = Uri.UnescapeDataString(match.Groups["sid"].Value);

            // The session id is the capability token: reject unknown sessions.
            if (!_sessionManager.HasSession(sessionId))
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            var socket = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            var webSocket = socket.WebSocket;
            _clients[sessionId] = webSocket;

            try
            {
                await RunClientAsync(sessionId, webSocket, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _clients.TryRemove(sessionId, out _);
                try
                {
                    webSocket.Dispose();
                }
                catch
                {
                }
            }
        }
        catch (WebSocketException)
        {
            // Upgrade rejected (e.g. not a WebSocket request).
            try
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
            catch
            {
            }
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
    }

    private static async Task RunClientAsync(string sessionId, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            using var ms = new System.IO.MemoryStream();
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    return;
                }

                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", ct).ConfigureAwait(false);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(ms.ToArray());
            var response = ProcessMessage(json, sessionId);
            if (response != null)
            {
                var bytes = Encoding.UTF8.GetBytes(response);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
    }

    private static string? ProcessMessage(string json, string sessionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idElement) ||
                !root.TryGetProperty("method", out var methodElement))
            {
                return BuildErrorResponse(-1, "invalid argument", "BiDi messages require 'id' and 'method'.");
            }

            var id = idElement.GetInt64();
            var method = methodElement.GetString() ?? string.Empty;

            switch (method)
            {
                case "session.status":
                    return BuildResultResponse(id, new Dictionary<string, object>
                    {
                        ["ready"] = true,
                        ["message"] = "FenBrowser WebDriver BiDi ready"
                    });

                case "session.new":
                    return BuildResultResponse(id, new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["capabilities"] = new Dictionary<string, object>()
                    });

                case "session.end":
                    return BuildResultResponse(id, new Dictionary<string, object>());

                case "session.subscribe":
                case "session.unsubscribe":
                    // Accept the subscription; event fan-out is minimal for now.
                    return BuildResultResponse(id, new Dictionary<string, object>
                    {
                        ["subscription"] = Array.Empty<string>()
                    });

                case "ping":
                    return BuildResultResponse(id, new Dictionary<string, object> { ["pong"] = true });

                default:
                    return BuildErrorResponse(id, "unknown command", $"Unknown BiDi method: {method}");
            }
        }
        catch (JsonException)
        {
            return BuildErrorResponse(-1, "invalid argument", "Malformed BiDi message JSON.");
        }
    }

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

    public void Dispose()
    {
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
        _cts.Dispose();
    }
}