using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseRejectionTrackerTests
{
    [Fact]
    public void InMemoryTrackerStartsEmpty()
    {
        var tracker = new InMemoryPromiseRejectionTracker();

        Assert.Empty(tracker.Events);
        Assert.Equal(0, tracker.RejectedCount);
        Assert.Equal(0, tracker.HandledCount);
    }

    [Fact]
    public void TrackRecordsRejectionAndIncrementsRejectedCount()
    {
        var tracker = new InMemoryPromiseRejectionTracker();
        var promise = JsValue.FromObject(new ObjectHandle(1, 1));

        tracker.Track(promise, PromiseRejectionOperation.Reject);

        Assert.Equal(1, tracker.RejectedCount);
        Assert.Equal(0, tracker.HandledCount);
        Assert.Single(tracker.Events);
        Assert.Equal(promise, tracker.Events[0].Promise);
        Assert.Equal(PromiseRejectionOperation.Reject, tracker.Events[0].Operation);
    }

    [Fact]
    public void TrackRecordsHandledTransitionSeparately()
    {
        var tracker = new InMemoryPromiseRejectionTracker();
        var promise = JsValue.FromObject(new ObjectHandle(1, 1));

        tracker.Track(promise, PromiseRejectionOperation.Reject);
        tracker.Track(promise, PromiseRejectionOperation.Handle);

        Assert.Equal(1, tracker.RejectedCount);
        Assert.Equal(1, tracker.HandledCount);
        Assert.Equal(2, tracker.Events.Count);
    }

    [Fact]
    public void ResetClearsEventsAndCounters()
    {
        var tracker = new InMemoryPromiseRejectionTracker();
        tracker.Track(JsValue.FromObject(new ObjectHandle(1, 1)), PromiseRejectionOperation.Reject);

        tracker.Reset();

        Assert.Empty(tracker.Events);
        Assert.Equal(0, tracker.RejectedCount);
        Assert.Equal(0, tracker.HandledCount);
    }
}
