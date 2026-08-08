// SpecRef: FenBrowser process startup and authenticated IPC contract
// CapabilityId: PROCESS-IPC-HANDSHAKE-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    internal enum RendererIpcMessageType
    {
        Hello,
        Ready,
        LogBatch,
        Navigate,
        Input,
        FrameRequest,
        FrameReady,
        MetadataChanged,
        TabActivated,
        TabClosed,
        Shutdown,
        Ack,
        Error,
        Ping,
        Pong
    }

    public sealed class RendererIpcEnvelope
    {
        public string Type { get; set; }
        public int TabId { get; set; }
        public string CorrelationId { get; set; }
        public string Token { get; set; }
        public string Payload { get; set; }
        public long TimestampUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public sealed class RendererNavigatePayload
    {
        public string Url { get; set; }
        public bool IsUserInput { get; set; }
        public float ViewportWidth { get; set; }
        public float ViewportHeight { get; set; }
    }

    public sealed class RendererFrameRequestPayload
    {
        public float ViewportWidth { get; set; }
        public float ViewportHeight { get; set; }
        /// <summary>
        /// Outer document scroll offset (top of viewport in document coordinates).
        /// The renderer translates the paint canvas by -ScrollY before rasterising.
        /// </summary>
        public float ScrollY { get; set; }
    }

    public sealed class RendererFrameReadyPayload
    {
        public string Url { get; set; }
        public long FrameTimestampUnixMs { get; set; }
        public float SurfaceWidth { get; set; }
        public float SurfaceHeight { get; set; }
        public int DirtyRegionCount { get; set; }
        public bool HasDamage { get; set; }
        /// <summary>
        /// Monotonically increasing frame counter. Allows the host to detect stale frames
        /// and discard out-of-order deliveries.
        /// </summary>
        public uint FrameSequenceNumber { get; set; }
        /// <summary>
        /// Raw BGRA pixel bytes copied out of shared memory by the host-side reader.
        /// Null when transmitted over IPC (pixels travel via shared memory, not the pipe).
        /// Set by <see cref="RendererChildSession"/> after reading from <see cref="FrameSharedMemory"/>.
        /// </summary>
        public byte[] PixelData { get; set; }
        public string RequestedBy { get; set; }
        public string InvalidationReason { get; set; }
        public string RasterMode { get; set; }
        public bool UsedDamageRasterization { get; set; }
        public float DamageAreaRatio { get; set; }
        public bool LayoutUpdated { get; set; }
        public bool PaintTreeRebuilt { get; set; }
        public bool WatchdogTriggered { get; set; }
        public string WatchdogReason { get; set; }
        public double TotalDurationMs { get; set; }
        public int DomNodeCount { get; set; }
        public int BoxCount { get; set; }
        public int PaintNodeCount { get; set; }
        /// <summary>
        /// Outer document scroll offset used as the top of this rasterized frame.
        /// The surface can be taller than the visible viewport to provide scroll overdraw.
        /// </summary>
        public float ScrollY { get; set; }
        /// <summary>
        /// Total document content height in CSS pixels (from LayoutResult). Allows the host
        /// to size the viewport scrollbar without re-running layout.
        /// </summary>
        public float ContentHeight { get; set; }
    }

    public sealed class RendererMetadataChangedPayload
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public bool FaviconChanged { get; set; }
        public byte[] FaviconPngBytes { get; set; }
    }

    internal static class RendererIpc
    {
        private const int MaxEnvelopeChars = 256 * 1024;
        private const int MaxPayloadChars = 192 * 1024;
        private const int MaxTypeChars = 32;
        private const int MaxCorrelationIdChars = 64;
        private const int MaxTokenChars = 512;
        private const long MaxClockSkewMs = 24L * 60L * 60L * 1000L;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static string SerializeEnvelope(RendererIpcEnvelope envelope)
        {
            return JsonSerializer.Serialize(envelope, JsonOptions);
        }

        public static bool TryDeserializeEnvelope(string line, out RendererIpcEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (line.Length > MaxEnvelopeChars)
            {
                return false;
            }

            try
            {
                envelope = JsonSerializer.Deserialize<RendererIpcEnvelope>(line, JsonOptions);
                return envelope != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryValidateInboundEnvelope(
            RendererIpcEnvelope envelope,
            int expectedTabId,
            out RendererIpcMessageType messageType,
            out string rejectionReason)
        {
            messageType = default;
            rejectionReason = string.Empty;

            if (envelope == null)
            {
                rejectionReason = "envelope-null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(envelope.Type) ||
                envelope.Type.Length > MaxTypeChars ||
                !Enum.TryParse<RendererIpcMessageType>(envelope.Type, ignoreCase: true, out messageType))
            {
                rejectionReason = "type-invalid";
                return false;
            }

            if (envelope.TabId != expectedTabId)
            {
                rejectionReason = "tab-mismatch";
                return false;
            }

            if (string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
                envelope.CorrelationId.Length > MaxCorrelationIdChars ||
                !Guid.TryParse(envelope.CorrelationId, out _))
            {
                rejectionReason = "correlation-invalid";
                return false;
            }

            if (!string.IsNullOrEmpty(envelope.Token) && envelope.Token.Length > MaxTokenChars)
            {
                rejectionReason = "token-too-large";
                return false;
            }

            if (!string.IsNullOrEmpty(envelope.Payload) && envelope.Payload.Length > MaxPayloadChars)
            {
                rejectionReason = "payload-too-large";
                return false;
            }

            if (envelope.TimestampUnixMs <= 0)
            {
                rejectionReason = "timestamp-missing";
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var delta = Math.Abs(now - envelope.TimestampUnixMs);
            if (delta > MaxClockSkewMs)
            {
                rejectionReason = "timestamp-out-of-range";
                return false;
            }

            return true;
        }

        public static bool IsAllowedBrokerInboundMessageType(RendererIpcMessageType messageType)
        {
            return messageType == RendererIpcMessageType.Ready ||
                   messageType == RendererIpcMessageType.FrameReady ||
                   messageType == RendererIpcMessageType.MetadataChanged ||
                   messageType == RendererIpcMessageType.Error ||
                   messageType == RendererIpcMessageType.LogBatch ||
                   messageType == RendererIpcMessageType.Pong;
        }

        public static string SerializePayload<T>(T payload)
        {
            return payload == null ? string.Empty : JsonSerializer.Serialize(payload, JsonOptions);
        }

        public static T DeserializePayload<T>(RendererIpcEnvelope envelope)
            where T : class
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(envelope.Payload, JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class RendererChildSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly object _writeLock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<RendererIpcEnvelope> _pendingOutbound = new();
        private StreamReader _reader;
        private StreamWriter _writer;
        private Task _readLoop;
        private DateTime _lastFrameRequestUtc = DateTime.MinValue;
        private const int MaxPendingOutboundMessages = 128;
        private const int OutboundQueueCapacity = 512;
        private readonly Channel<RendererIpcEnvelope> _outbound;
        private readonly Task _outboundLoop;
        private int _outboundFaulted;
        private FrameSharedMemory _frameSharedMemory;
        private readonly int _parentPid = Environment.ProcessId;

        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, RendererMetadataChangedPayload> MetadataChanged;

        public int TabId { get; }
        public string PipeName { get; }
        public string AuthToken { get; }
        public bool IsConnected => _pipe.IsConnected && _writer != null;
        public System.Diagnostics.Process ChildProcess { get; private set; }

        public RendererChildSession(int tabId, string pipeName, string authToken)
        {
            TabId = tabId;
            PipeName = pipeName;
            AuthToken = authToken;
            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            // All outbound IPC is serialized through a single writer task.  A
            // synchronous pipe write on the caller's thread (pointer-move
            // dispatch and navigation both run on the UI thread) would block
            // indefinitely once the renderer child stops draining the pipe —
            // e.g. during a long raster pass or after a crash — hanging the
            // whole window ("Not Responding").
            _outbound = Channel.CreateBounded<RendererIpcEnvelope>(new BoundedChannelOptions(OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _outboundLoop = Task.Run(OutboundLoopAsync);
        }

        public void AttachProcess(System.Diagnostics.Process childProcess)
        {
            ChildProcess = childProcess;
            if (childProcess != null)
            {
                childProcess.EnableRaisingEvents = true;
                childProcess.Exited += (_, __) => _readyTcs.TrySetResult(false);
            }
            _ = Task.Run(WaitForConnectionAsync);
        }

        public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_readyTcs.Task.IsCompleted)
            {
                return await _readyTcs.Task.ConfigureAwait(false);
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var delayTask = Task.Delay(timeout, linkedCts.Token);
            var completed = await Task.WhenAny(_readyTcs.Task, delayTask).ConfigureAwait(false);
            if (completed == _readyTcs.Task)
            {
                linkedCts.Cancel();
                return await _readyTcs.Task.ConfigureAwait(false);
            }

            return false;
        }

        public void SendNavigate(string url, bool isUserInput, float viewportWidth = 0f, float viewportHeight = 0f)
        {
            var payload = new RendererNavigatePayload
            {
                Url = url ?? string.Empty,
                IsUserInput = isUserInput,
                ViewportWidth = viewportWidth,
                ViewportHeight = viewportHeight
            };

            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.Navigate.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = RendererIpc.SerializePayload(payload)
            });
        }

        public void SendInput(RendererInputEvent inputEvent)
        {
            if (inputEvent == null || !inputEvent.IsMeaningful)
            {
                return;
            }

            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.Input.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = RendererIpc.SerializePayload(inputEvent)
            });
        }

        private float _lastFrameRequestScrollY;
        private float _lastFrameRequestViewportWidth;
        private float _lastFrameRequestViewportHeight;

        public void SendFrameRequest(float viewportWidth, float viewportHeight, float scrollY = 0f)
        {
            var now = DateTime.UtcNow;
            bool scrollChanged = Math.Abs(scrollY - _lastFrameRequestScrollY) > 0.5f;
            // Rate limit only when neither viewport nor scroll changed.
            bool viewportChanged = Math.Abs(viewportWidth - _lastFrameRequestViewportWidth) > 0.5f ||
                                   Math.Abs(viewportHeight - _lastFrameRequestViewportHeight) > 0.5f;
            if (!scrollChanged && !viewportChanged && (now - _lastFrameRequestUtc).TotalMilliseconds < 33)
            {
                return;
            }
            _lastFrameRequestUtc = now;
            _lastFrameRequestScrollY = scrollY;
            _lastFrameRequestViewportWidth = viewportWidth;
            _lastFrameRequestViewportHeight = viewportHeight;

            var payload = new RendererFrameRequestPayload
            {
                ViewportWidth = viewportWidth,
                ViewportHeight = viewportHeight,
                ScrollY = scrollY
            };

            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.FrameRequest.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = RendererIpc.SerializePayload(payload)
            });
        }

        public void SendTabActivated()
        {
            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.TabActivated.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N")
            });
        }

        public void SendTabClosed()
        {
            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.TabClosed.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N")
            });
        }

        public void SendShutdown()
        {
            Send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.Shutdown.ToString(),
                TabId = TabId,
                CorrelationId = Guid.NewGuid().ToString("N")
            });
        }

        private async Task WaitForConnectionAsync()
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                lock (_writeLock)
                {
                    _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                }

                Send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.Hello.ToString(),
                    TabId = TabId,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Token = AuthToken
                });
                FlushPendingOutbound();

                _readLoop = Task.Run(ReadLoopAsync);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[ProcessIsolation] IPC connected for tab {TabId} via pipe '{PipeName}'.");
            }
            catch (OperationCanceledException)
            {
                _readyTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] IPC connect failed for tab {TabId}: {ex.Message}");
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    if (!RendererIpc.TryDeserializeEnvelope(line, out var envelope))
                    {
                        continue;
                    }

                    if (!RendererIpc.TryValidateInboundEnvelope(envelope, TabId, out var messageType, out var rejectionReason))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Rejected renderer IPC envelope tab={TabId}: {rejectionReason}.");
                        continue;
                    }

                    if (!RendererIpc.IsAllowedBrokerInboundMessageType(messageType))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Rejected unexpected renderer message type for tab {TabId}: {messageType}.");
                        continue;
                    }

                    if (messageType == RendererIpcMessageType.Ready)
                    {
                        _readyTcs.TrySetResult(true);
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[ProcessIsolation] Renderer child ready for tab {TabId}.");
                    }
                    else if (messageType == RendererIpcMessageType.FrameReady)
                    {
                        var payload = RendererIpc.DeserializePayload<RendererFrameReadyPayload>(envelope);
                        var url = payload?.Url ?? "<unknown>";
                        var width = payload?.SurfaceWidth ?? 0f;
                        var height = payload?.SurfaceHeight ?? 0f;
                        var dirtyCount = payload?.DirtyRegionCount ?? 0;
                        EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[ProcessIsolation] FrameReady tab={TabId} url={url} surface={width}x{height} dirtyRegions={dirtyCount}");

                        if (payload != null)
                        {
                            // Lazily open the shared memory region on first FrameReady.
                            if (_frameSharedMemory == null)
                            {
                                try
                                {
                                    _frameSharedMemory = FrameSharedMemory.OpenForReader(TabId, _parentPid);
                                }
                                catch (Exception ex)
                                {
                                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Failed to open FrameSharedMemory for tab {TabId}: {ex.Message}");
                                }
                            }

                            if (_frameSharedMemory != null)
                            {
                                try
                                {
                                    var frameData = _frameSharedMemory.TryReadFrame();
                                    if (frameData.HasValue)
                                    {
                                        payload.PixelData = frameData.Value.pixels;
                                        payload.FrameSequenceNumber = frameData.Value.seq;
                                        // Use dimensions from shared memory header (authoritative).
                                        payload.SurfaceWidth = frameData.Value.width;
                                        payload.SurfaceHeight = frameData.Value.height;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] FrameSharedMemory read failed for tab {TabId}: {ex.Message}");
                                }
                            }

                            FrameReceived?.Invoke(TabId, payload);
                        }
                    }
                    else if (messageType == RendererIpcMessageType.MetadataChanged)
                    {
                        var payload = RendererIpc.DeserializePayload<RendererMetadataChangedPayload>(envelope);
                        if (payload != null)
                        {
                            MetadataChanged?.Invoke(TabId, payload);
                        }
                    }
                    else if (messageType == RendererIpcMessageType.Error)
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Renderer child error tab={TabId}: {envelope.Payload}");
                    }
                    else if (messageType == RendererIpcMessageType.LogBatch)
                    {
                        var batch = RendererIpc.DeserializePayload<EngineLogBatchPayload>(envelope);
                        ProcessIsolationLogCollector.PublishBatch(batch);
                    }
                }
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                if (!_cts.IsCancellationRequested)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] IPC read loop terminated for tab {TabId}: {ex.Message}");
                }
            }
        }

        private void Send(RendererIpcEnvelope envelope)
        {
            if (envelope == null)
            {
                return;
            }

            // The pipe write happens on the dedicated outbound writer task;
            // the caller (often the UI thread) never blocks on IPC I/O.
            if (Volatile.Read(ref _outboundFaulted) != 0)
            {
                // Pipe is known broken (write faulted): drop instead of queueing
                // into a dead session. The process-exit watcher handles teardown.
                return;
            }

            if (!_outbound.Writer.TryWrite(envelope))
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Warn,
                    $"[ProcessIsolation] Outbound IPC queue full for tab {TabId}; dropping message {envelope.Type}.");
            }
        }

        /// <summary>
        /// Single outbound writer: drains the bounded channel and performs the
        /// actual (potentially blocking) named-pipe write.  If the renderer child
        /// stops reading, only this task blocks; the UI thread stays responsive.
        /// </summary>
        private async Task OutboundLoopAsync()
        {
            try
            {
                await foreach (var envelope in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        lock (_writeLock)
                        {
                            if (!IsConnected)
                            {
                                BufferPendingOutbound(envelope);
                                continue;
                            }

                            WriteEnvelope(envelope);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Broken pipe / dead child. Stop writing and drop the
                        // remaining queue; the session will be torn down by the
                        // process-exit watcher.
                        Volatile.Write(ref _outboundFaulted, 1);
                        EngineLog.Write(
                            LogSubsystem.ProcessIsolation,
                            LogSeverity.Warn,
                            $"[ProcessIsolation] Outbound IPC write failed for tab {TabId}; dropping queue: {ex.Message}");
                        while (_outbound.Reader.TryRead(out _))
                        {
                            // Drain silently.
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            catch (Exception ex)
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Warn,
                    $"[ProcessIsolation] Outbound IPC loop terminated for tab {TabId}: {ex.Message}");
            }
        }

        private void BufferPendingOutbound(RendererIpcEnvelope envelope)
        {
            if (!ShouldBufferWhileDisconnected(envelope))
            {
                return;
            }

            if (_pendingOutbound.Count >= MaxPendingOutboundMessages)
            {
                _pendingOutbound.Dequeue();
            }

            _pendingOutbound.Enqueue(envelope);
        }

        private void FlushPendingOutbound()
        {
            lock (_writeLock)
            {
                if (!IsConnected || _pendingOutbound.Count == 0)
                {
                    return;
                }

                while (_pendingOutbound.Count > 0)
                {
                    var envelope = _pendingOutbound.Dequeue();
                    _outbound.Writer.TryWrite(envelope);
                }
            }
        }

        private void WriteEnvelope(RendererIpcEnvelope envelope)
        {
            var line = RendererIpc.SerializeEnvelope(envelope);
            _writer.WriteLine(line);
            _writer.Flush();
        }

        private static bool ShouldBufferWhileDisconnected(RendererIpcEnvelope envelope)
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.Type))
            {
                return false;
            }

            if (string.Equals(envelope.Type, RendererIpcMessageType.FrameRequest.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(envelope.Type, RendererIpcMessageType.Input.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _readyTcs.TrySetResult(false);
            _outbound.Writer.TryComplete();

            TryDispose(_writer, "writer");
            TryDispose(_reader, "reader");
            TryDispose(_pipe, "pipe");
            TryDispose(_cts, "cts");
            TryDispose(_frameSharedMemory, "frame-shared-memory");
            _frameSharedMemory = null;
        }

        private void TryDispose(IDisposable disposable, string resourceName)
        {
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, $"[ProcessIsolation] Dispose failed for {resourceName} (tab {TabId}): {ex.Message}");
            }
        }
    }
}

