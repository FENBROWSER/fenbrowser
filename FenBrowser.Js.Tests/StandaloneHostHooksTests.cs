using FenBrowser.Js.Host;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StandaloneHostHooksTests
{
    [Fact]
    public void EnqueuePromiseJobReachesTheUnderlyingQueue()
    {
        var hooks = new StandaloneHostHooks();
        var job = new PromiseReactionJob(
            new PromiseReaction(PromiseCapability.Empty, PromiseReactionType.Fulfill, JsValue.Undefined),
            argument: JsValue.FromInt32(1),
            realmId: 0);

        hooks.EnqueuePromiseJob(job);

        Assert.Equal(1, hooks.JobQueue.Count);
    }

    [Fact]
    public void ReportPromiseRejectionRoutesIntoRejectionTracker()
    {
        var hooks = new StandaloneHostHooks();
        var promise = JsValue.FromObject(new ObjectHandle(1, 1));

        hooks.ReportPromiseRejection(promise, PromiseRejectionOperation.Reject);

        Assert.Equal(1, hooks.RejectionTracker.RejectedCount);
        Assert.Equal(0, hooks.RejectionTracker.HandledCount);
    }

    [Fact]
    public void StandaloneHostRefusesHostPropertyReads()
    {
        var hooks = new StandaloneHostHooks();
        var handle = new HostObjectHandle(0, 1, 0, 1);

        Assert.False(hooks.TryGetHostProperty(handle, "anything", out var value));
        Assert.Equal(JsValueTag.Undefined, value.Tag);
    }

    [Fact]
    public void StandaloneHostRefusesHostPropertyWrites()
    {
        var hooks = new StandaloneHostHooks();
        var handle = new HostObjectHandle(0, 1, 0, 1);

        Assert.False(hooks.TrySetHostProperty(handle, "anything", JsValue.FromInt32(1)));
    }

    [Fact]
    public void StandaloneHostThrowsOnHostFunctionCalls()
    {
        var hooks = new StandaloneHostHooks();

        Assert.Throws<NotSupportedException>(() =>
            hooks.CallHostFunction(functionId: 42, JsValue.Undefined, ReadOnlySpan<JsValue>.Empty));
    }
}
