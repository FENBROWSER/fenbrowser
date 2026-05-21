using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JobQueueTests
{
    [Fact]
    public void NewQueueIsEmpty()
    {
        var queue = new JobQueue();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void EnqueueAndDequeuePreserveFifoOrder()
    {
        var queue = new JobQueue();
        var first = NewReactionJob(JsValue.FromInt32(1));
        var second = NewReactionJob(JsValue.FromInt32(2));
        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var a));
        Assert.Same(first, a);
        Assert.True(queue.TryDequeue(out var b));
        Assert.Same(second, b);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void RunMicrotaskCheckpointDrainsTheQueue()
    {
        var queue = new JobQueue();
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(1)));
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(2)));
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(3)));

        var runOrder = new List<int>();
        var ran = queue.RunMicrotaskCheckpoint(job =>
        {
            runOrder.Add(((PromiseReactionJob)job).Argument.AsInt32());
            return true;
        });

        Assert.Equal(3, ran);
        Assert.Equal(0, queue.Count);
        Assert.Equal(new[] { 1, 2, 3 }, runOrder);
    }

    [Fact]
    public void RunMicrotaskCheckpointAlsoDrainsJobsEnqueuedDuringTheCheckpoint()
    {
        // Microtasks scheduled by earlier microtasks must run in the same checkpoint
        // (8.4 step 4). Confirm the runner does not snapshot the queue and exit
        // early.
        var queue = new JobQueue();
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(1)));

        var runValues = new List<int>();
        var ran = queue.RunMicrotaskCheckpoint(job =>
        {
            var value = ((PromiseReactionJob)job).Argument.AsInt32();
            runValues.Add(value);
            if (value == 1)
            {
                queue.Enqueue(NewReactionJob(JsValue.FromInt32(2)));
            }

            return true;
        });

        Assert.Equal(2, ran);
        Assert.Equal(new[] { 1, 2 }, runValues);
    }

    [Fact]
    public void RunnerReturningFalseAbortsCheckpointAndLeavesRemainingJobs()
    {
        var queue = new JobQueue();
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(1)));
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(2)));
        queue.Enqueue(NewReactionJob(JsValue.FromInt32(3)));

        var ran = queue.RunMicrotaskCheckpoint(job =>
        {
            return ((PromiseReactionJob)job).Argument.AsInt32() < 2;
        });

        // Ran job 1 (returned true), then job 2 (returned false): only job 1 counts.
        Assert.Equal(1, ran);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void ResolveThenableJobCarriesAllThreeValues()
    {
        var job = new PromiseResolveThenableJob(
            promiseToResolve: JsValue.FromObject(new ObjectHandle(1, 1)),
            thenable: JsValue.FromObject(new ObjectHandle(2, 1)),
            then: JsValue.FromObject(new ObjectHandle(3, 1)),
            realmId: 0);

        Assert.Equal(JsValueTag.Object, job.PromiseToResolve.Tag);
        Assert.Equal(JsValueTag.Object, job.Thenable.Tag);
        Assert.Equal(JsValueTag.Object, job.Then.Tag);
    }

    private static PromiseReactionJob NewReactionJob(JsValue argument) => new(
        reaction: new PromiseReaction(PromiseCapability.Empty, PromiseReactionType.Fulfill, JsValue.Undefined),
        argument: argument,
        realmId: 0);
}
