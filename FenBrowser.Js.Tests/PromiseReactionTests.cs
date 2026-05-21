using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseReactionTests
{
    [Fact]
    public void ReactionWithHandlerReportsHasHandlerTrue()
    {
        var reaction = new PromiseReaction(
            PromiseCapability.Empty,
            PromiseReactionType.Fulfill,
            handler: JsValue.FromObject(new ObjectHandle(1, 1)));

        Assert.True(reaction.HasHandler);
        Assert.Equal(PromiseReactionType.Fulfill, reaction.Type);
    }

    [Fact]
    public void ReactionWithoutHandlerReportsHasHandlerFalse()
    {
        // 27.2.1.4 step "If reaction.[[Handler]] is empty" - identity pass-through
        // path when no then-callback was supplied.
        var reaction = new PromiseReaction(
            PromiseCapability.Empty,
            PromiseReactionType.Reject,
            handler: JsValue.Undefined);

        Assert.False(reaction.HasHandler);
    }

    [Fact]
    public void TracingReachesEveryObjectSlot()
    {
        var capability = new PromiseCapability(
            Promise: JsValue.FromObject(new ObjectHandle(1, 1)),
            Resolve: JsValue.FromObject(new ObjectHandle(2, 1)),
            Reject: JsValue.FromObject(new ObjectHandle(3, 1)));
        var handler = JsValue.FromObject(new ObjectHandle(4, 1));
        var reaction = new PromiseReaction(capability, PromiseReactionType.Fulfill, handler);

        var tracer = new RecordingTracer();
        reaction.Trace(tracer);

        Assert.Contains(new ObjectHandle(1, 1), tracer.Visited);
        Assert.Contains(new ObjectHandle(2, 1), tracer.Visited);
        Assert.Contains(new ObjectHandle(3, 1), tracer.Visited);
        Assert.Contains(new ObjectHandle(4, 1), tracer.Visited);
    }

    [Fact]
    public void TracingSkipsNonObjectSlots()
    {
        var reaction = new PromiseReaction(
            PromiseCapability.Empty,
            PromiseReactionType.Fulfill,
            handler: JsValue.Undefined);

        var tracer = new RecordingTracer();
        reaction.Trace(tracer);

        // PromiseCapability.Empty has undefined slots, handler is undefined: nothing
        // should be traced.
        Assert.Empty(tracer.Visited);
    }

    private sealed class RecordingTracer : FenBrowser.Js.Heap.IHeapTracer
    {
        public List<ObjectHandle> Visited { get; } = new();

        public void Trace(ObjectHandle handle) => Visited.Add(handle);
        public void Trace(StringHandle handle) { }
        public void Trace(SymbolHandle handle) { }
    }
}
