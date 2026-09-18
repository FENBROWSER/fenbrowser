using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.ProcessIsolation.Targets;
using FenBrowser.Media;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Host.ProcessIsolation.Media
{
    /// <summary>
    /// The renderer's handle on the media process (MEDIA_ENGINE_DESIGN §2.2, ADR-0004):
    /// launches it on first use, answers <see cref="IMediaDecodeSourceFactory"/> with
    /// sources that demux and decode over there, and turns the child's exit into
    /// <see cref="MediaProcessLostException"/> on every open request so only the players
    /// on it fail. The next open launches a fresh child.
    /// </summary>
    public sealed class MediaProcessClient : IMediaDecodeSourceFactory, IDisposable
    {
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TargetIpcEnvelope>> _pending = new(StringComparer.Ordinal);
        private TargetChildProcessHost _host;
        private int _generation;
        private bool _disposed;

        /// <summary>The child's process id while one is running (the crash-containment test kills it).</summary>
        public int? ChildProcessId
        {
            get
            {
                lock (_gate)
                {
                    return _host?.ChildProcessId;
                }
            }
        }

        /// <summary>How many times a child has been started; grows by one per relaunch.</summary>
        public int Generation => Volatile.Read(ref _generation);

        public IMediaDecodeSource Create(IByteSource source, string declaredMime, MediaPipelineContext context)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(context);
            return new RemoteMediaDecodeSource(this, source, declaredMime, context);
        }

        /// <summary>Sends a request and waits for its response, or throws <see cref="MediaProcessLostException"/>.</summary>
        internal async Task<TResponse> RequestAsync<TResponse>(
            TargetIpcMessageType type,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            where TResponse : class
        {
            var session = EnsureStarted();
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<TargetIpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
            try
            {
                if (!session.IsConnected)
                {
                    throw new MediaProcessLostException("The media process is not connected.");
                }

                session.Send(new TargetIpcEnvelope
                {
                    Type = type.ToString(),
                    RequestId = requestId,
                    Payload = TargetIpc.SerializePayload(payload)
                });

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                TargetIpcEnvelope envelope;
                try
                {
                    envelope = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A child that stops answering is as gone as one that exited.
                    OnChildLost(session, $"no answer to {type} within {timeout.TotalSeconds:0}s");
                    throw new MediaProcessLostException($"The media process did not answer {type}.");
                }

                var response = TargetIpc.DeserializePayload<TResponse>(envelope);
                if (response == null)
                {
                    throw new MediaProcessLostException($"The media process answered {type} with an unreadable payload.");
                }

                return response;
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        internal void Notify(TargetIpcMessageType type, object payload)
        {
            TargetProcessSession session;
            lock (_gate)
            {
                session = _host?.Session;
            }

            if (session == null || !session.IsConnected)
            {
                return;
            }

            session.Send(new TargetIpcEnvelope
            {
                Type = type.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = TargetIpc.SerializePayload(payload)
            });
        }

        /// <summary>Kills the running child, as a crash would. Tests use it; the next open relaunches.</summary>
        public void KillChildForTesting()
        {
            int? pid = ChildProcessId;
            if (pid == null)
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(pid.Value);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        private TargetProcessSession EnsureStarted()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_host?.Session?.IsConnected == true)
                {
                    return _host.Session;
                }

                _host?.Dispose();
                _host = null;

                var host = new TargetChildProcessHost(TargetProcessKind.Media);
                if (!host.TryStart())
                {
                    host.Dispose();
                    throw new MediaProcessLostException("The media process could not be started.");
                }

                var session = host.Session;
                session.ResponseReceived += OnResponse;
                session.TargetProcessCrashed += () => OnChildLost(session, "exited");
                _host = host;
                Interlocked.Increment(ref _generation);
                EngineLogBridge.Info($"[MediaProcess] Media child started (pid={host.ChildProcessId}, generation={_generation}).", LogCategory.ProcessIsolation);
                return session;
            }
        }

        private void OnResponse(TargetIpcEnvelope envelope)
        {
            if (!string.IsNullOrEmpty(envelope?.RequestId) && _pending.TryRemove(envelope.RequestId, out var tcs))
            {
                tcs.TrySetResult(envelope);
            }
        }

        private void OnChildLost(TargetProcessSession session, string reason)
        {
            // Disposing the host closes its Process, whose exit callback is what runs this
            // method while holding the Process's own lock: never dispose under _gate, or a
            // concurrent Dispose() and this callback wait on each other's lock.
            TargetChildProcessHost lost;
            lock (_gate)
            {
                if (_host?.Session != session)
                {
                    return; // an older child; its requests were failed already
                }

                EngineLogBridge.Warn($"[MediaProcess] Media child lost ({reason}); failing its players.", LogCategory.ProcessIsolation);
                lost = _host;
                _host = null;
            }

            lost.Dispose();
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var tcs))
                {
                    tcs.TrySetException(new MediaProcessLostException($"The media process {reason}."));
                }
            }
        }

        public void Dispose()
        {
            TargetChildProcessHost host;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                host = _host;
                _host = null;
            }

            host?.Dispose();
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var tcs))
                {
                    tcs.TrySetException(new MediaProcessLostException("The media process client was disposed."));
                }
            }
        }

        /// <summary>
        /// One session in the media process. The resource is copied into an input region once;
        /// each read brings one block back through the output region, or one picture through
        /// the video region, which is created once the track's size is known and grown when a
        /// picture turns out larger.
        /// </summary>
        private sealed class RemoteMediaDecodeSource : IMediaDecodeSource
        {
            private readonly MediaProcessClient _client;
            private readonly IByteSource _source;
            private readonly string _declaredMime;
            private readonly MediaPipelineContext _context;
            private readonly string _sessionId = Guid.NewGuid().ToString("N");
            private MediaSharedMemory _input;
            private MediaSharedMemory _output;
            private MediaSharedMemory _video;
            private int _videoRegions;
            private int _generation;
            private bool _open;
            private bool _disposed;

            public RemoteMediaDecodeSource(MediaProcessClient client, IByteSource source, string declaredMime, MediaPipelineContext context)
            {
                _client = client;
                _source = source;
                _declaredMime = declaredMime;
                _context = context;
            }

            public async ValueTask<MediaSourceInfo> OpenAsync(CancellationToken cancellationToken)
            {
                if (_open)
                {
                    throw new InvalidOperationException("The source is already open.");
                }

                long length = await FillInputRegionAsync(cancellationToken).ConfigureAwait(false);
                if (length == 0)
                {
                    throw new MediaUnsupportedException("The resource is empty.");
                }

                _output = MediaSharedMemory.Create($"fen_media_{Environment.ProcessId}_{_sessionId}_out", MediaIpcLimits.DefaultOutputCapacity);

                var response = await _client.RequestAsync<MediaOpenResponsePayload>(
                    TargetIpcMessageType.MediaOpen,
                    new MediaOpenPayload
                    {
                        SessionId = _sessionId,
                        InputRegion = _input.Name,
                        InputLength = length,
                        OutputRegion = _output.Name,
                        OutputCapacity = _output.SizeBytes,
                        DeclaredMime = _declaredMime
                    },
                    OpenTimeout,
                    cancellationToken).ConfigureAwait(false);
                _generation = _client.Generation;
                if (!response.Success)
                {
                    throw Failure(response.ErrorKind, response.ErrorMessage);
                }

                if (response.Tracks == null || response.Tracks.Length == 0 || response.Tracks.Length > MediaLimits.Default.MaxTracks)
                {
                    throw new MediaProcessLostException("The media process answered the open with no tracks.");
                }

                var tracks = new List<MediaTrackInfo>(response.Tracks.Length);
                MediaTrackInfo audio = null;
                MediaTrackInfo video = null;
                foreach (var t in response.Tracks)
                {
                    var kind = Enum.IsDefined(typeof(MediaTrackKind), t.Kind) ? (MediaTrackKind)t.Kind : MediaTrackKind.Audio;
                    var codec = Enum.IsDefined(typeof(MediaCodec), t.Codec) ? (MediaCodec)t.Codec : MediaCodec.Unknown;
                    if (kind == MediaTrackKind.Video)
                    {
                        _context.Limits.CheckVideoDimensions(t.Width, t.Height);
                    }

                    var config = new CodecConfig(kind, codec, t.CodecString, t.Width, t.Height, t.SampleRate, t.Channels);
                    var track = new MediaTrackInfo(t.Id, config, MediaTime.FromMicroseconds(t.DurationUs), t.Language ?? string.Empty, t.Label ?? string.Empty, t.IsDefault);
                    tracks.Add(track);
                    if (t.Id == response.AudioTrackId && kind == MediaTrackKind.Audio)
                    {
                        audio = track;
                    }
                    else if (t.Id == response.VideoTrackId && kind == MediaTrackKind.Video)
                    {
                        video = track;
                    }
                }

                if (audio == null && video == null)
                {
                    throw new MediaProcessLostException("The media process named no track it listed.");
                }

                if (video != null)
                {
                    // Size the picture region for the track now; a larger picture grows it later.
                    EnsureVideoRegion(VideoFrame.LayoutSize(VideoPixelFormat.I420, video.Config.Width, video.Config.Height));
                }

                _open = true;
                return new MediaSourceInfo(tracks, audio, video, MediaTime.FromMicroseconds(response.DurationUs), response.IsSeekable);
            }

            private void EnsureVideoRegion(int requiredBytes)
            {
                if (requiredBytes <= 0)
                {
                    throw new MediaProcessLostException("The media process asked for an empty picture region.");
                }

                if (requiredBytes > MediaIpcLimits.MaxVideoCapacity)
                {
                    throw new MediaLimitExceededException("MaxVideoCapacity", requiredBytes, MediaIpcLimits.MaxVideoCapacity);
                }

                if (_video != null && _video.SizeBytes >= requiredBytes)
                {
                    return;
                }

                int granularity = MediaIpcLimits.VideoCapacityGranularity;
                int capacity = (int)Math.Min(MediaIpcLimits.MaxVideoCapacity, ((long)requiredBytes + granularity - 1) / granularity * granularity);
                capacity = Math.Max(capacity, MediaIpcLimits.MinOutputCapacity);
                var region = MediaSharedMemory.Create($"fen_media_{Environment.ProcessId}_{_sessionId}_v{++_videoRegions}", capacity);
                _video?.Dispose();
                _video = region;
            }

            public async ValueTask<DecodedMedia?> ReadAsync(CancellationToken cancellationToken)
            {
                ThrowIfNotOpen();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var response = await _client.RequestAsync<MediaReadResponsePayload>(
                        TargetIpcMessageType.MediaRead,
                        new MediaReadPayload { SessionId = _sessionId, VideoRegion = _video?.Name, VideoCapacity = _video?.SizeBytes ?? 0 },
                        RequestTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!response.Success)
                    {
                        throw Failure(response.ErrorKind, response.ErrorMessage);
                    }

                    if (response.EndOfStream)
                    {
                        return null;
                    }

                    if (response.Kind == 0)
                    {
                        return new DecodedMedia(ReadAudio(response));
                    }

                    if (response.Kind != 1)
                    {
                        throw new MediaProcessLostException("The media process answered a read with an unknown kind.");
                    }

                    if (response.RegionTooSmall)
                    {
                        EnsureVideoRegion(response.RequiredBytes);
                        continue;
                    }

                    return new DecodedMedia(ReadVideo(response));
                }

                throw new MediaProcessLostException("The media process kept asking for a larger picture region.");
            }

            private AudioBlock ReadAudio(MediaReadResponsePayload response)
            {
                // Never trust the child's shape: the region is the bound, the limits the rest.
                if (response.FrameCount <= 0 || response.Channels <= 0 || response.SampleRate <= 0 ||
                    (long)response.FrameCount * response.Channels * sizeof(float) > _output.SizeBytes)
                {
                    throw new MediaProcessLostException("The media process described a block that does not fit its region.");
                }

                var block = AudioBlock.Allocate(_context.Limits, response.SampleRate, response.Channels, response.FrameCount, MediaTime.FromMicroseconds(response.TimestampUs));
                _output.Read(0, MemoryMarshal.AsBytes(block.Samples));
                return block;
            }

            private VideoFrame ReadVideo(MediaReadResponsePayload response)
            {
                if (_video == null)
                {
                    throw new MediaProcessLostException("The media process sent a picture before a region existed.");
                }

                if (!Enum.IsDefined(typeof(VideoPixelFormat), response.PixelFormat) || response.DurationUs < 0)
                {
                    throw new MediaProcessLostException("The media process described a picture in an unknown format.");
                }

                var format = (VideoPixelFormat)response.PixelFormat;
                _context.Limits.CheckVideoDimensions(response.Width, response.Height);
                var frame = VideoFrame.Allocate(_context.Limits, format, response.Width, response.Height,
                    MediaTime.FromMicroseconds(response.TimestampUs), MediaTime.FromMicroseconds(response.DurationUs));
                if (frame.TotalBytes > _video.SizeBytes)
                {
                    frame.Dispose();
                    throw new MediaProcessLostException("The media process described a picture that does not fit its region.");
                }

                int offset = 0;
                for (int plane = 0; plane < frame.PlaneCount; plane++)
                {
                    var bytes = frame.GetPlane(plane);
                    _video.Read(offset, bytes);
                    offset += bytes.Length;
                }

                return frame;
            }

            public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
            {
                ThrowIfNotOpen();
                var response = await _client.RequestAsync<MediaSeekResponsePayload>(
                    TargetIpcMessageType.MediaSeek,
                    new MediaSeekPayload { SessionId = _sessionId, TargetUs = target.Microseconds },
                    RequestTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!response.Success)
                {
                    throw Failure(response.ErrorKind, response.ErrorMessage);
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_open && _generation == _client.Generation)
                {
                    _client.Notify(TargetIpcMessageType.MediaClose, new MediaClosePayload { SessionId = _sessionId });
                }

                _output?.Dispose();
                _video?.Dispose();
                _input?.Dispose();
                await _source.DisposeAsync().ConfigureAwait(false);
            }

            private void ThrowIfNotOpen()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_open)
                {
                    throw new InvalidOperationException("The source is not open.");
                }

                if (_generation != _client.Generation)
                {
                    throw new MediaProcessLostException("The media process this session lived in exited.");
                }
            }

            private async ValueTask<long> FillInputRegionAsync(CancellationToken cancellationToken)
            {
                var name = $"fen_media_{Environment.ProcessId}_{_sessionId}_in";
                long? known = _source.Length;
                if (known is > MediaIpcLimits.MaxInputLength)
                {
                    throw new MediaLimitExceededException("resource-bytes", known.Value, MediaIpcLimits.MaxInputLength);
                }

                if (known is { } length && length > 0)
                {
                    _input = MediaSharedMemory.Create(name, (int)length);
                    long copied = await CopyAsync(_input, (int)length, cancellationToken).ConfigureAwait(false);
                    return copied;
                }

                // Unknown length: gather first, then map exactly that much.
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    int read = await _source.ReadAsync(buffer.Length, chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MediaIpcLimits.MaxInputLength)
                    {
                        throw new MediaLimitExceededException("resource-bytes", buffer.Length + read, MediaIpcLimits.MaxInputLength);
                    }

                    buffer.Write(chunk, 0, read);
                }

                if (buffer.Length == 0)
                {
                    return 0;
                }

                _input = MediaSharedMemory.Create(name, (int)buffer.Length);
                _input.Write(0, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                return buffer.Length;
            }

            private async ValueTask<long> CopyAsync(MediaSharedMemory region, int length, CancellationToken cancellationToken)
            {
                var chunk = new byte[Math.Min(length, 1024 * 1024)];
                int position = 0;
                while (position < length)
                {
                    int read = await _source.ReadAsync(position, chunk.AsMemory(0, Math.Min(chunk.Length, length - position)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    region.Write(position, chunk.AsSpan(0, read));
                    position += read;
                }

                return position;
            }

            private static Exception Failure(string kind, string message)
            {
                message ??= string.Empty;
                return kind switch
                {
                    MediaErrorKinds.Unsupported => new MediaUnsupportedException(message),
                    MediaErrorKinds.Format => new MediaFormatException(message),
                    MediaErrorKinds.Decoder => new MediaDecoderException(message),
                    MediaErrorKinds.Limit => new MediaLimitExceededException(message),
                    _ => new MediaProcessLostException($"The media process failed the request: {message}")
                };
            }
        }
    }
}
