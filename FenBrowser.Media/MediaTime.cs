using System.Globalization;

namespace FenBrowser.Media;

/// <summary>
/// A media timestamp or duration in microseconds.
/// </summary>
/// <remarks>
/// Microseconds are exact for every container timescale that divides 1,000,000,
/// such as Matroska's default of 1 ms. Other timescales, such as a 48 kHz sample
/// clock, round down by less than 1 µs. Arithmetic saturates at the infinities instead of
/// overflowing, and infinity is sticky, matching how HTML treats an unbounded
/// <c>duration</c>.
/// </remarks>
public readonly record struct MediaTime(long Microseconds) : IComparable<MediaTime>
{
    public const long MicrosecondsPerSecond = 1_000_000;

    public static readonly MediaTime Zero = new(0);
    public static readonly MediaTime PositiveInfinity = new(long.MaxValue);
    public static readonly MediaTime NegativeInfinity = new(long.MinValue);

    public bool IsInfinite => Microseconds is long.MaxValue or long.MinValue;

    public double TotalSeconds => Microseconds switch
    {
        long.MaxValue => double.PositiveInfinity,
        long.MinValue => double.NegativeInfinity,
        _ => Microseconds / (double)MicrosecondsPerSecond,
    };

    public static MediaTime FromMicroseconds(long microseconds) => new(microseconds);

    /// <summary>Converts seconds, as exposed to script, rounding to the nearest microsecond.</summary>
    /// <exception cref="ArgumentException"><paramref name="seconds"/> is NaN.</exception>
    public static MediaTime FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds))
            throw new ArgumentException("A media time cannot be NaN.", nameof(seconds));

        double micro = Math.Round(seconds * MicrosecondsPerSecond, MidpointRounding.AwayFromZero);
        if (micro >= long.MaxValue)
            return PositiveInfinity;
        if (micro <= long.MinValue)
            return NegativeInfinity;
        return new MediaTime((long)micro);
    }

    /// <summary>
    /// Converts a container timestamp (<paramref name="value"/> ticks of 1/<paramref name="timescale"/> s),
    /// rounding toward negative infinity so a frame never appears to start later than it does.
    /// </summary>
    public static MediaTime FromTimescale(long value, long timescale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);

        Int128 scaled = (Int128)value * MicrosecondsPerSecond;
        Int128 quotient = Int128.DivRem(scaled, timescale).Quotient;
        if (scaled < 0 && quotient * timescale != scaled)
            quotient--;
        return Saturate(quotient);
    }

    /// <summary>
    /// Converts to ticks of 1/<paramref name="timescale"/> s, rounding to the nearest tick.
    /// </summary>
    /// <remarks>
    /// <see cref="FromTimescale"/> loses less than 1 µs, so rounding to nearest recovers the
    /// original tick exactly for any timescale up to 500 kHz. Rounding down here would turn a
    /// 90 kHz timestamp into the previous tick, so a seek would land on the wrong frame.
    /// </remarks>
    public long ToTimescale(long timescale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        if (IsInfinite)
            return Microseconds;

        Int128 scaled = (Int128)Microseconds * timescale;
        Int128 half = MicrosecondsPerSecond / 2;
        Int128 quotient = scaled >= 0
            ? (scaled + half) / MicrosecondsPerSecond
            : -((-scaled + half) / MicrosecondsPerSecond);
        return Saturate(quotient).Microseconds;
    }

    public static MediaTime operator +(MediaTime left, MediaTime right)
    {
        if (left.IsInfinite)
            return left;
        if (right.IsInfinite)
            return right;
        return Saturate((Int128)left.Microseconds + right.Microseconds);
    }

    public static MediaTime operator -(MediaTime left, MediaTime right)
    {
        if (left.IsInfinite)
            return left;
        if (right.IsInfinite)
            return right == PositiveInfinity ? NegativeInfinity : PositiveInfinity;
        return Saturate((Int128)left.Microseconds - right.Microseconds);
    }

    public static bool operator <(MediaTime left, MediaTime right) => left.Microseconds < right.Microseconds;
    public static bool operator >(MediaTime left, MediaTime right) => left.Microseconds > right.Microseconds;
    public static bool operator <=(MediaTime left, MediaTime right) => left.Microseconds <= right.Microseconds;
    public static bool operator >=(MediaTime left, MediaTime right) => left.Microseconds >= right.Microseconds;

    public static MediaTime Min(MediaTime a, MediaTime b) => a <= b ? a : b;
    public static MediaTime Max(MediaTime a, MediaTime b) => a >= b ? a : b;

    public int CompareTo(MediaTime other) => Microseconds.CompareTo(other.Microseconds);

    public override string ToString() => Microseconds switch
    {
        long.MaxValue => "+inf",
        long.MinValue => "-inf",
        _ => TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture) + "s",
    };

    private static MediaTime Saturate(Int128 microseconds)
    {
        if (microseconds >= long.MaxValue)
            return PositiveInfinity;
        if (microseconds <= long.MinValue)
            return NegativeInfinity;
        return new MediaTime((long)microseconds);
    }
}
