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
using FenBrowser.Core.Network.Handlers;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    internal enum BodyChunkAppendResult
    {
        Accepted,
        SequenceMismatch,
        SizeExceeded,
        BufferFull,
        ChannelClosed
    }

    /// <summary>
    /// A forward-only stream reading from a Channel of byte chunks.
    /// </summary>
    internal sealed class ChannelStream : Stream
    {
        private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
        private byte[] _currentChunk;
        private int _chunkOffset;

        public ChannelStream(System.Threading.Channels.ChannelReader<byte[]> reader)
        {
            _reader = reader;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
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
            return await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── NetworkProcessCoordinator ─────────────────────────────────────────────
    // Broker-side coordinator that bridges ResourceManager's HttpClient calls
    // to the sandboxed Network child process over IPC.
    //
    // Responsibilities:
    //  1. Accept HttpRequestMessage from callers (ResourceManager / Fetch API).
    //  2. Validate origin, credentials, and policy before forwarding.
    //  3. Mint a request-bound capability token for each IPC fetch.
    //  4. Forward via NetworkProcessSession.SendFetch().
    //  5. Collect streaming response head + body chunks from the session events.
    //  6. Rely on the session to validate the exact request capability on inbound envelopes.
    //  7. Return an HttpResponseMessage to the caller.
    //
    // Thread-safety: all public methods are thread-safe. Pending requests are
    // tracked in a ConcurrentDictionary keyed by requestId.
    // ── NetworkProcessCoordinator ─────────────────────────────────────────────
    // Broker-side coordinator that bridges ResourceManager's HttpClient calls
    // to the sandboxed Network child process over IPC.
    //
    // Responsibilities:
    //  1. Accept HttpRequestMessage from callers (ResourceManager / Fetch API).
    //  2. Validate origin, credentials, and policy before forwarding.
    //  3. Mint a request-bound capability token for each IPC fetch.
    //  4. Forward via NetworkProcessSession.SendFetch().
    //  5. Collect streaming response head + body chunks from the session events.
    //  6. Rely on the session to validate the exact request capability on inbound envelopes.
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
        private const int BufferedBodyChunkCapacity = 8;

        public string RequestId { get; }
        public CancellationToken CancellationToken { get; }

        // Signals completion of the response HEAD (status + headers).
        private readonly TaskCompletionSource<NetworkFetchResponseHeadPayload> _headTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Keep decoded response buffering bounded. The IPC read loop remains
        // non-blocking across requests: when a consumer falls too far behind,
        // the request is failed and cancelled instead of retaining more chunks.
        private readonly System.Threading.Channels.Channel<byte[]> _bodyChannel;
        private readonly object _bodyLock = new object();
        private long _bodyBytes;
        private int _nextChunkIndex;
        private bool _bodyTerminal;
        private readonly TaskCompletionSource<bool> _bodyCompleteTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _disposed;

        public PendingNetworkRequest(
            string requestId,
            CancellationToken cancellationToken)
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

            // Cancellation must terminate both the stream and the completion task
            // so the coordinator can release request state even after HEAD returned.
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
            int maxBodyBytes,
            bool isComplete)
        {
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

                // The bounded channel uses Wait mode so TryWrite returns false
                // when capacity is exhausted rather than dropping a response chunk.
                if (chunk.Length > 0 && !_bodyChannel.Writer.TryWrite(chunk))
                {
                    return BodyChunkAppendResult.BufferFull;
                }

                _bodyBytes += chunk.LongLength;
                _nextChunkIndex++;

                // Complete only after the final chunk has been accepted and queued.
                // Keeping append + completion under one lock prevents EOF from racing
                // ahead of a rejected final chunk.
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
        private readonly ConcurrentDictionary<string, PendingNetworkRequest> _pending = new();
        private NetworkProcessSession _session;
        private int _disposed;

        // Maximum body size accepted from the network process (64 MB).
        private const int MaxBodyBytes = 64 * 1024 * 1024;
        private readonly int _maxBodyBytes;

        public NetworkProcessCoordinator() : this(MaxBodyBytes)
        {
        }

        internal NetworkProcessCoordinator(int maxBodyBytes)
        {
            if (maxBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
            _maxBodyBytes = maxBodyBytes;
        }

        /// <summary>
        /// Attach a live NetworkProcessSession. Wire up response events.
        /// Safe to call multiple times (re-wires on reconnect).
        /// </summary>
        public void AttachSession(NetworkProcessSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NetworkProcessCoordinator));

            // Detach old session if any
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

        /// <summary>
        /// Send an HTTP request through the sandboxed network process.
        /// </summary>
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
                    $"[NetworkCoordinator] Sandboxed network process unavailable; blocking request to {request.RequestUri}.",
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

            // Build IPC payload
            var fetchPayload = BuildFetchPayload(request, initiatorOrigin);

            // Register pending state before sending (avoids race with fast responses)
            var pending = new PendingNetworkRequest(requestId, cancellationToken);
            _pending[requestId] = pending;
            var fetchSent = false;

            try
            {
                // Mint the request-bound capability and write the request under
                // the same cleanup scope as the pending entry.
                session.SendFetch(fetchPayload, requestId);
                fetchSent = true;

                // Wait for response head (status + headers). The session has already
                // validated the exact request capability before raising this event.
                var head = await pending.HeadTask
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);

                // Return response with streaming body immediately after receiving HEAD
                // We do NOT wait for BodyCompleteTask here.
                var stream = new ChannelStream(pending.BodyReader);

                // Keep the pending request alive in the dictionary until the body finishes,
                // but we return the stream to the caller now.
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
                throw new HttpRequestException($"Network process request timed out: {request.RequestUri}");
            }
            catch (Exception)
            {
                // Any failure before returning the response stream owns the whole
                // pending request. Close the body path and stop child-side work.
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

        private async Task CleanupAfterBodyCompleteAsync(PendingNetworkRequest pending, NetworkProcessSession session, string requestId)
        {
            try
            {
                await pending.BodyCompleteTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore exceptions here; they will be observed by the stream reader
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
                session.ReleaseCapabilityToken(requestId);
                pending.Dispose();
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
                    EngineLogBridge.Warn($"[NetworkCoordinator] Body base64 decode failed: {ex.Message}", LogCategory.Network);
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
                EngineLogBridge.Warn($"[NetworkCoordinator] Request {fail.RequestId} failed: {error}", LogCategory.Network);
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

        // ── Helpers ────────────────────────────────────────────────────────────

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
                    EngineLogBridge.Warn($"[NetworkCoordinator] Failed to read request body: {ex.Message}", LogCategory.Network);
                }
            }

            return new NetworkFetchRequestPayload
            {
                Url = request.RequestUri?.AbsoluteUri ?? "",
                Method = request.Method.Method,
                Headers = headers,
                BodyBase64 = bodyBase64,
                Mode = GetFetchMode(request),
                Credentials = CorsHandler.GetCredentialsMode(request),
                InitiatorOrigin = initiatorOrigin ?? "",
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
