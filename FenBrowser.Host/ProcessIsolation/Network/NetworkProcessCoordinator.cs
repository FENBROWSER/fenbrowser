using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network.Handlers;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    internal sealed class PendingNetworkRequest : IDisposable
    {
        public string RequestId { get; }
        public CancellationToken CancellationToken { get; }
        public NetworkBodyPipe BodyPipe { get; }

        private readonly TaskCompletionSource<NetworkFetchResponseHeadPayload> _headTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _disposed;

        public PendingNetworkRequest(
            string requestId,
            CancellationToken cancellationToken,
            NetworkBodyPipe bodyPipe)
        {
            RequestId = requestId;
            CancellationToken = cancellationToken;
            BodyPipe = bodyPipe ?? throw new ArgumentNullException(nameof(bodyPipe));
            _cancellationRegistration = CancellationToken.Register(SetCancelled);
        }

        public Task<NetworkFetchResponseHeadPayload> HeadTask => _headTcs.Task;

        public void SetHead(NetworkFetchResponseHeadPayload head) =>
            _headTcs.TrySetResult(head);

        public void SetHeadFailed(string error) =>
            _headTcs.TrySetException(new HttpRequestException(error));

        public void SetCancelled()
        {
            _headTcs.TrySetCanceled(CancellationToken);
            BodyPipe.Dispose();
        }

        public void SetFailed(string error) => SetHeadFailed(error);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            BodyPipe.Dispose();
        }
    }

    /// <summary>
    /// Broker-side coordinator routing all network I/O through the sandboxed
    /// Network child process. If that security boundary is unavailable, requests
    /// fail closed instead of silently switching to in-process networking.
    /// </summary>
    public sealed class NetworkProcessCoordinator : IDisposable
    {
        private const long DefaultResponseBodyLimit = 512L * 1024 * 1024;

        private readonly ConcurrentDictionary<string, PendingNetworkRequest> _pending = new();
        private NetworkProcessSession _session;
        private int _disposed;
        private readonly long _defaultResponseBodyLimit;

        public NetworkProcessCoordinator() : this(DefaultResponseBodyLimit)
        {
        }

        internal NetworkProcessCoordinator(long defaultResponseBodyLimit)
        {
            if (defaultResponseBodyLimit <= 0) throw new ArgumentOutOfRangeException(nameof(defaultResponseBodyLimit));
            _defaultResponseBodyLimit = defaultResponseBodyLimit;
        }

        public void AttachSession(NetworkProcessSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NetworkProcessCoordinator));

            DetachSession();

            _session = session;
            _session.ResponseHeadReceived += OnResponseHeadReceived;
            _session.RequestFailed += OnRequestFailed;
            _session.NetworkProcessCrashed += OnNetworkProcessCrashed;

            EngineLogBridge.Info("[NetworkCoordinator] Session attached.", LogCategory.Network);
        }

        private void DetachSession()
        {
            if (_session == null) return;
            _session.ResponseHeadReceived -= OnResponseHeadReceived;
            _session.RequestFailed -= OnRequestFailed;
            _session.NetworkProcessCrashed -= OnNetworkProcessCrashed;
            _session = null;
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            string initiatorOrigin,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NetworkProcessCoordinator));

            var session = _session;
            if (session == null || !session.IsConnected)
            {
                EngineLogBridge.Warn(
                    $"[NetworkCoordinator] Sandboxed network process unavailable; blocking request to {GetSafeUriForLog(request.RequestUri)}.",
                    LogCategory.Security);
                throw new HttpRequestException("Sandboxed network process is unavailable; request blocked by process-isolation policy.");
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
            var bodyPipe = NetworkBodyPipe.CreateServer();
            var fetchPayload = BuildFetchPayload(request, initiatorOrigin, bodyPipe);
            var pending = new PendingNetworkRequest(requestId, cancellationToken, bodyPipe);
            if (!_pending.TryAdd(requestId, pending))
            {
                pending.Dispose();
                throw new InvalidOperationException("Network request ID collision.");
            }

            var fetchSent = false;

            try
            {
                session.SendFetch(fetchPayload, requestId);
                fetchSent = true;

                await bodyPipe.WaitForAuthenticatedClientAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);

                var requestBodyPump = bodyPipe.SendContentAsync(request.Content, cancellationToken);
                _ = ObserveRequestBodyPumpAsync(requestBodyPump, requestId, pending, session);

                var head = await pending.HeadTask
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);

                if (head.StatusCode < 100 || head.StatusCode > 599)
                {
                    throw new HttpRequestException("Network process returned an invalid HTTP status code.");
                }

                var responseBodyLimit = ResolveResponseBodyLimit(request);
                if (head.ContentLength > responseBodyLimit)
                {
                    throw new HttpRequestException(
                        $"Network response exceeds the configured {responseBodyLimit}-byte destination limit.");
                }

                var stream = bodyPipe.OpenReadStream(
                    responseBodyLimit,
                    () => CompleteResponse(requestId, pending, session));

                return BuildHttpResponse(head, stream, request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_pending.TryRemove(requestId, out var cancelledPending))
                {
                    cancelledPending.SetCancelled();
                }
                session.SendCancel(requestId);
                pending.Dispose();
                throw;
            }
            catch (TimeoutException)
            {
                EngineLogBridge.Warn($"[NetworkCoordinator] Request {requestId} timed out.", LogCategory.Network);
                _pending.TryRemove(requestId, out _);
                pending.SetFailed("Network process request timed out.");
                session.SendCancel(requestId);
                pending.Dispose();
                throw new HttpRequestException(
                    $"Network process request timed out for {GetSafeUriForLog(request.RequestUri)}");
            }
            catch (Exception)
            {
                _pending.TryRemove(requestId, out _);
                pending.SetFailed("Network process request failed before the response stream was established.");
                if (fetchSent)
                {
                    session.SendCancel(requestId);
                }
                else
                {
                    session.ReleaseCapabilityToken(requestId);
                }
                pending.Dispose();
                throw;
            }
        }

        private async Task ObserveRequestBodyPumpAsync(
            Task requestBodyPump,
            string requestId,
            PendingNetworkRequest pending,
            NetworkProcessSession session)
        {
            try
            {
                await requestBodyPump.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(requestId, out var removed))
                {
                    removed.SetFailed("Request body streaming failed.");
                    removed.Dispose();
                }

                session.SendCancel(requestId);
                EngineLogBridge.Warn(
                    $"[NetworkCoordinator] Request body streaming failed for {requestId}: {ex.GetType().Name}",
                    LogCategory.Network);
            }
        }

        private void CompleteResponse(
            string requestId,
            PendingNetworkRequest pending,
            NetworkProcessSession session)
        {
            if (_pending.TryRemove(requestId, out var removed))
            {
                removed.Dispose();
            }

            session.SendCancel(requestId);
        }

        private void OnResponseHeadReceived(NetworkFetchResponseHeadPayload head)
        {
            if (head == null || string.IsNullOrEmpty(head.RequestId)) return;

            if (_pending.TryGetValue(head.RequestId, out var pending))
            {
                pending.SetHead(head);
            }
        }

        private void OnRequestFailed(NetworkFetchFailedPayload fail)
        {
            if (fail == null || string.IsNullOrEmpty(fail.RequestId)) return;

            if (_pending.TryRemove(fail.RequestId, out var pending))
            {
                var error = $"[{fail.ErrorCode}] {fail.ErrorMessage}";
                pending.SetHeadFailed(error);
                pending.Dispose();
                EngineLogBridge.Warn(
                    $"[NetworkCoordinator] Request {fail.RequestId} failed with code {fail.ErrorCode}.",
                    LogCategory.Network);
            }
        }

        private void OnNetworkProcessCrashed()
        {
            EngineLogBridge.Warn("[NetworkCoordinator] Network process crashed; failing all pending requests.", LogCategory.Network);

            foreach (var kv in _pending)
            {
                kv.Value.SetFailed("Network process disconnected during request.");
                kv.Value.Dispose();
            }
            _pending.Clear();
            DetachSession();
        }

        private static string GetFetchMode(HttpRequestMessage request)
        {
            if (request != null && request.Headers.TryGetValues("Sec-Fetch-Mode", out var values))
            {
                foreach (var value in values)
                {
                    var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (normalized is "cors" or "no-cors" or "same-origin" or "navigate")
                    {
                        return normalized;
                    }
                }
            }

            return "cors";
        }

        private static NetworkFetchRequestPayload BuildFetchPayload(
            HttpRequestMessage request,
            string initiatorOrigin,
            NetworkBodyPipe bodyPipe)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            return new NetworkFetchRequestPayload
            {
                Url = request.RequestUri?.AbsoluteUri ?? string.Empty,
                Method = request.Method.Method,
                Headers = headers,
                HasBody = request.Content != null,
                BodyPipeName = bodyPipe.PipeName,
                BodyPipeToken = bodyPipe.AuthenticationToken,
                Mode = GetFetchMode(request),
                Credentials = CorsHandler.GetCredentialsMode(request),
                InitiatorOrigin = initiatorOrigin ?? string.Empty,
            };
        }

        private long ResolveResponseBodyLimit(HttpRequestMessage request)
        {
            if (request?.Headers.TryGetValues("Sec-Fetch-Dest", out var values) == true)
            {
                var destination = string.Join(string.Empty, values).Trim().ToLowerInvariant();
                if (destination is "audio" or "video" or "track")
                    return 4L * 1024 * 1024 * 1024;
                if (destination is "document" or "iframe")
                    return 1024L * 1024 * 1024;
                if (destination == "image")
                    return 256L * 1024 * 1024;
                if (destination is "script" or "style" or "font")
                    return 128L * 1024 * 1024;
            }

            return _defaultResponseBodyLimit;
        }

        private static HttpResponseMessage BuildHttpResponse(
            NetworkFetchResponseHeadPayload head,
            Stream bodyStream,
            HttpRequestMessage request)
        {
            var response = new HttpResponseMessage((HttpStatusCode)head.StatusCode)
            {
                ReasonPhrase = head.StatusText ?? string.Empty,
                RequestMessage = request,
                Content = new StreamContent(bodyStream),
            };

            if (head.Headers != null)
            {
                foreach (var kv in head.Headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                    {
                        response.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }
            }

            return response;
        }

        private static string GetSafeUriForLog(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return string.Empty;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return uri.GetLeftPart(UriPartial.Authority);
                }
                catch
                {
                    return uri.Scheme + ":";
                }
            }

            return uri.Scheme + ":";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var session = _session;
            DetachSession();

            foreach (var kv in _pending)
            {
                if (!_pending.TryRemove(kv.Key, out var pending))
                {
                    continue;
                }

                pending.SetFailed("Network process coordinator was disposed.");
                session?.SendCancel(kv.Key);
                pending.Dispose();
            }
        }
    }
}
