using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    internal enum RendererIpcMessageType
    {
        Hello,
        Ready,
        Navigate,
        Input,
        FrameRequest,
        FrameReady,
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
    }

    public sealed class RendererFrameRequestPayload
    {
        public float ViewportWidth { get; set; }
        public float ViewportHeight { get; set; }
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
    }

    internal static class RendererIpc
    {
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
        private FrameSharedMemory _frameSharedMemory;
        private readonly int _parentPid = Environment.ProcessId;

        public event Action<int, RendererFrameReadyPayload> FrameReceived;

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

        public void SendNavigate(string url, bool isUserInput)
        {
            var payload = new RendererNavigatePayload
            {
                Url = url ?? string.Empty,
                IsUserInput = isUserInput
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
            if (inputEvent == null)
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

        public void SendFrameRequest(float viewportWidth, float viewportHeight)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFrameRequestUtc).TotalMilliseconds < 66)
            {
                return;
            }
            _lastFrameRequestUtc = now;

            var payload = new RendererFrameRequestPayload
            {
                ViewportWidth = viewportWidth,
                ViewportHeight = viewportHeight
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
                _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                Send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.Hello.ToString(),
                    TabId = TabId,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Token = AuthToken
                });
                FlushPendingOutbound();

                _readLoop = Task.Run(ReadLoopAsync);
                FenLogger.Info($"[ProcessIsolation] IPC connected for tab {TabId} via pipe '{PipeName}'.", LogCategory.General);
            }
            catch (OperationCanceledException)
            {
                _readyTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                FenLogger.Warn($"[ProcessIsolation] IPC connect failed for tab {TabId}: {ex.Message}", LogCategory.General);
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

                    if (string.Equals(envelope.Type, RendererIpcMessageType.Ready.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        _readyTcs.TrySetResult(true);
                        FenLogger.Info($"[ProcessIsolation] Renderer child ready for tab {TabId}.", LogCategory.General);
                    }
                    else if (string.Equals(envelope.Type, RendererIpcMessageType.FrameReady.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = RendererIpc.DeserializePayload<RendererFrameReadyPayload>(envelope);
                        var url = payload?.Url ?? "<unknown>";
                        var width = payload?.SurfaceWidth ?? 0f;
                        var height = payload?.SurfaceHeight ?? 0f;
                        var dirtyCount = payload?.DirtyRegionCount ?? 0;
                        FenLogger.Debug($"[ProcessIsolation] FrameReady tab={TabId} url={url} surface={width}x{height} dirtyRegions={dirtyCount}", LogCategory.Rendering);

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
                                    FenLogger.Warn($"[ProcessIsolation] Failed to open FrameSharedMemory for tab {TabId}: {ex.Message}", LogCategory.General);
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
                                    FenLogger.Warn($"[ProcessIsolation] FrameSharedMemory read failed for tab {TabId}: {ex.Message}", LogCategory.General);
                                }
                            }

                            FrameReceived?.Invoke(TabId, payload);
                        }
                    }
                    else if (string.Equals(envelope.Type, RendererIpcMessageType.Error.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        FenLogger.Warn($"[ProcessIsolation] Renderer child error tab={TabId}: {envelope.Payload}", LogCategory.General);
                    }
                }
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                if (!_cts.IsCancellationRequested)
                {
                    FenLogger.Warn($"[ProcessIsolation] IPC read loop terminated for tab {TabId}: {ex.Message}", LogCategory.General);
                }
            }
        }

        private void Send(RendererIpcEnvelope envelope)
        {
            if (envelope == null)
            {
                return;
            }

            try
            {
                lock (_writeLock)
                {
                    if (!IsConnected)
                    {
                        BufferPendingOutbound(envelope);
                        return;
                    }

                    WriteEnvelope(envelope);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[ProcessIsolation] Failed to send IPC message for tab {TabId}: {ex.Message}", LogCategory.General);
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
                    WriteEnvelope(envelope);
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

            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _frameSharedMemory?.Dispose(); } catch { }
            _frameSharedMemory = null;
        }
    }
}
