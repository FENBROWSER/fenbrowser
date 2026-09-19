using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Mse;

/// <summary>MSE <c>AppendMode</c>.</summary>
public enum AppendMode
{
    Segments,
    Sequence,
}

/// <summary>How an append ended (MSE §3.5.4 "buffer append").</summary>
public enum AppendOutcome
{
    /// <summary>The bytes were parsed; whatever was complete is now buffered.</summary>
    Ok,

    /// <summary>The bytes broke the byte stream format: the "append error" algorithm ran (§3.5.3).</summary>
    DecodeError,
}

/// <summary>
/// The MSE <c>SourceBuffer</c> without its DOM surface (§3.5): the append state machine,
/// the segment parser loop, "initialization segment received", "coded frame processing",
/// "coded frame removal" and "coded frame eviction", plus the buffered ranges. The DOM
/// object owns the <c>updating</c> flag and its events; this runs the algorithms it names.
/// </summary>
public sealed class SourceBufferModel
{
    private readonly MediaSourceModel _owner;
    private readonly SegmentParser _parser;
    private readonly Dictionary<int, TrackBuffer> _trackBuffers = [];
    private readonly Dictionary<int, EncodedPacket> _held = [];
    private MediaTime _timestampOffset = MediaTime.Zero;
    private MediaTime? _groupStartTimestamp;
    private MediaTime _groupEndTimestamp = MediaTime.Zero;
    private bool _firstInitializationSegmentReceived;
    private bool _generateTimestamps;
    private int _configVersion;

    internal SourceBufferModel(MediaSourceModel owner, string type, SegmentParser parser, bool generateTimestamps)
    {
        _owner = owner;
        Type = type;
        _parser = parser;
        _generateTimestamps = generateTimestamps;
        Mode = generateTimestamps ? AppendMode.Sequence : AppendMode.Segments;
    }

    public string Type { get; private set; }

    /// <summary>Why the last append failed, for diagnostics.</summary>
    public string? LastParseError => Parser.LastError;

    /// <summary>§3.5.1: a media segment is partly appended (mode and timestampOffset are locked).</summary>
    public bool ParsingMediaSegment => Parser.ParsingMediaSegment;

    public AppendMode Mode { get; private set; }

    public MediaTime TimestampOffset => _timestampOffset;

    public MediaTime AppendWindowStart { get; set; } = MediaTime.Zero;

    public MediaTime AppendWindowEnd { get; set; } = MediaTime.PositiveInfinity;

    /// <summary>Every track this buffer has received an initialization segment for.</summary>
    public IReadOnlyCollection<TrackBuffer> TrackBuffers => _trackBuffers.Values;

    public bool HasTracks => _trackBuffers.Count > 0;

    public long Bytes => _trackBuffers.Values.Sum(t => t.Bytes);

    /// <summary>§3.5.4 "prepare append" / "buffer full": the byte budget for this buffer's kind of media.</summary>
    public long Quota => _trackBuffers.Values.Any(t => t.Kind == MediaTrackKind.Video) ? _owner.Limits.VideoBufferQuota : _owner.Limits.AudioBufferQuota;

    /// <summary>The intersection of the track buffered ranges (§3.1 buffered attribute), with the ended-state extension.</summary>
    public MediaTimeRanges Buffered
    {
        get
        {
            if (_trackBuffers.Count == 0)
                return MediaTimeRanges.Empty;
            MediaTime highestEnd = MediaTime.Zero;
            foreach (var track in _trackBuffers.Values)
            {
                var ranges = track.Buffered;
                if (ranges.Count > 0 && ranges.End(ranges.Count - 1) > highestEnd)
                    highestEnd = ranges.End(ranges.Count - 1);
            }

            MediaTimeRanges? intersection = null;
            foreach (var track in _trackBuffers.Values)
            {
                var ranges = track.Buffered;
                if (_owner.ReadyState == MediaSourceReadyState.Ended && ranges.Count > 0)
                    ranges = MediaSourceModel.ExtendLastRange(ranges, highestEnd); // step 5.2

                intersection = intersection is null ? ranges : intersection.Intersect(ranges);
            }

            return intersection ?? MediaTimeRanges.Empty;
        }
    }

    /// <summary>§3.5.8 "mode" setter semantics that need buffer state.</summary>
    public void SetMode(AppendMode mode)
    {
        if (_generateTimestamps && mode == AppendMode.Segments)
            throw new MseInvalidOperationException("TypeError", "The byte stream generates timestamps, so segments mode is not allowed.");
        if (mode == AppendMode.Sequence && Mode == AppendMode.Segments)
            _groupStartTimestamp = _groupEndTimestamp;
        Mode = mode;
    }

