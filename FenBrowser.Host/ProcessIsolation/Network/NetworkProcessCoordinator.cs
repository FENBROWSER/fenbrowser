using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    // ── NetworkProcessCoordinator ─────────────────────────────────────────────
    // Broker-side coordinator that bridges ResourceManager's HttpClient calls
    // to the sandboxed Network child process over IPC.
    //
    // Responsibilities:
    //  1. Accept HttpRequestMessage from callers (ResourceManager / Fetch API).
    //  2. Validate origin, credentials, and policy before forwarding.
    //  3. Mint a NetworkCapabilityToken per request (origin-locked, expiring).
    //  4. Forward via NetworkProcessSession.SendFetch().
    //  5. Collect streaming response head + body chunks from the session events.
    //  6. Validate the capability token on each inbound envelope.
    //  7. Return an HttpResponseMessage to the caller.
    //
    // Thread-safety: all public methods are thread-safe. Pending requests are
    // tracked in a ConcurrentDictionary keyed by requestId.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-flight request state held in the broker while waiting for the
    /// network process to complete a fetch.
    /// </summary>
    internal sealed class PendingNetworkRequest : IDisposable
    {
        public string RequestId { get; }
        public string CapabilityTokenValue { get; }
        public string InitiatorOrigin { get; }
        public CancellationToken CancellationToken { get; }

        // Signals completion of the response HEAD (status + headers).
        private readonly TaskCompletionSource<NetworkFetchResponseHeadPayload> _headTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Accumulates base64-decoded body chunks in order.
        private readonly List<byte[]> _bodyChunks = new();
        private readonly SemaphoreSlim _bodyLock = new(1, 1);
        private readonly TaskCompletionSource<bool> _bodyCompleteTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingNetworkRequest(
            string requestId,
            string capabilityTokenValue,
            string initiatorOrigin,
            CancellationToken cancellationToken)
        {
            RequestId = requestId;
            CapabilityTokenValue = capabilityTokenValue;
            InitiatorOrigin = initiatorOrigin;
            CancellationToken = cancellationToken;
        }

        public Task<NetworkFetchResponseHeadPayload> HeadTask => _headTcs.Task;
        public Task<bool> BodyCompleteTask => _bodyCompleteTcs.Task;

        public void SetHead(NetworkFetchResponseHeadPayload head) =>
            _headTcs.TrySetResult(head);

        public void SetHeadFailed(string error) =>
            _headTcs.TrySetException(new HttpRequestException(error));

        public void SetCancelled()
        {
            _headTcs.TrySetCanceled();
            _bodyCompleteTcs.TrySetCanceled();
        }

        public async Task AppendBodyChunkAsync(byte[] chunk)
        {
            await _bodyLock.WaitAsync().ConfigureAwait(false);
            try { _bodyChunks.Add(chunk); }
            finally { _bodyLock.Release(); }
        }

        public void SetBodyComplete() => _bodyCompleteTcs.TrySetResult(true);
        public void SetBodyFailed(string error) =>
            _bodyCompleteTcs.TrySetException(new HttpRequestException(error));

        public async Task<byte[]> GetBodyAsync()
        {
            await _bodyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_bodyChunks.Count == 0) return Array.Empty<byte>();
                int total = 0;
                foreach (var c in _bodyChunks) total += c.Length;
                var buf = new byte[total];
                int offset = 0;
                foreach (var c in _bodyChunks)
                {
                    Buffer.BlockCopy(c, 0, buf, offset, c.Length);
                    offset += c.Length;
                }
                return buf;
            }
            finally { _bodyLock.Release(); }
        }

        public void Dispose() => _bodyLock.Dispose();
    }

    /// <summary>
    /// Broker-side coordinator routing all network I/O through the sandboxed
    /// Network child process. Falls back to in-process HttpClient when the
    /// network process is unavailable.
    /// </summary>
    public sealed class NetworkProcessCoordinator : IDisposable
    {
        private readonly ConcurrentDictionary<string, PendingNetworkRequest> _pending = new();
        private readonly ConcurrentDictionary<string, string> _requestIdToCapToken = new();
        private NetworkProcessSession _session;
        private readonly HttpClient _fallbackClient;
        private volatile bool _disposed;

        // Maximum body size accepted from the network process (64 MB).
        private const int MaxBodyBytes = 64 * 1024 * 1024;

        public NetworkProcessCoordinator()
        {
            _fallbackClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 20,
                CheckCertificateRevocationList = false,
                ServerCertificateCustomValidationCallback = null,
            });
            _fallbackClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Attach a live NetworkProcessSession. Wire up response events.
        /// Safe to call multiple times (re-wires on reconnect).
        /// </summary>
        public void AttachSession(NetworkProcessSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            // Detach old session if any
            DetachSession();

            _session = session;
            _session.ResponseHeadReceived += OnResponseHeadReceived;
            _session.ResponseBodyReceived += OnResponseBodyReceived;
            _session.RequestFailed += OnRequestFailed;
            _session.NetworkProcessCrashed += OnNetworkProcessCrashed;

            FenLogger.Info("[NetworkCoordinator] Session attached.", LogCategory.Network);
        }

        private void DetachSession()
        {
            if (_session == null) return;
            _session.ResponseHeadReceived -= OnResponseHeadReceived;
            _session.ResponseBodyReceived -= OnResponseBodyReceived;
            _session.RequestFailed -= OnRequestFailed;
            _session.NetworkProcessCrashed -= OnNetworkProcessCrashed;
            _session = null;
        }

        /// <summary>
        /// Send an HTTP request, routing through the network process when
        /// available, or falling back to an in-process HttpClient.
        /// </summary>
        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            string initiatorOrigin,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var session = _session;
            if (session == null || !session.IsConnected)
            {
                FenLogger.Debug(
                    $"[NetworkCoordinator] Network process unavailable; using in-process fallback for {request.RequestUri}.",
                    LogCategory.Network);
                return await _fallbackClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            return await SendViaNetworkProcessAsync(request, session, initiatorOrigin, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendViaNetworkProcessAsync(
            HttpRequestMessage request,
            NetworkProcessSession session,
            string initiatorOrigin,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");

            // Build IPC payload
            var fetchPayload = BuildFetchPayload(request, initiatorOrigin);

            // Register pending state before sending (avoids race with fast responses)
            using var pending = new PendingNetworkRequest(requestId, string.Empty, initiatorOrigin, cancellationToken);
            _pending[requestId] = pending;

            // Register cancellation
            using var ctreg = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(requestId, out var pr))
                {
                    pr.SetCancelled();
                    session.SendCancel(requestId);
                }
            });

            // Mint capability token and send
            var capToken = session.SendFetch(fetchPayload, initiatorOrigin ?? "");
            _requestIdToCapToken[requestId] = capToken.Value;

            try
            {
                // Wait for response head (status + headers)
                var head = await pending.HeadTask
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);

                // Validate capability token on head
                if (!string.IsNullOrEmpty(head.RequestId) &&
                    _requestIdToCapToken.TryGetValue(head.RequestId, out var expectedToken))
                {
                    if (!session.ValidateCapabilityToken(expectedToken, head.Url ?? ""))
                    {
                        FenLogger.Warn(
                            $"[NetworkCoordinator] Capability token validation failed for request {requestId}.",
                            LogCategory.Network);
                        throw new HttpRequestException("Network process response failed capability token validation.");
                    }
                }

                // Wait for body completion
                await pending.BodyCompleteTask
                    .WaitAsync(TimeSpan.FromSeconds(60), cancellationToken)
                    .ConfigureAwait(false);

                var bodyBytes = await pending.GetBodyAsync().ConfigureAwait(false);
                return BuildHttpResponse(head, bodyBytes);
            }
            catch (TimeoutException)
            {
                FenLogger.Warn($"[NetworkCoordinator] Request {requestId} timed out.", LogCategory.Network);
                session.SendCancel(requestId);
                throw new HttpRequestException($"Network process request timed out: {request.RequestUri}");
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
                _requestIdToCapToken.TryRemove(requestId, out _);
            }
        }

        // ── Session event handlers ────────────────────────────────────────────

        private void OnResponseHeadReceived(NetworkFetchResponseHeadPayload head)
        {
            if (head == null || string.IsNullOrEmpty(head.RequestId)) return;

            if (_pending.TryGetValue(head.RequestId, out var pending))
            {
                pending.SetHead(head);
            }
        }

        private void OnResponseBodyReceived(NetworkFetchResponseBodyPayload body)
        {
            if (body == null || string.IsNullOrEmpty(body.RequestId)) return;

            if (_pending.TryGetValue(body.RequestId, out var pending))
            {
                if (!string.IsNullOrEmpty(body.BodyChunkBase64))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(body.BodyChunkBase64);
                        if (bytes.Length > MaxBodyBytes)
                        {
                            FenLogger.Warn(
                                $"[NetworkCoordinator] Body chunk exceeds max size for request {body.RequestId}; dropping.",
                                LogCategory.Network);
                            pending.SetBodyFailed("Response body exceeded maximum allowed size.");
                            return;
                        }
                        pending.AppendBodyChunkAsync(bytes).GetAwaiter().GetResult();
                    }
                    catch (FormatException ex)
                    {
                        FenLogger.Warn($"[NetworkCoordinator] Body base64 decode failed: {ex.Message}", LogCategory.Network);
                    }
                }

                if (body.IsComplete)
                {
                    pending.SetBodyComplete();
                }
            }
        }

        private void OnRequestFailed(NetworkFetchFailedPayload fail)
        {
            if (fail == null || string.IsNullOrEmpty(fail.RequestId)) return;

            if (_pending.TryRemove(fail.RequestId, out var pending))
            {
                var error = $"[{fail.ErrorCode}] {fail.ErrorMessage}";
                pending.SetHeadFailed(error);
                pending.SetBodyFailed(error);
                FenLogger.Warn($"[NetworkCoordinator] Request {fail.RequestId} failed: {error}", LogCategory.Network);
            }
        }

        private void OnNetworkProcessCrashed()
        {
            FenLogger.Warn("[NetworkCoordinator] Network process crashed; failing all pending requests.", LogCategory.Network);

            foreach (var kv in _pending)
            {
                kv.Value.SetCancelled();
            }
            _pending.Clear();
            _requestIdToCapToken.Clear();
            DetachSession();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static NetworkFetchRequestPayload BuildFetchPayload(
            HttpRequestMessage request,
            string initiatorOrigin)
        {
            var headers = new Dictionary<string, string>();
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(", ", h.Value);
            }
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers)
                {
                    headers[h.Key] = string.Join(", ", h.Value);
                }
            }

            string bodyBase64 = null;
            if (request.Content != null)
            {
                try
                {
                    var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    bodyBase64 = Convert.ToBase64String(bytes);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[NetworkCoordinator] Failed to read request body: {ex.Message}", LogCategory.Network);
                }
            }

            return new NetworkFetchRequestPayload
            {
                Url = request.RequestUri?.AbsoluteUri ?? "",
                Method = request.Method.Method,
                Headers = headers,
                BodyBase64 = bodyBase64,
                InitiatorOrigin = initiatorOrigin ?? "",
            };
        }

        private static HttpResponseMessage BuildHttpResponse(
            NetworkFetchResponseHeadPayload head,
            byte[] bodyBytes)
        {
            var response = new HttpResponseMessage((HttpStatusCode)head.StatusCode)
            {
                ReasonPhrase = head.StatusText ?? string.Empty,
                RequestMessage = null,
                Content = new ByteArrayContent(bodyBytes ?? Array.Empty<byte>()),
            };

            if (head.Headers != null)
            {
                foreach (var kv in head.Headers)
                {
                    // Attempt response headers first, then content headers
                    if (!response.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                    {
                        response.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }
            }

            return response;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DetachSession();
            _fallbackClient.Dispose();
            foreach (var kv in _pending) kv.Value.Dispose();
            _pending.Clear();
        }
    }
}
