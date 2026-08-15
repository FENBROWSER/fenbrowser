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
    MouseWheel,
    ScrollTo,
    ScrollAnimationTick,
    ClipboardCommand,
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
    string Text = null,
    int ReceiptThreadId = 0,
    float DeltaX = 0,
    float DeltaY = 0,
    string Command = null);

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
    private readonly int _hardMaxPendingEvents;
    private long _coalescedMouseMoveCount;
    private long _coalescedMouseWheelCount;
    private long _droppedMouseMoveCount;
    private long _droppedOverflowCount;

    public BrowserInputQueue(int maxPendingEvents = 256)
    {
        if (maxPendingEvents < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingEvents));
        }

        _maxPendingEvents = maxPendingEvents;
        _hardMaxPendingEvents = checked(Math.Max(64, maxPendingEvents * 4));
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

    public long CoalescedMouseWheelCount
    {
        get
        {
            lock (_sync)
            {
                return _coalescedMouseWheelCount;
            }
        }
    }

    public long DroppedOverflowCount
    {
        get
        {
            lock (_sync)
            {
                return _droppedOverflowCount;
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

            if (input.Type == BrowserInputType.MouseWheel &&
                _pending.Last is { Value: var lastWheel } &&
                lastWheel.Type == BrowserInputType.MouseWheel)
            {
                _pending.Last.Value = input with
                {
                    DeltaX = lastWheel.DeltaX + input.DeltaX,
                    DeltaY = lastWheel.DeltaY + input.DeltaY
                };
                _coalescedMouseWheelCount++;
                return;
            }

            if (input.Type == BrowserInputType.ScrollTo &&
                _pending.Last is { Value.Type: BrowserInputType.ScrollTo })
            {
                _pending.Last.Value = input;
                return;
            }

            if (input.Type == BrowserInputType.ScrollAnimationTick &&
                _pending.Last is { Value: var lastTick } &&
                lastTick.Type == BrowserInputType.ScrollAnimationTick)
            {
                _pending.Last.Value = input with
                {
                    DeltaX = lastTick.DeltaX + input.DeltaX,
                    DeltaY = lastTick.DeltaY + input.DeltaY
                };
                return;
            }

            if (_pending.Count >= _maxPendingEvents)
            {
                var staleInput = FindOldestCoalescibleInput();
                if (staleInput == null && IsStateTransition(input))
                {
                    staleInput = FindOldestNonTransitionInput();
                }

                if (staleInput != null)
                {
                    RecordDrop(staleInput.Value);
                    _pending.Remove(staleInput);
                }
                else if (CanCoalesceMouseMove(input))
                {
                    _droppedMouseMoveCount++;
                    return;
                }
                else if (!IsStateTransition(input))
                {
                    _droppedOverflowCount++;
                    return;
                }
                else if (_pending.Count >= _hardMaxPendingEvents)
                {
                    // The soft limit deliberately lets press/release transitions through
                    // so a delayed MouseUp/KeyUp cannot leave page state permanently
                    // pressed. That must not become an unbounded-memory promise under an
                    // event flood, however. At the hard ceiling, preserve release events
                    // by sacrificing the oldest queued press; new press events can be
                    // dropped safely because no new pressed state has reached the page.
                    if (IsReleaseTransition(input))
                    {
                        var queuedPress = FindOldestPressTransition();
                        if (queuedPress != null)
                        {
                            RecordDrop(queuedPress.Value);
                            _pending.Remove(queuedPress);
                        }
                        else
                        {
                            _droppedOverflowCount++;
                            return;
                        }
                    }
                    else
                    {
                        _droppedOverflowCount++;
                        return;
                    }
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

    private void RecordDrop(BrowserInputEvent input)
    {
        if (CanCoalesceMouseMove(input))
        {
            _droppedMouseMoveCount++;
        }
        else
        {
            // Wheel/scroll/tick coalescible events were previously removed here
            // without incrementing any drop counter, making overload invisible.
            _droppedOverflowCount++;
        }
    }

    private LinkedListNode<BrowserInputEvent> FindOldestCoalescibleInput()
    {
        for (var node = _pending.First; node != null; node = node.Next)
        {
            if (IsCoalescibleInput(node.Value))
            {
                return node;
            }
        }

        return null;
    }

    private LinkedListNode<BrowserInputEvent> FindOldestNonTransitionInput()
    {
        for (var node = _pending.First; node != null; node = node.Next)
        {
            if (!IsStateTransition(node.Value))
            {
                return node;
            }
        }

        return null;
    }

    private LinkedListNode<BrowserInputEvent> FindOldestPressTransition()
    {
        for (var node = _pending.First; node != null; node = node.Next)
        {
            if (IsPressTransition(node.Value))
            {
                return node;
            }
        }

        return null;
    }

    private static bool IsStateTransition(BrowserInputEvent input) =>
        input.Type is BrowserInputType.MouseDown or BrowserInputType.MouseUp or
            BrowserInputType.KeyDown or BrowserInputType.KeyUp;

    private static bool IsPressTransition(BrowserInputEvent input) =>
        input.Type is BrowserInputType.MouseDown or BrowserInputType.KeyDown;

    private static bool IsReleaseTransition(BrowserInputEvent input) =>
        input.Type is BrowserInputType.MouseUp or BrowserInputType.KeyUp;

    private static bool IsCoalescibleInput(BrowserInputEvent input) =>
        CanCoalesceMouseMove(input) ||
        input.Type is BrowserInputType.MouseWheel or
            BrowserInputType.ScrollTo or
            BrowserInputType.ScrollAnimationTick;

    private static bool CanCoalesceMouseMove(BrowserInputEvent input) =>
        input.Type == BrowserInputType.MouseMove && input.Buttons == 0;
}