    /// <summary>§3.1 timestampOffset setter: takes effect for the next coded frame group.</summary>
    public void SetTimestampOffset(MediaTime offset)
    {
        _timestampOffset = offset;
        if (Mode == AppendMode.Sequence)
            _groupStartTimestamp = offset;
    }

    /// <summary>§3.5.2 "changeType": switches the byte stream format; the tracks stay.</summary>
    public void ChangeType(string type, SegmentParser parser, bool generateTimestamps)
    {
        Type = type;
        ResetParserState();
        if (generateTimestamps && !_generateTimestamps)
            Mode = AppendMode.Sequence;
        _generateTimestamps = generateTimestamps;
        _parserOverride = parser;
    }

    private SegmentParser? _parserOverride;

    private SegmentParser Parser => _parserOverride ?? _parser;

    /// <summary>
    /// §3.5.4 "buffer append" without the asynchrony: runs the segment parser loop over the
    /// bytes and returns how it ended. The caller has already run "prepare append" (buffer
    /// full → <see cref="EvictToFit"/>) and set the updating flag.
    /// </summary>
    public AppendOutcome Append(ReadOnlySpan<byte> bytes)
    {
        var status = Parser.Append(bytes, OnInitializationSegment, OnPacketWithDuration);
        if (status == SegmentParseStatus.Error)
        {
            _owner.Context.Log.Emit(_owner.Context.Player, MediaEventKind.Error, MediaLogLevel.Warn, $"Append error: {Parser.LastError}", ("type", Type));
            ResetParserState();
            return AppendOutcome.DecodeError;
        }

        FlushHeldPackets();
        FlushGroupStash();
        return AppendOutcome.Ok;
    }

    // In sequence mode the first frames of a new coded frame group are held back until the
    // append is parsed, so the group can start from the earliest presentation timestamp
    // among the tracks rather than from whichever track's frame the byte stream lists
    // first (what the other engines do; mediasource-sequencemode-append-buffer expects
    // -min(first video pts, first audio pts)).
    private readonly List<EncodedPacket> _groupStash = [];

    private void FlushGroupStash()
    {
        if (_groupStash.Count == 0)
            return;
        if (_groupStartTimestamp is { } groupStart)
        {
            var earliest = MediaTime.PositiveInfinity;
            foreach (var packet in _groupStash)
            {
                var pts = _generateTimestamps ? MediaTime.Zero : (packet.HasPts ? packet.Pts : packet.Dts);
                if (pts < earliest)
                    earliest = pts;
            }

            // §3.5.11 steps 1.1-1.3 for the group.
            _timestampOffset = groupStart - earliest;
            _groupEndTimestamp = groupStart;
            foreach (var t in _trackBuffers.Values)
                t.NeedRandomAccessPoint = true;
            _groupStartTimestamp = null;
        }

        var stashed = _groupStash.ToArray();
        _groupStash.Clear();
        foreach (var packet in stashed)
            ProcessPacket(packet);
    }

    private void DropGroupStash()
    {
        foreach (var packet in _groupStash)
            packet.Dispose();
        _groupStash.Clear();
    }

    /// <summary>
    /// A coded frame needs a duration (§3.5.11 step 1.3). A WebM block carries none - a
    /// TrackEntry DefaultDuration is nominal, not what the muxer timed - so, as the other
    /// engines do, a WebM frame is held until the track's next frame gives it the distance
    /// to that frame, and the last frame of a media segment takes the distance measured
    /// for the frame before it. ISO BMFF sample durations are explicit and used as they are.
    /// </summary>
    private void OnPacketWithDuration(EncodedPacket packet)
    {
        if (packet.Duration > MediaTime.Zero && Parser.FrameDurationsAreReliable)
        {
            FlushHeld(packet.TrackId);
            OnPacket(packet);
            return;
        }

        if (_held.Remove(packet.TrackId, out var held))
        {
            var distance = PresentationTime(packet) - PresentationTime(held);
            if (distance > MediaTime.Zero)
            {
                _lastDistance[packet.TrackId] = distance;
                OnPacket(WithDuration(held, distance));
            }
            else
            {
                OnPacket(held);
            }
        }

        _held[packet.TrackId] = packet;
    }

    private readonly Dictionary<int, MediaTime> _lastDistance = [];

    private void FlushHeldPackets()
    {
        foreach (var trackId in _held.Keys.ToList())
            FlushHeld(trackId);
    }

