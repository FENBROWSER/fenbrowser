using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host;
using Xunit;

namespace FenBrowser.Tests.Host;

[Collection("Host UI Tests")]
public sealed class BrowserInputThreadOwnershipTests
{
    [Fact]
    public async Task HandleKeyPress_DispatchesPageInputOnEngineThread()
    {
        var integration = new BrowserIntegration();
        try
        {
            var receiptThreadId = Environment.CurrentManagedThreadId;

            await integration.HandleKeyPress("x");
            await WaitForAsync(
                () => integration.LastInputDispatchThreadId != 0,
                "queued key input to dispatch");

            Assert.NotEqual(receiptThreadId, integration.LastInputDispatchThreadId);
            Assert.Equal(integration.EngineThreadId, integration.LastInputDispatchThreadId);
        }
        finally
        {
            ShutdownEngineLoop(integration);
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }

    private static void ShutdownEngineLoop(BrowserIntegration integration)
    {
        var runningField = typeof(BrowserIntegration).GetField(
            "_running",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var wakeField = typeof(BrowserIntegration).GetField(
            "_wakeEvent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var engineThreadField = typeof(BrowserIntegration).GetField(
            "_engineThread",
            BindingFlags.Instance | BindingFlags.NonPublic);

        runningField?.SetValue(integration, false);
        (wakeField?.GetValue(integration) as AutoResetEvent)?.Set();
        (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
    }
}
