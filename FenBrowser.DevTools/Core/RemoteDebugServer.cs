using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Authenticated local remote-debugging endpoint. HTTP discovery and WebSocket framing
/// share one transport, but every upgraded client owns exactly one outbound writer.
/// </summary>
public sealed class RemoteDebugServer : IDisposable
{
    private const int MaxHttpHeaderBytes = 32 * 1024;
    private const int MaxFrameBytes = 10 * 1024 * 1024;
    private const int MaxMessageBytes = 10 * 1024 * 1024;
    private const int OutboundQueueCapacity = 256;
    private const int HeartbeatIntervalMs = 30_000;
    private const int HeartbeatTimeoutMs = 65_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly TcpListener _listener;
    private readonly DevToolsServer _devToolsServer;
    private readonly int _port;
    private readonly string _advertisedHost;
    private readonly string _authToken;
    private readonly bool _usesEphemeralAuthToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ClientConnection> _clients = new();
    private System.Threading.Timer _heartbeatTimer;
    private long _messagesSent;
    private long _messagesReceived;
    private int _started;
    private int _disposed;

    public int ConnectionCount
    {
        get
        {
            lock (_clients)
            {
                return _clients.Count;
            }
        }
    }

    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
    public string AuthToken => _authToken;
    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : _port;

    public RemoteDebugServer(
        DevToolsServer devToolsServer,
        int port = 9222,
        string bindAddress = "127.0.0.1",
        string authToken = null)
    {
        _devToolsServer = devToolsServer ?? throw new ArgumentNullException(nameof(devToolsServer));
        _port = port;
        _authToken = NormalizeAuthToken(authToken, out _usesEphemeralAuthToken);

        if (!IPAddress.TryParse(bindAddress, out var bindIp))
        {
            bindIp = IPAddress.Loopback;
        }

        var allowRemoteClients = string.Equals(
            Environment.GetEnvironmentVariable("FEN_REMOTE_DEBUG_ALLOW_REMOTE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        var bindDecision = BrowserSecurityPolicy.EvaluateRemoteDebugBinding(
            bindIp,
            allowRemoteClients,
            !string.IsNullOrWhiteSpace(authToken));

        if (!bindDecision.IsAllowed)
        {
            bindDecision.Log(LogCategory.Security);
            throw new InvalidOperationException(bindDecision.Message);
        }

        bindDecision.Log(LogCategory.Security, LogLevel.Info);
        _advertisedHost = bindIp.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{bindIp}]"
            : bindIp.ToString();

        _listener = new TcpListener(bindIp, port);
        _devToolsServer.OnJsonOutput(BroadcastToClients);
    }

