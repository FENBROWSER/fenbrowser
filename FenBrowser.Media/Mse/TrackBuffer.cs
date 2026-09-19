using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Mse;

/// <summary>One coded frame in a track buffer: the packet (owned by the buffer) with the timing the coded frame processing gave it.</summary>
public sealed class CodedFrame
{
    public CodedFrame(EncodedPacket packet, MediaTime pts, MediaTime dts, MediaTime duration, int configVersion)
    {
        Packet = packet;
        Pts = pts;
        Dts = dts;
        Duration = duration;
        ConfigVersion = configVersion;
    }

    public EncodedPacket Packet { get; }

    /// <summary>Presentation timestamp after the timestamp offset.</summary>
    public MediaTime Pts { get; }

    /// <summary>Decode timestamp after the timestamp offset.</summary>
    public MediaTime Dts { get; }

    public MediaTime Duration { get; }

    public MediaTime End => Pts + Duration;

    public bool IsKeyframe => Packet.IsKeyframe;

    public int Bytes => Packet.Length;

    /// <summary>Which initialization segment's codec configuration decodes this frame.</summary>
    public int ConfigVersion { get; }
}

/// <summary>
/// The MSE "track buffer" (§3.5.11): coded frames in decode order with the per-track
/// state the coded frame processing algorithm keeps, and the buffered ranges they cover.
/// </summary>
public sealed class TrackBuffer
{
    private readonly List<CodedFrame> _frames = [];

    public TrackBuffer(int trackId, MediaTrackKind kind)
    {
        TrackId = trackId;
        Kind = kind;
    }

    public int TrackId { get; }

    public MediaTrackKind Kind { get; }

    private readonly Dictionary<int, CodecConfig> _configs = [];

    /// <summary>The track as the latest initialization segment described it (language, label).</summary>
    public MediaTrackInfo? Info { get; set; }

    /// <summary>The codec configuration in force for new frames.</summary>
    public CodecConfig? Config { get; private set; }

    public int ConfigVersion { get; private set; }

    /// <summary>A new initialization segment: frames appended from now on decode with <paramref name="config"/>.</summary>
    public void SetConfig(int version, CodecConfig config)
    {
        Config = config;
        ConfigVersion = version;
        _configs[version] = config;
    }

    /// <summary>The configuration a frame of that <see cref="CodedFrame.ConfigVersion"/> decodes with.</summary>
    public CodecConfig? ConfigFor(int version) => _configs.GetValueOrDefault(version);

    public MediaTime? LastDecodeTimestamp { get; set; }

    public MediaTime? LastFrameDuration { get; set; }

    public MediaTime? HighestEndTimestamp { get; set; }

    public bool NeedRandomAccessPoint { get; set; } = true;

    public IReadOnlyList<CodedFrame> Frames => _frames;

    /// <summary>Counts removals: a reader that saw one value knows the frames it was following may be gone.</summary>
    public int Generation { get; private set; }

    public long Bytes { get; private set; }

    public MediaTimeRanges Buffered { get; private set; } = MediaTimeRanges.Empty;

    /// <summary>The frame with the largest end time, or null when empty.</summary>
    public MediaTime? HighestBufferedEnd => _frames.Count == 0 ? null : _frames.Max(f => f.End);

    /// <summary>
    /// Adds a frame in decode order: after every frame with a decode time at or before its
    /// own, and after every frame it is presented after. The second rule keeps a new coded
    /// frame group behind the one before it when its first decode timestamps dip below
    /// that group's last (H.264 with B-frames appended after WebM, say): overlap removal
    /// went by presentation time, so what is left in front is presented in front.
    /// </summary>
    public void Add(CodedFrame frame)
    {
        int at = _frames.Count;
        while (at > 0 && _frames[at - 1].Dts > frame.Dts && _frames[at - 1].Pts > frame.Pts)
            at--;
        _frames.Insert(at, frame);
        Bytes += frame.Bytes;
        RecomputeBuffered();
    }

    /// <summary>
    /// MSE §3.5.11 steps 14-15: removes the frames whose presentation timestamp lies in
    /// [<paramref name="start"/>, <paramref name="end"/>) and every frame that depended on
    /// them (the frames after each removed one up to the next random access point).
    /// </summary>
    public void RemoveOverlapping(MediaTime start, MediaTime end)
    {
        if (_frames.Count == 0 || end <= start)
            return;
        var removed = new HashSet<CodedFrame>();
        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            if (frame.Pts >= start && frame.Pts < end)
                removed.Add(frame);
        }

