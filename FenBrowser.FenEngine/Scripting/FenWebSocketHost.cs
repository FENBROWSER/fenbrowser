using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Bridges a System.Net.WebSockets.ClientWebSocket to the JS WebSocket API.
    /// https://websockets.spec.whatwg.org/
    /// </summary>
    public sealed class FenWebSocketHost : IDisposable
    {
        private ClientWebSocket _socket;
        private CancellationTokenSource _cancelSource;
        private Task _receiveLoop;
        private readonly object _lock = new();

        // Queued messages from the receive loop, consumed by the JS event polling.
        private readonly ConcurrentQueue<WsMessage> _incomingMessages = new();
        private readonly ConcurrentQueue<WsEvent> _incomingEvents = new();

        public FenWebSocketHost()
        {
        }

        public WebSocketState ReadyState
        {
            get
            {
                var s = _socket;
                return s?.State ?? WebSocketState.None;
            }
        }

        public string Url { get; private set; } = string.Empty;
        public string Protocol { get; private set; } = string.Empty;

        /// <summary>
        /// Connect to a WebSocket server. Returns null on success, or an error string.
        /// </summary>
        public string Connect(string url, string[] protocols)
        {
            lock (_lock)
            {
                if (_socket != null && _socket.State != WebSocketState.Closed && _socket.State != WebSocketState.Aborted)
                    return "Already connected";

                try
                {
                    _cancelSource = new CancellationTokenSource();
                    _socket = new ClientWebSocket();

                    if (protocols != null && protocols.Length > 0)
                    {
                        foreach (var p in protocols)
                        {
                            if (!string.IsNullOrWhiteSpace(p))
                                _socket.Options.AddSubProtocol(p);
                        }
                    }

                    Url = url;
                    Protocol = string.Empty;

                    // Connect synchronously (the JS side is synchronous for the constructor).
                    var connectTask = _socket.ConnectAsync(new Uri(url), _cancelSource.Token);
                    connectTask.GetAwaiter().GetResult();

                    Protocol = _socket.SubProtocol ?? string.Empty;

                    // Start the receive loop.
                    _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cancelSource.Token));

                    _incomingEvents.Enqueue(WsEvent.Open());
                    return null;
                }
                catch (Exception ex)
                {
                    _socket?.Dispose();
                    _socket = null;
                    _incomingEvents.Enqueue(WsEvent.Error(ex.Message));
                    return ex.Message;
                }
            }
        }

        /// <summary>
        /// Send text or binary data. Returns null on success, or an error string.
        /// </summary>
        public string Send(string data)
        {
            var s = _socket;
            if (s == null || s.State != WebSocketState.Open)
                return "InvalidStateError";

            try
            {
                var bytes = Encoding.UTF8.GetBytes(data ?? string.Empty);
                var sendTask = s.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    _cancelSource.Token);
                sendTask.GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Send binary data. Returns null on success, or an error string.
        /// </summary>
        public string SendBinary(byte[] data)
        {
            var s = _socket;
            if (s == null || s.State != WebSocketState.Open)
                return "InvalidStateError";

            try
            {
                var sendTask = s.SendAsync(
                    new ArraySegment<byte>(data ?? Array.Empty<byte>()),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    _cancelSource.Token);
                sendTask.GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Close the WebSocket connection.
        /// </summary>
        public void Close(int code = 1000, string reason = "")
        {
            lock (_lock)
            {
                var s = _socket;
                if (s == null || s.State == WebSocketState.Closed || s.State == WebSocketState.Aborted)
                    return;

                try
                {
                    if (s.State == WebSocketState.Open)
                    {
                        var closeTask = s.CloseAsync(
                            (WebSocketCloseStatus)(code > 0 ? code : 1000),
                            reason ?? string.Empty,
                            CancellationToken.None);
                        closeTask.GetAwaiter().GetResult();
                    }

                    _cancelSource?.Cancel();
                }
                catch
                {
                    // Best effort close.
                }
                finally
                {
                    _incomingEvents.Enqueue(WsEvent.Close(code, reason ?? string.Empty));
                }
            }
        }

        /// <summary>
        /// Poll for the next incoming message. Returns null if no messages queued.
        /// </summary>
        public WsMessage PollMessage()
        {
            _incomingMessages.TryDequeue(out var msg);
            return msg;
        }

        /// <summary>
        /// Poll for the next event (open, error, close).
        /// </summary>
        public WsEvent PollEvent()
        {
            _incomingEvents.TryDequeue(out var evt);
            return evt;
        }

        private async Task ReceiveLoopAsync(CancellationToken cancel)
        {
            var buffer = new byte[65536];
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var s = _socket;
                    if (s == null || s.State != WebSocketState.Open)
                        break;

                    var result = await s.ReceiveAsync(new ArraySegment<byte>(buffer), cancel).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _incomingEvents.Enqueue(WsEvent.Close(
                            (int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure),
                            result.CloseStatusDescription ?? string.Empty));
                        break;
                    }

                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(data);
                        _incomingMessages.Enqueue(WsMessage.Text(text));
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        _incomingMessages.Enqueue(WsMessage.Binary(data));
                    }

                    if (result.EndOfMessage)
                    {
                        // Message complete — ready for next.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (WebSocketException)
            {
                _incomingEvents.Enqueue(WsEvent.Error("WebSocket connection error"));
            }
            catch (Exception)
            {
                _incomingEvents.Enqueue(WsEvent.Error("WebSocket receive error"));
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cancelSource?.Cancel();
                _cancelSource?.Dispose();
                _cancelSource = null;

                try
                {
                    _socket?.Dispose();
                }
                catch { }

                _socket = null;
            }
        }
    }

    /// <summary>
    /// Represents an incoming WebSocket message (text or binary).
    /// </summary>
    public sealed class WsMessage
    {
        public bool IsText { get; private set; }
        public string TextData { get; private set; }
        public byte[] BinaryData { get; private set; }

        public static WsMessage Text(string data) => new WsMessage { IsText = true, TextData = data ?? string.Empty };
        public static WsMessage Binary(byte[] data) => new WsMessage { IsText = false, BinaryData = data ?? Array.Empty<byte>() };
    }

    /// <summary>
    /// Represents a WebSocket lifecycle event (open, error, close).
    /// </summary>
    public sealed class WsEvent
    {
        public string Type { get; private set; }  // "open", "error", "close"
        public int Code { get; private set; }
        public string Reason { get; private set; }
        public string ErrorMessage { get; private set; }

        public bool IsOpen => Type == "open";
        public bool IsError => Type == "error";
        public bool IsClose => Type == "close";

        public static WsEvent Open() => new WsEvent { Type = "open" };
        public static WsEvent Error(string msg) => new WsEvent { Type = "error", ErrorMessage = msg ?? string.Empty };
        public static WsEvent Close(int code, string reason) => new WsEvent { Type = "close", Code = code, Reason = reason ?? string.Empty };
    }
}
