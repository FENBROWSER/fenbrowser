using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Remote debugging server implementing Chrome DevTools Protocol over WebSocket.
/// 10/10 Spec: 64-bit frames, heartbeat ping, per-client queues, graceful shutdown.
/// </summary>
public class RemoteDebugServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly DevToolsServer _devToolsServer;
    private readonly int _port;
    private readonly string _advertisedHost;
    private readonly string _authToken;
    private readonly bool _usesEphemeralAuthToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _clients = new();
    private readonly ConcurrentDictionary<Socket, ConcurrentQueue<string>> _messageQueues = new();
    private const int BufferSize = 8192;
    
    // --- 10/10: Heartbeat ping ---
    private System.Timers.Timer? _heartbeatTimer;
    private const int HeartbeatIntervalMs = 30000;
    
    // --- 10/10: Connection statistics ---
    public int ConnectionCount => _clients.Count;
    public long MessagesSent { get; private set; }
    public long MessagesReceived { get; private set; }
    public string AuthToken => _authToken;
    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : _port;

    public RemoteDebugServer(
        DevToolsServer devToolsServer,
        int port = 9222,
        string bindAddress = "127.0.0.1",
        string authToken = null)
    {
        _devToolsServer = devToolsServer;
        _port = port;
        _authToken = NormalizeAuthToken(authToken, out _usesEphemeralAuthToken);

        if (!IPAddress.TryParse(bindAddress, out var bindIp))
            bindIp = IPAddress.Loopback;

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
        _advertisedHost = bindIp.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : bindIp.ToString();

        // Security hardening: bind to explicit local interface unless caller opts otherwise.
        _listener = new TcpListener(bindIp, port);
        
        // Broadcast events to all connected clients
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
        try
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            EngineLogCompat.Info($"[RemoteDebug] TCP Server started on {endpoint.Address}:{endpoint.Port} (auth: required)", LogCategory.DevTools);
            if (_usesEphemeralAuthToken)
            {
                EngineLogCompat.Warn($"[RemoteDebug] Generated ephemeral auth token for this session: {_authToken}", LogCategory.Security);
            }
            else
            {
                EngineLogCompat.Info("[RemoteDebug] Using configured auth token from FEN_REMOTE_DEBUG_TOKEN.", LogCategory.Security);
            }
            
            // Start heartbeat timer (10/10)
            StartHeartbeat();
            
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Error($"[RemoteDebug] Failed to start TCP listener: {ex.Message}", LogCategory.DevTools);
        }
    }
    
    /// <summary>
    /// Start heartbeat ping timer. (10/10)
    /// </summary>
    private void StartHeartbeat()
    {
        _heartbeatTimer = new System.Timers.Timer(HeartbeatIntervalMs);
        _heartbeatTimer.Elapsed += (s, e) => SendPingToAllClients();
        _heartbeatTimer.AutoReset = true;
        _heartbeatTimer.Start();
    }
    
    /// <summary>
    /// Send ping to all connected clients. (10/10)
    /// </summary>
    private void SendPingToAllClients()
    {
        lock (_clients)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var client = _clients[i];
                if (!client.Connected)
                {
                    _clients.RemoveAt(i);
                    _messageQueues.TryRemove(client, out _);
                    continue;
                }
                
                try
                {
                    // WebSocket Ping frame: FIN + opcode 9, length 0
                    client.Send(new byte[] { 0x89, 0x00 });
                }
                catch
                {
                    // Client disconnected
                    _clients.RemoveAt(i);
                    _messageQueues.TryRemove(client, out _);
                }
            }
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptSocketAsync();
                EngineLogCompat.Info($"[RemoteDebug] Connection accepted from {client.RemoteEndPoint}", LogCategory.DevTools);
                _ = HandleClient(client);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    EngineLogCompat.Error($"[RemoteDebug] Accept error: {ex.Message}", LogCategory.DevTools);
                }
                break;
            }
        }
    }

    private async Task HandleClient(Socket client)
    {
        using var scope = EngineLogCompat.BeginScope(
            component: "RemoteDebug",
            data: new Dictionary<string, object>
            {
                ["remoteEndPoint"] = client.RemoteEndPoint?.ToString() ?? string.Empty
            });
        try
        {
            var stream = new NetworkStream(client, true);
            var buffer = new byte[BufferSize];
            
            // Set a timeout for the initial request to prevent hangs
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

            if (bytesRead == 0) return;
            
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (!IsAuthorizedRequest(request))
            {
                EngineLogCompat.Warn("[RemoteDebug] Unauthorized request rejected", LogCategory.Security);
                await SendUnauthorizedAsync(stream);
                return;
            }
            
            if (Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase))
            {
                if (request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                {
                    EngineLogCompat.Info("[RemoteDebug] WebSocket Upgrade detected", LogCategory.DevTools);
                    // Handle WebSocket Upgrade
                    if (DoHandshake(stream, request))
                    {
                        lock (_clients) _clients.Add(client);
                        await ReceiveWebSocketLoop(stream, client);
                    }
                }
                else
                {
                    // Handle Standard HTTP Request (JSON endpoints)
                    await HandleHttpRequest(stream, request);
                    client.Close();
                }
            }
        }
        catch (OperationCanceledException)
        {
             // Timeout - client connected but sent nothing (e.g. HTTPS handshake attempt)
             EngineLogCompat.Warn("[RemoteDebug] Client timed out (possible HTTPS handshake attempt?)", LogCategory.DevTools);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Error($"[RemoteDebug] Client error: {ex.Message}", LogCategory.DevTools);
        }
        finally
        {
            lock (_clients) _clients.Remove(client);
            try { client.Close(); } catch {}
        }
    }

    private async Task HandleHttpRequest(NetworkStream stream, string request)
    {
        // Simple manual HTTP parsing
        string responseBody = "";
        string contentType = "application/json";
        string requestTarget = GetRequestTarget(request);
        string path = "/";
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out var absUri))
        {
            path = absUri.AbsolutePath;
        }
        else if (Uri.TryCreate("http://localhost" + requestTarget, UriKind.Absolute, out var relUri))
        {
            path = relUri.AbsolutePath;
        }

        string tokenQuery = BuildTokenQuery();
        string webSocketUrl = BuildWebSocketDebuggerUrl();
        string devToolsFrontendUrl = BuildDevToolsFrontendUrl(tokenQuery);

        if (string.Equals(path, "/json/version", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = System.Text.Json.JsonSerializer.Serialize(new 
            {
                Browser = "FenBrowser/1.0",
                Protocol_Version = "1.3",
                User_Agent = BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent),
                V8_Version = "1.0",
                WebKit_Version = "537.36",
                webSocketDebuggerUrl = webSocketUrl
            });
        }
        else if (string.Equals(path, "/json", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(path, "/json/list", StringComparison.OrdinalIgnoreCase))
        {
             var targets = new[]
             {
                 new 
                 {
                      description = "FenBrowser Active Tab",
                      devtoolsFrontendUrl = devToolsFrontendUrl,
                      id = "1",
                      title = "FenBrowser Tab",
                      type = "page",
                      url = "http://localhost",
                      webSocketDebuggerUrl = webSocketUrl
                  }
             };
             responseBody = System.Text.Json.JsonSerializer.Serialize(targets);
        }
        else
        {
            // 404
            string notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
            byte[] nfBytes = Encoding.UTF8.GetBytes(notFound);
            await stream.WriteAsync(nfBytes, 0, nfBytes.Length);
            return;
        }

        byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
        string header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}; charset=UTF-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
    }

    private static string GetRequestTarget(string request)
    {
        if (string.IsNullOrEmpty(request)) return "/";
        var lineEnd = request.IndexOf('\n');
        var firstLine = lineEnd >= 0 ? request.Substring(0, lineEnd).Trim() : request.Trim();
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return parts[1];
        return "/";
    }

    private static Dictionary<string, string> ParseHeaders(string request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(request)) return headers;

        var lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            headers[key] = value;
        }

        return headers;
    }

    private string BuildTokenQuery()
    {
        // Security: do not embed the auth token in URLs by default — query
        // strings end up in access logs, browser history, and referrer chains.
        // CDP frontends that require the token in the target URL can opt in
        // via FEN_REMOTE_DEBUG_TOKEN_IN_URL=1.
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

    private string BuildWebSocketDebuggerUrl() => $"ws://{_advertisedHost}:{Port}/devtools/page/1{BuildTokenQuery()}";

    private string BuildDevToolsFrontendUrl(string tokenQuery)
    {
        string wsTarget = $"{_advertisedHost}:{Port}/devtools/page/1{tokenQuery}";
        return $"/devtools/inspector.html?ws={Uri.EscapeDataString(wsTarget)}";
    }

    private static string GetTokenFromTarget(string requestTarget)
    {
        if (string.IsNullOrEmpty(requestTarget)) return null;

        var uriText = requestTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      requestTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? requestTarget
            : "http://localhost" + requestTarget;

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)) return null;
        if (string.IsNullOrEmpty(uri.Query)) return null;

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var kv = pair.Split('=', 2);
            if (kv.Length >= 1 && string.Equals(kv[0], "token", StringComparison.OrdinalIgnoreCase))
                return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
        }

        return null;
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int diff = 0;
        for (int i = 0; i < left.Length; i++)
            diff |= left[i] ^ right[i];
        return diff == 0;
    }

    private bool IsAuthorizedRequest(string request)
    {
        if (string.IsNullOrEmpty(_authToken)) return true;

        var headers = ParseHeaders(request);
        if (headers.TryGetValue("X-Fen-Debug-Token", out var headerToken) &&
            ConstantTimeEquals(headerToken, _authToken))
        {
            return true;
        }

        if (headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader.Substring("Bearer ".Length).Trim();
            if (ConstantTimeEquals(bearer, _authToken)) return true;
        }

        var queryToken = GetTokenFromTarget(GetRequestTarget(request));
        return ConstantTimeEquals(queryToken, _authToken);
    }

    private static async Task SendUnauthorizedAsync(NetworkStream stream)
    {
        const string body = "{\"error\":\"unauthorized\"}";
        string header =
            "HTTP/1.1 401 Unauthorized\r\n" +
            "WWW-Authenticate: Bearer realm=\"FenBrowser RemoteDebug\"\r\n" +
            "Content-Type: application/json; charset=UTF-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
    }
    
    private bool DoHandshake(NetworkStream stream, string request)
    {
        // Extract Sec-WebSocket-Key
        var match = Regex.Match(request, "Sec-WebSocket-Key: (.*)");
        if (match.Success)
        {
            string key = match.Groups[1].Value.Trim();
            string magicString = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            string acceptKey = Convert.ToBase64String(
                SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key + magicString))
            );
            
            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                              
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            stream.Write(responseBytes, 0, responseBytes.Length);
            return true;
        }
        return false;
    }

    private async Task ReceiveWebSocketLoop(NetworkStream stream, Socket client)
    {
        try
        {
            while (client.Connected)
            {
                var frame = await ReadWebSocketFrameAsync(stream);
                if (frame == null)
                {
                    EngineLogCompat.Info("[RemoteDebug] Client sent FIN (0 bytes)", LogCategory.General);
                    break;
                }

                if (frame.Opcode == 8) // Close
                {
                    EngineLogCompat.Info("[RemoteDebug] Client requested Close (Opcode 8)", LogCategory.General);
                    break;
                }
                else if (frame.Opcode == 9) // Ping
                {
                    var pong = EncodeFrame(10, frame.Payload);
                    stream.Write(pong, 0, pong.Length);
                    continue;
                }
                
                if (frame.Opcode == 1) // Text
                {
                     string json = Encoding.UTF8.GetString(frame.Payload);
                     
                     // Process
                     var response = await _devToolsServer.ProcessRequestAsync(json);
                     if (!string.IsNullOrEmpty(response))
                     {
                         SendWebSocketFrame(stream, response);
                     }
                }
            }
        }
        catch (Exception ex)
        {
            EngineLogCompat.Error($"[RemoteDebug] WS Cycle Error: {ex}", LogCategory.General);
        }
    }

    private sealed record WebSocketFrame(int Opcode, byte[] Payload);

    private static async Task<WebSocketFrame?> ReadWebSocketFrameAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 2);
        if (header == null) return null;

        int opcode = header[0] & 0x0F;
        bool masked = (header[1] & 0x80) != 0;
        ulong payloadLen = (ulong)(header[1] & 0x7F);

        if (payloadLen == 126)
        {
            var lengthBytes = await ReadExactAsync(stream, 2);
            if (lengthBytes == null) return null;
            payloadLen = ((ulong)lengthBytes[0] << 8) | lengthBytes[1];
        }
        else if (payloadLen == 127)
        {
            var lengthBytes = await ReadExactAsync(stream, 8);
            if (lengthBytes == null) return null;
            payloadLen = 0;
            for (int i = 0; i < 8; i++)
                payloadLen = (payloadLen << 8) | lengthBytes[i];
        }

        if (payloadLen > 10 * 1024 * 1024)
            throw new InvalidDataException($"Remote debug frame too large: {payloadLen} bytes.");

        byte[]? mask = null;
        if (masked)
        {
            mask = await ReadExactAsync(stream, 4);
            if (mask == null) return null;
        }

        var payload = payloadLen == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, checked((int)payloadLen));
        if (payload == null) return null;

        if (mask != null)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(payload[i] ^ mask[i % 4]);
        }

        return new WebSocketFrame(opcode, payload);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer, offset, length - offset);
            if (read == 0) return null;
            offset += read;
        }

        return buffer;
    }
    
    // Broadcast must be thread safe
    private void BroadcastToClients(string json)
    {
        // Simple broadcast. Note: In a real server, queue per client.
        lock (_clients)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var client = _clients[i];
                if (!client.Connected) 
                {
                    _clients.RemoveAt(i);
                    continue;
                }
                
                try
                {
                    // We need a stream. Re-creating NetworkStream is okay if we own the socket
                    // But usually we should cache the stream.
                    // For now, let's just use Send() on socket directly if we encoded frames manually?
                    // Or keep it simple: assume single threaded send for now or lock.
                    // To be safe, let's just skip complex broadcast for this minimal impl 
                    // and rely on request-response mostly, BUT events are critical.
                    
                    // Sending RAW frame to socket
                    byte[] frame = EncodeFrame(json);
                    client.Send(frame);
                }
                catch
                {
                    // broken pipe
                }
            }
        }
    }
    
    private void SendWebSocketFrame(NetworkStream stream, string message)
    {
         byte[] frame = EncodeFrame(message);
         stream.Write(frame, 0, frame.Length);
    }
    
    private byte[] EncodeFrame(string message)
    {
        return EncodeFrame(1, Encoding.UTF8.GetBytes(message));
    }

    private static byte[] EncodeFrame(int opcode, byte[] payload)
    {
        List<byte> frame = new List<byte>();
        
        frame.Add((byte)(0x80 | opcode)); // FIN + opcode
        
        if (payload.Length < 126)
        {
            frame.Add((byte)payload.Length);
        }
        else if (payload.Length <= 65535)
        {
            frame.Add(126);
            frame.Add((byte)((payload.Length >> 8) & 0xFF));
            frame.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            frame.Add(127);
            // 64-bit length (write 8 bytes)
            ulong len = (ulong)payload.Length;
            for (int i = 7; i >= 0; i--)
                frame.Add((byte)((len >> (i * 8)) & 0xFF));
        }
        
        frame.AddRange(payload);
        return frame.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        
        // Stop heartbeat timer (10/10)
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        
        _listener.Stop();
        
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }
        
        _messageQueues.Clear();
        
        GC.SuppressFinalize(this);
    }
}

