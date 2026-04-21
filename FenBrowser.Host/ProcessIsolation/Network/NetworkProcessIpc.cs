using System;
using System.Collections.Concurrent;
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

namespace FenBrowser.Host.ProcessIsolation.Network
{
    // ── Network Process IPC ───────────────────────────────────────────────────
    // The Network process is a Target process (sandboxed, least-privilege).
    // The Broker (BrowserProcess) communicates with it over a named pipe.
    // Control plane: JSON envelopes over pipe.
    // Data plane: response bodies flow through a shared-memory ring.
    // Security: all messages validated; capability tokens required; no raw file
    //           paths or OS handles sent to renderer without broker mediation.
    // ─────────────────────────────────────────────────────────────────────────

    public enum NetworkIpcMessageType
    {
        // Broker → Network
        Hello,
        FetchRequest,
        CancelRequest,
        CookieSet,
        CookieGet,
        CacheInvalidate,
        DnsPreconnect,
        Shutdown,
        Ping,
        // Network → Broker
        Ready,
        FetchResponseHead,
        FetchResponseBody,
        FetchFailed,
        LogBatch,
        CookieResult,
        Pong,
        Error,
    }

    /// <summary>
    /// Capability token minted by the Broker for each network request.
    /// The Network process must echo it back in responses; the Broker
    /// validates it before forwarding the response to the renderer.
    /// </summary>
    public sealed class NetworkCapabilityToken
    {
        public string Value { get; }
        public string OriginLock { get; }      // eTLD+1 or exact origin the fetch is allowed for
        public bool AllowCredentials { get; }
        public DateTimeOffset ExpiresAt { get; }

        public NetworkCapabilityToken(string originLock, bool allowCredentials, TimeSpan ttl)
        {
            Value = Guid.NewGuid().ToString("N");
            OriginLock = originLock;
            AllowCredentials = allowCredentials;
            ExpiresAt = DateTimeOffset.UtcNow + ttl;
        }

        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

        public bool IsValidFor(string requestOrigin)
        {
            if (IsExpired) return false;
            if (string.IsNullOrEmpty(OriginLock)) return false;
            return requestOrigin == OriginLock ||
                   requestOrigin?.EndsWith("." + OriginLock, StringComparison.Ordinal) == true;
        }
    }

    /// <summary>Wire envelope — JSON-serialised, one line per message.</summary>
    public sealed class NetworkIpcEnvelope
    {
        public string Type { get; set; }
        public string RequestId { get; set; }
        public string CapabilityToken { get; set; }
        public string Payload { get; set; }
        public long TimestampUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public sealed class NetworkFetchRequestPayload
    {
        public string Url { get; set; }
        public string Method { get; set; } = "GET";
        public System.Collections.Generic.Dictionary<string, string> Headers { get; set; }
        public string BodyBase64 { get; set; }           // null = no body
        public string Mode { get; set; } = "cors";       // cors | no-cors | same-origin | navigate
        public string Credentials { get; set; } = "same-origin";
        public string Cache { get; set; } = "default";
        public string Redirect { get; set; } = "follow";
        public string Referrer { get; set; }
        public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";
        public string Integrity { get; set; }
        public bool Keepalive { get; set; }
        public string InitiatorOrigin { get; set; }      // origin of the renderer making the request
    }

    public sealed class NetworkFetchResponseHeadPayload
    {
        public string RequestId { get; set; }
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public System.Collections.Generic.Dictionary<string, string> Headers { get; set; }
        public string Url { get; set; }                  // final URL after redirects
        public string ResponseType { get; set; }         // basic | cors | opaque | error
        public bool Cors { get; set; }
        public bool Opaque { get; set; }
        public long ContentLength { get; set; } = -1;
    }

    public sealed class NetworkFetchResponseBodyPayload
    {
        public string RequestId { get; set; }
        public bool IsComplete { get; set; }
        public int ChunkIndex { get; set; }
        public string BodyChunkBase64 { get; set; }      // base64-encoded chunk
        public long BytesTotal { get; set; }
    }

