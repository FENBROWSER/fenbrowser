namespace FenBrowser.Media;

/// <summary>
/// An immutable, normalized set of time ranges.
/// </summary>
/// <remarks>
/// WHATWG HTML §4.8.11.14 "Time ranges": a normalized TimeRanges object has ranges that
/// are ordered, do not overlap, are not empty, and do not touch (adjacent ranges are
/// folded into one). Every instance of this type satisfies those rules, so the DOM
/// <c>buffered</c>, <c>seekable</c> and <c>played</c> attributes can expose it directly.
/// </remarks>
public sealed class MediaTimeRanges
{
    public static readonly MediaTimeRanges Empty = new([]);

    private readonly (MediaTime Start, MediaTime End)[] _ranges;

    private MediaTimeRanges((MediaTime Start, MediaTime End)[] normalized)
    {
        _ranges = normalized;
    }

    public int Count => _ranges.Length;

    public MediaTime Start(int index) => _ranges[index].Start;

    public MediaTime End(int index) => _ranges[index].End;

    public static MediaTimeRanges Single(MediaTime start, MediaTime end) => From([(start, end)]);

    /// <summary>Builds a normalized set from arbitrary ranges. Empty or reversed ranges are dropped.</summary>
    public static MediaTimeRanges From(IEnumerable<(MediaTime Start, MediaTime End)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var list = new List<(MediaTime Start, MediaTime End)>();
        foreach (var range in ranges)
        {
            if (range.Start < range.End)
                list.Add(range);
        }

        if (list.Count == 0)
            return Empty;

        list.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(MediaTime Start, MediaTime End)>(list.Count) { list[0] };
        for (int i = 1; i < list.Count; i++)
        {
            var last = merged[^1];
            var next = list[i];
            if (next.Start <= last.End)
                merged[^1] = (last.Start, MediaTime.Max(last.End, next.End));
            else
                merged.Add(next);
        }

        return new MediaTimeRanges([.. merged]);
    }

    public MediaTimeRanges Union(MediaTimeRanges other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count == 0)
            return this;
        if (Count == 0)
            return other;
        return From(_ranges.Concat(other._ranges));
    }

    public MediaTimeRanges Intersect(MediaTimeRanges other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var result = new List<(MediaTime Start, MediaTime End)>();
        int i = 0, j = 0;
        while (i < _ranges.Length && j < other._ranges.Length)
        {
            var a = _ranges[i];
            var b = other._ranges[j];
            var start = MediaTime.Max(a.Start, b.Start);
            var end = MediaTime.Min(a.End, b.End);
            if (start < end)
                result.Add((start, end));

            if (a.End < b.End)
                i++;
            else
                j++;
        }

        return result.Count == 0 ? Empty : new MediaTimeRanges([.. result]);
    }

    /// <summary>True when <paramref name="time"/> lies in a range, end-inclusive as HTML seeking requires.</summary>
    public bool Contains(MediaTime time) => IndexOf(time) >= 0;

    /// <summary>Index of the range containing <paramref name="time"/> (end-inclusive), or -1.</summary>
    public int IndexOf(MediaTime time)
    {
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var range = _ranges[mid];
            if (time < range.Start)
                hi = mid - 1;
            else if (time > range.End)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

    /// <summary>
    /// The position in the set nearest to <paramref name="time"/>, preferring the earlier
    /// position on a tie. The HTML "seeking" algorithm uses this when the new playback
    /// position is outside the seekable ranges. Returns null for an empty set.
    /// </summary>
    public MediaTime? Nearest(MediaTime time)
    {
        if (_ranges.Length == 0)
            return null;
        if (Contains(time))
            return time;

        MediaTime best = _ranges[0].Start;
        long bestDistance = Distance(time, best);
        foreach (var (start, end) in _ranges)
        {
            foreach (var candidate in (ReadOnlySpan<MediaTime>)[start, end])
            {
                long distance = Distance(time, candidate);
                if (distance < bestDistance || (distance == bestDistance && candidate < best))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    public override string ToString() =>
        _ranges.Length == 0 ? "[]" : string.Join(" ", _ranges.Select(r => $"[{r.Start}, {r.End})"));

    private static long Distance(MediaTime a, MediaTime b)
    {
        Int128 d = (Int128)a.Microseconds - b.Microseconds;
        if (d < 0)
            d = -d;
        return d > long.MaxValue ? long.MaxValue : (long)d;
    }
}
