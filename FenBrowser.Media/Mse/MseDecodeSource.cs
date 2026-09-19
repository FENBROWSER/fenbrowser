using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Mse;

/// <summary>
/// The playback side of a MediaSource: an <see cref="IMediaDecodeSource"/> that reads coded
/// frames from the source buffers' track buffers in decode order and decodes them with the
/// registries' decoders, switching decoder configuration when a new initialization segment
/// changed a track's codec configuration. When the next frame is not buffered yet and the
/// source is not ended, <see cref="ReadAsync"/> returns null with <see cref="WaitingForData"/>
/// set; the player treats that as a stall rather than the end of the stream.
/// </summary>
public sealed class MseDecodeSource : IMediaDecodeSource
{
    private readonly MediaSourceModel _model;
    private readonly DecoderRegistry _decoders;
    private readonly MediaPipelineContext _context;
    private readonly Queue<DecodedMedia> _decoded = new();
    private readonly AudioOutput _audioOutput;
    private readonly VideoOutput _videoOutput;
    private TrackCursor? _audio;
    private TrackCursor? _video;
    private bool _open;

    public MseDecodeSource(MediaSourceModel model, DecoderRegistry decoders, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(context);
        _model = model;
        _decoders = decoders;
        _context = context;
        _audioOutput = new AudioOutput(_decoded);
        _videoOutput = new VideoOutput(_decoded);
    }

    /// <summary>True after a null read that meant "nothing buffered here yet", not the end.</summary>
    public bool WaitingForData { get; private set; }

    public async ValueTask<MediaSourceInfo> OpenAsync(CancellationToken cancellationToken)
    {
        if (_open)
            throw new InvalidOperationException("The source is already open.");
        // HTML §4.8.11.5: with a MediaSource the resource is "available" once the first
        // initialization segment is in (MSE §3.5.8 step 6-8 fire loadedmetadata).
        var arrived = await _model.FirstInitializationSegment.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!arrived)
            throw new MediaUnsupportedException("The MediaSource was detached before an initialization segment arrived.");

        MediaTrackInfo? audio = null;
        MediaTrackInfo? video = null;
        var tracks = new List<MediaTrackInfo>();
        MediaTime duration;
        lock (_model.Gate)
        {
            foreach (var buffer in _model.SourceBuffers)
            {
                foreach (var track in buffer.TrackBuffers)
                {
                    var info = new MediaTrackInfo(track.TrackId, track.Config!, MediaTime.Zero);
                    tracks.Add(info);
                    if (track.Kind == MediaTrackKind.Audio && audio is null)
                    {
                        audio = info;
                        _audio = new TrackCursor(track);
                    }
                    else if (track.Kind == MediaTrackKind.Video && video is null)
                    {
                        video = info;
                        _video = new TrackCursor(track);
                    }
                }
            }

            duration = _model.Duration ?? MediaTime.PositiveInfinity;
        }

        if (audio is null && video is null)
            throw new MediaUnsupportedException("The MediaSource has no audio or video track.");

        if (_audio is not null)
        {
            _audio.Decoder = await DecoderSelector.SelectAsync(_decoders.GetAudioCandidates(audio!.Config), audio.Config, _context, cancellationToken).ConfigureAwait(false);
            if (_audio.Decoder is null)
            {
                _audio = null;
                audio = null;
            }
            else
            {
                _audio.ConfigVersion = _audio.Track.ConfigVersion;
            }
        }

        if (_video is not null)
        {
            _video.VideoDecoder = await DecoderSelector.SelectAsync(_decoders.GetVideoCandidates(video!.Config), video.Config, _context, cancellationToken).ConfigureAwait(false);
            if (_video.VideoDecoder is null)
            {
                _video = null;
                video = null;
            }
            else
            {
                _video.ConfigVersion = _video.Track.ConfigVersion;
            }
        }

        if (audio is null && video is null)
            throw new MediaUnsupportedException($"No decoder for {string.Join(", ", tracks.Select(t => t.Config.Codec))}.");

