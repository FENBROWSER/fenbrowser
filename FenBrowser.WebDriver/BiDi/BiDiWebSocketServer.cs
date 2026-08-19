using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;
using FenBrowser.WebDriver.Commands;

namespace FenBrowser.WebDriver.BiDi;

/// <summary>
/// Experimental WebDriver BiDi WebSocket transport for the currently implemented
/// session-control subset. Unsupported BiDi commands fail explicitly instead of being
/// advertised as implemented.
///
/// The transport is loopback-only and requires an already-created WebDriver
/// session. The session id is treated as a capability and every upgrade is
/// subjected to the same remote-endpoint and browser-Origin checks as the HTTP
/// WebDriver endpoint.
/// </summary>
public sealed class BiDiWebSocketServer : IDisposable, IAsyncDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int MaxMethodChars = 256;
    private const int MaxConcurrentConnections = 16;
    private const int EventQueueCapacity = 256;
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "browsingContext.navigationStarted",
        "browsingContext.domContentLoaded",
        "browsingContext.load",
        "log.entryAdded"
    };
    private static readonly Regex SessionPathRegex = new(
        @"^/session/(?<sid>[0-9a-fA-F]{32})/bidi$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpListener _listener;
    private readonly SessionManager _sessionManager;
    private readonly IBrowserDriver? _browser;
    private readonly CommandHandler? _commandHandler;
    private readonly OriginValidator _originValidator;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectionAdmission = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sessionReservations = new(StringComparer.Ordinal);
    private readonly int _port;
    private Task? _listenTask;
    private int _started;
    private int _disposed;

    public BiDiWebSocketServer(
        SessionManager sessionManager,
        int port,
        IBrowserDriver? browser = null,
        CommandHandler? commandHandler = null)
    {
        // HttpListener cannot use port 0 as an "ephemeral port" request. Accepting it
        // here produced a server object that could never establish its advertised URL.
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "BiDi listener port must be between 1 and 65535.");
        }

        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _browser = browser;
        _commandHandler = commandHandler;
        _port = port;
        _originValidator = new OriginValidator(allowLocalhostOnly: true);

        _listener = new HttpListener();
        if (port != 0)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/session/");
        }
    }

    public int ConnectedClientCount => _clients.Count;

    public void Start()
    {
        ThrowIfDisposed();
        if (_port == 0)
        {
            throw new InvalidOperationException("BiDi requires an explicit listener port.");
        }
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
            try
            {
                _connectionAdmission.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose may race a final admitted connection. Connection admission
                // is already shutting down; releasing the disposed gate has no value.
            }
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
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        await using var client = new BiDiClient(socket, ct);
        try
        {
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

                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                    {
                        if (buffer.Length >= MaxMessageBytes)
                        {
                            await CloseBestEffortAsync(
                                socket,
                                WebSocketCloseStatus.MessageTooBig,
                                "message too large",
                                ct).ConfigureAwait(false);
                            return;
                        }

                        var expanded = ArrayPool<byte>.Shared.Rent(
                            Math.Min(MaxMessageBytes, buffer.Length * 2));
                        buffer.AsSpan(0, count).CopyTo(expanded);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = expanded;
                    }

                    result = await socket.ReceiveAsync(
                        buffer.AsMemory(count, Math.Min(buffer.Length - count, MaxMessageBytes - count)),
                        ct).ConfigureAwait(false);
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

                    count += result.Count;
                }
                while (!result.EndOfMessage);

                var processed = await ProcessMessageAsync(
                    buffer.AsMemory(0, count),
                    sessionId,
                    client.Subscriptions).ConfigureAwait(false);
                using (processed.Response)
                {
                    await client.SendResponseAsync(processed.Response).ConfigureAwait(false);
                }

                if (processed.Events != null)
                {
                    foreach (var evt in processed.Events)
                    {
                        client.TryQueueEvent(evt);
                    }
                }

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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<BiDiMessageResult> ProcessMessageAsync(
        ReadOnlyMemory<byte> json,
        string sessionId,
        SubscriptionState subscriptions)
    {
        long id = -1;
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
                !idElement.TryGetInt64(out id) ||
                id < 0 ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(-1, "invalid argument", "BiDi messages require a non-negative integer 'id' and string 'method'.");
            }

            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method) || method.Length > MaxMethodChars)
            {
                return Error(id, "invalid argument", $"BiDi method must contain 1-{MaxMethodChars} characters.");
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

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
                    return Subscribe(id, parameters, subscriptions, sessionId);

                case "session.unsubscribe":
                    return Unsubscribe(id, parameters, subscriptions);

                case "browsingContext.getTree":
                    return await GetBrowsingContextTreeAsync(id, parameters, sessionId).ConfigureAwait(false);

                case "browsingContext.navigate":
                    return await NavigateAsync(id, parameters, sessionId, subscriptions).ConfigureAwait(false);

                case "script.getRealms":
                    return GetRealms(id, parameters, sessionId);

                case "script.evaluate":
                    return await EvaluateAsync(id, parameters, sessionId).ConfigureAwait(false);

                case "script.callFunction":
                    return await CallFunctionAsync(id, parameters, sessionId).ConfigureAwait(false);

                case "ping":
                    return Success(id, new Dictionary<string, object> { ["pong"] = true });

                default:
                    return Error(id, "unknown command", $"Unknown BiDi method: {method}");
            }
        }
        catch (JsonException)
        {
            return Error(id, "invalid argument", "Malformed BiDi message JSON.");
        }
        catch (BiDiProtocolException ex)
        {
            return Error(id, ex.Error, ex.Message);
        }
        catch (WebDriverException ex)
        {
            return Error(id, ex.ErrorCode, ex.Message);
        }
        catch (Exception)
        {
            return Error(id, "unknown error", "BiDi command failed.");
        }
    }

    private BiDiMessageResult Subscribe(
        long id,
        JsonElement parameters,
        SubscriptionState subscriptions,
        string sessionId)
    {
        RequireObject(parameters);
        if (!parameters.TryGetProperty("events", out var eventsElement) ||
            eventsElement.ValueKind != JsonValueKind.Array)
        {
            throw new BiDiProtocolException("invalid argument", "events must be a non-empty array.");
        }

        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in eventsElement.EnumerateArray())
        {
            var eventName = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (eventName == null || !SupportedEvents.Contains(eventName))
            {
                throw new BiDiProtocolException("invalid argument", $"Unsupported event: {eventName ?? "(non-string)"}");
            }

            events.Add(eventName);
        }

        if (events.Count == 0)
        {
            throw new BiDiProtocolException("invalid argument", "events must be a non-empty array.");
        }

        var contexts = ParseContexts(parameters, sessionId);
        var subscriptionId = subscriptions.Add(events, contexts);
        return Success(id, new Dictionary<string, object> { ["subscription"] = subscriptionId });
    }

    private static BiDiMessageResult Unsubscribe(
        long id,
        JsonElement parameters,
        SubscriptionState subscriptions)
    {
        RequireObject(parameters);
        if (!parameters.TryGetProperty("subscriptions", out var idsElement) ||
            idsElement.ValueKind != JsonValueKind.Array)
        {
            throw new BiDiProtocolException("invalid argument", "subscriptions must be an array.");
        }

        foreach (var item in idsElement.EnumerateArray())
        {
            var subscriptionId = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (subscriptionId == null || !subscriptions.Remove(subscriptionId))
            {
                throw new BiDiProtocolException("invalid argument", "Unknown subscription.");
            }
        }

        return Success(id, new Dictionary<string, object>());
    }

    private async Task<BiDiMessageResult> GetBrowsingContextTreeAsync(
        long id,
        JsonElement parameters,
        string sessionId)
    {
        if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            throw new BiDiProtocolException("invalid argument", "params must be an object.");
        }

        var session = _sessionManager.GetSession(sessionId);
        var requestedRoot = parameters.ValueKind == JsonValueKind.Object &&
                            parameters.TryGetProperty("root", out var rootElement)
            ? rootElement.GetString()
            : null;
        if (requestedRoot != null && !session.WindowHandles.Contains(requestedRoot))
        {
            throw new BiDiProtocolException("no such frame", "Browsing context was not found.");
        }

        var url = "about:blank";
        var title = string.Empty;
        if (_browser != null && session.CurrentWindowHandle != null)
        {
            url = await _browser.GetCurrentUrlAsync().ConfigureAwait(false);
            title = await _browser.GetTitleAsync().ConfigureAwait(false);
        }

        var contexts = new List<object>();
        foreach (var handle in session.WindowHandles)
        {
            if (requestedRoot != null && !string.Equals(handle, requestedRoot, StringComparison.Ordinal))
            {
                continue;
            }

            contexts.Add(new Dictionary<string, object?>
            {
                ["context"] = handle,
                ["url"] = string.Equals(handle, session.CurrentWindowHandle, StringComparison.Ordinal) ? url : "about:blank",
                ["userContext"] = "default",
                ["originalOpener"] = null,
                ["clientWindow"] = handle,
                ["children"] = null,
                ["parent"] = null,
                ["title"] = string.Equals(handle, session.CurrentWindowHandle, StringComparison.Ordinal) ? title : string.Empty
            });
        }

        return Success(id, new Dictionary<string, object> { ["contexts"] = contexts });
    }

    private async Task<BiDiMessageResult> NavigateAsync(
        long id,
        JsonElement parameters,
        string sessionId,
        SubscriptionState subscriptions)
    {
        RequireObject(parameters);
        var context = GetRequiredString(parameters, "context");
        var url = GetRequiredString(parameters, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new BiDiProtocolException("invalid argument", "url must be absolute.");
        }

        var session = _sessionManager.GetSession(sessionId);
        if (!session.WindowHandles.Contains(context))
        {
            throw new BiDiProtocolException("no such frame", "Browsing context was not found.");
        }

        if (_browser == null)
        {
            throw new BiDiProtocolException("unsupported operation", "Browser integration is unavailable.");
        }

        if (!string.Equals(session.CurrentWindowHandle, context, StringComparison.Ordinal))
        {
            await _browser.SwitchToWindowAsync(context).ConfigureAwait(false);
            session.CurrentWindowHandle = context;
        }

        var navigation = Guid.NewGuid().ToString("N");
        await _browser.NavigateAsync(uri.AbsoluteUri).ConfigureAwait(false);
        var committedUrl = await _browser.GetCurrentUrlAsync().ConfigureAwait(false);
        var events = new List<PooledBufferWriter>();
        AddNavigationEvent(events, subscriptions, "browsingContext.navigationStarted", context, navigation, committedUrl);
        AddNavigationEvent(events, subscriptions, "browsingContext.domContentLoaded", context, navigation, committedUrl);
        AddNavigationEvent(events, subscriptions, "browsingContext.load", context, navigation, committedUrl);

        return Success(
            id,
            new Dictionary<string, object?>
            {
                ["navigation"] = navigation,
                ["url"] = committedUrl
            },
            events: events);
    }

    private BiDiMessageResult GetRealms(long id, JsonElement parameters, string sessionId)
    {
        if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            throw new BiDiProtocolException("invalid argument", "params must be an object.");
        }

        var session = _sessionManager.GetSession(sessionId);
        var requestedContext = parameters.ValueKind == JsonValueKind.Object &&
                               parameters.TryGetProperty("context", out var contextElement)
            ? contextElement.GetString()
            : null;
        var realms = new List<object>();
        foreach (var context in session.WindowHandles)
        {
            if (requestedContext != null && !string.Equals(context, requestedContext, StringComparison.Ordinal))
            {
                continue;
            }

            realms.Add(new Dictionary<string, object>
            {
                ["realm"] = RealmId(context),
                ["origin"] = "null",
                ["type"] = "window",
                ["context"] = context
            });
        }

        if (requestedContext != null && realms.Count == 0)
        {
            throw new BiDiProtocolException("no such frame", "Browsing context was not found.");
        }

        return Success(id, new Dictionary<string, object> { ["realms"] = realms });
    }

    private async Task<BiDiMessageResult> EvaluateAsync(long id, JsonElement parameters, string sessionId)
    {
        RequireObject(parameters);
        var expression = GetRequiredString(parameters, "expression");
        var context = GetTargetContext(parameters, sessionId);
        await SelectContextAsync(context, sessionId).ConfigureAwait(false);
        _commandHandler?.EnsureScriptAllowed(sessionId, expression);
        if (_browser == null)
        {
            throw new BiDiProtocolException("unsupported operation", "Browser integration is unavailable.");
        }

        var value = await _browser.ExecuteScriptAsync(
            $"return ({expression});",
            Array.Empty<object>()).ConfigureAwait(false);
        return ScriptSuccess(id, context, value);
    }

    private async Task<BiDiMessageResult> CallFunctionAsync(long id, JsonElement parameters, string sessionId)
    {
        RequireObject(parameters);
        var declaration = GetRequiredString(parameters, "functionDeclaration");
        var context = GetTargetContext(parameters, sessionId);
        await SelectContextAsync(context, sessionId).ConfigureAwait(false);
        _commandHandler?.EnsureScriptAllowed(sessionId, declaration);
        if (_browser == null)
        {
            throw new BiDiProtocolException("unsupported operation", "Browser integration is unavailable.");
        }

        var arguments = new List<object?> { null };
        if (parameters.TryGetProperty("arguments", out var argsElement))
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
            {
                throw new BiDiProtocolException("invalid argument", "arguments must be an array.");
            }

            foreach (var argument in argsElement.EnumerateArray())
            {
                arguments.Add(ParseLocalValue(argument));
            }
        }

        if (parameters.TryGetProperty("this", out var thisElement))
        {
            arguments[0] = ParseLocalValue(thisElement);
        }

        var value = await _browser.ExecuteScriptAsync(
            $"return ({declaration}).apply(arguments[0], Array.prototype.slice.call(arguments, 1));",
            arguments.ToArray()!).ConfigureAwait(false);
        return ScriptSuccess(id, context, value);
    }

    private HashSet<string>? ParseContexts(JsonElement parameters, string sessionId)
    {
        if (!parameters.TryGetProperty("contexts", out var contextsElement))
        {
            return null;
        }

        if (contextsElement.ValueKind != JsonValueKind.Array)
        {
            throw new BiDiProtocolException("invalid argument", "contexts must be an array.");
        }

        var session = _sessionManager.GetSession(sessionId);
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in contextsElement.EnumerateArray())
        {
            var context = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (context == null || !session.WindowHandles.Contains(context))
            {
                throw new BiDiProtocolException("no such frame", "Browsing context was not found.");
            }

            contexts.Add(context);
        }

        return contexts;
    }

    private static void RequireObject(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new BiDiProtocolException("invalid argument", "params must be an object.");
        }
    }

    private static string GetRequiredString(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new BiDiProtocolException("invalid argument", $"{name} must be a non-empty string.");
        }

        return element.GetString()!;
    }

    private string GetTargetContext(JsonElement parameters, string sessionId)
    {
        if (!parameters.TryGetProperty("target", out var target) ||
            target.ValueKind != JsonValueKind.Object)
        {
            throw new BiDiProtocolException("invalid argument", "target must be an object.");
        }

        var context = GetRequiredString(target, "context");
        if (!_sessionManager.GetSession(sessionId).WindowHandles.Contains(context))
        {
            throw new BiDiProtocolException("no such frame", "Browsing context was not found.");
        }

        return context;
    }

    private async Task SelectContextAsync(string context, string sessionId)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (string.Equals(session.CurrentWindowHandle, context, StringComparison.Ordinal))
        {
            return;
        }

        if (_browser == null)
        {
            throw new BiDiProtocolException("unsupported operation", "Browser integration is unavailable.");
        }

        await _browser.SwitchToWindowAsync(context).ConfigureAwait(false);
        session.CurrentWindowHandle = context;
    }

    private static object? ParseLocalValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            throw new BiDiProtocolException("invalid argument", "Local value must have a type.");
        }

        var type = typeElement.GetString();
        element.TryGetProperty("value", out var value);
        return type switch
        {
            "undefined" or "null" => null,
            "string" when value.ValueKind == JsonValueKind.String => value.GetString(),
            "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            "number" when value.ValueKind == JsonValueKind.Number => value.TryGetInt64(out var integer)
                ? integer
                : value.GetDouble(),
            "array" when value.ValueKind == JsonValueKind.Array =>
                value.EnumerateArray().Select(ParseLocalValue).ToArray(),
            _ => throw new BiDiProtocolException("invalid argument", $"Unsupported local value type: {type}")
        };
    }

    private static BiDiMessageResult ScriptSuccess(long id, string context, object? value)
        => Success(id, new Dictionary<string, object>
        {
            ["realm"] = RealmId(context),
            ["result"] = ToRemoteValue(value)
        });

    private static object ToRemoteValue(object? value)
    {
        if (value == null)
        {
            return new Dictionary<string, object> { ["type"] = "null" };
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => ToRemoteValue(null),
                JsonValueKind.String => ToRemoteValue(element.GetString()),
                JsonValueKind.True or JsonValueKind.False => ToRemoteValue(element.GetBoolean()),
                JsonValueKind.Number => ToRemoteValue(element.TryGetInt64(out var integer) ? integer : element.GetDouble()),
                JsonValueKind.Array => new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["value"] = element.EnumerateArray().Select(item => ToRemoteValue(item)).ToArray()
                },
                _ => new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["value"] = element.EnumerateObject()
                        .Select(property => new object[] { property.Name, ToRemoteValue(property.Value) })
                        .ToArray()
                }
            };
        }

        if (value is string text)
        {
            return new Dictionary<string, object> { ["type"] = "string", ["value"] = text };
        }

        if (value is bool boolean)
        {
            return new Dictionary<string, object> { ["type"] = "boolean", ["value"] = boolean };
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return new Dictionary<string, object> { ["type"] = "number", ["value"] = value };
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = new List<object>();
            foreach (var item in enumerable)
            {
                items.Add(ToRemoteValue(item));
            }

            return new Dictionary<string, object> { ["type"] = "array", ["value"] = items };
        }

        return new Dictionary<string, object> { ["type"] = "string", ["value"] = value.ToString() ?? string.Empty };
    }

    private static string RealmId(string context) => "window-" + context;

    private static void AddNavigationEvent(
        List<PooledBufferWriter> events,
        SubscriptionState subscriptions,
        string method,
        string context,
        string navigation,
        string url)
    {
        if (!subscriptions.IsSubscribed(method, context))
        {
            return;
        }

        events.Add(BuildEvent(method, new Dictionary<string, object>
        {
            ["context"] = context,
            ["navigation"] = navigation,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["url"] = url
        }));
    }

    private static BiDiMessageResult Success(
        long id,
        object result,
        bool closeAfterResponse = false,
        List<PooledBufferWriter>? events = null)
        => new(BuildResultResponse(id, result), closeAfterResponse, events);

    private static BiDiMessageResult Error(long id, string error, string message)
        => new(BuildErrorResponse(id, error, message), false, null);

    private static PooledBufferWriter BuildResultResponse(long id, object result)
    {
        return BuildMessage(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "success");
            writer.WriteNumber("id", id);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result);
            writer.WriteEndObject();
        });
    }

    private static PooledBufferWriter BuildErrorResponse(long id, string error, string message)
    {
        return BuildMessage(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "error");
            writer.WriteNumber("id", id);
            writer.WriteString("error", error);
            writer.WriteString("message", message);
            writer.WriteString("stacktrace", string.Empty);
            writer.WriteEndObject();
        });
    }

    private static PooledBufferWriter BuildEvent(string method, object parameters)
    {
        return BuildMessage(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "event");
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters);
            writer.WriteEndObject();
        });
    }

    private static PooledBufferWriter BuildMessage(Action<Utf8JsonWriter> write)
    {
        var buffer = new PooledBufferWriter();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            write(writer);
            writer.Flush();
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
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

    private readonly record struct BiDiMessageResult(
        PooledBufferWriter Response,
        bool CloseAfterResponse,
        List<PooledBufferWriter>? Events);

    private sealed class BiDiProtocolException : Exception
    {
        public BiDiProtocolException(string error, string message)
            : base(message)
        {
            Error = error;
        }

        public string Error { get; }
    }

    private sealed class SubscriptionState
    {
        private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

        public string Add(HashSet<string> events, HashSet<string>? contexts)
        {
            var id = Guid.NewGuid().ToString("N");
            _subscriptions.Add(id, new Subscription(events, contexts));
            return id;
        }

        public bool Remove(string id) => _subscriptions.Remove(id);

        public bool IsSubscribed(string eventName, string context)
            => _subscriptions.Values.Any(subscription =>
                subscription.Events.Contains(eventName) &&
                (subscription.Contexts == null || subscription.Contexts.Contains(context)));

        private sealed record Subscription(HashSet<string> Events, HashSet<string>? Contexts);
    }

    private sealed class BiDiClient : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly CancellationToken _cancellationToken;
        private readonly Channel<PooledBufferWriter> _events;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly Task _eventPump;

        public BiDiClient(WebSocket socket, CancellationToken cancellationToken)
        {
            _socket = socket;
            _cancellationToken = cancellationToken;
            _events = Channel.CreateBounded<PooledBufferWriter>(new BoundedChannelOptions(EventQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            _eventPump = Task.Run(PumpEventsAsync);
        }

        public SubscriptionState Subscriptions { get; } = new();

        public async Task SendResponseAsync(PooledBufferWriter response)
        {
            await _sendGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    response.WrittenMemory,
                    WebSocketMessageType.Text,
                    true,
                    _cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public void TryQueueEvent(PooledBufferWriter message)
        {
            if (!_events.Writer.TryWrite(message))
            {
                message.Dispose();
            }
        }

        private async Task PumpEventsAsync()
        {
            try
            {
                await foreach (var message in _events.Reader.ReadAllAsync(_cancellationToken).ConfigureAwait(false))
                {
                    using (message)
                    {
                        await _sendGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await _socket.SendAsync(
                                message.WrittenMemory,
                                WebSocketMessageType.Text,
                                true,
                                _cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            _sendGate.Release();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                while (_events.Reader.TryRead(out var message))
                {
                    message.Dispose();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            try
            {
                await _eventPump.ConfigureAwait(false);
            }
            catch
            {
            }
            _sendGate.Dispose();
        }
    }

    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(512);
        private int _written;

        public ReadOnlyMemory<byte> WrittenMemory
            => _buffer == null
                ? ReadOnlyMemory<byte>.Empty
                : _buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            if (_buffer == null || count < 0 || _written > _buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_buffer == null)
            {
                throw new ObjectDisposedException(nameof(PooledBufferWriter));
            }

            sizeHint = Math.Max(sizeHint, 1);
            if (sizeHint <= _buffer.Length - _written)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(
                Math.Max(_written + sizeHint, _buffer.Length * 2));
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
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

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
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
        GC.SuppressFinalize(this);
    }
}
