namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1.2 [[Type]] slot of a PromiseReaction Record. Determines whether the
// reaction was attached to the fulfillment path (then/finally fulfill side) or the
// rejection path (catch/finally reject side) of the parent promise.
public enum PromiseReactionType : byte
{
    Fulfill,
    Reject,
}
