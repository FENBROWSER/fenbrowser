using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseObjectTests
{
    [Fact]
    public void NewPromiseStartsPendingAndUnhandled()
    {
        var promise = new PromiseObject();

        Assert.Equal(PromiseState.Pending, promise.State);
        Assert.False(promise.IsHandled);
        Assert.Equal(JsValueTag.Undefined, promise.GetResultUnchecked().Tag);
    }

    [Fact]
    public void FulfillTransitionsStateAndStoresValue()
    {
        var promise = new PromiseObject();

        Assert.True(promise.TrySettle(PromiseState.Fulfilled, JsValue.FromInt32(42)));
        Assert.Equal(PromiseState.Fulfilled, promise.State);
        Assert.Equal(42, promise.GetResultUnchecked().AsInt32());
    }

    [Fact]
    public void RejectTransitionsStateAndStoresReason()
    {
        var promise = new PromiseObject();

        Assert.True(promise.TrySettle(PromiseState.Rejected, JsValue.FromInt32(-1)));
        Assert.Equal(PromiseState.Rejected, promise.State);
        Assert.Equal(-1, promise.GetResultUnchecked().AsInt32());
    }

    [Fact]
    public void SettledPromiseIgnoresSubsequentSettleAttempts()
    {
        // ECMA-262 27.2.1.4/7 assert the state is Pending. We surface a false return
        // so the resolver function can be idempotent without raising.
        var promise = new PromiseObject();
        promise.TrySettle(PromiseState.Fulfilled, JsValue.FromInt32(1));

        Assert.False(promise.TrySettle(PromiseState.Fulfilled, JsValue.FromInt32(2)));
        Assert.False(promise.TrySettle(PromiseState.Rejected, JsValue.FromInt32(3)));
        Assert.Equal(1, promise.GetResultUnchecked().AsInt32());
    }

    [Fact]
    public void TrySettleWithPendingArgumentThrows()
    {
        var promise = new PromiseObject();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => promise.TrySettle(PromiseState.Pending, JsValue.Undefined));
    }

    [Fact]
    public void QueuedReactionsOnPendingPromiseAreDelivered()
    {
        var promise = new PromiseObject();
        promise.QueueFulfillReaction(new ObjectHandle(1, 1));
        promise.QueueFulfillReaction(new ObjectHandle(2, 1));
        promise.QueueRejectReaction(new ObjectHandle(3, 1));

        Assert.Equal(2, promise.FulfillReactionCountForTest);
        Assert.Equal(1, promise.RejectReactionCountForTest);

        var drained = promise.DrainFulfillReactions();
        Assert.Collection(drained,
            r => Assert.Equal(new ObjectHandle(1, 1), r),
            r => Assert.Equal(new ObjectHandle(2, 1), r));
        Assert.Empty(promise.DrainFulfillReactions());
    }

    [Fact]
    public void QueueingReactionsAfterSettleIsIgnored()
    {
        // PerformPromiseThen on an already-settled promise schedules the reaction
        // directly as a job rather than appending to the list. The promise object
        // therefore drops any post-settle reaction submission to keep the list
        // semantically empty.
        var promise = new PromiseObject();
        promise.TrySettle(PromiseState.Fulfilled, JsValue.FromInt32(1));

        promise.QueueFulfillReaction(new ObjectHandle(7, 1));
        promise.QueueRejectReaction(new ObjectHandle(8, 1));

        Assert.Equal(0, promise.FulfillReactionCountForTest);
        Assert.Equal(0, promise.RejectReactionCountForTest);
    }
}
