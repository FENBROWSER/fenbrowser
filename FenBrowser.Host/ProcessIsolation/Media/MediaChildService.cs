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
    /// The media process's side of the media IPC: one <see cref="LocalAudioDecodeSource"/>
    /// per session, reading the resource from the input region the renderer filled and
    /// writing each decoded block into the output region before answering. Every field of
    /// every payload is checked against <see cref="MediaIpcLimits"/> before it is used; a
    /// bad message gets a protocol error, never an exception out of the loop.
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
                case TargetIpcMessageType.MediaClose:
                    await CloseAsync(envelope).ConfigureAwait(false);
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
                var source = new LocalAudioDecodeSource(new SharedMemoryByteSource(input, payload.InputLength), payload.DeclaredMime, _demuxers, _decoders, context);
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
                    AudioTrackId = info.AudioTrack.Id
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return Response(envelope, TargetIpcMessageType.MediaOpenResponse, Error<MediaOpenResponsePayload>(KindOf(ex), ex.Message));
            }
        }

        private async Task<TargetIpcEnvelope> ReadAsync(TargetIpcEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = TargetIpc.DeserializePayload<MediaReadPayload>(envelope);
            if (payload == null || !TryValidateSessionId(payload.SessionId) || !_sessions.TryGetValue(payload.SessionId, out var session))
            {
                return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(MediaErrorKinds.Protocol, "unknown_session"));
            }

            try
            {
                var block = session.Carry;
                session.Carry = null;
                block ??= await session.Source.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (block == null)
                {
                    return Response(envelope, TargetIpcMessageType.MediaReadResponse, new MediaReadResponsePayload { Success = true, EndOfStream = true });
                }

                // The region carries one block; a block longer than the region goes over in pieces.
                int bytesPerFrame = block.Channels * sizeof(float);
                int maxFrames = session.Output.SizeBytes / bytesPerFrame;
                int frames = block.FrameCount;
                if (frames > maxFrames)
                {
                    frames = maxFrames;
                    var rest = AudioBlock.Allocate(session.Context.Limits, block.SampleRate, block.Channels, block.FrameCount - frames,
                        block.Timestamp + MediaTime.FromTimescale(frames, block.SampleRate));
                    block.Samples[(frames * block.Channels)..].CopyTo(rest.Samples);
                    session.Carry = rest;
                }

                var samples = block.Samples[..(frames * block.Channels)];
                session.Output.Write(0, MemoryMarshal.AsBytes(samples));
                var response = new MediaReadResponsePayload
                {
                    Success = true,
                    SampleRate = block.SampleRate,
                    Channels = block.Channels,
                    FrameCount = frames,
                    TimestampUs = block.Timestamp.Microseconds
                };
                block.Dispose();
                return Response(envelope, TargetIpcMessageType.MediaReadResponse, response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Response(envelope, TargetIpcMessageType.MediaReadResponse, Error<MediaReadResponsePayload>(KindOf(ex), ex.Message));
            }
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
            public Session(LocalAudioDecodeSource source, MediaSharedMemory input, MediaSharedMemory output, MediaPipelineContext context)
            {
                Source = source;
                Input = input;
                Output = output;
                Context = context;
            }

            public LocalAudioDecodeSource Source { get; }
            public MediaSharedMemory Input { get; }
            public MediaSharedMemory Output { get; }
            public MediaPipelineContext Context { get; }
            public AudioBlock Carry { get; set; }

            public async ValueTask DisposeAsync()
            {
                Carry?.Dispose();
                Carry = null;
                await Source.DisposeAsync().ConfigureAwait(false);
                Output.Dispose();
                // The source's byte source disposed the input mapping.
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
