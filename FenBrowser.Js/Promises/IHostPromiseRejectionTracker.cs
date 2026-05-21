using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1.9 host hook. Embedders implement this to surface unhandled
// rejections (the standalone shell prints to stderr; FenBrowser-as-renderer fires the
// `unhandledrejection` / `rejectionhandled` events on the global object).
//
// The engine calls Track exactly when the spec mandates HostPromiseRejectionTracker:
//   * Whenever a Promise transitions to Rejected and there are no fulfill reactions
//     yet (initially-unhandled) -> operation = Reject.
//   * Whenever a previously-unhandled Promise has a then() attached after the fact ->
//     operation = Handle.
public interface IHostPromiseRejectionTracker
{
    void Track(JsValue promise, PromiseRejectionOperation operation);
}

// Default tracker that records every callback into an in-memory log. Used by the
// standalone shell at startup and by tests; the FenBrowser renderer replaces it with
// a tracker that fires DOM events.
public sealed class InMemoryPromiseRejectionTracker : IHostPromiseRejectionTracker
{
    private readonly List<TrackedEvent> _events = new();

    public IReadOnlyList<TrackedEvent> Events => _events;

    public int RejectedCount { get; private set; }
    public int HandledCount { get; private set; }

    public void Track(JsValue promise, PromiseRejectionOperation operation)
    {
        _events.Add(new TrackedEvent(promise, operation));
        switch (operation)
        {
            case PromiseRejectionOperation.Reject:
                RejectedCount++;
                break;
            case PromiseRejectionOperation.Handle:
                HandledCount++;
                break;
        }
    }

    public void Reset()
    {
        _events.Clear();
        RejectedCount = 0;
        HandledCount = 0;
    }

    public readonly record struct TrackedEvent(JsValue Promise, PromiseRejectionOperation Operation);
}
