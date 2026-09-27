namespace FenBrowser.Media.Tests;

public class MediaTimeTests
{
    [Theory]
    [InlineData(1, 1000, 1_000)]          // Matroska 1 ms
    [InlineData(90_000, 90_000, 1_000_000)]
    [InlineData(1, 48_000, 20)]           // 20.83 µs rounds down
    [InlineData(-1, 48_000, -21)]         // toward negative infinity, not zero
    [InlineData(0, 7, 0)]
    public void FromTimescale_RoundsTowardNegativeInfinity(long value, long timescale, long expectedMicros)
    {
        Assert.Equal(expectedMicros, MediaTime.FromTimescale(value, timescale).Microseconds);
    }

    [Fact]
    public void FromTimescale_SaturatesInsteadOfOverflowing()
    {
        Assert.Equal(MediaTime.PositiveInfinity, MediaTime.FromTimescale(long.MaxValue, 1));
        Assert.Equal(MediaTime.NegativeInfinity, MediaTime.FromTimescale(long.MinValue, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromTimescale_RejectsNonPositiveTimescale(long timescale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTime.FromTimescale(1, timescale));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(90_000)]
    [InlineData(192_000)]
    [InlineData(500_000)]
    public void ToTimescale_RoundTripsEveryTick(long timescale)
    {
        var random = new Random(unchecked((int)timescale));
        for (int i = 0; i < 20_000; i++)
        {
            long ticks = random.NextInt64(-1_000_000_000_000, 1_000_000_000_000);
            Assert.Equal(ticks, MediaTime.FromTimescale(ticks, timescale).ToTimescale(timescale));
        }
    }

    [Fact]
    public void ToTimescale_RoundsToNearestTick()
    {
        Assert.Equal(123_456, MediaTime.FromTimescale(123_456, 90_000).ToTimescale(90_000));
        Assert.Equal(-21, MediaTime.FromMicroseconds(-437).ToTimescale(48_000));
        Assert.Equal(0, MediaTime.FromMicroseconds(10).ToTimescale(48_000));
        Assert.Equal(1, MediaTime.FromMicroseconds(11).ToTimescale(48_000));
        Assert.Equal(long.MaxValue, MediaTime.PositiveInfinity.ToTimescale(48_000));
    }

    [Fact]
    public void FromSeconds_RoundsAndMapsInfinities()
    {
        Assert.Equal(1_500_000, MediaTime.FromSeconds(1.5).Microseconds);
        Assert.Equal(1, MediaTime.FromSeconds(0.0000005).Microseconds);
        Assert.Equal(MediaTime.PositiveInfinity, MediaTime.FromSeconds(double.PositiveInfinity));
        Assert.Equal(MediaTime.NegativeInfinity, MediaTime.FromSeconds(double.NegativeInfinity));
        Assert.Throws<ArgumentException>(() => MediaTime.FromSeconds(double.NaN));
    }

    [Fact]
    public void Arithmetic_KeepsInfinitySticky()
    {
        var one = MediaTime.FromSeconds(1);
        Assert.Equal(MediaTime.PositiveInfinity, MediaTime.PositiveInfinity + one);
        Assert.Equal(MediaTime.PositiveInfinity, one + MediaTime.PositiveInfinity);
        Assert.Equal(MediaTime.NegativeInfinity, one - MediaTime.PositiveInfinity);
        Assert.Equal(MediaTime.PositiveInfinity, MediaTime.FromMicroseconds(long.MaxValue - 1) + one);
        Assert.Equal(MediaTime.FromSeconds(2), one + one);
    }

    [Fact]
    public void TotalSeconds_And_ToString_AreCultureInvariant()
    {
        Assert.Equal(double.PositiveInfinity, MediaTime.PositiveInfinity.TotalSeconds);
        Assert.Equal("1.250000s", MediaTime.FromSeconds(1.25).ToString());
        Assert.Equal("+inf", MediaTime.PositiveInfinity.ToString());
    }

    [Fact]
    public void Comparison_OrdersByMicroseconds()
    {
        var a = MediaTime.FromMicroseconds(1);
        var b = MediaTime.FromMicroseconds(2);
        Assert.True(a < b);
        Assert.True(b >= a);
        Assert.Equal(a, MediaTime.Min(a, b));
        Assert.Equal(b, MediaTime.Max(a, b));
        Assert.True(MediaTime.NegativeInfinity < a);
    }
}