    public sealed class NetworkFetchFailedPayload
    {
        public string RequestId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal static class NetworkIpc
    {
        private const int MaxEnvelopeChars = 256 * 1024;
        private const int MaxPayloadChars = 224 * 1024;
        private const int MaxTypeChars = 40;
        private const int MaxRequestIdChars = 96;
        private const int MaxCapabilityTokenChars = 512;
        private const long MaxClockSkewMs = 24L * 60L * 60L * 1000L;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        public static string Serialize(NetworkIpcEnvelope e) => JsonSerializer.Serialize(e, JsonOpts);

        public static bool TryDeserialize(string line, out NetworkIpcEnvelope e)
        {
            e = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.Length > MaxEnvelopeChars) return false;
            try { e = JsonSerializer.Deserialize<NetworkIpcEnvelope>(line, JsonOpts); return e != null; }
            catch { return false; }
        }

        public static string SerializePayload<T>(T p) =>
            p == null ? string.Empty : JsonSerializer.Serialize(p, JsonOpts);

        public static T DeserializePayload<T>(NetworkIpcEnvelope e) where T : class
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Payload)) return null;
            try { return JsonSerializer.Deserialize<T>(e.Payload, JsonOpts); }
            catch { return null; }
        }

        public static bool TryValidateInboundEnvelope(
            NetworkIpcEnvelope envelope,
            out NetworkIpcMessageType messageType,
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
                !Enum.TryParse<NetworkIpcMessageType>(envelope.Type, ignoreCase: true, out messageType))
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

        public static bool IsAllowedBrokerInboundMessageType(NetworkIpcMessageType messageType)
        {
            return messageType == NetworkIpcMessageType.Ready ||
                   messageType == NetworkIpcMessageType.FetchResponseHead ||
                   messageType == NetworkIpcMessageType.FetchResponseBody ||
                   messageType == NetworkIpcMessageType.FetchFailed ||
                   messageType == NetworkIpcMessageType.LogBatch ||
                   messageType == NetworkIpcMessageType.Pong ||
                   messageType == NetworkIpcMessageType.Error;
        }
    }

    /// <summary>
    /// Broker-side session that manages the Network child process IPC connection.
    /// The Network process is sandboxed; it performs all HTTP(S) fetches and
    /// returns results to the Broker.  The Broker enforces CORS/referrer policy,
    /// response tainting, and CSP before forwarding to the renderer.
    /// </summary>
    public sealed class NetworkProcessSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<NetworkFetchResponseHeadPayload>> _pending = new();
        private readonly ConcurrentDictionary<string, NetworkCapabilityToken> _capTokens = new();
        private readonly object _writeLock = new();
        private StreamReader _reader;
        private StreamWriter _writer;
        private Task _readLoop;
        private bool _connected;
        private Process _childProcess;

        public string PipeName { get; }
        public string AuthToken { get; }
        public bool IsConnected => _connected && _pipe.IsConnected;

        public event Action<NetworkFetchResponseHeadPayload> ResponseHeadReceived;
        public event Action<NetworkFetchResponseBodyPayload> ResponseBodyReceived;
        public event Action<NetworkFetchFailedPayload> RequestFailed;
        public event Action NetworkProcessCrashed;

        public NetworkProcessSession(string pipeName, string authToken)
        {
            PipeName = pipeName;
            AuthToken = authToken;
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

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
                    NetworkProcessCrashed?.Invoke();
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, "[NetworkProcess] Child process exited.");
                };
            }

            _ = Task.Run(() => WaitForConnectionAsync(childProcess));
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

        private async Task WaitForConnectionAsync(System.Diagnostics.Process child)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                _connected = true;

                // Send Hello with auth token
                Send(new NetworkIpcEnvelope
                {
                    Type = NetworkIpcMessageType.Hello.ToString(),
                    RequestId = Guid.NewGuid().ToString("N"),
                    CapabilityToken = AuthToken,
                });

                _readLoop = Task.Run(ReadLoopAsync);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[NetworkProcess] IPC connected on pipe '{PipeName}'.");

            }
            catch (OperationCanceledException)
            {
                _readyTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] IPC connect failed: {ex.Message}");
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (!NetworkIpc.TryDeserialize(line, out var env)) continue;
                    if (!NetworkIpc.TryValidateInboundEnvelope(env, out var messageType, out var rejectionReason))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] Rejected IPC envelope: {rejectionReason}.");
                        continue;
                    }
                    if (!NetworkIpc.IsAllowedBrokerInboundMessageType(messageType))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] Rejected unexpected IPC message type: {messageType}.");
                        continue;
                    }
                    DispatchInbound(env);
                }
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetResult(false);
                if (!_cts.IsCancellationRequested)
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] Read loop error: {ex.Message}");
            }
        }

        private void DispatchInbound(NetworkIpcEnvelope env)
        {
            if (!NetworkIpc.TryValidateInboundEnvelope(env, out var msgType, out _)) return;

            switch (msgType)
            {
                case NetworkIpcMessageType.FetchResponseHead:
                    var head = NetworkIpc.DeserializePayload<NetworkFetchResponseHeadPayload>(env);
                    if (head != null) ResponseHeadReceived?.Invoke(head);
                    break;

                case NetworkIpcMessageType.FetchResponseBody:
                    var body = NetworkIpc.DeserializePayload<NetworkFetchResponseBodyPayload>(env);
                    if (body != null) ResponseBodyReceived?.Invoke(body);
                    break;

                case NetworkIpcMessageType.FetchFailed:
                    var fail = NetworkIpc.DeserializePayload<NetworkFetchFailedPayload>(env);
                    if (fail != null) RequestFailed?.Invoke(fail);
                    break;

                case NetworkIpcMessageType.LogBatch:
                    var batch = NetworkIpc.DeserializePayload<EngineLogBatchPayload>(env);
                    ProcessIsolationLogCollector.PublishBatch(batch);
                    break;

                case NetworkIpcMessageType.Ready:
                    _readyTcs.TrySetResult(true);
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, "[NetworkProcess] Network process reported ready.");
                    break;

                case NetworkIpcMessageType.Pong:
                    break;

                case NetworkIpcMessageType.Error:
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] Error from child: {env.Payload}");
                    break;
            }
        }

        /// <summary>
        /// Issue a fetch request to the Network process.
        /// Returns the capability token so the Broker can validate responses.
        /// </summary>
        public NetworkCapabilityToken SendFetch(NetworkFetchRequestPayload request, string initiatorOrigin)
        {
            var cap = new NetworkCapabilityToken(
                originLock: initiatorOrigin ?? "",
                allowCredentials: request.Credentials != "omit",
                ttl: TimeSpan.FromMinutes(5));

            _capTokens[cap.Value] = cap;

            var requestId = Guid.NewGuid().ToString("N");

            Send(new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchRequest.ToString(),
                RequestId = requestId,
                CapabilityToken = cap.Value,
                Payload = NetworkIpc.SerializePayload(request),
            });

            return cap;
        }

        public void SendCancel(string requestId)
        {
            Send(new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.CancelRequest.ToString(),
                RequestId = requestId,
            });
        }

        public void SendShutdown()
        {
            try
            {
                Send(new NetworkIpcEnvelope { Type = NetworkIpcMessageType.Shutdown.ToString() });
            }
            catch { }
        }

        public bool ValidateCapabilityToken(string tokenValue, string requestOrigin)
        {
            if (!_capTokens.TryGetValue(tokenValue, out var token)) return false;
            return token.IsValidFor(requestOrigin);
        }

        private void Send(NetworkIpcEnvelope env)
        {
            if (env == null) return;
            try
            {
                lock (_writeLock)
                {
                    if (!IsConnected) return;
                    _writer.WriteLine(NetworkIpc.Serialize(env));
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkProcess] Send failed: {ex.Message}");
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
    }
}

