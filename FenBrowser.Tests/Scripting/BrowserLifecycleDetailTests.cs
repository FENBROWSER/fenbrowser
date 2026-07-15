using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class BrowserLifecycleDetailTests
{
    [Fact]
    public void TimedOutEventLoopSample_IsExplicitlyHistorical()
    {
        var snapshot = new BrowserEventLoopSnapshot
        {
            Status = "running",
            DocumentReadyState = "loading",
            DomContentLoadedFired = false,
            LoadFired = false,
            PendingHostTimers = 0
        };

        var detail = BrowserHost.FormatDocumentLifecycleSettleDetail(
            navigationId: 7,
            snapshot,
            settled: false,
            elapsedMs: 1500,
            settleTimeoutMs: 1500);

        Assert.Contains("eventLoopObservation=transition-time", detail);
        Assert.Contains("eventLoopObservationTimedOut=1", detail);
        Assert.Contains("documentReadyStateAtObservation=loading", detail);
        Assert.Contains("domContentLoadedAtObservation=0", detail);
        Assert.Contains("loadAtObservation=0", detail);
        Assert.DoesNotContain("documentReadyState=loading", detail);
    }
}
