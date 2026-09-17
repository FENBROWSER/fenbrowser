namespace FenBrowser.Media.Tests;

public class MediaTimeRangesTests
{
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static MediaTimeRanges R(params (double Start, double End)[] ranges) =>
        MediaTimeRanges.From(ranges.Select(r => (S(r.Start), S(r.End))));

    private static void AssertRanges(MediaTimeRanges actual, params (double Start, double End)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(S(expected[i].Start), actual.Start(i));
            Assert.Equal(S(expected[i].End), actual.End(i));
        }
    }

    [Fact]
    public void From_Normalizes_SortsMergesAndDropsEmpty()
    {
        // Overlapping, touching, unsorted, empty and reversed input.
        var ranges = R((5, 6), (0, 1), (1, 2), (1.5, 3), (4, 4), (9, 8));
        AssertRanges(ranges, (0, 3), (5, 6));
    }

    [Fact]
    public void From_EmptyInput_IsEmpty()
    {
        Assert.Same(MediaTimeRanges.Empty, R());
        Assert.Same(MediaTimeRanges.Empty, R((3, 3)));
    }

    [Fact]
    public void Union_MergesAcrossSets()
    {
        AssertRanges(R((0, 1), (4, 5)).Union(R((1, 2), (3, 4.5))), (0, 2), (3, 5));
    }

    [Fact]
    public void Intersect_KeepsOnlyOverlap()
    {
        var a = R((0, 5), (10, 15));
        var b = R((3, 12), (14, 20));
        AssertRanges(a.Intersect(b), (3, 5), (10, 12), (14, 15));
        Assert.Equal(0, a.Intersect(R((6, 9))).Count);
    }

    [Fact]
    public void Contains_IsEndInclusive()
    {
        var ranges = R((1, 2), (4, 5));
        Assert.True(ranges.Contains(S(1)));
        Assert.True(ranges.Contains(S(2)));
        Assert.False(ranges.Contains(S(3)));
        Assert.Equal(1, ranges.IndexOf(S(4.5)));
        Assert.Equal(-1, ranges.IndexOf(S(0)));
    }

    [Fact]
    public void Nearest_PrefersEarlierPositionOnTie()
    {
        var ranges = R((1, 2), (4, 5));
        Assert.Equal(S(2), ranges.Nearest(S(3)));      // 1s from both 2 and 4
        Assert.Equal(S(4), ranges.Nearest(S(3.9)));
        Assert.Equal(S(1), ranges.Nearest(S(-10)));
        Assert.Equal(S(5), ranges.Nearest(S(100)));
        Assert.Equal(S(1.5), ranges.Nearest(S(1.5)));
        Assert.Null(MediaTimeRanges.Empty.Nearest(S(1)));
    }

    [Fact]
    public void Nearest_HandlesInfiniteEnd()
    {
        var live = MediaTimeRanges.Single(S(10), MediaTime.PositiveInfinity);
        Assert.Equal(S(10), live.Nearest(S(0)));
        Assert.Equal(S(1e6), live.Nearest(S(1e6)));
    }
}
