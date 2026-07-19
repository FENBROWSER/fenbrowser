using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.Host.Input;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class BrowserInputQueueTests
{
    [Fact]
    public void Enqueue_OrdinaryMouseMoves_CoalescesToLatestPosition()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 8);

        for (var sequence = 1; sequence <= 100; sequence++)
        {
            queue.Enqueue(Input(BrowserInputType.MouseMove, sequence, x: sequence, y: sequence * 2));
        }

        Assert.Equal(1, queue.Count);
        Assert.Equal(99, queue.CoalescedMouseMoveCount);
        Assert.True(queue.TryDequeue(out var latest));
        Assert.Equal(100, latest.Sequence);
        Assert.Equal(100, latest.X);
        Assert.Equal(200, latest.Y);
    }

    [Fact]
    public void Enqueue_ButtonTransitions_AreNeverCoalescedOrDroppedByMoveFlood()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 4);
        queue.Enqueue(Input(BrowserInputType.MouseMove, 1));
        queue.Enqueue(Input(BrowserInputType.MouseDown, 2, buttons: 1));
        for (var sequence = 3; sequence <= 50; sequence++)
        {
            queue.Enqueue(Input(BrowserInputType.MouseMove, sequence));
        }
        queue.Enqueue(Input(BrowserInputType.MouseUp, 51));

        var drained = new List<BrowserInputEvent>();
        queue.Drain(drained.Add, TimeSpan.FromSeconds(1), maxEvents: 16);

        Assert.Contains(drained, input => input.Type == BrowserInputType.MouseDown && input.Sequence == 2);
        Assert.Contains(drained, input => input.Type == BrowserInputType.MouseUp && input.Sequence == 51);
        Assert.True(drained.FindIndex(input => input.Type == BrowserInputType.MouseDown) <
                    drained.FindIndex(input => input.Type == BrowserInputType.MouseUp));
        Assert.True(drained.Count <= 4);
    }

    [Fact]
    public void Drain_CountBudget_LeavesWorkForNextFrame()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 16);
        queue.Enqueue(Input(BrowserInputType.MouseDown, 1, buttons: 1));
        queue.Enqueue(Input(BrowserInputType.MouseUp, 2));
        queue.Enqueue(Input(BrowserInputType.Click, 3));

        var processed = new List<long>();
        var result = queue.Drain(input => processed.Add(input.Sequence), TimeSpan.FromSeconds(1), maxEvents: 2);

        Assert.Equal(new long[] { 1, 2 }, processed);
        Assert.Equal(1, result.RemainingCount);
        Assert.True(result.CountBudgetExhausted);
    }

    [Fact]
    public void Drain_TimeBudget_StopsBeforeDrainingEntireQueue()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 16);
        queue.Enqueue(Input(BrowserInputType.MouseDown, 1, buttons: 1));
        queue.Enqueue(Input(BrowserInputType.MouseUp, 2));
        queue.Enqueue(Input(BrowserInputType.Click, 3));

        var result = queue.Drain(
            _ => Thread.Sleep(10),
            TimeSpan.FromMilliseconds(2),
            maxEvents: 16);

        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(2, result.RemainingCount);
        Assert.True(result.TimeBudgetExhausted);
    }

    [Fact]
    public void Enqueue_ContextMenuSequence_PreservesDiscreteEventOrder()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 8);
        queue.Enqueue(Input(BrowserInputType.MouseDown, 1, buttons: 4));
        queue.Enqueue(Input(BrowserInputType.MouseUp, 2));
        queue.Enqueue(Input(BrowserInputType.ContextMenu, 3));

        var dispatched = new List<BrowserInputType>();
        queue.Drain(input => dispatched.Add(input.Type), TimeSpan.FromSeconds(1), maxEvents: 8);

        Assert.Equal(
            new[] { BrowserInputType.MouseDown, BrowserInputType.MouseUp, BrowserInputType.ContextMenu },
            dispatched);
    }

    [Fact]
    public void Enqueue_AdjacentMouseWheels_CoalescesAccumulatedDelta()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 8);
        queue.Enqueue(Input(BrowserInputType.MouseWheel, 1) with { DeltaX = 1, DeltaY = -2 });
        queue.Enqueue(Input(BrowserInputType.MouseWheel, 2) with { DeltaX = 3, DeltaY = -4 });

        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.CoalescedMouseWheelCount);
        Assert.True(queue.TryDequeue(out var wheel));
        Assert.Equal(2, wheel.Sequence);
        Assert.Equal(4, wheel.DeltaX);
        Assert.Equal(-6, wheel.DeltaY);
    }

    [Fact]
    public void Enqueue_DiscreteFlood_NeverExceedsConfiguredCapacity()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 4);

        for (var sequence = 1; sequence <= 20; sequence++)
        {
            queue.Enqueue(Input(BrowserInputType.Click, sequence));
        }

        Assert.Equal(4, queue.Count);
        Assert.Equal(16, queue.DroppedOverflowCount);
        var dispatched = new List<long>();
        queue.Drain(input => dispatched.Add(input.Sequence), TimeSpan.FromSeconds(1), maxEvents: 8);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, dispatched);
    }

    [Fact]
    public void Enqueue_AdjacentScrollTicks_CoalescesElapsedTime()
    {
        var queue = new BrowserInputQueue(maxPendingEvents: 8);
        queue.Enqueue(Input(BrowserInputType.ScrollAnimationTick, 1) with { DeltaY = 0.01f });
        queue.Enqueue(Input(BrowserInputType.ScrollAnimationTick, 2) with { DeltaY = 0.02f });

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out var tick));
        Assert.Equal(0.03f, tick.DeltaY, precision: 4);
    }

    private static BrowserInputEvent Input(
        BrowserInputType type,
        long sequence,
        float x = 0,
        float y = 0,
        int buttons = 0) =>
        new(type, x, y, 0, buttons, 0, sequence, sequence);
}
