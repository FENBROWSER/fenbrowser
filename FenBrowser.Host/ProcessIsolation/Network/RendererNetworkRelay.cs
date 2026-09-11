using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    /// <summary>
    /// Broker end of renderer networking. A renderer child has no sockets of its
    /// own - the sandbox gives it none - so every fetch it makes arrives here as a
    /// <c>NetworkFetch</c> message on its IPC pipe. The relay hands the request to
    /// <see cref="NetworkProcessCoordinator"/>, which applies the broker's policy
    /// and performs the I/O in the sandboxed network child, and streams the
    /// response back to the renderer over a per-request <see cref="NetworkBodyPipe"/>.
    /// </summary>
    /// <remarks>
    /// Wire sequence for one request:
    /// <list type="number">
    /// <item>renderer → broker <c>NetworkFetch</c> (correlation id = request id)</item>
    /// <item>broker → renderer <c>NetworkFetchBodyPipe</c>; renderer connects and, if the
    /// request has a body, streams it</item>
    /// <item>broker → renderer <c>NetworkFetchResponseHead</c>, then the response body
    /// frames on the same pipe - or <c>NetworkFetchFailed</c></item>
    /// </list>
    /// The renderer may send <c>NetworkFetchCancel</c> at any point.
    ///
    /// Security: the renderer chooses the URL and headers exactly as it did when
    /// it held a socket itself; what changes is that the request now crosses the
    /// broker, where the coordinator's origin and destination checks apply and the
    /// only process that ever talks to the network is the one sandboxed for it.
    /// Only http(s) URLs are relayed; anything else is answered in-engine.
    /// </remarks>
    internal sealed class RendererNetworkRelay : IDisposable
    {
        private static readonly TimeSpan BodyPipeConnectTimeout = TimeSpan.FromSeconds(10);

        private readonly int _tabId;
        private readonly Action<RendererIpcEnvelope> _send;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
        private int _disposed;

        public RendererNetworkRelay(int tabId, Action<RendererIpcEnvelope> send)
        {
            _tabId = tabId;
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public void HandleFetch(RendererIpcEnvelope envelope)
        {
            if (envelope == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var requestId = envelope.CorrelationId;
            var payload = RendererIpc.DeserializePayload<NetworkFetchRequestPayload>(envelope);
            if (payload == null || !NetworkFetchMessages.IsRelayableUrl(payload.Url, out _))
            {
                SendFailure(requestId, "invalid_request", "Fetch URL is missing or not an http(s) URL.");
                return;
            }

            var coordinator = ProcessIsolationRuntime.NetworkCoordinator;
            if (coordinator == null)
            {
                // Same fail-closed answer the broker gives its own clients when the
                // network child is not running.
                SendFailure(requestId, "network_unavailable",
                    "Sandboxed network process is unavailable; request blocked by process-isolation policy.");
                return;
            }

            var cts = new CancellationTokenSource();
            if (!_active.TryAdd(requestId, cts))
            {
                cts.Dispose();
                SendFailure(requestId, "invalid_request", "Duplicate request id.");
                return;
            }

            _ = Task.Run(() => RelayAsync(requestId, payload, coordinator, cts.Token));
        }

        public void HandleCancel(RendererIpcEnvelope envelope)
        {
            if (envelope == null)
            {
                return;
            }

            if (_active.TryGetValue(envelope.CorrelationId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private async Task RelayAsync(
            string requestId,
            NetworkFetchRequestPayload payload,
            NetworkProcessCoordinator coordinator,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var bodyPipe = NetworkBodyPipe.CreateServer();
                _send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.NetworkFetchBodyPipe.ToString(),
                    TabId = _tabId,
                    CorrelationId = requestId,
                    Payload = RendererIpc.SerializePayload(new RendererNetworkBodyPipePayload
                    {
                        RequestId = requestId,
                        PipeName = bodyPipe.PipeName,
                        PipeToken = bodyPipe.AuthenticationToken
                    })
                });

                await bodyPipe.WaitForAuthenticatedClientAsync(BodyPipeConnectTimeout, cancellationToken).ConfigureAwait(false);

                using var requestBody = payload.HasBody ? bodyPipe.OpenReadStream(long.MaxValue) : null;
                using var request = NetworkFetchMessages.BuildRequest(payload, requestBody);
                var initiatorOrigin = ProcessIsolationRuntime.GetInitiatorOrigin(request);
                if (string.IsNullOrEmpty(initiatorOrigin))
                {
                    initiatorOrigin = payload.InitiatorOrigin;
                }

                using var response = await coordinator.SendAsync(request, initiatorOrigin, cancellationToken).ConfigureAwait(false);

                _send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.NetworkFetchResponseHead.ToString(),
                    TabId = _tabId,
                    CorrelationId = requestId,
                    Payload = RendererIpc.SerializePayload(
                        NetworkFetchMessages.BuildResponseHead(response, requestId, payload.Mode, payload.Url))
                });

                using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await bodyPipe.SendStreamAsync(bodyStream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SendFailure(requestId, "cancelled", "Request cancelled.");
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[RendererNetworkRelay] Fetch {requestId} for tab {_tabId} failed: {ex.Message}");
                SendFailure(requestId, "fetch_failed", ex.Message);
            }
            finally
            {
                if (_active.TryRemove(requestId, out var cts))
                {
                    cts.Dispose();
                }
            }
        }

        private void SendFailure(string requestId, string errorCode, string errorMessage)
        {
            _send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.NetworkFetchFailed.ToString(),
                TabId = _tabId,
                CorrelationId = requestId,
                Payload = RendererIpc.SerializePayload(new NetworkFetchFailedPayload
                {
                    RequestId = requestId,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage ?? string.Empty
                })
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var kv in _active)
            {
                try
                {
                    kv.Value.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
