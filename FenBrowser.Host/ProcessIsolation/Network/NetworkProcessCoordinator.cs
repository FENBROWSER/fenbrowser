using System;
using System.Buffers;
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
    internal enum BodyChunkAppendResult
    {
        Accepted,
        SequenceMismatch,
        ByteCountMismatch,
        SizeExceeded,
        BufferFull,
        ChannelClosed
    }

    /// <summary>
    /// A forward-only stream reading from a Channel of byte chunks. Disposing the
    /// stream notifies the broker so an abandoned response does not keep downloading
    /// in the network child until the bounded channel happens to fill.
    /// </summary>
    internal sealed class ChannelStream : Stream
    {
        private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
        private readonly Action _onDispose;
        private byte[] _currentChunk;
        private int _chunkOffset;
        private int _disposed;

        public ChannelStream(
            System.Threading.Channels.ChannelReader<byte[]> reader,
            Action onDispose = null)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _onDispose = onDispose;
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (_currentChunk == null || _chunkOffset >= _currentChunk.Length)
            {
                if (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_reader.TryRead(out var chunk))
                    {
                        if (chunk.Length == 0) continue;
                        _currentChunk = chunk;
                        _chunkOffset = 0;
                    }
                }
                else
                {
                    return 0; // EOF
                }
            }

            int toCopy = Math.Min(buffer.Length, _currentChunk.Length - _chunkOffset);
            _currentChunk.AsSpan(_chunkOffset, toCopy).CopyTo(buffer.Span);
            _chunkOffset += toCopy;
            return toCopy;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _currentChunk = null;
                try
                {
                    _onDispose?.Invoke();
                }
                catch
                {
                    // Stream disposal must remain best-effort; broker cleanup has its
                    // own idempotent request/capability ownership checks.
                }
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// In-flight request state held in the broker while waiting for the
    /// network process to complete a fetch.
    /// </summary>
    internal sealed class PendingNetworkRequest : IDisposable
    {
        private const int BufferedBodyChunkCapacity = 8;

        public string RequestId { get; }
        public CancellationToken CancellationToken { get; }

        private readonly TaskCompletionSource<NetworkFetchResponseHeadPayload> _headTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Threading.Channels.Channel<byte[]> _bodyChannel;
        private readonly object _bodyLock = new object();
        private long _bodyBytes;
        private int _nextChunkIndex;
        private bool _bodyTerminal;
        private readonly TaskCompletionSource<bool> _bodyCompleteTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _disposed;

        public PendingNetworkRequest(string requestId, CancellationToken cancellationToken)
        {
            RequestId = requestId;
            CancellationToken = cancellationToken;
            _bodyChannel = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                new System.Threading.Channels.BoundedChannelOptions(BufferedBodyChunkCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });

            _cancellationRegistration = CancellationToken.Register(SetCancelled);
        }

        public Task<NetworkFetchResponseHeadPayload> HeadTask => _headTcs.Task;
        public Task<bool> BodyCompleteTask => _bodyCompleteTcs.Task;
        public System.Threading.Channels.ChannelReader<byte[]> BodyReader => _bodyChannel.Reader;

        public void SetHead(NetworkFetchResponseHeadPayload head) =>
            _headTcs.TrySetResult(head);

        public void SetHeadFailed(string error) =>
            _headTcs.TrySetException(new HttpRequestException(error));

        public void SetCancelled()
        {
            lock (_bodyLock)
            {
                if (_bodyTerminal)
                {
                    return;
                }

                _bodyTerminal = true;
                _headTcs.TrySetCanceled(CancellationToken);
                _bodyCompleteTcs.TrySetCanceled(CancellationToken);
                _bodyChannel.Writer.TryComplete(new OperationCanceledException(CancellationToken));
            }
        }

        public void SetFailed(string error)
        {
            SetHeadFailed(error);
            SetBodyFailed(error);
        }

        public BodyChunkAppendResult TryAppendBodyChunk(
            byte[] chunk,
            int chunkIndex,
            long reportedBytesTotal,
            int maxBodyBytes,
            bool isComplete)
        {
            chunk ??= Array.Empty<byte>();

            lock (_bodyLock)
            {
                if (_bodyTerminal)
                {
                    return BodyChunkAppendResult.ChannelClosed;
                }

                if (chunkIndex != _nextChunkIndex)
                {
                    return BodyChunkAppendResult.SequenceMismatch;
                }

                if (chunk.LongLength > maxBodyBytes - _bodyBytes)
                {
                    return BodyChunkAppendResult.SizeExceeded;
                }

                var projectedBytes = _bodyBytes + chunk.LongLength;
                if (reportedBytesTotal < 0 || reportedBytesTotal != projectedBytes)
                {
                    return BodyChunkAppendResult.ByteCountMismatch;
                }

                // Wait mode plus TryWrite means no data is dropped. If the consumer
                // falls behind, fail this request rather than retaining unbounded data
                // on the broker's IPC read loop.
                if (chunk.Length > 0 && !_bodyChannel.Writer.TryWrite(chunk))
                {
                    return BodyChunkAppendResult.BufferFull;
                }

                _bodyBytes = projectedBytes;
                _nextChunkIndex++;

                if (isComplete)
                {
                    _bodyTerminal = true;
                    _bodyCompleteTcs.TrySetResult(true);
                    _bodyChannel.Writer.TryComplete();
                }

                return BodyChunkAppendResult.Accepted;
            }
        }

        public void SetBodyFailed(string error)
        {
            lock (_bodyLock)
            {
                if (_bodyTerminal)
                {
                    return;
                }

                _bodyTerminal = true;
                var ex = new HttpRequestException(error);
                _bodyCompleteTcs.TrySetException(ex);
                _bodyChannel.Writer.TryComplete(ex);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
        }
    }

    /// <summary>
    /// Broker-side coordinator routing all network I/O through the sandboxed
    /// Network child process. If that security boundary is unavailable, requests
    /// fail closed instead of silently switching to in-process networking.
    /// </summary>
    public sealed class NetworkProcessCoordinator : IDisposable
    {
        private const int MaxBodyBytes = 64 * 1024 * 1024;
        // FetchRequest currently travels in the JSON control plane, whose payload is
        // capped at 224 KiB. Admit raw request bodies conservatively so Base64 + JSON
        // metadata fit instead of allocating a huge body only to reject the envelope.
        // Larger upload bodies require the future binary/streaming request data plane.
        private const int MaxRequestBodyBytes = 128 * 1024;
        private const int RequestBodyReadBufferBytes = 16 * 1024;

        private readonly ConcurrentDictionary<string, PendingNetworkRequest> _pending = new();
        private NetworkProcessSession _session;
        private int _disposed;
        private readonly int _maxBodyBytes;

        public NetworkProcessCoordinator() : this(MaxBodyBytes)
        {
        }

        internal NetworkProcessCoordinator(int maxBodyBytes)
        {
            if (maxBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
            _maxBodyBytes = maxBodyBytes;
        }

        public void AttachSession(NetworkProcessSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NetworkProcessCoordinator));

            DetachSession();

            _session = session;
            _session.ResponseHeadReceived += OnResponseHeadReceived;
            _session.ResponseBodyReceived += OnResponseBodyReceived;
            _session.RequestFailed += OnRequestFailed;
            _session.NetworkProcessCrashed += OnNetworkProcessCrashed;

            EngineLogBridge.Info("[NetworkCoordinator] Session attached.", LogCategory.Network);
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

            // Build/validate the request before creating pending IPC state. Failure to
            // read a body must fail the request; silently replacing it with an empty
            // body changes POST/PUT semantics and can create dangerous retries.
            var fetchPayload = await BuildFetchPayloadAsync(
                request,
                initiatorOrigin,
                cancellationToken).ConfigureAwait(false);

            var pending = new PendingNetworkRequest(requestId, cancellationToken);
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

                var head = await pending.HeadTask
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);

                if (head.StatusCode < 100 || head.StatusCode > 599)
                {
                    throw new HttpRequestException("Network process returned an invalid HTTP status code.");
                }

                if (head.ContentLength > _maxBodyBytes)
                {
                    throw new HttpRequestException(
                        $"Network response exceeds the configured {_maxBodyBytes}-byte body limit.");
                }

                var stream = new ChannelStream(
                    pending.BodyReader,
                    () => CancelAbandonedResponse(requestId, pending, session));

                _ = CleanupAfterBodyCompleteAsync(pending, session, requestId);

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

        private void CancelAbandonedResponse(
            string requestId,
            PendingNetworkRequest pending,
            NetworkProcessSession session)
        {
            if (_pending.TryRemove(requestId, out var removed))
            {
                removed.SetCancelled();
            }
            else
            {
                pending.SetCancelled();
            }

            session.SendCancel(requestId);
        }

        private async Task CleanupAfterBodyCompleteAsync(
            PendingNetworkRequest pending,
            NetworkProcessSession session,
            string requestId)
        {
            try
            {
                await pending.BodyCompleteTask.ConfigureAwait(false);
            }
            catch
            {
                // The stream observes body failures/cancellation. This continuation
                // owns only dictionary/capability/lifetime cleanup.
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
                session.ReleaseCapabilityToken(requestId);
                pending.Dispose();
            }
        }

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
                if (body.BodyChunkBase64 == null)
                {
                    pending.SetBodyFailed("Response body was malformed.");
                    _session?.SendCancel(body.RequestId);
                    return;
                }

                try
                {
                    var bytes = Convert.FromBase64String(body.BodyChunkBase64);
                    var appendResult = pending.TryAppendBodyChunk(
                        bytes,
                        body.ChunkIndex,
                        body.BytesTotal,
                        _maxBodyBytes,
                        body.IsComplete);

                    if (appendResult != BodyChunkAppendResult.Accepted)
                    {
                        string error;
                        switch (appendResult)
                        {
                            case BodyChunkAppendResult.SequenceMismatch:
                                error = "Response body chunk sequence was invalid.";
                                EngineLogBridge.Warn(
                                    $"[NetworkCoordinator] Body chunk sequence mismatch for request {body.RequestId}; rejecting.",
                                    LogCategory.Network);
                                break;
                            case BodyChunkAppendResult.ByteCountMismatch:
                                error = "Response body byte-count sequence was invalid.";
                                EngineLogBridge.Warn(
                                    $"[NetworkCoordinator] Body byte-count mismatch for request {body.RequestId}; rejecting.",
                                    LogCategory.Network);
                                break;
                            case BodyChunkAppendResult.SizeExceeded:
                                error = "Response body exceeded maximum allowed size.";
                                EngineLogBridge.Warn(
                                    $"[NetworkCoordinator] Aggregate body exceeds max size for request {body.RequestId}; rejecting.",
                                    LogCategory.Network);
                                break;
                            case BodyChunkAppendResult.BufferFull:
                                error = "Response body consumer exceeded the bounded IPC buffer.";
                                EngineLogBridge.Warn(
                                    $"[NetworkCoordinator] Response consumer fell behind bounded body buffer for request {body.RequestId}; cancelling.",
                                    LogCategory.Network);
                                break;
                            default:
                                error = "Response body arrived after the body channel was closed.";
                                EngineLogBridge.Warn(
                                    $"[NetworkCoordinator] Body chunk arrived after stream closure for request {body.RequestId}; rejecting.",
                                    LogCategory.Network);
                                break;
                        }

                        pending.SetBodyFailed(error);
                        _session?.SendCancel(body.RequestId);
                    }
                }
                catch (FormatException ex)
                {
                    EngineLogBridge.Warn(
                        $"[NetworkCoordinator] Body base64 decode failed for request {body.RequestId}: {ex.GetType().Name}",
                        LogCategory.Network);
                    pending.SetBodyFailed("Response body was malformed.");
                    _session?.SendCancel(body.RequestId);
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

        private static async Task<NetworkFetchRequestPayload> BuildFetchPayloadAsync(
            HttpRequestMessage request,
            string initiatorOrigin,
            CancellationToken cancellationToken)
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

            string bodyBase64 = null;
            if (request.Content != null)
            {
                var declaredLength = request.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > MaxRequestBodyBytes)
                {
                    throw new HttpRequestException(
                        $"Request body exceeds the brokered-network {MaxRequestBodyBytes}-byte control-plane limit.");
                }

                var initialCapacity = declaredLength is > 0 and <= MaxRequestBodyBytes
                    ? (int)declaredLength.Value
                    : 0;
                using var bodyBuffer = initialCapacity > 0
                    ? new MemoryStream(initialCapacity)
                    : new MemoryStream();
                var rented = ArrayPool<byte>.Shared.Rent(RequestBodyReadBufferBytes);
                try
                {
                    using var bodyStream = await request.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);

                    var total = 0;
                    while (true)
                    {
                        var read = await bodyStream.ReadAsync(
                            rented.AsMemory(0, RequestBodyReadBufferBytes),
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (total > MaxRequestBodyBytes - read)
                        {
                            throw new HttpRequestException(
                                $"Request body exceeds the brokered-network {MaxRequestBodyBytes}-byte control-plane limit.");
                        }

                        total += read;
                        bodyBuffer.Write(rented, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                if (bodyBuffer.Length > 0)
                {
                    if (bodyBuffer.TryGetBuffer(out var segment) && segment.Array != null)
                    {
                        bodyBase64 = Convert.ToBase64String(segment.Array, segment.Offset, segment.Count);
                    }
                    else
                    {
                        bodyBase64 = Convert.ToBase64String(bodyBuffer.ToArray());
                    }
                }
                else
                {
                    // Preserve an explicitly present zero-length content body as an
                    // empty payload rather than conflating it with no HttpContent.
                    bodyBase64 = string.Empty;
                }
            }

            return new NetworkFetchRequestPayload
            {
                Url = request.RequestUri?.AbsoluteUri ?? string.Empty,
                Method = request.Method.Method,
                Headers = headers,
                BodyBase64 = bodyBase64,
                Mode = GetFetchMode(request),
                Credentials = CorsHandler.GetCredentialsMode(request),
                InitiatorOrigin = initiatorOrigin ?? string.Empty,
            };
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
