using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 11 regression: the interaction re-cascade debounce must never dispose a
/// <see cref="CancellationTokenSource"/> that another active task still owns.
/// Rapid interaction-state changes (hover/focus flapping) previously raced the
/// caller's Dispose against the debounce task's own Dispose and threw
/// ObjectDisposedException / surfaced unobserved task exceptions.
/// </summary>
public sealed class Phase11InteractionRecascadeCtsTests
{
    [Fact]
    public async Task RapidRecascadeScheduling_DoesNotThrowOrLeakUnobservedExceptions()
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            Interlocked.Increment(ref unobserved);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            using var host = new BrowserHost();

            var schedule = typeof(BrowserHost).GetMethod(
                "ScheduleInteractionRecascade",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(schedule);

            // Fire far more rapid re-cascade requests than the 75ms debounce can
            // ever run, so nearly every token source is cancelled and superseded
            // while its owning task is still inside Task.Delay.
            for (int i = 0; i < 1000; i++)
            {
                schedule.Invoke(host, Array.Empty<object>());
            }

            // Let the surviving/cancelled debounce tasks run their finally blocks
            // (each disposes only the source it owns).
            await Task.Delay(300);

            // Force finalization so any CancellationTokenSource that was double
            // disposed would surface as an unobserved exception here.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public async Task DisposeDuringPendingRecascade_IsSafe()
    {
        var host = new BrowserHost();

        var schedule = typeof(BrowserHost).GetMethod(
            "ScheduleInteractionRecascade",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Schedule work, then dispose immediately while the debounce task is still
        // waiting. Dispose must only cancel (not dispose) the source it does not own.
        for (int i = 0; i < 50; i++)
        {
            schedule.Invoke(host, Array.Empty<object>());
        }

        host.Dispose();

        // The pending task should observe cancellation / disposal without throwing.
        await Task.Delay(200);
    }
}
