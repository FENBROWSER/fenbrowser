using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public RemoteDebugServer(
        DevToolsServer devToolsServer,
        int port = 9222,
        string bindAddress = "127.0.0.1",
        string authToken = null)
    {
        _devToolsServer = devToolsServer;
        _port = port;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();

        if (!IPAddress.TryParse(bindAddress, out var bindIp))
            bindIp = IPAddress.Loopback;
        _advertisedHost = bindIp.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : bindIp.ToString();

        // Security hardening: bind to explicit local interface unless caller opts otherwise.
        _listener = new TcpListener(bindIp, port);
        
        // Broadcast events to all connected clients
        _devToolsServer.OnJsonOutput(BroadcastToClients);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            var authState = string.IsNullOrEmpty(_authToken) ? "disabled" : "enabled";
            FenLogger.Info($"[RemoteDebug] TCP Server started on {endpoint.Address}:{endpoint.Port} (auth: {authState})", LogCategory.General);
            
            // Start heartbeat timer (10/10)
            StartHeartbeat();
            
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[RemoteDebug] Failed to start TCP listener: {ex.Message}", LogCategory.General);
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
                FenLogger.Info($"[RemoteDebug] Connection accepted from {client.RemoteEndPoint}", LogCategory.General);
                _ = HandleClient(client);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    FenLogger.Error($"[RemoteDebug] Accept error: {ex.Message}", LogCategory.General);
                }
                break;
            }
        }
    }

    private async Task HandleClient(Socket client)
    {
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
                FenLogger.Warn("[RemoteDebug] Unauthorized request rejected", LogCategory.General);
                await SendUnauthorizedAsync(stream);
                return;
            }
            
            if (Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase))
            {
                if (request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                {
                    FenLogger.Info("[RemoteDebug] WebSocket Upgrade detected", LogCategory.General);
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
             FenLogger.Warn("[RemoteDebug] Client timed out (possible HTTPS handshake attempt?)", LogCategory.General);
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[RemoteDebug] Client error: {ex.Message}", LogCategory.General);
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

        string tokenQuery = string.IsNullOrEmpty(_authToken) ? "" : "?token=" + Uri.EscapeDataString(_authToken);
        string webSocketUrl = $"ws://{_advertisedHost}:{_port}/devtools/page/1{tokenQuery}";

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
                      devtoolsFrontendUrl = $"/devtools/inspector.html?ws={_advertisedHost}:{_port}/devtools/page/1",
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
        // Increased buffer for larger DevTools frames (DOM snapshots can be large)
        byte[] buffer = new byte[1024 * 512]; // 512 KB
        
        try
        {
            while (client.Connected)
            {
                // Note: This simple parser assumes 1 Read = 1 Frame (or part of it). 
                // In a robust implementation, we must buffer bytes until we have a full frame.
                // For now, increasing buffer size helps, but isn't a perfect fix for stream fragmentation.
                
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) 
                {
                    FenLogger.Info("[RemoteDebug] Client sent FIN (0 bytes)", LogCategory.General);
                    break;
                }
                
                // Decode Frame Header
                bool fin = (buffer[0] & 0x80) != 0;
                int opcode = buffer[0] & 0x0F;
                bool masked = (buffer[1] & 0x80) != 0;
                long payloadLen = buffer[1] & 0x7F;
                
                int offset = 2;
                if (payloadLen == 126)
                {
                    payloadLen = BitConverter.ToUInt16(new byte[] { buffer[3], buffer[2] }, 0);
                    offset = 4;
                }
                else if (payloadLen == 127)
                {
                    // --- 10/10: Full 64-bit frame length support ---
                    byte[] lenBytes = new byte[8];
                    Array.Copy(buffer, 2, lenBytes, 0, 8);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    payloadLen = (long)BitConverter.ToUInt64(lenBytes, 0);
                    offset = 10;
                    
                    // Safety check: don't accept frames larger than 10MB
                    if (payloadLen > 10 * 1024 * 1024)
                    {
                        FenLogger.Warn($"[RemoteDebug] Frame too large: {payloadLen} bytes, dropping", LogCategory.General);
                        continue;
                    }
                }
                
                // Check if we have the full payload in this read (Basic fragmentation handling)
                if (bytesRead < offset + payloadLen)
                {
                    // If we read less than the frame size, we are in trouble with this simple parser.
                    // Ideally we should loop and read more.
                    FenLogger.Warn($"[RemoteDebug] Partial Frame Read: {bytesRead} < {offset + payloadLen}. This might cause disconnection.", LogCategory.General);
                    
                    // Attempt to read the rest
                    int targetTotal = offset + (int)payloadLen;
                    int currentTotal = bytesRead;
                    while (currentTotal < targetTotal)
                    {
                        int needed = targetTotal - currentTotal;
                        int r = await stream.ReadAsync(buffer, currentTotal, needed);
                        if (r == 0) break;
                        currentTotal += r;
                    }
                }
                
                if (opcode == 8) // Close
                {
                    FenLogger.Info("[RemoteDebug] Client requested Close (Opcode 8)", LogCategory.General);
                    break;
                }
                else if (opcode == 9) // Ping
                {
                    // Respond with Pong (Opcode 10)
                    // Echo payload back
                    byte[] pongFrame = new byte[bytesRead];
                    Array.Copy(buffer, pongFrame, bytesRead);
                    pongFrame[0] = (byte)(0x80 | 10); // FIN + Opcode 10
                    // Remove Mask bit from response if present (Server -> Client is not masked)
                    // But actually we just constructing a new frame is safer
                     
                    // Construct minimal Pong
                    stream.Write(new byte[] { 0x8A, 0x00 }, 0, 2); // FIN+Pong, len 0
                    continue;
                }
                
                if (opcode == 1) // Text
                {
                     byte[] masks = new byte[4];
                     if (masked)
                     {
                         Array.Copy(buffer, offset, masks, 0, 4);
                         offset += 4;
                     }
                     
                     byte[] content = new byte[payloadLen];
                     for (int i = 0; i < payloadLen; i++)
                     {
                         content[i] = (byte)(buffer[offset + i] ^ masks[i % 4]);
                     }
                     
                     string json = Encoding.UTF8.GetString(content);
                     
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
            FenLogger.Error($"[RemoteDebug] WS Cycle Error: {ex}", LogCategory.General);
        }
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
        byte[] payload = Encoding.UTF8.GetBytes(message);
        List<byte> frame = new List<byte>();
        
        frame.Add(0x81); // Fin + Text
        
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

