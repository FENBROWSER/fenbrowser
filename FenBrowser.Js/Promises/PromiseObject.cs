using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.6 Properties of Promise Instances.
//
// Internal slots modeled here:
//   [[PromiseState]]            - PromiseState enum
//   [[PromiseResult]]           - JsValue (set when state settles)
//   [[PromiseFulfillReactions]] - queue of reactions to run on fulfillment
//   [[PromiseRejectReactions]]  - queue of reactions to run on rejection
//   [[PromiseIsHandled]]        - has any then/catch attached to it yet?
//
// The reactions lists are stored as object handles (each reaction is a heap-allocated
// PromiseReaction record landed in D.3). At this commit the lists are kept generic so
// the type compiles without the reaction type, and reactions can be added via the
// raw handle API. The tracing path traces every queued reaction handle plus the
// settled value so resolved-but-not-yet-collected promises keep their reactions
// reachable.
public sealed class PromiseObject : ITraceable
{
    private readonly List<ObjectHandle> _fulfillReactions = new();
    private readonly List<ObjectHandle> _rejectReactions = new();
    private JsValue _result;

    public PromiseObject()
    {
        State = PromiseState.Pending;
        _result = JsValue.Undefined;
        IsHandled = false;
    }

    public PromiseState State { get; private set; }

    public bool IsHandled { get; set; }

    // 27.2.1.4 FulfillPromise / 27.2.1.7 RejectPromise: caller asserts state is
    // Pending. Returns false when the promise has already settled so the caller can
    // ignore the duplicate settle attempt (matches the spec's "Assert" plus the
    // common idempotent resolver semantics in CreateResolvingFunctions).
    public bool TrySettle(PromiseState terminalState, JsValue value)
    {
        if (terminalState is not (PromiseState.Fulfilled or PromiseState.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState),
                "Promises only settle to Fulfilled or Rejected.");
        }

        if (State != PromiseState.Pending)
        {
            return false;
        }

        State = terminalState;
        _result = value;
        return true;
    }

    public JsValue GetResultUnchecked() => _result;

    // 27.2.6.1 PerformPromiseThen step "Append fulfillReaction as the last element of
    // the List that is the value of promise.[[PromiseFulfillReactions]]." The list
    // exists only while the promise is Pending; once settled it is cleared so the
    // reactions can be GC'd.
    public void QueueFulfillReaction(ObjectHandle reaction)
    {
        if (State != PromiseState.Pending)
        {
            return;
        }

        _fulfillReactions.Add(reaction);
    }

    public void QueueRejectReaction(ObjectHandle reaction)
    {
        if (State != PromiseState.Pending)
        {
            return;
        }

        _rejectReactions.Add(reaction);
    }

    public IReadOnlyList<ObjectHandle> DrainFulfillReactions()
    {
        var copy = _fulfillReactions.ToArray();
        _fulfillReactions.Clear();
        return copy;
    }

    public IReadOnlyList<ObjectHandle> DrainRejectReactions()
    {
        var copy = _rejectReactions.ToArray();
        _rejectReactions.Clear();
        return copy;
    }

    public int FulfillReactionCountForTest => _fulfillReactions.Count;
    public int RejectReactionCountForTest => _rejectReactions.Count;

    public void Trace(IHeapTracer tracer)
    {
        if (_result.Tag == JsValueTag.Object)
        {
            tracer.Trace(_result.AsObjectHandle());
        }

        foreach (var reaction in _fulfillReactions)
        {
            tracer.Trace(reaction);
        }

        foreach (var reaction in _rejectReactions)
        {
            tracer.Trace(reaction);
        }
    }
}