    private void FlushHeld(int trackId)
    {
        if (!_held.Remove(trackId, out var held))
            return;
        MediaTime? estimate = _lastDistance.TryGetValue(trackId, out var last) ? last
            : held.Duration > MediaTime.Zero ? held.Duration
            : _trackBuffers.TryGetValue(trackId, out var track) ? track.LastFrameDuration : null;
        OnPacket(estimate is { } duration && duration > MediaTime.Zero ? WithDuration(held, duration) : held);
    }

    private static MediaTime PresentationTime(EncodedPacket packet) => packet.HasPts ? packet.Pts : packet.Dts;

    private EncodedPacket WithDuration(EncodedPacket packet, MediaTime duration)
    {
        var kind = _trackBuffers.TryGetValue(packet.TrackId, out var track) ? track.Kind : MediaTrackKind.Video;
        var copy = EncodedPacket.Rent(_owner.MediaLimits, kind, packet.TrackId, packet.Length, PresentationTime(packet), packet.Dts, duration, packet.IsKeyframe);
        packet.Span.CopyTo(copy.Memory.Span);
        packet.Dispose();
        return copy;
    }

    /// <summary>§3.5.4 "prepare append" step 6-7: the coded frame eviction algorithm; false when the append still cannot fit.</summary>
    public bool EvictToFit(long newBytes, MediaTime currentTime)
    {
        long quota = Quota;
        if (Bytes + newBytes <= quota)
            return true;
        // Evict whole ranges that end before the current playback position, earliest first,
        // never the range holding the current position (§3.5.13 "coded frame eviction").
        var before = currentTime - MediaTime.FromSeconds(3);
        bool progress = true;
        while (Bytes + newBytes > quota && progress)
        {
            progress = false;
            foreach (var track in _trackBuffers.Values)
            {
                if (track.EvictBefore(before) > 0)
                    progress = true;
            }
        }

        return Bytes + newBytes <= quota;
    }

    /// <summary>§3.5.10 "coded frame removal" for [start, end).</summary>
    public void Remove(MediaTime start, MediaTime end)
    {
        foreach (var track in _trackBuffers.Values)
            track.RemoveRange(start, end);
        _owner.BufferedChanged();
    }

    /// <summary>§3.5.5 "reset parser state".</summary>
    public void ResetParserState()
    {
        Parser.ResetParserState();
        foreach (var held in _held.Values)
            held.Dispose();
        _held.Clear();
        DropGroupStash();
        _lastDistance.Clear();
        foreach (var track in _trackBuffers.Values)
        {
            track.LastDecodeTimestamp = null;
            track.LastFrameDuration = null;
            track.HighestEndTimestamp = null;
            track.NeedRandomAccessPoint = true;
        }

        if (Mode == AppendMode.Sequence)
            _groupStartTimestamp = _groupEndTimestamp;
    }

    /// <summary>§3.5.16 the highest end timestamp across tracks, for endOfStream's duration.</summary>
    public MediaTime? HighestBufferedEnd
    {
        get
        {
            MediaTime? highest = null;
            foreach (var track in _trackBuffers.Values)
            {
                if (track.HighestBufferedEnd is { } end && (highest is null || end > highest))
                    highest = end;
            }

            return highest;
        }
    }

    public void Clear()
    {
        foreach (var track in _trackBuffers.Values)
            track.Clear();
    }

    // -- the algorithms --------------------------------------------------------------------

    /// <summary>§3.5.8 "initialization segment received".</summary>
    private void OnInitializationSegment(DemuxerInfo info)
    {
        var tracks = info.Tracks.Where(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Video).ToList();
        if (tracks.Count == 0)
            throw new MediaFormatException("The initialization segment has no audio or video track.");

        if (_firstInitializationSegmentReceived)
        {
            // The track set must match the first initialization segment (step 3.2).
            if (tracks.Count != _trackBuffers.Count || tracks.Any(t => !_trackBuffers.TryGetValue(t.Id, out var existing) || existing.Kind != t.Kind))
                throw new MediaFormatException("A later initialization segment changed the tracks.");
        }
        else
        {
            // Track IDs are only unique within the byte stream: two SourceBuffers fed from
            // two files both carry a track 1.
            foreach (var track in tracks)
                _trackBuffers[track.Id] = new TrackBuffer(track.Id, track.Kind);
        }

        _configVersion++;
        foreach (var track in tracks)
        {
            _trackBuffers[track.Id].SetConfig(_configVersion, track.Config);
            _trackBuffers[track.Id].Info = track;
        }

        _owner.OnInitializationSegment(this, info, first: !_firstInitializationSegmentReceived);
        _firstInitializationSegmentReceived = true;
    }

