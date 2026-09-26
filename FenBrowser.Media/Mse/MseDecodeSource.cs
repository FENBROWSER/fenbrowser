using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Eme;
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
    }

    /// <summary>True after a null read that meant "nothing buffered here yet", not the end.</summary>
    public bool WaitingForData { get; private set; }

    public bool WaitingForKey { get; private set; }

    private readonly CencDecryptor _decryptor = new();
    private IMediaKeySource? _keys;

    public IReadOnlyList<(string InitDataType, byte[] InitData)> TakePendingInitializationData()
    {
        lock (_model.Gate)
            return _model.TakePendingInitializationData();
    }

    public ValueTask SetMediaKeysAsync(IMediaKeySource? keys, CancellationToken cancellationToken)
    {
        _keys = keys;
        WaitingForKey = false;
        return ValueTask.CompletedTask;
    }

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
                    // The configuration playback starts with: the first buffered frame's
                    // when segments under a later initialization segment are in already.
                    var config = (track.Frames.Count > 0 ? track.ConfigFor(track.Frames[0].ConfigVersion) : null) ?? track.Config!;
                    var info = new MediaTrackInfo(track.TrackId, config, MediaTime.Zero);
                    tracks.Add(info);
                    if (track.Kind == MediaTrackKind.Audio && audio is null)
                    {
                        audio = info;
                        _audio = new TrackCursor(track, _decoded);
                    }
                    else if (track.Kind == MediaTrackKind.Video && video is null)
                    {
                        video = info;
                        _video = new TrackCursor(track, _decoded);
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
                _audio.ConfigVersion = _audio.Track.Frames.Count > 0 ? _audio.Track.Frames[0].ConfigVersion : _audio.Track.ConfigVersion;
                _audio.Codec = audio.Config.Codec;
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
                _video.ConfigVersion = _video.Track.Frames.Count > 0 ? _video.Track.Frames[0].ConfigVersion : _video.Track.ConfigVersion;
                _video.Codec = video.Config.Codec;
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
        WaitingForKey = false;
        while (_decoded.Count == 0)
        {
            TrackCursor? cursor;
            CodedFrame? frame;
            EncodedPacket? packet = null;
            bool ended;
            bool blockedForKey = false;
            lock (_model.Gate)
            {
                ended = _model.ReadyState == MediaSourceReadyState.Ended;
                cursor = NextCursor(out frame);
                // The decoder gets its own copy, taken under the gate: the buffered frame
                // stays in the track buffer, and a remove() or an overlapping append on
                // the element's thread may dispose it the moment the gate is released.
                if (cursor is not null && frame is not null)
                {
                    packet = EncodedPacket.Rent(_context.Limits, cursor.Track.Kind, cursor.Track.TrackId, frame.Bytes, frame.Pts, frame.Dts, frame.Duration, frame.IsKeyframe);
                    frame.Packet.Span.CopyTo(packet.Memory.Span);
                    packet.Encryption = frame.Packet.Encryption;

                    // A frame whose key has not arrived is left where it is: the cursor
                    // does not move, so the same frame is read again once the licence
                    // lands, and nothing in the buffer is lost.
                    if (packet.Encryption is { } encryption && (_keys is null || !_keys.TryGetKey(encryption.KeyId, out _)))
                    {
                        packet.Dispose();
                        packet = null;
                        blockedForKey = true;
                    }
                    else
                    {
                        // The cursor moves under the gate too: it reads the frame list for
                        // its successor, which an append on the element's thread may be
                        // changing.
                        cursor.Advance(frame);
                    }
                }
            }

            if (blockedForKey)
            {
                WaitingForKey = true;
                _context.Log.Emit(_context.Player, MediaEventKind.EmeWaitingForKey, MediaLogLevel.Info,
                    "Waiting for the key a buffered frame needs.");
                return null;
            }

            if (cursor is null || frame is null || packet is null)
            {
                // Nothing to read from any track. Ended: drain the decoders and finish.
                // Otherwise the data has not been appended yet.
                if (!ended && AnyTrackStarved())
                {
                    WaitingForData = true;
                    LogStarved();
                    return null;
                }

                if (_audio is { Decoder: { } audioDecoder, Drained: false } a)
                {
                    await audioDecoder.DrainAsync(a.Output, cancellationToken).ConfigureAwait(false);
                    a.Drained = true;
                }

                if (_video is { VideoDecoder: { } videoDecoder, Drained: false } v)
                {
                    await videoDecoder.DrainAsync(v.Output, cancellationToken).ConfigureAwait(false);
                    v.Drained = true;
                }

                return _decoded.Count > 0 ? _decoded.Dequeue() : null;
            }

            // The decoder was drained at an end of stream that script then reopened with
            // another append, or the frames it was following were replaced under the
            // cursor: it restarts from a random access point.
            if (cursor.Drained || cursor.NeedsReset)
            {
                if (cursor.Decoder is { } drainedAudio)
                    await drainedAudio.ResetAsync(cancellationToken).ConfigureAwait(false);
                if (cursor.VideoDecoder is { } drainedVideo)
                    await drainedVideo.ResetAsync(cancellationToken).ConfigureAwait(false);
                cursor.Drained = false;
                cursor.NeedsReset = false;
            }

            // A new initialization segment changed the codec configuration: drain the old
            // decoder's pictures, then configure for the new one (MSE §3.5.8 step 3.3). A
            // changeType() may have switched the codec itself, which takes a new decoder.
            if (frame.ConfigVersion != cursor.ConfigVersion)
            {
                CodecConfig? config;
                lock (_model.Gate)
                    config = cursor.Track.ConfigFor(frame.ConfigVersion);
                if (config is not null)
                {
                    if (cursor.Decoder is { } ad)
                    {
                        await ad.DrainAsync(cursor.Output, cancellationToken).ConfigureAwait(false);
                        if (config.Codec == cursor.Codec)
                        {
                            await ad.ConfigureAsync(config, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await ad.DisposeAsync().ConfigureAwait(false);
                            cursor.Decoder = await DecoderSelector.SelectAsync(_decoders.GetAudioCandidates(config), config, _context, cancellationToken).ConfigureAwait(false)
                                ?? throw new MediaUnsupportedException($"No decoder for {config.Codec}.");
                        }
                    }

                    if (cursor.VideoDecoder is { } vd)
                    {
                        await vd.DrainAsync(cursor.Output, cancellationToken).ConfigureAwait(false);
                        if (config.Codec == cursor.Codec)
                        {
                            await vd.ConfigureAsync(config, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await vd.DisposeAsync().ConfigureAwait(false);
                            cursor.VideoDecoder = await DecoderSelector.SelectAsync(_decoders.GetVideoCandidates(config), config, _context, cancellationToken).ConfigureAwait(false)
                                ?? throw new MediaUnsupportedException($"No decoder for {config.Codec}.");
                        }
                    }

                    cursor.Codec = config.Codec;
                }

                cursor.ConfigVersion = frame.ConfigVersion;
            }

            using (packet)
            {
                if (packet.Encryption is { } protection)
                {
                    if (!_keys!.TryGetKey(protection.KeyId, out byte[] key))
                        throw new MediaFormatException("The key for a buffered frame went away while it was being read.");
                    try
                    {
                        if (!_decryptor.TryDecrypt(packet.Memory.Span, protection, key))
                            throw new MediaFormatException("A protected frame does not match its encryption description.");
                    }
                    finally
                    {
                        Array.Clear(key);
                    }
                }

                if (cursor.Decoder is { } audioDecoder2)
                    await audioDecoder2.DecodeAsync(packet, cursor.Output, cancellationToken).ConfigureAwait(false);
                else if (cursor.VideoDecoder is { } videoDecoder2)
                    await videoDecoder2.DecodeAsync(packet, cursor.Output, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Says once per starvation which track ran out and where, so a stall can be read off the media log.</summary>
    private void LogStarved()
    {
        string state;
        lock (_model.Gate)
        {
            state = string.Join("; ", new[] { _audio, _video }.Where(c => c is not null).Select(c =>
                $"{c!.Track.Kind} frames={c.Track.Frames.Count} buffered={c.Track.Buffered} last={c.LastDts?.ToString() ?? "-"} seek={c.SeekTarget?.ToString() ?? "-"} next={(c.Peek() is { } f ? f.Dts.ToString() : "none")}"));
        }

        if (state == _lastStarvedState)
            return;
        _lastStarvedState = state;
        _context.Log.Emit(_context.Player, MediaEventKind.Stall, MediaLogLevel.Debug, "MediaSource starved: " + state, ("state", state));
    }

    private string? _lastStarvedState;

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

    private sealed class TrackCursor
    {
        public TrackCursor(TrackBuffer track, Queue<DecodedMedia> decoded)
        {
            Track = track;
            Output = new DecodeOutput(decoded);
            _generation = track.Generation;
        }

        private int _generation;
        private CodedFrame? _last;
        private CodedFrame? _successor;

        public TrackBuffer Track { get; }

        /// <summary>Where this track's decoder emits; it drops what a restart decodes again.</summary>
        public DecodeOutput Output { get; }

        public IMediaDecoder<AudioBlock>? Decoder { get; set; }

        public IMediaDecoder<VideoFrame>? VideoDecoder { get; set; }

        public int ConfigVersion { get; set; }

        /// <summary>The codec the current decoder was selected for.</summary>
        public MediaCodec Codec { get; set; }

        /// <summary>The decode timestamp of the last frame handed to the decoder; the next frame is the first after it.</summary>
        public MediaTime? LastDts { get; set; }

        /// <summary>A pending seek: the next frame is the random access point for this target, once it is buffered.</summary>
        public MediaTime? SeekTarget { get; set; }

        public bool Drained { get; set; }

        /// <summary>The decoder must be flushed before the next frame goes in.</summary>
        public bool NeedsReset { get; set; }

        /// <summary>
        /// Where the next frame is: right after the last one handed over while it is still
        /// buffered, else after its decode timestamp (a later group may start below it, so
        /// the frame itself is the anchor while it lasts).
        /// </summary>
        private int NextIndex()
        {
            if (_last is not null && Track.IndexOf(_last) is var at && at >= 0)
                return at + 1;
            return LastDts is { } last ? Track.IndexAfter(last) : 0;
        }

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

            index = NextIndex();
            if (LastDts is { } decodedUpTo && Track.Generation != _generation)
            {
                // Frames were removed since the cursor moved. If the frame it was going to
                // read next is gone and what took its place cannot be decoded on its own,
                // an overlapping append replaced the coded frame group under the cursor
                // (MSE §3.5.11 steps 14-15): restart from the new group's random access
                // point at or before the position, flush what the decoder still holds of
                // the old group, and drop what it emits again up to the last picture out.
                _generation = Track.Generation;
                var candidate = index < frames.Count ? frames[index] : null;
                if (candidate is not null && !candidate.IsKeyframe && !ReferenceEquals(candidate, _successor))
                {
                    SeekTarget = Output.LastEmitted ?? decodedUpTo;
                    Output.DropUpTo = Output.LastEmitted;
                    NeedsReset = true;
                    return Peek();
                }
            }

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

        /// <summary>The frame from <see cref="Peek"/> was taken; the cursor moves past it (under the gate).</summary>
        public void Advance(CodedFrame frame)
        {
            SeekTarget = null;
            LastDts = frame.Dts;
            _last = frame;
            _generation = Track.Generation;
            var frames = Track.Frames;
            int next = NextIndex();
            _successor = next < frames.Count ? frames[next] : null;
        }

        public void SeekTo(MediaTime target)
        {
            LastDts = null;
            _last = null;
            _successor = null;
            Output.DropUpTo = null;
            Output.LastEmitted = null;
            SeekTarget = target;
        }
    }

    /// <summary>Queues decoded audio and pictures for <see cref="ReadAsync"/>, less those a restart decoded a second time.</summary>
    private sealed class DecodeOutput(Queue<DecodedMedia> queue) : IDecodeOutput<AudioBlock>, IDecodeOutput<VideoFrame>
    {
        /// <summary>Items timed at or before this were emitted before a restart; they are dropped until one passes it.</summary>
        public MediaTime? DropUpTo { get; set; }

        /// <summary>The greatest timestamp queued since the last seek.</summary>
        public MediaTime? LastEmitted { get; set; }

        public void Emit(AudioBlock item) => Enqueue(new DecodedMedia(item));

        public void Emit(VideoFrame item) => Enqueue(new DecodedMedia(item));

        private void Enqueue(DecodedMedia item)
        {
            if (DropUpTo is { } upTo)
            {
                if (item.Timestamp <= upTo)
                {
                    item.Dispose();
                    return;
                }

                DropUpTo = null;
            }

            if (LastEmitted is null || item.Timestamp > LastEmitted)
                LastEmitted = item.Timestamp;
            queue.Enqueue(item);
        }
    }
}
