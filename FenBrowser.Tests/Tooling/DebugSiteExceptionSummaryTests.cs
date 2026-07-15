using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class DebugSiteExceptionSummaryTests
{
    [Fact]
    public void Build_UsesEventLoopFailureTotalAndPreservesRetainedOrder()
    {
        var eventLoop = new BrowserEventLoopSnapshot
        {
            CallbackFailures = 3,
            CallbackFailureRecords =
            [
                new BrowserCallbackFailureRecord { Sequence = 2, CallbackId = "callback-2" },
                new BrowserCallbackFailureRecord { Sequence = 1, CallbackId = "callback-1" }
            ]
        };
        var other = new[]
        {
            new DebugSiteExceptionRecord("NavigateAsync", "Exception", "fixture navigation failure")
        };

        var summary = DebugSiteExceptionSummaryBuilder.Build(eventLoop, other);

        Assert.Equal(1, summary.SchemaVersion);
        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(3, summary.CallbackFailureCount);
        Assert.Equal(2, summary.RetainedCallbackFailureCount);
        Assert.Equal(1, summary.OtherExceptionCount);
        Assert.Equal(new[] { "callback-1", "callback-2" }, summary.CallbackFailures.Select(failure => failure.CallbackId));
    }
}
