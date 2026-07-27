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

        if (!execution.IsCompleted &&
            !((IAsyncResult)execution).AsyncWaitHandle.WaitOne(timeoutMs))
        {
            ObserveLateFailure(execution);
            throw new TimeoutException();
        }

        return await execution.ConfigureAwait(false);
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
