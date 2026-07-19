using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FenBrowser.Host.Input;

public enum BrowserInputType
{
    MouseMove,
    MouseDown,
    MouseUp,
    Click,
    DoubleClick,
    ContextMenu,
    KeyDown,
    KeyUp,
    TextInput
}

public readonly record struct BrowserInputEvent(
    BrowserInputType Type,
    float X,
    float Y,
    int Button,
    int Buttons,
    int Modifiers,
    long Timestamp,
    long Sequence,
    string Text = null);

public readonly record struct BrowserInputDrainResult(
    int ProcessedCount,
    int RemainingCount,
    TimeSpan Elapsed,
    bool CountBudgetExhausted,
    bool TimeBudgetExhausted);

/// <summary>
/// Thread-safe host-to-engine input queue. Ordinary pointer movement is
/// coalesced without crossing button transitions, while discrete input keeps
/// FIFO order.
/// </summary>
public sealed class BrowserInputQueue
{
    private readonly object _sync = new();
    private readonly LinkedList<BrowserInputEvent> _pending = new();
    private readonly int _maxPendingEvents;
    private long _coalescedMouseMoveCount;
    private long _droppedMouseMoveCount;

    public BrowserInputQueue(int maxPendingEvents = 256)
    {
        if (maxPendingEvents < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingEvents));
        }

        _maxPendingEvents = maxPendingEvents;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public long CoalescedMouseMoveCount
    {
        get
        {
            lock (_sync)
            {
                return _coalescedMouseMoveCount;
            }
        }
    }

    public long DroppedMouseMoveCount
    {
        get
        {
            lock (_sync)
            {
                return _droppedMouseMoveCount;
            }
        }
    }

    public void Enqueue(BrowserInputEvent input)
    {
        lock (_sync)
        {
            if (CanCoalesceMouseMove(input) &&
                _pending.Last is { Value: var last } &&
                CanCoalesceMouseMove(last))
            {
                _pending.Last.Value = input;
                _coalescedMouseMoveCount++;
                return;
            }

            if (_pending.Count >= _maxPendingEvents)
            {
                var staleMove = FindOldestCoalescibleMouseMove();
                if (staleMove != null)
                {
                    _pending.Remove(staleMove);
                    _droppedMouseMoveCount++;
                }
                else if (CanCoalesceMouseMove(input))
                {
                    _droppedMouseMoveCount++;
                    return;
                }
            }

            _pending.AddLast(input);
        }
    }

    public bool TryDequeue(out BrowserInputEvent input)
    {
        lock (_sync)
        {
            if (_pending.First == null)
            {
                input = default;
                return false;
            }

            input = _pending.First.Value;
            _pending.RemoveFirst();
            return true;
        }
    }

    public BrowserInputDrainResult Drain(
        Action<BrowserInputEvent> dispatch,
        TimeSpan timeBudget,
        int maxEvents)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (timeBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudget));
        }
        if (maxEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        }

        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        while (processed < maxEvents &&
               Stopwatch.GetElapsedTime(started) < timeBudget &&
               TryDequeue(out var input))
        {
            dispatch(input);
            processed++;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var remaining = Count;
        return new BrowserInputDrainResult(
            processed,
            remaining,
            elapsed,
            remaining > 0 && processed >= maxEvents,
            remaining > 0 && elapsed >= timeBudget);
    }

    private LinkedListNode<BrowserInputEvent> FindOldestCoalescibleMouseMove()
    {
        for (var node = _pending.First; node != null; node = node.Next)
        {
            if (CanCoalesceMouseMove(node.Value))
            {
                return node;
            }
        }

        return null;
    }

    private static bool CanCoalesceMouseMove(BrowserInputEvent input) =>
        input.Type == BrowserInputType.MouseMove && input.Buttons == 0;
}
