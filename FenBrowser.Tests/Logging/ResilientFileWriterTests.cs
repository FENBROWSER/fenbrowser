using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

public class ResilientFileWriterTests
{
    [Fact]
    public void ComputeRetryDelayMilliseconds_IsBoundedAndNonDecreasingByAttemptFloor()
    {
        var d1 = ResilientFileWriter.ComputeRetryDelayMilliseconds(1);
        var d2 = ResilientFileWriter.ComputeRetryDelayMilliseconds(2);
        var d3 = ResilientFileWriter.ComputeRetryDelayMilliseconds(3);
        var d4 = ResilientFileWriter.ComputeRetryDelayMilliseconds(4);

        Assert.InRange(d1, 4, 7);
        Assert.InRange(d2, 8, 11);
        Assert.InRange(d3, 16, 19);
        Assert.InRange(d4, 32, 35);

        Assert.True(d2 >= d1);
        Assert.True(d3 >= d2);
        Assert.True(d4 >= d3);
    }
}
