using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Host;
using FenBrowser.Host.Input;
using Xunit;

namespace FenBrowser.Tests.Host;

public sealed class BrowserInputDeferredActivationTests
{
    [Fact]
    public void ObserveDeferredInputTask_DoesNotWaitForNavigationTail()
    {
        var navigationTail = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stopwatch = Stopwatch.StartNew();
        BrowserIntegration.ObserveDeferredInputTask(
            navigationTail.Task,
            sequence: 42,
            inputType: BrowserInputType.Click);
        stopwatch.Stop();

        Assert.False(navigationTail.Task.IsCompleted);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"input activation observer blocked for {stopwatch.Elapsed.TotalMilliseconds:F0}ms");

        navigationTail.SetResult(null);
    }
}
