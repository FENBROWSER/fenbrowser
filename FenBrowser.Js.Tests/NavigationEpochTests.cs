using FenBrowser.Js.Host;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NavigationEpochTests
{
    [Fact]
    public void NavigationEpochInitialIsValueOne()
    {
        Assert.Equal(1L, NavigationEpoch.Initial.Value);
    }

    [Fact]
    public void NavigationEpochNextIncrements()
    {
        var current = NavigationEpoch.Initial;
        var next = current.Next();
        Assert.Equal(current.Value + 1, next.Value);
    }

    [Fact]
    public void NavigationEpochIsValidOnlyAgainstSameValue()
    {
        var captured = NavigationEpoch.Initial;
        Assert.True(captured.IsValidIn(NavigationEpoch.Initial));
        Assert.False(captured.IsValidIn(NavigationEpoch.Initial.Next()));
    }

    [Fact]
    public void DocumentEpochInitialIsValueOne()
    {
        Assert.Equal(1L, DocumentEpoch.Initial.Value);
    }

    [Fact]
    public void DocumentEpochNextIncrements()
    {
        var current = DocumentEpoch.Initial;
        var next = current.Next();
        Assert.Equal(current.Value + 1, next.Value);
    }

    [Fact]
    public void DocumentEpochIsValidOnlyAgainstSameValue()
    {
        var captured = DocumentEpoch.Initial;
        Assert.True(captured.IsValidIn(DocumentEpoch.Initial));
        Assert.False(captured.IsValidIn(DocumentEpoch.Initial.Next()));
    }
}
