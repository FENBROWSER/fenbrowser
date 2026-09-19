using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host.ProcessIsolation.Targets;
using FenBrowser.Media;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Host.ProcessIsolation.Media
{
    /// <summary>
    /// Decoders that live in the media process (design §2.2, ADR-0004): the renderer keeps
    /// the MSE SourceBuffers and hands coded frames over one at a time, the child holds the
    /// codec. A packet goes in through the session's input region; decoded audio and
    /// pictures come back through the read path, as for a resource session.
    /// </summary>
    public sealed partial class MediaProcessClient
    {
        /// <summary>
        /// A registry whose decoders run in the media process, answering support from the
        /// renderer's own registry (the child was built with the same one).
        /// </summary>
        public DecoderRegistry CreateRemoteDecoders(DecoderRegistry local)
        {
            ArgumentNullException.ThrowIfNull(local);
            var registry = new DecoderRegistry();
            registry.Register(new RemoteDecoderFactory<AudioBlock>(this, local));
            registry.Register(new RemoteDecoderFactory<VideoFrame>(this, local));
            return registry;
        }

        private sealed class RemoteDecoderFactory<T> : IDecoderFactory<T>
            where T : class, IDisposable
        {
            private readonly MediaProcessClient _client;
            private readonly DecoderRegistry _local;

            public RemoteDecoderFactory(MediaProcessClient client, DecoderRegistry local)
            {
                _client = client;
                _local = local;
            }

            public string Name => typeof(T) == typeof(AudioBlock) ? "media-process-audio" : "media-process-video";

            public bool IsHardwareAccelerated => false;

            public int Priority => 100;

            public DecoderSupport Supports(CodecConfig config) =>
                config.Kind == (typeof(T) == typeof(AudioBlock) ? MediaTrackKind.Audio : MediaTrackKind.Video) ? _local.GetSupport(config) : DecoderSupport.Unsupported;

            public IMediaDecoder<T> Create(MediaPipelineContext context) => new RemoteDecoder<T>(_client, context);
        }

        private sealed class RemoteDecoder<T> : IMediaDecoder<T>
            where T : class, IDisposable
        {
            private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

            private readonly MediaProcessClient _client;
            private readonly MediaPipelineContext _context;
            private readonly bool _isAudio = typeof(T) == typeof(AudioBlock);
            private string _sessionId;
            private MediaSharedMemory _input;
            private MediaSharedMemory _output;
            private MediaSharedMemory _video;
            private int _videoRegions;
            private int _generation;
            private bool _open;
            private bool _disposed;

            public RemoteDecoder(MediaProcessClient client, MediaPipelineContext context)
            {
                _client = client;
                _context = context;
            }

            public string Name { get; private set; } = "media-process";

            public async ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(config);
                ObjectDisposedException.ThrowIf(_disposed, this);
                await CloseSessionAsync().ConfigureAwait(false);

                _sessionId = Guid.NewGuid().ToString("N");
                int packetCapacity = _isAudio ? _context.Limits.MaxAudioPacketBytes : MediaIpcLimits.MaxPacketRegionCapacity;
                packetCapacity = Math.Clamp(packetCapacity, MediaIpcLimits.MinOutputCapacity, MediaIpcLimits.MaxPacketRegionCapacity);
                _input = MediaSharedMemory.Create($"fen_media_{Environment.ProcessId}_{_sessionId}_pkt", packetCapacity);
                _output = MediaSharedMemory.Create($"fen_media_{Environment.ProcessId}_{_sessionId}_out", MediaIpcLimits.DefaultOutputCapacity);
                if (!_isAudio)
                {
                    EnsureVideoRegion(VideoFrame.LayoutSize(VideoPixelFormat.I420, Math.Max(16, config.Width), Math.Max(16, config.Height)));
                }

                var response = await _client.RequestAsync<MediaDecoderOpenResponsePayload>(
                    TargetIpcMessageType.MediaDecoderOpen,
                    new MediaDecoderOpenPayload
                    {
                        SessionId = _sessionId,
                        InputRegion = _input.Name,
                        InputCapacity = _input.SizeBytes,
                        OutputRegion = _output.Name,
                        OutputCapacity = _output.SizeBytes,
                        Kind = (int)config.Kind,
                        Codec = (int)config.Codec,
                        CodecString = config.CodecString,
                        SampleRate = config.SampleRate,
                        Channels = config.Channels,
                        Width = config.Width,
                        Height = config.Height,
                        ExtradataBase64 = config.Extradata.IsEmpty ? null : Convert.ToBase64String(config.Extradata.Span)
                    },
                    OpenTimeout,
                    cancellationToken).ConfigureAwait(false);
                _generation = _client.Generation;
                if (!response.Success)
                {
                    throw new MediaDecoderException($"The media process could not open a decoder: {response.ErrorKind}: {response.ErrorMessage}");
                }

                Name = "media-process/" + (response.DecoderName ?? "decoder");
                _open = true;
            }

            public async ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<T> output, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(packet);
                ArgumentNullException.ThrowIfNull(output);
                ThrowIfNotOpen();
                if (packet.Length > _input.SizeBytes)
                {
                    throw new MediaLimitExceededException("MaxPacketRegionCapacity", packet.Length, _input.SizeBytes);
                }

                _input.Write(0, packet.Span);
                int outputs = await PushAsync(new MediaDecoderPushPayload
                {
                    SessionId = _sessionId,
                    Length = packet.Length,
                    HasPts = packet.HasPts,
                    PtsUs = packet.HasPts ? packet.Pts.Microseconds : 0,
                    DtsUs = packet.Dts.Microseconds,
                    DurationUs = Math.Max(0, packet.Duration.Microseconds),
                    IsKeyframe = packet.IsKeyframe
                }, cancellationToken).ConfigureAwait(false);
                await PullAsync(outputs, output, cancellationToken).ConfigureAwait(false);
            }

            public async ValueTask DrainAsync(IDecodeOutput<T> output, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(output);
                ThrowIfNotOpen();
                int outputs = await PushAsync(new MediaDecoderPushPayload { SessionId = _sessionId, Drain = true }, cancellationToken).ConfigureAwait(false);
                await PullAsync(outputs, output, cancellationToken).ConfigureAwait(false);
            }

            public async ValueTask ResetAsync(CancellationToken cancellationToken)
            {
                if (!_open)
                {
                    return;
                }

                ThrowIfNotOpen();
                await PushAsync(new MediaDecoderPushPayload { SessionId = _sessionId, Reset = true }, cancellationToken).ConfigureAwait(false);
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await CloseSessionAsync().ConfigureAwait(false);
            }

            private async ValueTask<int> PushAsync(MediaDecoderPushPayload payload, CancellationToken cancellationToken)
            {
                var response = await _client.RequestAsync<MediaDecoderPushResponsePayload>(
                    TargetIpcMessageType.MediaDecoderPush, payload, RequestTimeout, cancellationToken).ConfigureAwait(false);
                if (!response.Success)
                {
                    throw Failure(response.ErrorKind, response.ErrorMessage);
                }

                // The child says how many items wait; a rogue count is bounded by what the
                // reads then bring back (each read is its own request).
                return Math.Clamp(response.Outputs, 0, 4096);
            }

            /// <summary>Reads the decoded items the push produced; a read that comes back empty ends the pull early.</summary>
            private async ValueTask PullAsync(int count, IDecodeOutput<T> output, CancellationToken cancellationToken)
            {
                for (int i = 0; i < count; i++)
                {
                    bool got = false;
                    for (int attempt = 0; attempt < 3 && !got; attempt++)
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
                            return;
                        }

                        if (_isAudio)
                        {
                            if (response.Kind != 0)
                            {
                                throw new MediaProcessLostException("The media process answered an audio decoder with a picture.");
                            }

                            output.Emit((T)(object)ReadAudio(response));
                            got = true;
                        }
                        else
                        {
                            if (response.Kind != 1)
                            {
                                throw new MediaProcessLostException("The media process answered a video decoder with audio.");
                            }

                            if (response.RegionTooSmall)
                            {
                                EnsureVideoRegion(response.RequiredBytes);
                                continue;
                            }

                            output.Emit((T)(object)ReadVideo(response));
                            got = true;
                        }
                    }

                    if (!got)
                    {
                        throw new MediaProcessLostException("The media process kept asking for a larger picture region.");
                    }
                }
            }

            private AudioBlock ReadAudio(MediaReadResponsePayload response)
            {
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

            private void ThrowIfNotOpen()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_open)
                {
                    throw new InvalidOperationException("ConfigureAsync has not run.");
                }

                if (_generation != _client.Generation)
                {
                    throw new MediaProcessLostException("The media process this decoder lived in exited.");
                }
            }

            private ValueTask CloseSessionAsync()
            {
                if (_open && _generation == _client.Generation)
                {
                    _client.Notify(TargetIpcMessageType.MediaClose, new MediaClosePayload { SessionId = _sessionId });
                }

                _open = false;
                _input?.Dispose();
                _output?.Dispose();
                _video?.Dispose();
                _input = null;
                _output = null;
                _video = null;
                _videoRegions = 0;
                return ValueTask.CompletedTask;
            }

            private static Exception Failure(string kind, string message) => kind switch
            {
                MediaErrorKinds.Limit => new MediaLimitExceededException(message ?? "limit"),
                MediaErrorKinds.Unsupported => new MediaUnsupportedException(message ?? "unsupported"),
                MediaErrorKinds.Format => new MediaFormatException(message ?? "format"),
                MediaErrorKinds.Decoder => new MediaDecoderException(message ?? "decoder"),
                _ => new MediaProcessLostException(message ?? kind ?? "unknown"),
            };
        }
    }
}
