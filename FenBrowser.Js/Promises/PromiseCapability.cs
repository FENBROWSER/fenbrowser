using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1.1 PromiseCapability Records.
//
// A PromiseCapability is a triple {[[Promise]], [[Resolve]], [[Reject]]} created by
// NewPromiseCapability(C). The Promise is the externally-visible object; Resolve and
// Reject are the paired functions that settle it. The three values travel together so
// callers (async / await desugaring, Promise.all chunks, host APIs producing a
// promise to hand back to JS) can hold one struct rather than three JsValues.
//
// Stored as a readonly record struct so it can live in registers/stack frames without
// heap allocation. The Promise/Resolve/Reject JsValues themselves usually carry
// ObjectHandles into the JS heap.
public readonly record struct PromiseCapability(
    JsValue Promise,
    JsValue Resolve,
    JsValue Reject)
{
    public static PromiseCapability Empty
        => new(JsValue.Undefined, JsValue.Undefined, JsValue.Undefined);

    // IsCallable in the spec sense requires heap access to confirm the value is a
    // JsFunctionObject; this method is a fast shape-only check that the slot is at
    // least an object reference. Call sites that hold a heap should verify the
    // callable shape before invocation - this just rules out the trivial Undefined
    // case set by NewPromiseCapability before its resolving functions are installed.
    public bool IsComplete
        => Promise.Tag == JsValueTag.Object
            && Resolve.Tag == JsValueTag.Object
            && Reject.Tag == JsValueTag.Object;
}
