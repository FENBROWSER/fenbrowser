using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.ProcessIsolation;

namespace FenBrowser.Host.ProcessIsolation.Targets
{
    public enum TargetProcessKind
    {
        Gpu,
        Utility
    }

    public enum TargetIpcMessageType
    {
        Hello,
        Ready,
        LogBatch,
        Ping,
        Pong,
        Shutdown,
        Error
    }

    public sealed class TargetIpcEnvelope
    {
        public string Type { get; set; }
        public string RequestId { get; set; }
        public string CapabilityToken { get; set; }
        public string Payload { get; set; }
        public long TimestampUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public sealed class TargetReadyPayload
    {
        public string ProcessKind { get; set; }
        public string SandboxProfile { get; set; }
        public string Capabilities { get; set; }
        public int ProcessId { get; set; }
    }

    internal static class TargetIpc
    {
        private const int MaxEnvelopeChars = 128 * 1024;
        private const int MaxPayloadChars = 96 * 1024;
        private const int MaxTypeChars = 40;
        private const int MaxRequestIdChars = 96;
        private const int MaxCapabilityTokenChars = 512;
        private const long MaxClockSkewMs = 24L * 60L * 60L * 1000L;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string Serialize(TargetIpcEnvelope envelope) => JsonSerializer.Serialize(envelope, JsonOpts);

        public static bool TryDeserialize(string line, out TargetIpcEnvelope envelope)
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
                envelope = JsonSerializer.Deserialize<TargetIpcEnvelope>(line, JsonOpts);
                return envelope != null;
            }
            catch
            {
                return false;
            }
        }

        public static string SerializePayload<T>(T payload)
            => payload == null ? string.Empty : JsonSerializer.Serialize(payload, JsonOpts);

        public static T DeserializePayload<T>(TargetIpcEnvelope envelope) where T : class
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(envelope.Payload, JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public static bool TryValidateInboundEnvelope(
            TargetIpcEnvelope envelope,
            out TargetIpcMessageType messageType,
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
                !Enum.TryParse<TargetIpcMessageType>(envelope.Type, ignoreCase: true, out messageType))
            {
                rejectionReason = "type-invalid";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(envelope.RequestId))
            {
                if (envelope.RequestId.Length > MaxRequestIdChars || !Guid.TryParse(envelope.RequestId, out _))
                {
                    rejectionReason = "requestid-invalid";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(envelope.CapabilityToken) && envelope.CapabilityToken.Length > MaxCapabilityTokenChars)
            {
                rejectionReason = "cap-token-too-large";
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

        public static bool IsAllowedBrokerInboundMessageType(TargetIpcMessageType messageType)
        {
            return messageType == TargetIpcMessageType.Ready ||
                   messageType == TargetIpcMessageType.LogBatch ||
                   messageType == TargetIpcMessageType.Pong ||
                   messageType == TargetIpcMessageType.Error;
        }
    }

    public sealed class TargetProcessSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeLock = new();
        private readonly TargetProcessKind _targetKind;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Process _childProcess;
        private bool _connected;

        public TargetProcessSession(TargetProcessKind targetKind, string pipeName, string authToken)
        {
            _targetKind = targetKind;
            PipeName = pipeName;
            AuthToken = authToken;
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        public string PipeName { get; }
        public string AuthToken { get; }
        public bool IsConnected => _connected && _pipe.IsConnected;
        public event Action TargetProcessCrashed;

        public void Start(Process childProcess)
        {
            _childProcess = childProcess;
            if (_childProcess != null)
            {
                _childProcess.EnableRaisingEvents = true;
                _childProcess.Exited += (_, _) =>
                {
                    _connected = false;
                    _readyTcs.TrySetResult(false);
                    TargetProcessCrashed?.Invoke();
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Child process exited.");
                };
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

        public void SendShutdown()
        {
            try
            {
                Send(new TargetIpcEnvelope { Type = TargetIpcMessageType.Shutdown.ToString() });
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _readyTcs.TrySetResult(false);
            try { SendShutdown(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        private async Task WaitForConnectionAsync()
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                _connected = true;

                Send(new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.Hello.ToString(),
                    RequestId = Guid.NewGuid().ToString("N"),
                    CapabilityToken = AuthToken,
                    Payload = TargetIpc.SerializePayload(new Dictionary<string, string>
                    {
                        ["kind"] = _targetKind.ToString().ToLowerInvariant()
                    })
                });

                _ = Task.Run(ReadLoopAsync);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[{_targetKind}Process] IPC connected on pipe '{PipeName}'.");
            }
            catch (OperationCanceledException)
            {
                _readyTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] IPC connect failed: {ex.Message}");
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

                    if (!TargetIpc.TryDeserialize(line, out var envelope))
                    {
                        continue;
                    }

                    if (!TargetIpc.TryValidateInboundEnvelope(envelope, out var messageType, out var rejectionReason))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Rejected IPC envelope: {rejectionReason}.");
                        continue;
                    }
                    if (!TargetIpc.IsAllowedBrokerInboundMessageType(messageType))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Rejected unexpected IPC message type: {messageType}.");
                        continue;
                    }

                    DispatchInbound(envelope);
                }
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                if (!_cts.IsCancellationRequested)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Read loop error: {ex.Message}");
                }
            }
        }

        private void DispatchInbound(TargetIpcEnvelope envelope)
        {
            if (!TargetIpc.TryValidateInboundEnvelope(envelope, out var messageType, out _))
            {
                return;
            }

            switch (messageType)
            {
                case TargetIpcMessageType.Ready:
                    _readyTcs.TrySetResult(true);
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[{_targetKind}Process] Target process reported ready.");
                    break;
                case TargetIpcMessageType.Error:
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Error from child: {envelope.Payload}");
                    break;
                case TargetIpcMessageType.LogBatch:
                    var batch = TargetIpc.DeserializePayload<EngineLogBatchPayload>(envelope);
                    ProcessIsolationLogCollector.PublishBatch(batch);
                    break;
                case TargetIpcMessageType.Pong:
                    break;
            }
        }

        private void Send(TargetIpcEnvelope envelope)
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
                        return;
                    }

                    _writer.WriteLine(TargetIpc.Serialize(envelope));
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{_targetKind}Process] Send failed: {ex.Message}");
            }
        }
    }
}

