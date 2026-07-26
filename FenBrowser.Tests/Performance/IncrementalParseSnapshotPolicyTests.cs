using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class IncrementalParseSnapshotPolicyTests
{
    [Theory]
    [InlineData(32 * 1024, 4)]
    [InlineData(64 * 1024, 2)]
    [InlineData(128 * 1024, 1)]
    [InlineData(512 * 1024, 0)]
    public void RepaintLimit_ScalesDownWithDocumentSize(int htmlLength, int expectedLimit)
    {
        Assert.Equal(expectedLimit, InvokeResolveLimit(htmlLength));
    }

    [Theory]
    [InlineData(1, false, 0, 4, true)]
    [InlineData(2, false, 1, 4, true)]
    [InlineData(3, false, 1, 4, false)]
    [InlineData(5, true, 3, 4, true)]
    [InlineData(5, true, 4, 4, false)]
    [InlineData(1, false, 0, 0, false)]
    public void RepaintDecision_RespectsStrideFinalCheckpointAndAdaptiveCap(
        int checkpointOrdinal,
        bool isFinal,
        int emittedCount,
        int maxRepaintCount,
        bool expected)
    {
        Assert.Equal(expected, InvokeShouldEmit(checkpointOrdinal, isFinal, emittedCount, maxRepaintCount));
    }

    private static int InvokeResolveLimit(int htmlLength)
    {
        var method = typeof(CustomHtmlEngine).GetMethod(
            "ResolveIncrementalParseRepaintMaxCount",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<int>(method.Invoke(null, new object[] { htmlLength }));
    }

    private static bool InvokeShouldEmit(
        int checkpointOrdinal,
        bool isFinal,
        int emittedCount,
        int maxRepaintCount)
    {
        var method = typeof(CustomHtmlEngine).GetMethod(
            "ShouldEmitIncrementalParseRepaint",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(
            null,
            new object[] { checkpointOrdinal, isFinal, emittedCount, maxRepaintCount }));
    }
}