    private static string NormalizeAuthToken(string authToken, out bool usesEphemeralAuthToken)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            usesEphemeralAuthToken = false;
            return authToken.Trim();
        }

        usesEphemeralAuthToken = true;
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        try
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            EngineLogCompat.Info(
                $"[RemoteDebug] TCP server started on {endpoint.Address}:{endpoint.Port} (auth required)",
                LogCategory.DevTools);

            if (_usesEphemeralAuthToken)
            {
                EngineLogCompat.Info(
                    "[RemoteDebug] Generated ephemeral auth token; credential value is not logged.",
                    LogCategory.Security);
            }
            else
            {
                EngineLogCompat.Info(
                    "[RemoteDebug] Using configured remote-debug auth token.",
                    LogCategory.Security);
            }

            _heartbeatTimer = new System.Threading.Timer(
                static state => ((RemoteDebugServer)state).HeartbeatTick(),
                this,
                HeartbeatIntervalMs,
                HeartbeatIntervalMs);

            _ = Task.Run(ListenLoopAsync);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client = null;
            try
            {
                client = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                EngineLogCompat.Info(
                    $"[RemoteDebug] Connection accepted from {client.RemoteEndPoint}",
                    LogCategory.DevTools);
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (!_cts.IsCancellationRequested)
                {
                    EngineLogCompat.Error(
                        $"[RemoteDebug] Accept error: {ex.Message}",
                        LogCategory.DevTools);
                }
            }
        }
    }

    private async Task HandleClientAsync(Socket socket)
    {
        using var scope = EngineLogCompat.BeginScope(
            component: "RemoteDebug",
            data: new Dictionary<string, object>
            {
                ["remoteEndPoint"] = socket.RemoteEndPoint?.ToString() ?? string.Empty
            });

        NetworkStream stream = null;
        ClientConnection connection = null;
        try
        {
            stream = new NetworkStream(socket, ownsSocket: false);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = await ReadHttpHeaderAsync(stream, handshakeCts.Token).ConfigureAwait(false);
            if (request == null)
            {
                return;
            }

            if (!IsAuthorizedRequest(request))
            {
                EngineLogCompat.Warn(
                    "[RemoteDebug] Unauthorized request rejected",
                    LogCategory.Security);
                await SendHttpResponseAsync(
                    stream,
                    "401 Unauthorized",
                    "{\"error\":\"unauthorized\"}",
                    "application/json",
                    new Dictionary<string, string>
                    {
                        ["WWW-Authenticate"] = "Bearer realm=\"FenBrowser RemoteDebug\""
                    },
                    handshakeCts.Token).ConfigureAwait(false);
                return;
            }

            if (!TryParseRequestLine(request, out var method, out _, out _))
            {
                await SendHttpResponseAsync(
                    stream,
                    "400 Bad Request",
                    string.Empty,
                    "text/plain",
                    null,
                    handshakeCts.Token).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await SendHttpResponseAsync(
                    stream,
                    "405 Method Not Allowed",
                    string.Empty,
                    "text/plain",
                    new Dictionary<string, string> { ["Allow"] = "GET" },
                    handshakeCts.Token).ConfigureAwait(false);
                return;
            }

            var headers = ParseHeaders(request);
            if (IsWebSocketUpgrade(headers))
            {
                if (!await DoHandshakeAsync(stream, headers, handshakeCts.Token).ConfigureAwait(false))
                {
                    return;
                }

                connection = new ClientConnection(
                    socket,
                    stream,
                    OutboundQueueCapacity,
                    () => Interlocked.Increment(ref _messagesSent),
                    RemoveClient);
                stream = null;

                AddClient(connection);
                await ReceiveWebSocketLoopAsync(connection, _cts.Token).ConfigureAwait(false);
                return;
            }

            await HandleHttpRequestAsync(stream, request, handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            EngineLogCompat.Warn(
                "[RemoteDebug] Client handshake/read timed out.",
                LogCategory.DevTools);
        }
        catch (InvalidDataException ex)
        {
            EngineLogCompat.Warn(
                $"[RemoteDebug] Protocol violation: {ex.Message}",
                LogCategory.Security);
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                EngineLogCompat.Error(
                    $"[RemoteDebug] Client error: {ex.Message}",
                    LogCategory.DevTools);
            }
        }
        finally
        {
            if (connection != null)
            {
                RemoveClient(connection);
                connection.Dispose();
            }
            else
            {
                stream?.Dispose();
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private void AddClient(ClientConnection client)
    {
        lock (_clients)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                client.Dispose();
                return;
            }

            _clients.Add(client);
        }
    }

    private void RemoveClient(ClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        lock (_clients)
        {
            _clients.Remove(client);
        }
    }

    private async Task HandleHttpRequestAsync(
        NetworkStream stream,
        string request,
        CancellationToken cancellationToken)
    {
        var requestTarget = GetRequestTarget(request);
        var path = "/";
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }
        else if (Uri.TryCreate("http://localhost" + requestTarget, UriKind.Absolute, out var relativeUri))
        {
            path = relativeUri.AbsolutePath;
        }

        var tokenQuery = BuildTokenQuery();
        var webSocketUrl = BuildWebSocketDebuggerUrl();
        var devToolsFrontendUrl = BuildDevToolsFrontendUrl(tokenQuery);

        if (string.Equals(path, "/json/version", StringComparison.OrdinalIgnoreCase))
        {
            var version = typeof(RemoteDebugServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var responseBody = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Browser"] = $"FenBrowser/{version}",
                ["Protocol-Version"] = "1.3",
                ["User-Agent"] = BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent),
                ["webSocketDebuggerUrl"] = webSocketUrl
            });

            await SendHttpResponseAsync(
                stream,
                "200 OK",
                responseBody,
                "application/json",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/json/list", StringComparison.OrdinalIgnoreCase))
        {
            var targets = new[]
            {
                new
                {
                    description = "FenBrowser Active Tab",
                    devtoolsFrontendUrl,
                    id = "1",
                    title = "FenBrowser Tab",
                    type = "page",
                    url = "about:blank",
                    webSocketDebuggerUrl = webSocketUrl
                }
            };

            await SendHttpResponseAsync(
                stream,
                "200 OK",
                JsonSerializer.Serialize(targets),
                "application/json",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendHttpResponseAsync(
            stream,
            "404 Not Found",
            string.Empty,
            "text/plain",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendHttpResponseAsync(
        NetworkStream stream,
        string status,
        string body,
        string contentType,
        IReadOnlyDictionary<string, string> extraHeaders,
        CancellationToken cancellationToken)
    {
        body ??= string.Empty;
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("; charset=UTF-8\r\n");
        builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("X-Content-Type-Options: nosniff\r\n");

        if (extraHeaders != null)
        {
            foreach (var pair in extraHeaders)
            {
                if (pair.Key.IndexOfAny(new[] { '\r', '\n', ':' }) >= 0 ||
                    pair.Value?.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                {
                    continue;
                }

                builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
        }

        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        using var header = new MemoryStream(capacity: 1024);
        var state = 0;

        while (header.Length < MaxHttpHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return header.Length == 0
                    ? null
                    : throw new InvalidDataException("Connection closed before HTTP headers completed.");
            }

            var value = buffer[0];
            header.WriteByte(value);
            state = state switch
            {
                0 when value == (byte)'\r' => 1,
                1 when value == (byte)'\n' => 2,
                2 when value == (byte)'\r' => 3,
                3 when value == (byte)'\n' => 4,
                _ when value == (byte)'\r' => 1,
                _ => 0
            };

            if (state == 4)
            {
                return Encoding.ASCII.GetString(header.GetBuffer(), 0, checked((int)header.Length));
            }
        }

        throw new InvalidDataException($"HTTP header exceeded {MaxHttpHeaderBytes} bytes.");
    }

    private static bool TryParseRequestLine(
        string request,
        out string method,
        out string target,
        out string version)
    {
        method = string.Empty;
        target = string.Empty;
        version = string.Empty;

        if (string.IsNullOrEmpty(request))
        {
            return false;
        }

        var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = lineEnd >= 0 ? request[..lineEnd] : request;
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        method = parts[0];
        target = parts[1];
        version = parts[2];
        return string.Equals(version, "HTTP/1.1", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRequestTarget(string request)
    {
        return TryParseRequestLine(request, out _, out var target, out _)
            ? target
            : "/";
    }

    private static Dictionary<string, string> ParseHeaders(string request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(request))
        {
            return headers;
        }

        var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                break;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0)
            {
                headers[key] = value;
            }
        }

        return headers;
    }

    private static bool HeaderContainsToken(string value, string token)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWebSocketUpgrade(IReadOnlyDictionary<string, string> headers)
    {
        return headers.TryGetValue("Upgrade", out var upgrade) &&
               string.Equals(upgrade.Trim(), "websocket", StringComparison.OrdinalIgnoreCase) &&
               headers.TryGetValue("Connection", out var connection) &&
               HeaderContainsToken(connection, "Upgrade");
    }

    private async Task<bool> DoHandshakeAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) ||
            !string.Equals(version.Trim(), "13", StringComparison.Ordinal))
        {
            await SendHttpResponseAsync(
                stream,
                "426 Upgrade Required",
                string.Empty,
                "text/plain",
                new Dictionary<string, string> { ["Sec-WebSocket-Version"] = "13" },
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var keyText))
        {
            await SendHttpResponseAsync(
                stream,
                "400 Bad Request",
                string.Empty,
                "text/plain",
                null,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyText.Trim());
        }
        catch (FormatException)
        {
            keyBytes = Array.Empty<byte>();
        }

        if (keyBytes.Length != 16)
        {
            await SendHttpResponseAsync(
                stream,
                "400 Bad Request",
                string.Empty,
                "text/plain",
                null,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(keyText.Trim() + magic)));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        var responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ReceiveWebSocketLoopAsync(
        ClientConnection client,
        CancellationToken cancellationToken)
    {
        MemoryStream fragmentedPayload = null;
        var fragmentedOpcode = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !client.IsClosed)
            {
                var frame = await ReadWebSocketFrameAsync(
                    client.Stream,
                    cancellationToken).ConfigureAwait(false);
                if (frame == null)
                {
                    break;
                }

                if (frame.Opcode >= 0x8)
                {
                    if (frame.Opcode == 0x8)
                    {
                        if (frame.Payload.Length == 1)
                        {
                            throw new InvalidDataException("WebSocket close frame has an invalid one-byte payload.");
                        }

                        client.TryQueueFrame(EncodeFrame(0x8, frame.Payload));
                        break;
                    }

                    if (frame.Opcode == 0x9)
                    {
                        if (!client.TryQueueFrame(EncodeFrame(0xA, frame.Payload)))
                        {
                            break;
                        }

                        continue;
                    }

                    if (frame.Opcode == 0xA)
                    {
                        client.MarkPong();
                        continue;
                    }

                    throw new InvalidDataException($"Unsupported WebSocket control opcode {frame.Opcode}.");
                }

                if (frame.Opcode == 0x0)
                {
                    if (fragmentedPayload == null)
                    {
                        throw new InvalidDataException("Unexpected WebSocket continuation frame.");
                    }

                    AppendFragment(fragmentedPayload, frame.Payload);
                    if (!frame.Fin)
                    {
                        continue;
                    }

                    var completed = fragmentedPayload.ToArray();
                    var opcode = fragmentedOpcode;
                    fragmentedPayload.Dispose();
                    fragmentedPayload = null;
                    fragmentedOpcode = 0;
                    if (!await ProcessWebSocketMessageAsync(client, opcode, completed).ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }

                if (frame.Opcode is not (0x1 or 0x2))
                {
                    throw new InvalidDataException($"Unsupported WebSocket opcode {frame.Opcode}.");
                }

                if (fragmentedPayload != null)
                {
                    throw new InvalidDataException("New data frame arrived before fragmented message completed.");
                }

                if (!frame.Fin)
                {
                    fragmentedOpcode = frame.Opcode;
                    fragmentedPayload = new MemoryStream(
                        capacity: Math.Min(MaxMessageBytes, Math.Max(frame.Payload.Length, 256)));
                    AppendFragment(fragmentedPayload, frame.Payload);
                    continue;
                }

                if (!await ProcessWebSocketMessageAsync(
                        client,
                        frame.Opcode,
                        frame.Payload).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            fragmentedPayload?.Dispose();
        }
    }

    private async Task<bool> ProcessWebSocketMessageAsync(
        ClientConnection client,
        int opcode,
        byte[] payload)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"WebSocket message exceeded {MaxMessageBytes} bytes.");
        }

        if (opcode == 0x2)
        {
            client.TryQueueFrame(EncodeCloseFrame(1003, "Binary messages are not supported."));
            return false;
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            client.TryQueueFrame(EncodeCloseFrame(1007, "Invalid UTF-8."));
            return false;
        }

        Interlocked.Increment(ref _messagesReceived);
        var response = await _devToolsServer.ProcessRequestAsync(json).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response) &&
            !client.TryQueueFrame(EncodeTextFrame(response)))
        {
            return false;
        }

        return true;
    }

    private static void AppendFragment(MemoryStream target, byte[] payload)
    {
        if (target.Length + payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Fragmented WebSocket message exceeded {MaxMessageBytes} bytes.");
        }

        target.Write(payload, 0, payload.Length);
    }

    private sealed record WebSocketFrame(bool Fin, int Opcode, byte[] Payload);

    private static async Task<WebSocketFrame> ReadWebSocketFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (header == null)
        {
            return null;
        }

        var fin = (header[0] & 0x80) != 0;
        var rsv = header[0] & 0x70;
        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        var payloadIndicator = header[1] & 0x7F;
        ulong payloadLength = (ulong)payloadIndicator;

        if (rsv != 0)
        {
            throw new InvalidDataException("WebSocket RSV bits are set without negotiated extensions.");
        }

        if (!masked)
        {
            throw new InvalidDataException("Client-to-server WebSocket frames must be masked.");
        }

        if (payloadIndicator == 126)
        {
            var lengthBytes = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            if (lengthBytes == null)
            {
                return null;
            }

            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (payloadLength < 126)
            {
                throw new InvalidDataException("WebSocket frame used non-minimal 16-bit length encoding.");
            }
        }
        else if (payloadIndicator == 127)
        {
            var lengthBytes = await ReadExactAsync(stream, 8, cancellationToken).ConfigureAwait(false);
            if (lengthBytes == null)
            {
                return null;
            }

            if ((lengthBytes[0] & 0x80) != 0)
            {
                throw new InvalidDataException("WebSocket 64-bit payload length has its reserved high bit set.");
            }

            payloadLength = BinaryPrimitives.ReadUInt64BigEndian(lengthBytes);
            if (payloadLength <= ushort.MaxValue)
            {
                throw new InvalidDataException("WebSocket frame used non-minimal 64-bit length encoding.");
            }
        }

        var controlFrame = opcode >= 0x8;
        if (controlFrame && (!fin || payloadLength > 125))
        {
            throw new InvalidDataException("WebSocket control frames must be final and at most 125 bytes.");
        }

        if (payloadLength > MaxFrameBytes)
        {
            throw new InvalidDataException($"Remote-debug WebSocket frame too large: {payloadLength} bytes.");
        }

        var mask = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (mask == null)
        {
            return null;
        }

        var payload = payloadLength == 0
            ? Array.Empty<byte>()
            : await ReadExactAsync(
                stream,
                checked((int)payloadLength),
                cancellationToken).ConfigureAwait(false);
        if (payload == null)
        {
            return null;
        }

        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] ^= mask[index & 3];
        }

        return new WebSocketFrame(fin, opcode, payload);
    }

    private static async Task<byte[]> ReadExactAsync(
        NetworkStream stream,
        int length,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private void BroadcastToClients(string json)
    {
        if (string.IsNullOrEmpty(json) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var frame = EncodeTextFrame(json);
        ClientConnection[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
        }

        foreach (var client in clients)
        {
            if (!client.TryQueueFrame(frame))
            {
                RemoveClient(client);
                client.Dispose();
            }
        }
    }

    private void HeartbeatTick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ClientConnection[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
        }

        foreach (var client in clients)
        {
            if (!client.TryQueuePing(TimeSpan.FromMilliseconds(HeartbeatTimeoutMs)))
            {
                RemoveClient(client);
                client.Dispose();
            }
        }
    }

    private string BuildTokenQuery()
    {
        if (EmitTokenInUrls)
        {
            return "?token=" + Uri.EscapeDataString(_authToken);
        }

        return string.Empty;
    }

    private bool EmitTokenInUrls { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("FEN_REMOTE_DEBUG_TOKEN_IN_URL"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private string BuildWebSocketDebuggerUrl()
        => $"ws://{_advertisedHost}:{Port}/devtools/page/1{BuildTokenQuery()}";

    private string BuildDevToolsFrontendUrl(string tokenQuery)
    {
        var wsTarget = $"{_advertisedHost}:{Port}/devtools/page/1{tokenQuery}";
        return $"/devtools/inspector.html?ws={Uri.EscapeDataString(wsTarget)}";
    }

    private static string GetTokenFromTarget(string requestTarget)
    {
        if (string.IsNullOrEmpty(requestTarget))
        {
            return null;
        }

        var uriText =
            requestTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            requestTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? requestTarget
                : "http://localhost" + requestTarget;

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split(
            '&',
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var keyValue = pair.Split('=', 2);
            if (keyValue.Length >= 1 &&
                string.Equals(keyValue[0], "token", StringComparison.OrdinalIgnoreCase))
            {
                return keyValue.Length == 2
                    ? Uri.UnescapeDataString(keyValue[1])
                    : string.Empty;
            }
        }

        return null;
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private bool IsAuthorizedRequest(string request)
    {
        var headers = ParseHeaders(request);
        if (headers.TryGetValue("X-Fen-Debug-Token", out var headerToken) &&
            ConstantTimeEquals(headerToken, _authToken))
        {
            return true;
        }

        if (headers.TryGetValue("Authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            ConstantTimeEquals(
                authorization["Bearer ".Length..].Trim(),
                _authToken))
        {
            return true;
        }

        return ConstantTimeEquals(
            GetTokenFromTarget(GetRequestTarget(request)),
            _authToken);
    }

    private static byte[] EncodeTextFrame(string message)
        => EncodeFrame(0x1, Encoding.UTF8.GetBytes(message ?? string.Empty));

    private static byte[] EncodeCloseFrame(ushort code, string reason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason ?? string.Empty);
        if (reasonBytes.Length > 123)
        {
            Array.Resize(ref reasonBytes, 123);
        }

        var payload = new byte[2 + reasonBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), code);
        reasonBytes.CopyTo(payload, 2);
        return EncodeFrame(0x8, payload);
    }

    private static byte[] EncodeFrame(int opcode, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        var headerLength = payload.Length < 126
            ? 2
            : payload.Length <= ushort.MaxValue
                ? 4
                : 10;
        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | (opcode & 0x0F));

        var offset = 2;
        if (payload.Length < 126)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(
                frame.AsSpan(2, 2),
                checked((ushort)payload.Length));
            offset = 4;
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(
                frame.AsSpan(2, 8),
                (ulong)payload.Length);
            offset = 10;
        }

        payload.CopyTo(frame, offset);
        return frame;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        ClientConnection[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly Channel<byte[]> _outbound;
        private readonly CancellationTokenSource _cts = new();
        private readonly Action _onFrameSent;
        private readonly Action<ClientConnection> _onClosed;
        private readonly Task _writerTask;
        private long _lastPingTimestamp;
        private int _awaitingPong;
        private int _closed;

        public ClientConnection(
            Socket socket,
            NetworkStream stream,
            int outboundCapacity,
            Action onFrameSent,
            Action<ClientConnection> onClosed)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _onFrameSent = onFrameSent ?? throw new ArgumentNullException(nameof(onFrameSent));
            _onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));

            _outbound = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(outboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            _writerTask = Task.Run(WriterLoopAsync);
        }

        public NetworkStream Stream => _stream;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public bool TryQueueFrame(byte[] frame)
        {
            if (frame == null || IsClosed)
            {
                return false;
            }

            if (_outbound.Writer.TryWrite(frame))
            {
                return true;
            }

            Close();
            return false;
        }

        public bool TryQueuePing(TimeSpan timeout)
        {
            if (IsClosed)
            {
                return false;
            }

            if (Volatile.Read(ref _awaitingPong) != 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(
                    Interlocked.Read(ref _lastPingTimestamp));
                if (elapsed >= timeout)
                {
                    Close();
                    return false;
                }

                return true;
            }

            Interlocked.Exchange(ref _lastPingTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _awaitingPong, 1);
            if (!TryQueueFrame(EncodeFrame(0x9, Array.Empty<byte>())))
            {
                Volatile.Write(ref _awaitingPong, 0);
                return false;
            }

            return true;
        }

        public void MarkPong()
        {
            Volatile.Write(ref _awaitingPong, 0);
        }

        private async Task WriterLoopAsync()
        {
            try
            {
                await foreach (var frame in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    await _stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                    _onFrameSent();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch
            {
                Close();
            }
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _outbound.Writer.TryComplete();
            _cts.Cancel();

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            try
            {
                _socket.Close();
            }
            catch
            {
            }

            _onClosed(this);
        }

        public void Dispose()
        {
            Close();
            try
            {
                _stream.Dispose();
            }
            catch
            {
            }
        }
    }
}
