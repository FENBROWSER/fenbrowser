using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseCapabilityTests
{
    [Fact]
    public void EmptyCapabilityHasUndefinedSlots()
    {
        var empty = PromiseCapability.Empty;

        Assert.Equal(JsValueTag.Undefined, empty.Promise.Tag);
        Assert.Equal(JsValueTag.Undefined, empty.Resolve.Tag);
        Assert.Equal(JsValueTag.Undefined, empty.Reject.Tag);
        Assert.False(empty.IsComplete);
    }

    [Fact]
    public void CompleteCapabilityHasAllObjectSlots()
    {
        var capability = new PromiseCapability(
            Promise: JsValue.FromObject(new ObjectHandle(1, 1)),
            Resolve: JsValue.FromObject(new ObjectHandle(2, 1)),
            Reject: JsValue.FromObject(new ObjectHandle(3, 1)));

        Assert.True(capability.IsComplete);
    }

    [Fact]
    public void MissingResolveOrRejectKeepsCapabilityIncomplete()
    {
        var noResolve = new PromiseCapability(
            Promise: JsValue.FromObject(new ObjectHandle(1, 1)),
            Resolve: JsValue.Undefined,
            Reject: JsValue.FromObject(new ObjectHandle(3, 1)));
        Assert.False(noResolve.IsComplete);

        var noReject = noResolve with { Resolve = JsValue.FromObject(new ObjectHandle(2, 1)), Reject = JsValue.Undefined };
        Assert.False(noReject.IsComplete);
    }

    [Fact]
    public void RecordEqualityComparesAllThreeSlots()
    {
        var a = new PromiseCapability(
            JsValue.FromObject(new ObjectHandle(1, 1)),
            JsValue.FromObject(new ObjectHandle(2, 1)),
            JsValue.FromObject(new ObjectHandle(3, 1)));
        var b = a;

        Assert.Equal(a, b);
    }
}
