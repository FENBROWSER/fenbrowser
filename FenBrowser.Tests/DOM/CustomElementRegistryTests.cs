using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.DOM;
using FenExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;
using Xunit;

namespace FenBrowser.Tests.DOM;

public class CustomElementRegistryTests
{
    public CustomElementRegistryTests()
    {
        EngineContext.Reset();
        EventLoopCoordinator.ResetInstance();
    }

    [Fact]
    public void WhenDefined_PendingPromise_ResolvesAtMicrotaskCheckpoint()
    {
        var context = new FenExecutionContext();
        var registry = new CustomElementRegistry(context);
        var api = registry.ToFenObject();
        var promise = api.Get("whenDefined").AsFunction().Invoke(new[] { FenValue.FromString("x-test") }, context).AsObject();
        var callbackFired = false;

        promise.Get("then").AsFunction().Invoke(
            new[]
            {
                FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                {
                    callbackFired = true;
                    return FenValue.Undefined;
                }))
            },
            context);

        registry.Define("x-test", FenValue.FromFunction(new FenFunction("ctor", (args, _) => FenValue.Undefined)));

        Assert.Equal("pending", promise.Get("__state__").ToString());
        Assert.False(callbackFired);

        DrainMicrotasksUntil(() => promise.Get("__state__").ToString() == "fulfilled");

        Assert.Equal("fulfilled", promise.Get("__state__").ToString());
        Assert.True(callbackFired);
    }

    [Fact]
    public void WhenDefined_ThenAddedAfterFulfillment_RunsOnNextMicrotask()
    {
        var context = new FenExecutionContext();
        var registry = new CustomElementRegistry(context);
        var api = registry.ToFenObject();
        var promise = api.Get("whenDefined").AsFunction().Invoke(new[] { FenValue.FromString("x-late") }, context).AsObject();

        registry.Define("x-late", FenValue.FromFunction(new FenFunction("ctor", (args, _) => FenValue.Undefined)));
        DrainMicrotasksUntil(() => promise.Get("__state__").ToString() == "fulfilled");

        var callbackFired = false;
        promise.Get("then").AsFunction().Invoke(
            new[]
            {
                FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                {
                    callbackFired = true;
                    return FenValue.Undefined;
                }))
            },
            context);

        Assert.False(callbackFired);

        DrainMicrotasksUntil(() => callbackFired);

        Assert.True(callbackFired);
    }

    [Fact]
    public void WhenDefined_AlreadyDefinedPromise_ThenRunsOnNextMicrotask()
    {
        var context = new FenExecutionContext();
        var registry = new CustomElementRegistry(context);
        var api = registry.ToFenObject();

        registry.Define("x-ready", FenValue.FromFunction(new FenFunction("ctor", (args, _) => FenValue.Undefined)));

        var promise = api.Get("whenDefined").AsFunction().Invoke(new[] { FenValue.FromString("x-ready") }, context).AsObject();
        var callbackFired = false;

        promise.Get("then").AsFunction().Invoke(
            new[]
            {
                FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                {
                    callbackFired = true;
                    return FenValue.Undefined;
                }))
            },
            context);

        Assert.Equal("fulfilled", promise.Get("__state__").ToString());
        Assert.False(callbackFired);

        DrainMicrotasksUntil(() => callbackFired);

        Assert.True(callbackFired);
    }

    [Fact]
    public void WhenDefined_MissingName_RejectedCatchRunsOnNextMicrotask()
    {
        var context = new FenExecutionContext();
        var registry = new CustomElementRegistry(context);
        var api = registry.ToFenObject();
        var promise = api.Get("whenDefined").AsFunction().Invoke(System.Array.Empty<FenValue>(), context).AsObject();
        var callbackFired = false;

        promise.Get("catch").AsFunction().Invoke(
            new[]
            {
                FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                {
                    callbackFired = true;
                    return FenValue.Undefined;
                }))
            },
            context);

        Assert.Equal("rejected", promise.Get("__state__").ToString());
        Assert.False(callbackFired);

        DrainMicrotasksUntil(() => callbackFired);

        Assert.True(callbackFired);
    }

    private static void DrainMicrotasksUntil(System.Func<bool> condition)
    {
        var deadline = System.DateTime.UtcNow.AddSeconds(1);
        while (!condition() && System.DateTime.UtcNow < deadline)
        {
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            System.Threading.Thread.Yield();
        }

        Assert.True(condition(), "Timed out waiting for the custom-elements microtask queue to settle.");
    }
}
