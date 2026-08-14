using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        private const int ReceiveBufferBytes = 64 * 1024;
        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private const long MaxQueuedIncomingBytes = 32L * 1024L * 1024L;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cancelSource;
        private Task _receiveLoop;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _lock = new();
        private int _closeEventQueued;
        private long _queuedIncomingBytes;

        private readonly ConcurrentQueue<QueuedWsMessage> _incomingMessages = new();
        private readonly ConcurrentQueue<WsEvent> _incomingEvents = new();

        private readonly record struct QueuedWsMessage(WsMessage Message, int EncodedBytes);

        private enum QueueMessageResult
        {
            Queued,
            QueueLimitExceeded,
            InvalidUtf8
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
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss") ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return "Invalid WebSocket URL";
            }

            if (!ValidateProtocolList(protocols))
                return "Invalid WebSocket subprotocol";

            lock (_lock)
            {
                if (_socket != null && _socket.State != WebSocketState.Closed && _socket.State != WebSocketState.Aborted)
                    return "Already connected";

                try
                {
                    _cancelSource?.Cancel();
                    _cancelSource?.Dispose();
                    _socket?.Dispose();

                    _cancelSource = new CancellationTokenSource();
                    _socket = new ClientWebSocket();
                    Interlocked.Exchange(ref _closeEventQueued, 0);
                    Interlocked.Exchange(ref _queuedIncomingBytes, 0);
                    while (_incomingMessages.TryDequeue(out _)) { }
                    while (_incomingEvents.TryDequeue(out _)) { }

                    if (protocols != null)
                    {
                        foreach (var protocol in protocols)
                        {
                            // AddSubProtocol performs the RFC token validation. Any invalid
                            // token throws and the catch below tears the just-created socket down.
                            _socket.Options.AddSubProtocol(protocol);
                        }
                    }

                    Url = url;
                    Protocol = string.Empty;

                    var socket = _socket;
                    var token = _cancelSource.Token;
                    _ = ConnectAsync(socket, uri, token);
                    return null;
                }
                catch (Exception ex)
                {
                    _cancelSource?.Cancel();
                    _cancelSource?.Dispose();
                    _cancelSource = null;
                    _socket?.Dispose();
                    _socket = null;
                    _incomingEvents.Enqueue(WsEvent.Error(ex.Message));
                    return ex.Message;
                }
            }
        }

        private static bool ValidateProtocolList(string[] protocols)
        {
            if (protocols == null || protocols.Length == 0)
                return true;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var protocol in protocols)
            {
                if (string.IsNullOrWhiteSpace(protocol) || !seen.Add(protocol))
                    return false;
            }

            return true;
        }

        private async Task ConnectAsync(ClientWebSocket socket, Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

                lock (_lock)
                {
                    if (!ReferenceEquals(_socket, socket))
                    {
                        socket.Abort();
                        return;
                    }
                    Protocol = socket.SubProtocol ?? string.Empty;
                }

                _receiveLoop = ReceiveLoopAsync(socket, cancellationToken);
                _incomingEvents.Enqueue(WsEvent.Open());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _incomingEvents.Enqueue(WsEvent.Error(ex.Message));
                try { socket.Dispose(); } catch { }
                lock (_lock)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                }
            }
        }

        /// <summary>
        /// Send text data. Returns null on successful queueing, or an error string.
        /// </summary>
        public string Send(string data)
        {
            var s = _socket;
            var cts = _cancelSource;
            if (s == null || cts == null || s.State != WebSocketState.Open)
                return "InvalidStateError";

            try
            {
                var bytes = Encoding.UTF8.GetBytes(data ?? string.Empty);
                _ = SendAsync(s, bytes, WebSocketMessageType.Text, cts.Token);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Send binary data. Returns null on successful queueing, or an error string.
        /// </summary>
        public string SendBinary(byte[] data)
        {
            var s = _socket;
            var cts = _cancelSource;
            if (s == null || cts == null || s.State != WebSocketState.Open)
                return "InvalidStateError";

            try
            {
                var bytes = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
                _ = SendAsync(s, bytes, WebSocketMessageType.Binary, cts.Token);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async Task SendAsync(
            ClientWebSocket socket,
            byte[] data,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            try
            {
                await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (socket.State != WebSocketState.Open)
                        return;

                    await socket.SendAsync(
                        new ArraySegment<byte>(data),
                        messageType,
                        endOfMessage: true,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _incomingEvents.Enqueue(WsEvent.Error(ex.Message));
            }
        }

        /// <summary>
        /// Start a WebSocket close handshake.
        /// </summary>
        public void Close(int code = 1000, string reason = "")
        {
            ClientWebSocket socket;
            CancellationTokenSource cts;
            lock (_lock)
            {
                socket = _socket;
                cts = _cancelSource;
            }

            if (socket == null || cts == null ||
                socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
            {
                return;
            }

            reason ??= string.Empty;
            if (!IsValidCloseCode(code) || Encoding.UTF8.GetByteCount(reason) > 123)
            {
                _incomingEvents.Enqueue(WsEvent.Error("Invalid WebSocket close code or reason"));
                return;
            }

            _ = StartCloseHandshakeAsync(socket, (WebSocketCloseStatus)code, reason, cts);
        }

        private async Task StartCloseHandshakeAsync(
            ClientWebSocket socket,
            WebSocketCloseStatus closeStatus,
            string reason,
            CancellationTokenSource cts)
        {
            try
            {
                if (socket.State == WebSocketState.Connecting)
                {
                    socket.Abort();
                    QueueCloseOnce(1006, string.Empty);
                    return;
                }

                if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                    return;

                // Only send the close frame here. The dedicated receive loop remains the
                // sole receiver and waits for the peer close frame, avoiding concurrent
                // ReceiveAsync calls hidden inside ClientWebSocket.CloseAsync().
                await _sendGate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(closeStatus, reason, cts.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _incomingEvents.Enqueue(WsEvent.Error(ex.Message));
                try { socket.Abort(); } catch { }
                QueueCloseOnce(1006, string.Empty);
            }
        }

        /// <summary>
        /// Poll for the next incoming message. Returns null if no messages queued.
        /// </summary>
        public WsMessage PollMessage()
        {
            if (!_incomingMessages.TryDequeue(out var queued))
                return null;

            Interlocked.Add(ref _queuedIncomingBytes, -queued.EncodedBytes);
            return queued.Message;
        }

        /// <summary>
        /// Poll for the next event (open, error, close).
        /// </summary>
        public WsEvent PollEvent()
        {
            _incomingEvents.TryDequeue(out var evt);
            return evt;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancel)
        {
            var buffer = new byte[ReceiveBufferBytes];
            MemoryStream fragmentedMessage = null;
            WebSocketMessageType fragmentedType = WebSocketMessageType.Text;

            try
            {
                while (!cancel.IsCancellationRequested &&
                       ReferenceEquals(_socket, socket) &&
                       socket.State is WebSocketState.Open or WebSocketState.CloseSent)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        fragmentedMessage?.Dispose();
                        fragmentedMessage = null;

                        var status = result.CloseStatus ?? WebSocketCloseStatus.Empty;
                        var description = result.CloseStatusDescription ?? string.Empty;

                        if (socket.State == WebSocketState.CloseReceived)
                        {
                            try
                            {
                                await _sendGate.WaitAsync(cancel).ConfigureAwait(false);
                                try
                                {
                                    if (socket.State == WebSocketState.CloseReceived)
                                    {
                                        await socket.CloseOutputAsync(
                                            WebSocketCloseStatus.NormalClosure,
                                            string.Empty,
                                            cancel).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    _sendGate.Release();
                                }
                            }
                            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                            {
                            }
                            catch
                            {
                                try { socket.Abort(); } catch { }
                            }
                        }

                        QueueCloseOnce((int)status, description);
                        break;
                    }

                    if (fragmentedMessage == null && result.EndOfMessage)
                    {
                        if (result.Count > MaxMessageBytes)
                        {
                            await CloseForReceiveFailureAsync(
                                socket,
                                WebSocketCloseStatus.MessageTooBig,
                                "WebSocket message exceeds browser limit",
                                cancel).ConfigureAwait(false);
                            break;
                        }

                        var queueResult = QueueCompletedMessage(result.MessageType, buffer.AsSpan(0, result.Count));
                        if (queueResult != QueueMessageResult.Queued)
                        {
                            await CloseForQueueFailureAsync(socket, queueResult, cancel).ConfigureAwait(false);
                            break;
                        }
                        continue;
                    }

                    if (fragmentedMessage == null)
                    {
                        fragmentedType = result.MessageType;
                        fragmentedMessage = new MemoryStream(
                            Math.Min(MaxMessageBytes, Math.Max(ReceiveBufferBytes, result.Count * 2)));
                    }
                    else if (result.MessageType != fragmentedType)
                    {
                        throw new WebSocketException("WebSocket message type changed mid-message");
                    }

                    if (fragmentedMessage.Length + result.Count > MaxMessageBytes)
                    {
                        fragmentedMessage.Dispose();
                        fragmentedMessage = null;
                        await CloseForReceiveFailureAsync(
                            socket,
                            WebSocketCloseStatus.MessageTooBig,
                            "WebSocket message exceeds browser limit",
                            cancel).ConfigureAwait(false);
                        break;
                    }

                    fragmentedMessage.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    var completed = fragmentedMessage.ToArray();
                    fragmentedMessage.Dispose();
                    fragmentedMessage = null;

                    var completedResult = QueueCompletedMessage(fragmentedType, completed);
                    if (completedResult != QueueMessageResult.Queued)
                    {
                        await CloseForQueueFailureAsync(socket, completedResult, cancel).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
                _incomingEvents.Enqueue(WsEvent.Error("WebSocket connection error"));
            }
            catch (Exception)
            {
                _incomingEvents.Enqueue(WsEvent.Error("WebSocket receive error"));
            }
            finally
            {
                fragmentedMessage?.Dispose();
            }
        }

        private QueueMessageResult QueueCompletedMessage(WebSocketMessageType messageType, ReadOnlySpan<byte> data)
        {
            int encodedBytes = data.Length;
            long queuedBytes = Interlocked.Add(ref _queuedIncomingBytes, encodedBytes);
            if (queuedBytes > MaxQueuedIncomingBytes)
            {
                Interlocked.Add(ref _queuedIncomingBytes, -encodedBytes);
                return QueueMessageResult.QueueLimitExceeded;
            }

            byte[] bytes = data.ToArray();
            WsMessage message;
            if (messageType == WebSocketMessageType.Text)
            {
                try
                {
                    // WebSocket text messages require valid UTF-8. Decode only after the
                    // complete message is assembled so code points may span frames safely.
                    message = WsMessage.Text(StrictUtf8.GetString(bytes));
                }
                catch (DecoderFallbackException)
                {
                    Interlocked.Add(ref _queuedIncomingBytes, -encodedBytes);
                    return QueueMessageResult.InvalidUtf8;
                }
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                message = WsMessage.Binary(bytes);
            }
            else
            {
                Interlocked.Add(ref _queuedIncomingBytes, -encodedBytes);
                return QueueMessageResult.Queued;
            }

            _incomingMessages.Enqueue(new QueuedWsMessage(message, encodedBytes));
            return QueueMessageResult.Queued;
        }

        private Task CloseForQueueFailureAsync(
            ClientWebSocket socket,
            QueueMessageResult result,
            CancellationToken cancel)
        {
            return result switch
            {
                QueueMessageResult.InvalidUtf8 => CloseForReceiveFailureAsync(
                    socket,
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Invalid UTF-8 WebSocket text message",
                    cancel),
                _ => CloseForReceiveFailureAsync(
                    socket,
                    WebSocketCloseStatus.MessageTooBig,
                    "WebSocket receive queue exceeds browser limit",
                    cancel)
            };
        }

        private async Task CloseForReceiveFailureAsync(
            ClientWebSocket socket,
            WebSocketCloseStatus status,
            string reason,
            CancellationToken cancel)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _sendGate.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(status, reason, cancel).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _sendGate.Release();
                    }
                }
            }
            catch
            {
                try { socket.Abort(); } catch { }
            }

            QueueCloseOnce((int)status, reason);
        }

        private void QueueCloseOnce(int code, string reason)
        {
            if (Interlocked.Exchange(ref _closeEventQueued, 1) == 0)
            {
                _incomingEvents.Enqueue(WsEvent.Close(code, reason ?? string.Empty));
            }
        }

        private static bool IsValidCloseCode(int code) =>
            code == 1000 || (code >= 3000 && code <= 4999);

        public void Dispose()
        {
            ClientWebSocket socket;
            CancellationTokenSource cts;

            lock (_lock)
            {
                socket = _socket;
                cts = _cancelSource;
                _socket = null;
                _cancelSource = null;
            }

            try { cts?.Cancel(); } catch { }
            try { socket?.Abort(); } catch { }
            try { socket?.Dispose(); } catch { }
            cts?.Dispose();
            _sendGate.Dispose();

            while (_incomingMessages.TryDequeue(out _)) { }
            while (_incomingEvents.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedIncomingBytes, 0);
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
        public string Type { get; private set; }
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