    /// <summary>§3.5.11 "coded frame processing" for one frame.</summary>
    private void OnPacket(EncodedPacket packet)
    {
        if (Mode == AppendMode.Sequence && _groupStartTimestamp is not null)
        {
            _groupStash.Add(packet);
            return;
        }

        ProcessPacket(packet);
    }

    private void ProcessPacket(EncodedPacket packet)
    {
        if (!_trackBuffers.TryGetValue(packet.TrackId, out var track))
        {
            packet.Dispose();
            return;
        }

        var pts = packet.HasPts ? packet.Pts : packet.Dts;
        var dts = packet.Dts;
        var duration = packet.Duration;
        if (_generateTimestamps)
        {
            pts = MediaTime.Zero;
            dts = MediaTime.Zero;
        }

        while (true)
        {
            // 1-3. Sequence mode starts the group at the group start timestamp.
            if (Mode == AppendMode.Sequence && _groupStartTimestamp is { } groupStart)
            {
                _timestampOffset = groupStart - pts;
                _groupEndTimestamp = groupStart;
                foreach (var t in _trackBuffers.Values)
                    t.NeedRandomAccessPoint = true;
                _groupStartTimestamp = null;
            }

            // 4. Apply the timestamp offset.
            var framePts = pts + _timestampOffset;
            var frameDts = dts + _timestampOffset;
            var frameEnd = framePts + duration;

            // 6. A discontinuity ends the coded frame group.
            if (track.LastDecodeTimestamp is { } last)
            {
                var maxDuration = track.LastFrameDuration is { } lastDuration && lastDuration > duration ? lastDuration : duration;
                if (frameDts < last || frameDts - last > maxDuration + maxDuration)
                {
                    if (Mode == AppendMode.Segments)
                        _groupEndTimestamp = framePts;
                    else
                        _groupStartTimestamp = _groupEndTimestamp;
                    foreach (var t in _trackBuffers.Values)
                    {
                        t.LastDecodeTimestamp = null;
                        t.LastFrameDuration = null;
                        t.HighestEndTimestamp = null;
                        t.NeedRandomAccessPoint = true;
                    }

                    continue; // step 6.6: jump to the loop top with the new state
                }
            }

            // 8-9. The append window drops frames outside it.
            if (framePts < AppendWindowStart || frameEnd > AppendWindowEnd)
            {
                track.NeedRandomAccessPoint = true;
                packet.Dispose();
                return;
            }

            // 10. A track waiting for a random access point drops everything before one.
            if (track.NeedRandomAccessPoint)
            {
                if (!packet.IsKeyframe)
                {
                    packet.Dispose();
                    return;
                }

                track.NeedRandomAccessPoint = false;
            }

            // 14-15. Frames the new one overlaps (and their dependents) leave the buffer:
            // from the group's highest end when the frame continues the group, from the
            // frame itself when a new group starts (step 6 unset the highest end). A frame
            // inside the current group's span (a B-frame decoded after the frame it is
            // shown before, a 1-tick frame the next one spans) removes nothing.
            if (track.HighestEndTimestamp is null)
                track.RemoveOverlapping(framePts, frameEnd);
            else if (track.HighestEndTimestamp is { } highest && highest <= framePts)
                track.RemoveOverlapping(highest, frameEnd);

            // 16-20. Add the frame and advance the track state.
            var owned = EncodedPacket.Rent(_owner.MediaLimits, track.Kind, packet.TrackId, packet.Length, framePts, frameDts, duration, packet.IsKeyframe);
            packet.Span.CopyTo(owned.Memory.Span);
            packet.Dispose();
            track.Add(new CodedFrame(owned, framePts, frameDts, duration, track.ConfigVersion));
            track.LastDecodeTimestamp = frameDts;
            track.LastFrameDuration = duration;
            if (track.HighestEndTimestamp is null || frameEnd > track.HighestEndTimestamp)
                track.HighestEndTimestamp = frameEnd;
            if (frameEnd > _groupEndTimestamp)
                _groupEndTimestamp = frameEnd;
            if (_generateTimestamps)
                _timestampOffset = frameEnd;
            _owner.OnFrameAppended(frameEnd);
            return;
        }
    }
}

/// <summary>An MSE algorithm asked to throw a DOMException at the caller.</summary>
public sealed class MseInvalidOperationException : Exception
{
    public MseInvalidOperationException(string domExceptionName, string message)
        : base(message)
    {
        DomExceptionName = domExceptionName;
    }

    /// <summary>"InvalidStateError", "TypeError", "NotSupportedError", "QuotaExceededError", ...</summary>
    public string DomExceptionName { get; }
}
