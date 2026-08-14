using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.WebDriver;

internal static class WebDriverCommandDeadline
{
    public static async Task<T> WaitAsync<T>(Task<T> execution, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (timeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        try
        {
            // Do not materialize Task.AsyncWaitHandle or block a worker thread just to
            // enforce a WebDriver deadline. The runtime's Task.WaitAsync path remains
            // fully asynchronous and preserves the original task's completion state.
            return await execution
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The underlying browser operation is not necessarily cancellable. Ensure
            // a fault that arrives after the WebDriver timeout is still observed.
            ObserveLateFailure(execution);
            throw;
        }
    }

    private static void ObserveLateFailure(Task execution)
    {
        _ = execution.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
