using System.Threading;
using System.Threading.Tasks;
using FenBrowser.WebDriver;

namespace FenBrowser.Tests.WebDriver
{
    public class WebDriverCommandQueueTests
    {
        [Fact]
        public async Task ExecuteAsync_DoesNotOverlapCommands()
        {
            using var queue = new WebDriverCommandQueue();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = queue.ExecuteAsync(async () =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return 1;
            }, CancellationToken.None);

            await firstStarted.Task;

            var second = queue.ExecuteAsync(() =>
            {
                secondStarted.SetResult();
                return Task.FromResult(2);
            }, CancellationToken.None);

            var prematureStart = await Task.WhenAny(secondStarted.Task, Task.Delay(100));
            Assert.NotSame(secondStarted.Task, prematureStart);

            releaseFirst.SetResult();

            Assert.Equal(1, await first);
            Assert.Equal(2, await second);
            Assert.True(secondStarted.Task.IsCompleted);
        }

        [Fact]
        public async Task ExecuteAsync_CancelsAQueuedCommandWithoutReleasingTheActiveCommand()
        {
            using var queue = new WebDriverCommandQueue();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledCommandRan = false;

            var first = queue.ExecuteAsync(async () =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return 1;
            }, CancellationToken.None);

            await firstStarted.Task;
            using var cancellation = new CancellationTokenSource();
            var canceled = queue.ExecuteAsync(() =>
            {
                canceledCommandRan = true;
                return Task.FromResult(2);
            }, cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.False(canceledCommandRan);

            releaseFirst.SetResult();
            Assert.Equal(1, await first);
        }
    }
}