        if (removed.Count == 0)
            return;
        RemoveDependents(removed);
        Remove(removed);
    }

    /// <summary>
    /// MSE §3.5.10 "coded frame removal": frames in the removal range [start, end), where a
    /// frame whose start lies inside is removed and, when the range ends inside a frame, that
    /// frame goes too; dependents follow.
    /// </summary>
    public void RemoveRange(MediaTime start, MediaTime end)
    {
        if (_frames.Count == 0)
            return;
        var removed = new HashSet<CodedFrame>();
        foreach (var frame in _frames)
        {
            // The spec removes frames with start >= start and start < end, plus those whose
            // interval contains end (a frame straddling the removal end); a frame that only
            // starts before the range and ends inside it stays.
            if ((frame.Pts >= start && frame.Pts < end) || (frame.Pts < end && frame.End > end && frame.Pts >= start))
                removed.Add(frame);
        }

        if (removed.Count == 0)
            return;
        RemoveDependents(removed);
        Remove(removed);
        LastDecodeTimestamp = null;
        LastFrameDuration = null;
        HighestEndTimestamp = null;
        NeedRandomAccessPoint = true;
    }

    /// <summary>Removes the frames of the earliest whole buffered range that ends before <paramref name="before"/>; returns the bytes freed.</summary>
    public long EvictBefore(MediaTime before)
    {
        if (_frames.Count == 0 || Buffered.Count == 0)
            return 0;
        var first = Buffered.Start(0);
        var firstEnd = Buffered.End(0);
        if (firstEnd > before)
            return 0;
        long freed = Bytes;
        RemoveRange(first, firstEnd);
        return freed - Bytes;
    }

    public void Clear()
    {
        foreach (var frame in _frames)
            frame.Packet.Dispose();
        _frames.Clear();
        Generation++;
        Bytes = 0;
        Buffered = MediaTimeRanges.Empty;
        LastDecodeTimestamp = null;
        LastFrameDuration = null;
        HighestEndTimestamp = null;
        NeedRandomAccessPoint = true;
    }

    /// <summary>Index of <paramref name="frame"/> in decode order, or -1 once it was removed.</summary>
    public int IndexOf(CodedFrame frame) => _frames.IndexOf(frame);

    /// <summary>Index of the first frame after <paramref name="dts"/> in decode order, or the count when there is none.</summary>
    public int IndexAfter(MediaTime dts)
    {
        for (int i = 0; i < _frames.Count; i++)
        {
            if (_frames[i].Dts > dts)
                return i;
        }

        return _frames.Count;
    }

    /// <summary>
    /// Where playback from <paramref name="target"/> starts: the random access point with the
    /// greatest presentation timestamp at or before the target within its buffered range, or
    /// else the first random access point after it.
    /// </summary>
    public int SeekIndex(MediaTime target)
    {
        int best = -1;
        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            if (!frame.IsKeyframe)
                continue;
            if (frame.Pts <= target)
            {
                if (best < 0 || frame.Pts >= _frames[best].Pts)
                    best = i;
            }
        }

        if (best >= 0 && Buffered.Contains(target) && Buffered.IndexOf(_frames[best].Pts) == Buffered.IndexOf(target))
            return best;

        int next = -1;
        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            if (frame.IsKeyframe && frame.Pts >= target && (next < 0 || frame.Pts < _frames[next].Pts))
                next = i;
        }

        return next >= 0 ? next : (best >= 0 ? best : _frames.Count);
    }

    private void RemoveDependents(HashSet<CodedFrame> removed)
    {
        // Frames after a removed one, in decode order, up to the next random access point.
        bool dropping = false;
        foreach (var frame in _frames)
        {
            if (removed.Contains(frame))
            {
                dropping = true;
                continue;
            }

            if (dropping)
            {
                if (frame.IsKeyframe)
                    dropping = false;
                else
                    removed.Add(frame);
            }
        }
    }

    private void Remove(HashSet<CodedFrame> removed)
    {
        foreach (var frame in removed)
        {
            Bytes -= frame.Bytes;
            frame.Packet.Dispose();
        }

        _frames.RemoveAll(removed.Contains);
        Generation++;
        RecomputeBuffered();
    }

    private void RecomputeBuffered()
    {
        // Frames in presentation order, coalesced; adjacent frames within a small tolerance
        // (MSE's "buffered ranges" allow gaps that playback rides over) form one range.
        var tolerance = MediaTime.FromSeconds(0.1);
        var ranges = new List<(MediaTime Start, MediaTime End)>();
        foreach (var frame in _frames.OrderBy(f => f.Pts))
        {
            var end = frame.End > frame.Pts ? frame.End : frame.Pts;
            if (ranges.Count > 0 && frame.Pts <= ranges[^1].End + tolerance)
            {
                if (end > ranges[^1].End)
                    ranges[^1] = (ranges[^1].Start, end);
            }
            else
            {
                ranges.Add((frame.Pts, end));
            }
        }

        Buffered = MediaTimeRanges.From(ranges);
    }
}
