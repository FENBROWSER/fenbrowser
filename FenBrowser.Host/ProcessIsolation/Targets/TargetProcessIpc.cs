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
                    FenLogger.Warn($"[{_targetKind}Process] Child process exited.", LogCategory.General);
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
                FenLogger.Info($"[{_targetKind}Process] IPC connected on pipe '{PipeName}'.", LogCategory.General);
            }
            catch (OperationCanceledException)
            {
                _readyTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                FenLogger.Warn($"[{_targetKind}Process] IPC connect failed: {ex.Message}", LogCategory.General);
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

                    DispatchInbound(envelope);
                }
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                if (!_cts.IsCancellationRequested)
                {
                    FenLogger.Warn($"[{_targetKind}Process] Read loop error: {ex.Message}", LogCategory.General);
                }
            }
        }

        private void DispatchInbound(TargetIpcEnvelope envelope)
        {
            if (!Enum.TryParse<TargetIpcMessageType>(envelope.Type, true, out var messageType))
            {
                return;
            }

            switch (messageType)
            {
                case TargetIpcMessageType.Ready:
                    _readyTcs.TrySetResult(true);
                    FenLogger.Info($"[{_targetKind}Process] Target process reported ready.", LogCategory.General);
                    break;
                case TargetIpcMessageType.Error:
                    FenLogger.Warn($"[{_targetKind}Process] Error from child: {envelope.Payload}", LogCategory.General);
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
                FenLogger.Warn($"[{_targetKind}Process] Send failed: {ex.Message}", LogCategory.General);
            }
        }
    }
}