        _open = true;
        return new MediaSourceInfo(tracks, audio, video, duration, IsSeekable: true);
    }

    public async ValueTask<DecodedMedia?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_open)
            throw new InvalidOperationException("The source is not open.");
        WaitingForData = false;
        while (_decoded.Count == 0)
        {
            TrackCursor? cursor;
            CodedFrame? frame;
            bool ended;
            lock (_model.Gate)
            {
                ended = _model.ReadyState == MediaSourceReadyState.Ended;
                cursor = NextCursor(out frame);
            }

            if (cursor is null || frame is null)
            {
                // Nothing to read from any track. Ended: drain the decoders and finish.
                // Otherwise the data has not been appended yet.
                if (!ended && AnyTrackStarved())
                {
                    WaitingForData = true;
                    return null;
                }

                if (_audio is { Decoder: { } audioDecoder, Drained: false } a)
                {
                    await audioDecoder.DrainAsync(_audioOutput, cancellationToken).ConfigureAwait(false);
                    a.Drained = true;
                }

                if (_video is { VideoDecoder: { } videoDecoder, Drained: false } v)
                {
                    await videoDecoder.DrainAsync(_videoOutput, cancellationToken).ConfigureAwait(false);
                    v.Drained = true;
                }

                return _decoded.Count > 0 ? _decoded.Dequeue() : null;
            }

            // The decoder was drained at an end of stream that script then reopened with
            // another append: it restarts from the next random access point.
            if (cursor.Drained)
            {
                if (cursor.Decoder is { } drainedAudio)
                    await drainedAudio.ResetAsync(cancellationToken).ConfigureAwait(false);
                if (cursor.VideoDecoder is { } drainedVideo)
                    await drainedVideo.ResetAsync(cancellationToken).ConfigureAwait(false);
                cursor.Drained = false;
            }

            // A new initialization segment changed the codec configuration: drain the old
            // decoder's pictures, then configure for the new one (MSE §3.5.8 step 3.3).
            if (frame.ConfigVersion != cursor.ConfigVersion)
            {
                CodecConfig? config;
                lock (_model.Gate)
                    config = cursor.Track.Config;
                if (config is not null)
                {
                    if (cursor.Decoder is { } ad)
                    {
                        await ad.DrainAsync(_audioOutput, cancellationToken).ConfigureAwait(false);
                        await ad.ConfigureAsync(config, cancellationToken).ConfigureAwait(false);
                    }

                    if (cursor.VideoDecoder is { } vd)
                    {
                        await vd.DrainAsync(_videoOutput, cancellationToken).ConfigureAwait(false);
                        await vd.ConfigureAsync(config, cancellationToken).ConfigureAwait(false);
                    }
                }

                cursor.ConfigVersion = frame.ConfigVersion;
            }

            // The decoder gets its own copy: the buffered frame stays in the track buffer.
            var packet = EncodedPacket.Rent(_context.Limits, cursor.Track.Kind, cursor.Track.TrackId, frame.Bytes, frame.Pts, frame.Dts, frame.Duration, frame.IsKeyframe);
            frame.Packet.Span.CopyTo(packet.Memory.Span);
            cursor.Advance(frame);
            using (packet)
            {
                if (cursor.Decoder is { } audioDecoder2)
                    await audioDecoder2.DecodeAsync(packet, _audioOutput, cancellationToken).ConfigureAwait(false);
                else if (cursor.VideoDecoder is { } videoDecoder2)
                    await videoDecoder2.DecodeAsync(packet, _videoOutput, cancellationToken).ConfigureAwait(false);
            }
        }

        return _decoded.Dequeue();
    }

    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        if (!_open)
            throw new InvalidOperationException("The source is not open.");
        DropDecoded();
        WaitingForData = false;
        if (_audio is { } audio)
        {
            if (audio.Decoder is { } decoder)
                await decoder.ResetAsync(cancellationToken).ConfigureAwait(false);
            audio.SeekTo(target);
        }

        if (_video is { } video)
        {
            if (video.VideoDecoder is { } decoder)
                await decoder.ResetAsync(cancellationToken).ConfigureAwait(false);
            video.SeekTo(target);
        }
    }

    public async ValueTask DisposeAsync()
    {
        DropDecoded();
        if (_audio?.Decoder is { } audio)
            await audio.DisposeAsync().ConfigureAwait(false);
        if (_video?.VideoDecoder is { } video)
            await video.DisposeAsync().ConfigureAwait(false);
        _audio = null;
        _video = null;
    }

    /// <summary>The cursor whose next frame comes first in decode order, with that frame (under the gate).</summary>
    private TrackCursor? NextCursor(out CodedFrame? frame)
    {
        frame = null;
        TrackCursor? best = null;
        foreach (var cursor in new[] { _audio, _video })
        {
            if (cursor is null)
                continue;
            var next = cursor.Peek();
            if (next is null)
                continue;
            if (frame is null || next.Dts < frame.Dts)
            {
                frame = next;
                best = cursor;
            }
        }

        return best;
    }

    private bool AnyTrackStarved()
    {
        lock (_model.Gate)
        {
            foreach (var cursor in new[] { _audio, _video })
            {
                if (cursor is not null && cursor.Peek() is null)
                    return true;
            }
        }

        return false;
    }

    private void DropDecoded()
    {
        while (_decoded.TryDequeue(out var item))
            item.Dispose();
    }

    private sealed class TrackCursor(TrackBuffer track)
    {
        public TrackBuffer Track { get; } = track;

        public IMediaDecoder<AudioBlock>? Decoder { get; set; }

        public IMediaDecoder<VideoFrame>? VideoDecoder { get; set; }

        public int ConfigVersion { get; set; }

        /// <summary>The decode timestamp of the last frame handed to the decoder; the next frame is the first after it.</summary>
        public MediaTime? LastDts { get; set; }

        /// <summary>A pending seek: the next frame is the random access point for this target, once it is buffered.</summary>
        public MediaTime? SeekTarget { get; set; }

        public bool Drained { get; set; }

        /// <summary>The next frame in decode order, or null when nothing is buffered past the cursor (under the gate).</summary>
        public CodedFrame? Peek()
        {
            var frames = Track.Frames;
            int index;
            if (SeekTarget is { } target)
            {
                index = Track.SeekIndex(target);
                if (index >= frames.Count)
                    return null;
                return frames[index];
            }

            index = LastDts is { } last ? Track.IndexAfter(last) : 0;
            if (index >= frames.Count)
                return null;
            var frame = frames[index];
            // After a removal, or a drain, the next frame may not be decodable on its own:
            // start at the next random access point instead.
            if (!frame.IsKeyframe && (Drained || (LastDts is { } previous && frame.Dts - previous > frame.Duration + frame.Duration + MediaTime.FromSeconds(0.1))))
            {
                while (index < frames.Count && !frames[index].IsKeyframe)
                    index++;
                if (index >= frames.Count)
                    return null;
                frame = frames[index];
            }

            return frame;
        }

        /// <summary>The frame from <see cref="Peek"/> was taken; the cursor moves past it.</summary>
        public void Advance(CodedFrame frame)
        {
            SeekTarget = null;
            LastDts = frame.Dts;
        }

        public void SeekTo(MediaTime target)
        {
            LastDts = null;
            SeekTarget = target;
        }
    }

    private sealed class AudioOutput(Queue<DecodedMedia> queue) : IDecodeOutput<AudioBlock>
    {
        public void Emit(AudioBlock item) => queue.Enqueue(new DecodedMedia(item));
    }

    private sealed class VideoOutput(Queue<DecodedMedia> queue) : IDecodeOutput<VideoFrame>
    {
        public void Emit(VideoFrame item) => queue.Enqueue(new DecodedMedia(item));
    }
}
