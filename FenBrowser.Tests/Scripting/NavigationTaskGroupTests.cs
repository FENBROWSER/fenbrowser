using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class NavigationTaskGroupTests
{
    [Fact]
    public async Task CancelAndDrain_CancelsAndObservesOwnedWork()
    {
        using var group = new NavigationTaskGroup();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = group.Run(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await started.Task;
        await group.CancelAndDrainAsync();

        Assert.True(task.IsCanceled);
    }
}
