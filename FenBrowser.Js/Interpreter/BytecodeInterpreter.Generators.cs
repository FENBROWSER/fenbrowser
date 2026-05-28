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

    // ECMA-262 27.7.5.2 Await(value).
    // If the awaited value is a settled promise, return (or throw) its result
    // immediately so the interpreter can continue without suspension. If the
    // promise is pending, save the frame state to the AsyncContext and attach
    // fulfill/reject handlers that will resume execution when the promise settles.
    private JsValue AwaitValue(InterpreterFrame frame, JsValue value, int destReg)
    {
        var awaitedPromise = PromiseResolveStatic(value);
        if (awaitedPromise.Tag != JsValueTag.Object ||
            _heap.GetObject(awaitedPromise.AsObjectHandle()) is not PromiseInstance instance)
        {
            return value;
        }

        if (instance.Promise.State == PromiseState.Fulfilled)
            return instance.Promise.GetResultUnchecked();

        if (instance.Promise.State == PromiseState.Rejected)
            throw new JsThrownException(instance.Promise.GetResultUnchecked());

        if (frame.AsyncContext is null)
        {
            throw new JsThrownException(CreateTypeError("Pending await is not supported in this execution context."));
        }

        // Pending â€” suspend the async frame.
        SaveAsyncState(frame, destReg);

        var ctx = frame.AsyncContext!;
        var onFulfilled = GetOrCreateAsyncResumeCallback(isReject: false, ctx);
        var onRejected = GetOrCreateAsyncResumeCallback(isReject: true, ctx);
        var onFulfilledHandle = _heap.AllocateObject(onFulfilled, AllocationSite.Current());
        var onRejectedHandle = _heap.AllocateObject(onRejected, AllocationSite.Current());

        // Attach handlers to the awaited promise.
        PerformPromiseThen(
            awaitedPromise.AsObjectHandle(),
            instance.Promise,
            JsValue.FromObject(onFulfilledHandle),
            JsValue.FromObject(onRejectedHandle),
            GetDummyCapability());

        instance.Promise.IsHandled = true;
        return JsValue.Undefined;
    }


    // Creates (or reuses) a NativeFunctionObject that resumes the given
    // AsyncContext when the awaited promise settles. The callback captures
    // the async context by its ObjectHandle so GC can trace it.
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
            length: 1);
    }

}


