using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection("Synthetic CAPTCHA")]
public sealed class QueuedRenderUpdateTests
{
    [Fact]
    public async Task RenderCallback_DoesNotWaitForBusyRepaintGate()
    {
        using var engine = new CustomHtmlEngine();
        var gate = GetPrivateField<SemaphoreSlim>(engine, "_repaintGate");
        var callback = typeof(CustomHtmlEngine).GetMethod(
            "ProcessQueuedRenderUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(callback);
        await gate.WaitAsync();
        var releaseGate = Task.Run(async () =>
        {
            await Task.Delay(500);
            gate.Release();
        });

        var stopwatch = Stopwatch.StartNew();
        callback!.Invoke(engine, Array.Empty<object>());
        stopwatch.Stop();

        await releaseGate;
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"render callback blocked for {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        await WaitForQueuedWorkerAsync(engine);
    }

    private static async Task WaitForQueuedWorkerAsync(CustomHtmlEngine engine)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!GetPrivateField<bool>(engine, "_queuedRenderUpdateRunning"))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Queued render worker did not drain");
    }

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(owner));
    }
}
