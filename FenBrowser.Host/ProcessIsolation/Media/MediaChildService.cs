using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host.ProcessIsolation.Targets;
using FenBrowser.Media;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Host.ProcessIsolation.Media
{
    /// <summary>
    /// The media process's side of the media IPC: one <see cref="LocalMediaDecodeSource"/>
    /// per session, reading the resource from the input region the renderer filled and
    /// writing each decoded block into the output region, or each picture into the video
    /// region, before answering. Every field of every payload is checked against
    /// <see cref="MediaIpcLimits"/> before it is used; a bad message gets a protocol error,
    /// never an exception out of the loop.
    /// </summary>
    internal sealed class MediaChildService : IDisposable
    {
        private readonly DemuxerRegistry _demuxers;
        private readonly DecoderRegistry _decoders;
        private readonly IMediaLogSink _log;
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

        public MediaChildService(DemuxerRegistry demuxers, DecoderRegistry decoders, IMediaLogSink log)
        {
            _demuxers = demuxers ?? throw new ArgumentNullException(nameof(demuxers));
            _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public int SessionCount => _sessions.Count;

        /// <summary>
        /// Handles one media message. Returns false when the message is not a media
        /// request, so the caller can go on to the generic handlers.
        /// </summary>
        public async Task<bool> HandleAsync(TargetIpcEnvelope envelope, TargetIpcMessageType messageType, Action<TargetIpcEnvelope> send, CancellationToken cancellationToken)
        {
            switch (messageType)
            {
                case TargetIpcMessageType.MediaOpen:
                    send(await OpenAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                case TargetIpcMessageType.MediaRead:
                    send(await ReadAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                case TargetIpcMessageType.MediaSeek:
                    send(await SeekAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                case TargetIpcMessageType.MediaVideoDecode:
                    send(await VideoDecodeAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                case TargetIpcMessageType.MediaClose:
                    await CloseAsync(envelope).ConfigureAwait(false);
                    return true;
                case TargetIpcMessageType.MediaDecoderOpen:
                    send(await DecoderOpenAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                case TargetIpcMessageType.MediaDecoderPush:
                    send(await DecoderPushAsync(envelope, cancellationToken).ConfigureAwait(false));
                    return true;
                default:
                    return false;
            }
        }

        private async Task<TargetIpcEnvelope> OpenAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaOpenPayload>(envelope);
            if (payload == null ||
                !TryValidateSessionId(payload.SessionId) ||
                !IsValidRegionName(payload.InputRegion) ||
                !IsValidRegionName(payload.OutputRegion) ||
                string.Equals(payload.InputRegion, payload.OutputRegion, StringComparison.Ordinal) ||
                payload.InputLength <= 0 || payload.InputLength > MediaIpcLimits.MaxInputLength ||
                payload.OutputCapacity < MediaIpcLimits.MinOutputCapacity || payload.OutputCapacity > MediaIpcLimits.MaxOutputCapacity ||
                payload.DeclaredMime?.Length > MediaIpcLimits.MaxDeclaredMimeChars)
            {
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, new MediaOpenResponsePayload
                {
                    Success = false,
                    ErrorKind = MediaErrorKinds.Protocol,
                    ErrorMessage = "invalid_open_payload"
                });
            }

            if (_sessions.ContainsKey(payload.SessionId))
            {
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, Error<MediaOpenResponsePayload>(MediaErrorKinds.Protocol, "duplicate_session"));
            }

            if (_sessions.Count >= MediaIpcLimits.MaxSessionsPerProcess)
            {
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, Error<MediaOpenResponsePayload>(MediaErrorKinds.Limit, "too_many_sessions"));
            }

            Session session;
            try
            {
                var input = MediaSharedMemory.Open(payload.InputRegion, (int)payload.InputLength);
                MediaSharedMemory output;
                try
                {
                    output = MediaSharedMemory.Open(payload.OutputRegion, payload.OutputCapacity);
                }
                catch
                {
                    input.Dispose();
                    throw;
                }

                var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, _log);
                var source = new LocalMediaDecodeSource(new SharedMemoryByteSource(input, payload.InputLength), payload.DeclaredMime, _demuxers, _decoders, context);
                session = new Session(source, input, output, context);
            }
            catch (Exception ex)
            {
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, Error<MediaOpenResponsePayload>(MediaErrorKinds.Protocol, $"region_unavailable: {ex.Message}"));
            }

            try
            {
                var info = await session.Source.OpenAsync(cancellationToken).ConfigureAwait(false);
                _sessions[payload.SessionId] = session;
                var tracks = new MediaTrackData[info.Tracks.Count];
                for (int i = 0; i < tracks.Length; i++)
                {
                    var t = info.Tracks[i];
                    tracks[i] = new MediaTrackData
                    {
                        Id = t.Id,
                        Kind = (int)t.Config.Kind,
                        Codec = (int)t.Config.Codec,
                        CodecString = t.Config.CodecString,
                        SampleRate = t.Config.SampleRate,
                        Channels = t.Config.Channels,
                        Width = t.Config.Width,
                        Height = t.Config.Height,
                        DurationUs = t.Duration.Microseconds,
                        Language = t.Language,
                        Label = t.Label,
                        IsDefault = t.IsDefault
                    };
                }

                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, new MediaOpenResponsePayload
                {
                    Success = true,
                    DurationUs = info.Duration.Microseconds,
                    IsSeekable = info.IsSeekable,
                    Tracks = tracks,
                    AudioTrackId = info.AudioTrack?.Id ?? -1,
                    VideoTrackId = info.VideoTrack?.Id ?? -1
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, Error<MediaOpenResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        /// <summary>
        /// Opens a decoder session (MSE): the renderer keeps the SourceBuffers and pushes coded
        /// frames; the child selects and holds the decoder for the configuration.
        /// </summary>
        private async Task<TargetIpcEnvelope> DecoderOpenAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaDecoderOpenPayload>(envelope);
            if (payload == null ||
                !TryValidateSessionId(payload.SessionId) ||
                !IsValidRegionName(payload.InputRegion) ||
                !IsValidRegionName(payload.OutputRegion) ||
                string.Equals(payload.InputRegion, payload.OutputRegion, StringComparison.Ordinal) ||
                payload.InputCapacity < MediaIpcLimits.MinOutputCapacity || payload.InputCapacity > MediaIpcLimits.MaxPacketRegionCapacity ||
                payload.OutputCapacity < MediaIpcLimits.MinOutputCapacity || payload.OutputCapacity > MediaIpcLimits.MaxOutputCapacity ||
                !Enum.IsDefined(typeof(MediaTrackKind), payload.Kind) || (MediaTrackKind)payload.Kind == MediaTrackKind.Text ||
                !Enum.IsDefined(typeof(MediaCodec), payload.Codec) ||
                payload.CodecString?.Length > MediaIpcLimits.MaxDeclaredMimeChars ||
                payload.ExtradataBase64?.Length > MediaIpcLimits.MaxExtradataChars ||
                payload.SampleRate < 0 || payload.Channels < 0 || payload.Width < 0 || payload.Height < 0)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(MediaErrorKinds.Protocol, "invalid_decoder_open_payload"));
            }

            if (_sessions.ContainsKey(payload.SessionId))
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(MediaErrorKinds.Protocol, "duplicate_session"));
            }

            if (_sessions.Count >= MediaIpcLimits.MaxSessionsPerProcess)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(MediaErrorKinds.Limit, "too_many_sessions"));
            }

            byte[] extradata;
            try
            {
                extradata = string.IsNullOrEmpty(payload.ExtradataBase64) ? Array.Empty<byte>() : Convert.FromBase64String(payload.ExtradataBase64);
            }
            catch (FormatException)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(MediaErrorKinds.Protocol, "invalid_extradata"));
            }

            var kind = (MediaTrackKind)payload.Kind;
            var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, _log);
            CodecConfig config;
            try
            {
                if (kind == MediaTrackKind.Video)
                {
                    context.Limits.CheckVideoDimensions(payload.Width, payload.Height);
                }

                config = new CodecConfig(kind, (MediaCodec)payload.Codec, payload.CodecString, payload.Width, payload.Height, payload.SampleRate, payload.Channels, Extradata: extradata);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(KindOf(ex), ex.Message));
            }

            MediaSharedMemory input;
            MediaSharedMemory output;
            try
            {
                input = MediaSharedMemory.Open(payload.InputRegion, payload.InputCapacity);
                try
                {
                    output = MediaSharedMemory.Open(payload.OutputRegion, payload.OutputCapacity);
                }
                catch
                {
                    input.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(MediaErrorKinds.Protocol, $"region_unavailable: {ex.Message}"));
            }

            var source = new DecoderSessionSource(_decoders, config, context, input);
            var session = new Session(source, input, output, context);
            try
            {
                var name = await source.OpenDecoderAsync(cancellationToken).ConfigureAwait(false);
                _sessions[payload.SessionId] = session;
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, new MediaDecoderOpenResponsePayload { Success = true, DecoderName = name });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return Response(envelope, TargetIpcMessageType.MediaDecoderOpenResponse, Error<MediaDecoderOpenResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        /// <summary>One coded frame from the input region into the decoder (or a drain or a reset); the outputs wait for reads.</summary>
        private async Task<TargetIpcEnvelope> DecoderPushAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaDecoderPushPayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.TryGetValue(payload.SessionId, out var session) || session.Source is not DecoderSessionSource decoder)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderPushResponse, Error<MediaDecoderPushResponsePayload>(MediaErrorKinds.Protocol, "unknown_session"));
            }

            if (payload.Length < 0 || payload.Length > session.Input.SizeBytes || payload.DurationUs < 0 ||
                (payload.Drain && payload.Reset) || ((payload.Drain || payload.Reset) && payload.Length != 0))
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderPushResponse, Error<MediaDecoderPushResponsePayload>(MediaErrorKinds.Protocol, "invalid_push_payload"));
            }

            try
            {
                session.Carry?.Dispose();
                session.Carry = null;
                int outputs;
                if (payload.Reset)
                {
                    outputs = await decoder.ResetAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (payload.Drain)
                {
                    outputs = await decoder.DrainAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    outputs = await decoder.DecodeAsync(payload, cancellationToken).ConfigureAwait(false);
                }

                return Response(envelope, TargetIpcMessageType.MediaDecoderPushResponse, new MediaDecoderPushResponsePayload { Success = true, Outputs = outputs });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaDecoderPushResponse, Error<MediaDecoderPushResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        private async Task<TargetIpcEnvelope> ReadAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaReadPayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.TryGetValue(payload.SessionId, out var session))
            {
                return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(MediaErrorKinds.Protocol, "unknown_session"));
            }

            if (payload.VideoRegion != null)
            {
                if (!IsValidRegionName(payload.VideoRegion) ||
                    string.Equals(payload.VideoRegion, session.Input.Name, StringComparison.Ordinal) ||
                    string.Equals(payload.VideoRegion, session.Output.Name, StringComparison.Ordinal) ||
                    payload.VideoCapacity < MediaIpcLimits.MinOutputCapacity || payload.VideoCapacity > MediaIpcLimits.MaxVideoCapacity)
                {
                    return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(MediaErrorKinds.Protocol, "invalid_video_region"));
                }

                if (session.Video == null || !string.Equals(session.Video.Name, payload.VideoRegion, StringComparison.Ordinal))
                {
                    try
                    {
                        var video = MediaSharedMemory.Open(payload.VideoRegion, payload.VideoCapacity);
                        session.Video?.Dispose();
                        session.Video = video;
                    }
                    catch (Exception ex)
                    {
                        return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(MediaErrorKinds.Protocol, $"region_unavailable: {ex.Message}"));
                    }
                }
            }

            try
            {
                var carried = session.Carry;
                session.Carry = null;
                var item = carried ?? await session.Source.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (item is not { } decoded)
                {
                    return Response(envelope, TargetIpcMessageType.MediaReadResponse, new MediaReadResponsePayload { Success = true, EndOfStream = true });
                }

                return Response(envelope, TargetIpcMessageType.MediaReadResponse,
                    decoded.Audio != null ? WriteAudio(session, decoded.Audio) : WriteVideo(session, decoded.Video));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        /// <summary>The region carries one block; a block longer than the region goes over in pieces.</summary>
        private static MediaReadResponsePayload WriteAudio(Session session, AudioBlock block)
        {
            int bytesPerFrame = block.Channels * sizeof(float);
            int maxFrames = session.Output.SizeBytes / bytesPerFrame;
            int frames = block.FrameCount;
            if (frames > maxFrames)
            {
                frames = maxFrames;
                var rest = AudioBlock.Allocate(session.Context.Limits, block.SampleRate, block.Channels, block.FrameCount - frames,
                    block.Timestamp + MediaTime.FromTimescale(frames, block.SampleRate));
                block.Samples[(frames * block.Channels)..].CopyTo(rest.Samples);
                session.Carry = new DecodedMedia(rest);
            }

            var samples = block.Samples[..(frames * block.Channels)];
            session.Output.Write(0, MemoryMarshal.AsBytes(samples));
            var response = new MediaReadResponsePayload
            {
                Success = true,
                Kind = 0,
                SampleRate = block.SampleRate,
                Channels = block.Channels,
                FrameCount = frames,
                TimestampUs = block.Timestamp.Microseconds
            };
            block.Dispose();
            return response;
        }

        /// <summary>
        /// The picture goes into the video region, planes end to end in its own layout. Without
        /// a region large enough the picture waits in the session and the renderer is told the size.
        /// </summary>
        private static MediaReadResponsePayload WriteVideo(Session session, VideoFrame frame)
        {
            int required = frame.TotalBytes;
            if (session.Video == null || session.Video.SizeBytes < required)
            {
                session.Carry = new DecodedMedia(frame);
                return new MediaReadResponsePayload { Success = true, Kind = 1, RegionTooSmall = true, RequiredBytes = required };
            }

            int offset = 0;
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                var bytes = frame.GetPlane(plane);
                session.Video.Write(offset, bytes);
                offset += bytes.Length;
            }

            var response = new MediaReadResponsePayload
            {
                Success = true,
                Kind = 1,
                Width = frame.Width,
                Height = frame.Height,
                PixelFormat = (int)frame.Format,
                TimestampUs = frame.Timestamp.Microseconds,
                DurationUs = frame.Duration.Microseconds
            };
            frame.Dispose();
            return response;
        }

        private async Task<TargetIpcEnvelope> SeekAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaSeekPayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.TryGetValue(payload.SessionId, out var session))
            {
                return Response(envelope, TargetIpcMessageType.MediaSeekResponse, Error<MediaSeekResponsePayload>(MediaErrorKinds.Protocol, "unknown_session"));
            }

            if (payload.TargetUs < 0 || payload.TargetUs == long.MaxValue)
            {
                return Response(envelope, TargetIpcMessageType.MediaSeekResponse, Error<MediaSeekResponsePayload>(MediaErrorKinds.Protocol, "invalid_seek_target"));
            }

            try
            {
                session.Carry?.Dispose();
                session.Carry = null;
                await session.Source.SeekAsync(MediaTime.FromMicroseconds(payload.TargetUs), cancellationToken).ConfigureAwait(false);
                return Response(envelope, TargetIpcMessageType.MediaSeekResponse, new MediaSeekResponsePayload { Success = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaSeekResponse, Error<MediaSeekResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        /// <summary>
        /// Design section 5's background policy: the element is out of sight, so the
        /// session stops decoding its video track and drops the picture it was carrying.
        /// </summary>
        private async Task<TargetIpcEnvelope> VideoDecodeAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaVideoDecodePayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.TryGetValue(payload.SessionId, out var session))
            {
                return Response(envelope, TargetIpcMessageType.MediaVideoDecodeResponse, Error<MediaVideoDecodeResponsePayload>(MediaErrorKinds.Protocol, "unknown_session"));
            }

            try
            {
                if (!payload.Enabled && session.Carry is { Video: not null })
                {
                    session.Carry.Value.Dispose();
                    session.Carry = null;
                }

                await session.Source.SetVideoDecodeEnabledAsync(payload.Enabled, cancellationToken).ConfigureAwait(false);
                return Response(envelope, TargetIpcMessageType.MediaVideoDecodeResponse, new MediaVideoDecodeResponsePayload { Success = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaVideoDecodeResponse, Error<MediaVideoDecodeResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        private async Task CloseAsync(TargetIpcEnvelope envelope)
        {
            var payload = TargetIpc.DeserializePayload<MediaClosePayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.Remove(payload.SessionId, out var session))
            {
                return;
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _sessions.Clear();
        }

        private static bool TryValidateSessionId(string sessionId) =>
            !string.IsNullOrEmpty(sessionId) && sessionId.Length <= 64 && Guid.TryParse(sessionId, out _);

        private static bool IsValidRegionName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MediaIpcLimits.MaxRegionNameChars)
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static string KindOf(Exception ex) => ex switch
        {
            MediaUnsupportedException => MediaErrorKinds.Unsupported,
            MediaFormatException => MediaErrorKinds.Format,
            MediaDecoderException => MediaErrorKinds.Decoder,
            MediaLimitExceededException => MediaErrorKinds.Limit,
            _ => MediaErrorKinds.Internal
        };

        private static T Error<T>(string kind, string message) where T : new()
        {
            if (message != null && message.Length > MediaIpcLimits.MaxErrorMessageChars)
            {
                message = message[..MediaIpcLimits.MaxErrorMessageChars];
            }

            var result = new T();
            switch (result)
            {
                case MediaOpenResponsePayload open:
                    open.ErrorKind = kind;
                    open.ErrorMessage = message;
                    break;
                case MediaReadResponsePayload read:
                    read.ErrorKind = kind;
                    read.ErrorMessage = message;
                    break;
                case MediaSeekResponsePayload seek:
                    seek.ErrorKind = kind;
                    seek.ErrorMessage = message;
                    break;
                case MediaVideoDecodeResponsePayload video:
                    video.ErrorKind = kind;
                    video.ErrorMessage = message;
                    break;
            }

            return result;
        }

        private static TargetIpcEnvelope Response<T>(TargetIpcEnvelope request, TargetIpcMessageType type, T payload) => new()
        {
            Type = type.ToString(),
            RequestId = request.RequestId,
            Payload = TargetIpc.SerializePayload(payload)
        };

        private sealed class Session : IAsyncDisposable
        {
            public Session(IMediaDecodeSource source, MediaSharedMemory input, MediaSharedMemory output, MediaPipelineContext context)
            {
                Source = source;
                Input = input;
                Output = output;
                Context = context;
            }

            public IMediaDecodeSource Source { get; }
            public MediaSharedMemory Input { get; }
            public MediaSharedMemory Output { get; }
            public MediaSharedMemory Video { get; set; }
            public MediaPipelineContext Context { get; }
            public DecodedMedia? Carry { get; set; }

            public async ValueTask DisposeAsync()
            {
                Carry?.Dispose();
                Carry = null;
                await Source.DisposeAsync().ConfigureAwait(false);
                Output.Dispose();
                Video?.Dispose();
                // The source's byte source disposed the input mapping.
            }
        }

        /// <summary>
        /// A decoder session behind the read path: the renderer pushes coded frames, the
        /// decoder's outputs queue up, and each read takes the next one (a null read means
        /// the queue is empty, not the end of a stream). The input region is the packet.
        /// </summary>
        private sealed class DecoderSessionSource : IMediaDecodeSource, IDecodeOutput<AudioBlock>, IDecodeOutput<VideoFrame>
        {
            private readonly DecoderRegistry _decoders;
            private readonly CodecConfig _config;
            private readonly MediaPipelineContext _context;
            private readonly MediaSharedMemory _input;
            private readonly Queue<DecodedMedia> _outputs = new();
            private IMediaDecoder<AudioBlock> _audio;
            private IMediaDecoder<VideoFrame> _video;

            public DecoderSessionSource(DecoderRegistry decoders, CodecConfig config, MediaPipelineContext context, MediaSharedMemory input)
            {
                _decoders = decoders;
                _config = config;
                _context = context;
                _input = input;
            }

            public async ValueTask<string> OpenDecoderAsync(CancellationToken cancellationToken)
            {
                if (_config.Kind == MediaTrackKind.Audio)
                {
                    _audio = await DecoderSelector.SelectAsync(_decoders.GetAudioCandidates(_config), _config, _context, cancellationToken).ConfigureAwait(false)
                        ?? throw new MediaUnsupportedException($"No decoder for {_config.Codec}.");
                    return _audio.Name;
                }

                _video = await DecoderSelector.SelectAsync(_decoders.GetVideoCandidates(_config), _config, _context, cancellationToken).ConfigureAwait(false)
                    ?? throw new MediaUnsupportedException($"No decoder for {_config.Codec}.");
                return _video.Name;
            }

            public async ValueTask<int> DecodeAsync(MediaDecoderPushPayload payload, CancellationToken cancellationToken)
            {
                var packet = EncodedPacket.Rent(_context.Limits, _config.Kind, 0, payload.Length,
                    payload.HasPts ? MediaTime.FromMicroseconds(payload.PtsUs) : MediaTime.FromMicroseconds(payload.DtsUs),
                    MediaTime.FromMicroseconds(payload.DtsUs), MediaTime.FromMicroseconds(payload.DurationUs), payload.IsKeyframe);
                try
                {
                    _input.Read(0, packet.Memory.Span);
                    int before = _outputs.Count;
                    if (_audio != null)
                        await _audio.DecodeAsync(packet, this, cancellationToken).ConfigureAwait(false);
                    else
                        await _video.DecodeAsync(packet, this, cancellationToken).ConfigureAwait(false);
                    return _outputs.Count - before;
                }
                finally
                {
                    packet.Dispose();
                }
            }

            public async ValueTask<int> DrainAsync(CancellationToken cancellationToken)
            {
                int before = _outputs.Count;
                if (_audio != null)
                    await _audio.DrainAsync(this, cancellationToken).ConfigureAwait(false);
                else
                    await _video.DrainAsync(this, cancellationToken).ConfigureAwait(false);
                return _outputs.Count - before;
            }

            public async ValueTask<int> ResetAsync(CancellationToken cancellationToken)
            {
                DropOutputs();
                if (_audio != null)
                    await _audio.ResetAsync(cancellationToken).ConfigureAwait(false);
                else
                    await _video.ResetAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public void Emit(AudioBlock item) => _outputs.Enqueue(new DecodedMedia(item));

            public void Emit(VideoFrame item) => _outputs.Enqueue(new DecodedMedia(item));

            ValueTask<MediaSourceInfo> IMediaDecodeSource.OpenAsync(CancellationToken cancellationToken) =>
                throw new InvalidOperationException("A decoder session has no resource to open.");

            public ValueTask<DecodedMedia?> ReadAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult(_outputs.TryDequeue(out var item) ? item : (DecodedMedia?)null);

            public ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken) => throw new InvalidOperationException("A decoder session is reset through a push.");

            public async ValueTask DisposeAsync()
            {
                DropOutputs();
                if (_audio != null)
                    await _audio.DisposeAsync().ConfigureAwait(false);
                if (_video != null)
                    await _video.DisposeAsync().ConfigureAwait(false);
                _input.Dispose();
            }

            private void DropOutputs()
            {
                while (_outputs.TryDequeue(out var item))
                    item.Dispose();
            }
        }

        /// <summary>The resource as the renderer left it in shared memory; reads never leave the region.</summary>
        private sealed class SharedMemoryByteSource : IByteSource
        {
            private readonly MediaSharedMemory _region;
            private readonly long _length;
            private bool _disposed;

            public SharedMemoryByteSource(MediaSharedMemory region, long length)
            {
                _region = region;
                _length = Math.Min(length, region.SizeBytes);
            }

            public long? Length => _length;

            public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (position < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(position));
                }

                if (position >= _length)
                {
                    return ValueTask.FromResult(0);
                }

                int count = (int)Math.Min(destination.Length, _length - position);
                _region.Read((int)position, destination.Span[..count]);
                return ValueTask.FromResult(count);
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _region.Dispose();
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
