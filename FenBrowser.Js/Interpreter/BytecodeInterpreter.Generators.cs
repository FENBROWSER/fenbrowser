using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Generator/async resumption helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 6. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{


    // An await in a body with no async activation behind it, which is what an
    // async generator is here: the body runs as a plain generator and each
    // result is wrapped in a resolved promise, so there is no context to put the
    // frame away in. Keep the historical inline behaviour rather than failing
    // the whole evaluation.
    internal JsValue AwaitWithNothingToSuspendInto(PromiseInstance instance)
    {
        if (instance.Promise.State == PromiseState.Fulfilled)
            return instance.Promise.GetResultUnchecked();

        if (instance.Promise.State == PromiseState.Rejected)
            throw new JsThrownException(instance.Promise.GetResultUnchecked());

        throw new JsThrownException(CreateTypeError("Pending await is not supported in this execution context."));
    }

    // Creates (or reuses) a NativeFunctionObject that resumes the given
    // AsyncContext when the awaited promise settles. The callback declares the
    // context handle in capturedRoots so GC traces ctx → registers → environment
    // for exactly as long as this reaction can fire (audit JSRT-004/JSRT-015).
    private NativeFunctionObject GetOrCreateAsyncResumeCallback(bool isReject, AsyncContext ctx)
    {
        // We always create a fresh callback, capturing the specific context.
        // Reuse of prototype patterns could be added as an optimization.
        return new NativeFunctionObject(
            isReject ? "asyncReject" : "asyncResolve",
            (_, args) =>
            {
                var arg = args.Count > 0 ? args[0] : JsValue.Undefined;
                // If this callback is called it means the Context is still alive,
                // so we can safely resume.
                return ResumeAsyncFunction(ctx, arg, isReject);
            },
            length: 1,
            capturedRoots: ctx.SelfHandle is { } self
                ? new[] { JsValue.FromObject(self) }
                : null);
    }

}


