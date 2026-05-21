namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1 - the [[PromiseState]] internal slot.
//
// Pending : the initial state; the promise has been created but neither fulfilled nor
//           rejected. Reactions queued while pending fire when the state settles.
// Fulfilled : terminal; the value is the resolved completion value.
// Rejected : terminal; the value is the rejection reason.
//
// State transitions are one-way: Pending -> Fulfilled or Pending -> Rejected, never
// the reverse and never between fulfilled/rejected.
public enum PromiseState : byte
{
    Pending,
    Fulfilled,
    Rejected,
}
