using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.DevTools.Core;

public class RemoteDebugServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly DevToolsServer _devToolsServer;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _clients = new();
    private const int BufferSize = 8192;

    public RemoteDebugServer(DevToolsServer devToolsServer, int port = 9222)
    {
        _devToolsServer = devToolsServer;
        // Bind to IPv6Any and enable DualMode to support both IPv4 and IPv6
        _listener = new TcpListener(IPAddress.IPv6Any, port); 
        _listener.Server.DualMode = true;
        
        // Broadcast events to all connected clients
        _devToolsServer.OnJsonOutput(BroadcastToClients);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            FenLogger.Info($"[RemoteDebug] TCP Server started on port {endpoint.Port}", LogCategory.General);
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[RemoteDebug] Failed to start TCP listener: {ex.Message}", LogCategory.General);
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

        if (request.Contains("GET /json/version"))
        {
            responseBody = System.Text.Json.JsonSerializer.Serialize(new 
            {
                Browser = "FenBrowser/1.0",
                Protocol_Version = "1.3",
                User_Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) FenBrowser/1.0 Chrome/120.0.0.0 Safari/537.36",
                V8_Version = "1.0",
                WebKit_Version = "537.36",
                webSocketDebuggerUrl = "ws://localhost:9222/devtools/browser"
            });
        }
        else if (request.Contains("GET /json") || request.Contains("GET /json/list"))
        {
             var targets = new[]
             {
                 new 
                 {
                     description = "FenBrowser Active Tab",
                     devtoolsFrontendUrl = "/devtools/inspector.html?ws=localhost:9222/devtools/page/1",
                     id = "1",
                     title = "FenBrowser Tab",
                     type = "page",
                     url = "http://localhost",
                     webSocketDebuggerUrl = "ws://localhost:9222/devtools/page/1"
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
                     // Simple parser limitation: We don't support 64-bit length yet
                     FenLogger.Warn("[RemoteDebug] 64-bit frame length not supported", LogCategory.General);
                     offset = 10;
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
        _listener.Stop();
        lock (_clients)
        {
            foreach(var c in _clients) c.Close();
            _clients.Clear();
        }
    }
}
