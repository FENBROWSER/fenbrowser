using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    /// <summary>
    /// Renderer end of renderer networking: the request transport installed into
    /// <see cref="FenBrowser.Core.Network.HttpClientFactory"/> inside a renderer
    /// child, so that every HttpClient the engine creates there sends its requests
    /// to the broker instead of opening sockets. The broker's
    /// <see cref="RendererNetworkRelay"/> answers on the same IPC pipe; the
    /// renderer's message loop forwards those answers here.
    /// </summary>
    internal sealed class RendererNetworkClient : IDisposable
    {
        private static readonly TimeSpan BodyPipeWait = TimeSpan.FromSeconds(15);
        // The broker's own hop to the network child gives up after 30s and reports
        // a failure back, so this only fires if the broker itself went silent.
        private static readonly TimeSpan HeadWait = TimeSpan.FromSeconds(60);

        private readonly int _tabId;
        private readonly Action<RendererIpcEnvelope> _send;
        private readonly ConcurrentDictionary<string, PendingFetch> _pending = new(StringComparer.Ordinal);
        private int _disposed;

        public RendererNetworkClient(int tabId, Action<RendererIpcEnvelope> send)
        {
            _tabId = tabId;
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(RendererNetworkClient));
            }

            if (!NetworkFetchMessages.IsRelayableUrl(request.RequestUri?.AbsoluteUri, out _))
            {
                throw new HttpRequestException($"Only http(s) requests can be relayed to the broker: '{request.RequestUri}'.");
            }

            // Guid "N" is what the broker's envelope validation accepts as a correlation id.
            var requestId = Guid.NewGuid().ToString("N");
            var pending = new PendingFetch();
            if (!_pending.TryAdd(requestId, pending))
            {
                throw new InvalidOperationException("Renderer fetch request id collision.");
            }

            NetworkBodyPipe bodyPipe = null;
            try
            {
                _send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.NetworkFetch.ToString(),
                    TabId = _tabId,
                    CorrelationId = requestId,
                    Payload = RendererIpc.SerializePayload(NetworkFetchMessages.BuildFetchPayload(
                        request,
                        ProcessIsolationRuntime.GetInitiatorOrigin(request),
                        bodyPipe: null))
                });

                var pipeInfo = await pending.BodyPipe.Task
                    .WaitAsync(BodyPipeWait, cancellationToken)
                    .ConfigureAwait(false);

                bodyPipe = await NetworkBodyPipe.ConnectClientAsync(pipeInfo.PipeName, pipeInfo.PipeToken, cancellationToken)
                    .ConfigureAwait(false);

                if (request.Content != null)
                {
                    // Upload runs alongside the wait for the head: the broker reads
                    // the body lazily while forwarding the request, and a server
                    // may answer before it has consumed all of it.
                    _ = ObserveUploadAsync(bodyPipe.SendContentAsync(request.Content, cancellationToken), requestId);
                }

                var head = await pending.Head.Task
                    .WaitAsync(HeadWait, cancellationToken)
                    .ConfigureAwait(false);

                var capturedPipe = bodyPipe;
                var stream = bodyPipe.OpenReadStream(long.MaxValue, completed: () =>
                {
                    _pending.TryRemove(requestId, out _);
                    capturedPipe.Dispose();
                });
                bodyPipe = null; // now owned by the response stream
                return NetworkFetchMessages.BuildHttpResponse(head, stream, request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SendCancel(requestId);
                throw;
            }
            catch (TimeoutException ex)
            {
                SendCancel(requestId);
                throw new HttpRequestException("The broker did not answer the fetch in time.", ex);
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SendCancel(requestId);
                throw new HttpRequestException(ex.Message, ex);
            }
            finally
            {
                if (bodyPipe != null)
                {
                    _pending.TryRemove(requestId, out _);
                    bodyPipe.Dispose();
                }
            }
        }

        public void OnBodyPipe(RendererIpcEnvelope envelope)
        {
            var payload = RendererIpc.DeserializePayload<RendererNetworkBodyPipePayload>(envelope);
            if (payload == null || !_pending.TryGetValue(envelope.CorrelationId, out var pending))
            {
                return;
            }

            pending.BodyPipe.TrySetResult(payload);
        }

        public void OnResponseHead(RendererIpcEnvelope envelope)
        {
            var payload = RendererIpc.DeserializePayload<NetworkFetchResponseHeadPayload>(envelope);
            if (payload == null || !_pending.TryGetValue(envelope.CorrelationId, out var pending))
            {
                return;
            }

            pending.Head.TrySetResult(payload);
        }

        public void OnFailed(RendererIpcEnvelope envelope)
        {
            var payload = RendererIpc.DeserializePayload<NetworkFetchFailedPayload>(envelope);
            if (!_pending.TryRemove(envelope.CorrelationId, out var pending))
            {
                return;
            }

            var error = new HttpRequestException(
                payload == null ? "Fetch failed in the broker." : $"[{payload.ErrorCode}] {payload.ErrorMessage}");
            pending.BodyPipe.TrySetException(error);
            pending.Head.TrySetException(error);
        }

        private void SendCancel(string requestId)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _send(new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.NetworkFetchCancel.ToString(),
                TabId = _tabId,
                CorrelationId = requestId
            });
        }

        private static async Task ObserveUploadAsync(Task upload, string requestId)
        {
            try
            {
                await upload.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererNetworkClient] Request body upload for {requestId} ended early: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var error = new HttpRequestException("Renderer is shutting down.");
            foreach (var kv in _pending)
            {
                kv.Value.BodyPipe.TrySetException(error);
                kv.Value.Head.TrySetException(error);
            }
            _pending.Clear();
        }

        private sealed class PendingFetch
        {
            public TaskCompletionSource<RendererNetworkBodyPipePayload> BodyPipe { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<NetworkFetchResponseHeadPayload> Head { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
