using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// D.6 - Promise constructor, prototype, statics, and the spec algorithms that
// schedule and run PromiseJobs. The module is partial so the existing 10k-line
// interpreter file stays focused on bytecode dispatch while the spec-heavy
// Promise plumbing has its own home.
//
// References throughout: ECMA-262 27.2 Promise Objects.
public sealed partial class BytecodeInterpreter
{
    private const int DefaultPromiseRealmId = 0;

    private ObjectHandle EnsurePromiseConstructor()
    {
        if (_promiseConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);
        _promisePrototypeHandle = prototypeHandle;

        // ECMA-262 27.2.5.5 Promise.prototype [ @@toStringTag ] = "Promise".
        DefineBuiltinToStringTag(prototype, "Promise");

        var constructor = new NativeFunctionObject(
            "Promise",
            (_, _) => throw new JsThrownException(CreateTypeError("Promise constructor must be invoked with 'new'.")),
            args => PromiseConstruct(args),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));

        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 27.2.4.6 Promise.resolve(value).
        DefineIntrinsicFunction(constructorHandle, constructor, "resolve", (_, args) =>
        {
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseResolveStatic(value);
        }, length: 1);

        // 27.2.4.5 Promise.reject(reason).
        DefineIntrinsicFunction(constructorHandle, constructor, "reject", (_, args) =>
        {
            var reason = args.Count > 0 ? args[0] : JsValue.Undefined;
            var capability = NewPromiseCapability();
            _ = CallFunction(capability.Reject, new[] { reason }, JsValue.Undefined);
            return capability.Promise;
        }, length: 1);

        // 27.2.5.4 Promise.prototype.then.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "then", (thisValue, args) =>
        {
            var onFulfilled = args.Count > 0 ? args[0] : JsValue.Undefined;
            var onRejected = args.Count > 1 ? args[1] : JsValue.Undefined;
            return PromisePrototypeThen(thisValue, onFulfilled, onRejected);
        }, length: 2);

        // 27.2.5.1 Promise.prototype.catch(onRejected) === this.then(undefined, onRejected).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "catch", (thisValue, args) =>
        {
            var onRejected = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromisePrototypeThen(thisValue, JsValue.Undefined, onRejected);
        }, length: 1);

        // 27.2.5.3 Promise.prototype.finally(onFinally).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "finally", (thisValue, args) =>
        {
            var onFinally = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromisePrototypeFinally(thisValue, onFinally);
        }, length: 1);

        // 27.2.4.1 Promise.all(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "all", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAll(iterable);
        }, length: 1);

        // 27.2.4.2 Promise.allSettled(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "allSettled", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAllSettled(iterable);
        }, length: 1);

        // 27.2.4.3 Promise.any(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "any", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAny(iterable);
        }, length: 1);

        // 27.2.4.5 Promise.race(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "race", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseRace(iterable);
        }, length: 1);

        _promiseConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // Drain an iterable into a list of JsValues using the engine's existing
    // for-of iterator path so Symbol.iterator dispatch works for Arrays, Sets,
    // and user-defined iterables alike.
    private List<JsValue> DrainIterableToList(JsValue iterable, string operation)
    {
        if (iterable.Tag == JsValueTag.Undefined || iterable.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(operation + ": argument is not iterable."));
        }

        var values = new List<JsValue>();
        var iter = CreateForOfIterator(iterable);
        if (iter.Tag == JsValueTag.Object &&
            _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
        {
            while (forOf.TryMoveNext(out var v))
            {
                values.Add(v);
            }
        }
        return values;
    }

    // 27.2.4.1.1 PerformPromiseAll. Returns a promise that fulfills with an
    // Array of values once every input promise fulfills, or rejects with the
    // first rejection.
    private JsValue PromiseAll(JsValue iterable)
    {
        var capability = NewPromiseCapability();
        List<JsValue> sources;
        try
        {
            sources = DrainIterableToList(iterable, "Promise.all");
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            return capability.Promise;
        }

        if (sources.Count == 0)
        {
            var emptyArr = CreateArrayFromElements(Array.Empty<JsValue>());
            var emptyHandle = _heap.AllocateObject(emptyArr, AllocationSite.Current());
            _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(emptyHandle) }, JsValue.Undefined);
            return capability.Promise;
        }

        var slots = new JsValue[sources.Count];
        for (var i = 0; i < slots.Length; i++) slots[i] = JsValue.Undefined;
        var remaining = new[] { sources.Count };

        for (var i = 0; i < sources.Count; i++)
        {
            var index = i;
            var child = PromiseResolveStatic(sources[i]);
            var onFulfilled = AllocateNativeCallback((_, fnArgs) =>
            {
                slots[index] = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                remaining[0]--;
                if (remaining[0] == 0)
                {
                    var arr = CreateArrayFromElements(slots);
                    var handle = _heap.AllocateObject(arr, AllocationSite.Current());
                    _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(handle) }, JsValue.Undefined);
                }
                return JsValue.Undefined;
            });
            _ = PromisePrototypeThen(child, onFulfilled, capability.Reject);
        }

        return capability.Promise;
    }

    // 27.2.4.2.1 PerformPromiseAllSettled.
    private JsValue PromiseAllSettled(JsValue iterable)
    {
        var capability = NewPromiseCapability();
        List<JsValue> sources;
        try
        {
            sources = DrainIterableToList(iterable, "Promise.allSettled");
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            return capability.Promise;
        }

        if (sources.Count == 0)
        {
            var emptyArr = CreateArrayFromElements(Array.Empty<JsValue>());
            var emptyHandle = _heap.AllocateObject(emptyArr, AllocationSite.Current());
            _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(emptyHandle) }, JsValue.Undefined);
            return capability.Promise;
        }

        var slots = new JsValue[sources.Count];
        for (var i = 0; i < slots.Length; i++) slots[i] = JsValue.Undefined;
        var remaining = new[] { sources.Count };

        void TrySettleAggregate()
        {
            remaining[0]--;
            if (remaining[0] == 0)
            {
                var arr = CreateArrayFromElements(slots);
                var handle = _heap.AllocateObject(arr, AllocationSite.Current());
                _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(handle) }, JsValue.Undefined);
            }
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var index = i;
            var child = PromiseResolveStatic(sources[i]);
            var onFulfilled = AllocateNativeCallback((_, fnArgs) =>
            {
                var v = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                slots[index] = MakeSettledRecord("fulfilled", "value", v);
                TrySettleAggregate();
                return JsValue.Undefined;
            });
            var onRejected = AllocateNativeCallback((_, fnArgs) =>
            {
                var r = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                slots[index] = MakeSettledRecord("rejected", "reason", r);
                TrySettleAggregate();
                return JsValue.Undefined;
            });
            _ = PromisePrototypeThen(child, onFulfilled, onRejected);
        }

        return capability.Promise;
    }

    private JsValue MakeSettledRecord(string status, string payloadKey, JsValue payload)
    {
        var obj = CreateOrdinaryObject();
        _ = obj.DefineOwnProperty("status",
            new Objects.JsPropertyDescriptor(JsValue.FromString(status), Writable: true, Enumerable: true, Configurable: true));
        _ = obj.DefineOwnProperty(payloadKey,
            new Objects.JsPropertyDescriptor(payload, Writable: true, Enumerable: true, Configurable: true));
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    // 27.2.4.3.1 PerformPromiseAny - aggregates rejections into an AggregateError
    // when every input rejects; fulfills with the first fulfillment otherwise.
    private JsValue PromiseAny(JsValue iterable)
    {
        var capability = NewPromiseCapability();
        List<JsValue> sources;
        try
        {
            sources = DrainIterableToList(iterable, "Promise.any");
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            return capability.Promise;
        }

        if (sources.Count == 0)
        {
            _ = CallFunction(capability.Reject,
                new[] { BuildAggregateError(Array.Empty<JsValue>()) },
                JsValue.Undefined);
            return capability.Promise;
        }

        var errors = new JsValue[sources.Count];
        for (var i = 0; i < errors.Length; i++) errors[i] = JsValue.Undefined;
        var remaining = new[] { sources.Count };

        for (var i = 0; i < sources.Count; i++)
        {
            var index = i;
            var child = PromiseResolveStatic(sources[i]);
            var onRejected = AllocateNativeCallback((_, fnArgs) =>
            {
                errors[index] = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                remaining[0]--;
                if (remaining[0] == 0)
                {
                    _ = CallFunction(capability.Reject, new[] { BuildAggregateError(errors) }, JsValue.Undefined);
                }
                return JsValue.Undefined;
            });
            _ = PromisePrototypeThen(child, capability.Resolve, onRejected);
        }

        return capability.Promise;
    }

    private JsValue BuildAggregateError(IReadOnlyList<JsValue> errors)
    {
        var errArr = CreateArrayFromElements(errors.ToArray());
        var errArrHandle = _heap.AllocateObject(errArr, AllocationSite.Current());

        var err = new JsObject();
        err.SetPrototype(EnsureAggregateErrorPrototype());
        _ = err.DefineOwnProperty("errors",
            new Objects.JsPropertyDescriptor(JsValue.FromObject(errArrHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _ = err.DefineOwnProperty("message",
            new Objects.JsPropertyDescriptor(JsValue.FromString("All promises were rejected"),
                Writable: true, Enumerable: false, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(err, AllocationSite.Current()));
    }

    // 27.2.4.5.1 PerformPromiseRace - settles with the first input that settles.
    private JsValue PromiseRace(JsValue iterable)
    {
        var capability = NewPromiseCapability();
        List<JsValue> sources;
        try
        {
            sources = DrainIterableToList(iterable, "Promise.race");
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            return capability.Promise;
        }

        // Empty iterable returns a never-settling promise per spec.
        foreach (var source in sources)
        {
            var child = PromiseResolveStatic(source);
            _ = PromisePrototypeThen(child, capability.Resolve, capability.Reject);
        }

        return capability.Promise;
    }

    private JsValue AllocateNativeCallback(Func<JsValue, IReadOnlyList<JsValue>, JsValue> call)
    {
        var fn = new NativeFunctionObject("", call, length: 1);
        var handle = _heap.AllocateObject(fn, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    // 27.2.3.1 Promise(executor). executor is called synchronously with the
    // resolving functions; if it throws, the promise is rejected with the thrown
    // value (step 11). Returns the JsValue for the new promise.
    private JsValue PromiseConstruct(IReadOnlyList<JsValue> args)
    {
        var executor = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (!IsCallable(executor))
        {
            throw new JsThrownException(CreateTypeError("Promise executor must be callable."));
        }

        var promise = new PromiseObject();
        var instance = new PromiseInstance(promise);
        if (_promisePrototypeHandle is { } proto)
        {
            instance.SetPrototype(proto);
        }

        var promiseHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        var promiseValue = JsValue.FromObject(promiseHandle);

        var (resolveFn, rejectFn) = CreateResolvingFunctions(promiseHandle);

        try
        {
            _ = CallFunction(executor, new[] { resolveFn, rejectFn }, JsValue.Undefined);
        }
        catch (JsThrownException ex)
        {
            // 27.2.3.1 step 11.a - executor threw, route to reject.
            _ = CallFunction(rejectFn, new[] { ex.Value }, JsValue.Undefined);
        }

        return promiseValue;
    }

    // 27.2.4.6 Promise.resolve(value). If value is already a promise whose
    // constructor is %Promise%, return it; otherwise allocate a new promise and
    // resolve it with value (the resolve step handles thenables).
    private JsValue PromiseResolveStatic(JsValue value)
    {
        if (value.Tag == JsValueTag.Object && _heap.GetObject(value.AsObjectHandle()) is PromiseInstance existing)
        {
            // Spec checks the constructor identity; we have a single realm/single
            // %Promise% so any PromiseInstance round-trips unchanged.
            _ = existing;
            return value;
        }

        var capability = NewPromiseCapability();
        _ = CallFunction(capability.Resolve, new[] { value }, JsValue.Undefined);
        return capability.Promise;
    }

    // 27.2.1.5 NewPromiseCapability(%Promise%). Bundles a fresh promise with its
    // resolve/reject closures, suitable for places that want to feed a promise
    // from outside its constructor (Promise.reject, derived .then, etc.).
    private PromiseCapability NewPromiseCapability()
    {
        var promise = new PromiseObject();
        var instance = new PromiseInstance(promise);
        if (_promisePrototypeHandle is { } proto)
        {
            instance.SetPrototype(proto);
        }
        var handle = _heap.AllocateObject(instance, AllocationSite.Current());
        var (resolveFn, rejectFn) = CreateResolvingFunctions(handle);
        return new PromiseCapability(JsValue.FromObject(handle), resolveFn, rejectFn);
    }

    // 27.2.1.3 CreateResolvingFunctions(promise). Returns the (resolve, reject)
    // pair, each backed by a shared "already resolved" latch so subsequent calls
    // become no-ops. resolve handles the thenable-resolution shuffle by routing
    // foreign thenables through a PromiseResolveThenableJob.
    private (JsValue Resolve, JsValue Reject) CreateResolvingFunctions(ObjectHandle promiseHandle)
    {
        var alreadyResolved = new bool[1];

        var resolveFn = new NativeFunctionObject("", (_, fnArgs) =>
        {
            if (alreadyResolved[0]) return JsValue.Undefined;
            alreadyResolved[0] = true;
            var resolution = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
            ResolvePromise(promiseHandle, resolution);
            return JsValue.Undefined;
        }, length: 1);

        var rejectFn = new NativeFunctionObject("", (_, fnArgs) =>
        {
            if (alreadyResolved[0]) return JsValue.Undefined;
            alreadyResolved[0] = true;
            var reason = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
            RejectPromise(promiseHandle, reason);
            return JsValue.Undefined;
        }, length: 1);

        var resolveHandle = _heap.AllocateObject(resolveFn, AllocationSite.Current());
        var rejectHandle = _heap.AllocateObject(rejectFn, AllocationSite.Current());
        return (JsValue.FromObject(resolveHandle), JsValue.FromObject(rejectHandle));
    }

    // 27.2.1.3.2 Promise Resolve Functions. Self-resolution becomes a TypeError
    // rejection; thenable resolution is deferred to a PromiseResolveThenableJob;
    // non-objects and non-thenable objects settle the promise immediately.
    private void ResolvePromise(ObjectHandle promiseHandle, JsValue resolution)
    {
        if (_heap.GetObject(promiseHandle) is not PromiseInstance instance)
        {
            return;
        }

        var promise = instance.Promise;

        if (resolution.Tag == JsValueTag.Object && resolution.AsObjectHandle().Equals(promiseHandle))
        {
            RejectPromise(promiseHandle, CreateTypeError("Chaining cycle detected for promise."));
            return;
        }

        if (resolution.Tag != JsValueTag.Object)
        {
            FulfillPromise(promiseHandle, resolution);
            return;
        }

        // Thenable detection: read .then off the object. A throwing getter must
        // become a rejection (27.2.1.3.2 step 8).
        var resolutionObj = _heap.GetObject(resolution.AsObjectHandle());
        JsValue then;
        try
        {
            if (!TryGetPropertyValue(resolutionObj, resolution, "then", out then))
            {
                FulfillPromise(promiseHandle, resolution);
                return;
            }
        }
        catch (JsThrownException ex)
        {
            RejectPromise(promiseHandle, ex.Value);
            return;
        }

        if (!IsCallable(then))
        {
            FulfillPromise(promiseHandle, resolution);
            return;
        }

        // 27.2.2.2 NewPromiseResolveThenableJob - run thenable.then(resolve, reject)
        // off the microtask queue so observers see deferred ordering even when the
        // thenable is "synchronous".
        _jobQueue.Enqueue(new PromiseResolveThenableJob(
            JsValue.FromObject(promiseHandle), resolution, then, DefaultPromiseRealmId));
    }

    // 27.2.1.4 FulfillPromise(promise, value).
    private void FulfillPromise(ObjectHandle promiseHandle, JsValue value)
    {
        if (_heap.GetObject(promiseHandle) is not PromiseInstance instance) return;
        var promise = instance.Promise;
        if (!promise.TrySettle(PromiseState.Fulfilled, value)) return;

        var reactions = promise.DrainFulfillReactions();
        foreach (var reactionHandle in reactions)
        {
            EnqueueReactionJob(reactionHandle, value);
        }
    }

    // 27.2.1.7 RejectPromise(promise, reason). If the promise is unhandled at
    // settle-time we notify the rejection tracker; .then attachments later flip
    // IsHandled and the tracker is informed via HandlerAdded.
    private void RejectPromise(ObjectHandle promiseHandle, JsValue reason)
    {
        if (_heap.GetObject(promiseHandle) is not PromiseInstance instance) return;
        var promise = instance.Promise;
        if (!promise.TrySettle(PromiseState.Rejected, reason)) return;

        if (!promise.IsHandled)
        {
            _promiseRejectionTracker.Track(JsValue.FromObject(promiseHandle), PromiseRejectionOperation.Reject);
        }

        var reactions = promise.DrainRejectReactions();
        foreach (var reactionHandle in reactions)
        {
            EnqueueReactionJob(reactionHandle, reason);
        }
    }

    private void EnqueueReactionJob(ObjectHandle reactionHandle, JsValue argument)
    {
        if (_heap.GetObject(reactionHandle) is not ReactionCell cell) return;
        _jobQueue.Enqueue(new PromiseReactionJob(cell.Reaction, argument, DefaultPromiseRealmId));
    }

    // 27.2.5.4 Promise.prototype.then(onFulfilled, onRejected). Returns a fresh
    // child promise; reactions are either queued on the receiver (when still
    // pending) or scheduled immediately as PromiseReactionJobs.
    private JsValue PromisePrototypeThen(JsValue thisValue, JsValue onFulfilled, JsValue onRejected)
    {
        if (thisValue.Tag != JsValueTag.Object
            || _heap.GetObject(thisValue.AsObjectHandle()) is not PromiseInstance instance)
        {
            throw new JsThrownException(CreateTypeError("Promise.prototype.then called on non-Promise."));
        }

        var fulfillHandler = IsCallable(onFulfilled) ? onFulfilled : JsValue.Undefined;
        var rejectHandler = IsCallable(onRejected) ? onRejected : JsValue.Undefined;
        var resultCapability = NewPromiseCapability();
        return PerformPromiseThen(thisValue.AsObjectHandle(), instance.Promise,
            fulfillHandler, rejectHandler, resultCapability);
    }

    // 27.2.5.3 Promise.prototype.finally(onFinally). Spec: chain a then with
    // through-handlers that invoke onFinally with no args and propagate (or
    // re-throw) the underlying settlement.
    private JsValue PromisePrototypeFinally(JsValue thisValue, JsValue onFinally)
    {
        if (!IsCallable(onFinally))
        {
            // 27.2.5.3 step 5 - non-callable onFinally degenerates to then(undef, undef).
            return PromisePrototypeThen(thisValue, JsValue.Undefined, JsValue.Undefined);
        }

        var thenFinally = new NativeFunctionObject("", (_, args) =>
        {
            _ = CallFunction(onFinally, Array.Empty<JsValue>(), JsValue.Undefined);
            return args.Count > 0 ? args[0] : JsValue.Undefined;
        }, length: 1);

        var catchFinally = new NativeFunctionObject("", (_, args) =>
        {
            _ = CallFunction(onFinally, Array.Empty<JsValue>(), JsValue.Undefined);
            throw new JsThrownException(args.Count > 0 ? args[0] : JsValue.Undefined);
        }, length: 1);

        var thenHandle = _heap.AllocateObject(thenFinally, AllocationSite.Current());
        var catchHandle = _heap.AllocateObject(catchFinally, AllocationSite.Current());
        return PromisePrototypeThen(thisValue, JsValue.FromObject(thenHandle), JsValue.FromObject(catchHandle));
    }

    // 27.2.6.1 PerformPromiseThen.
    private JsValue PerformPromiseThen(
        ObjectHandle promiseHandle,
        PromiseObject promise,
        JsValue onFulfilled,
        JsValue onRejected,
        PromiseCapability resultCapability)
    {
        var fulfillReaction = new PromiseReaction(resultCapability, PromiseReactionType.Fulfill, onFulfilled);
        var rejectReaction = new PromiseReaction(resultCapability, PromiseReactionType.Reject, onRejected);
        var fulfillHandle = AllocateReactionCell(fulfillReaction);
        var rejectHandle = AllocateReactionCell(rejectReaction);

        switch (promise.State)
        {
            case PromiseState.Pending:
                promise.QueueFulfillReaction(fulfillHandle);
                promise.QueueRejectReaction(rejectHandle);
                break;
            case PromiseState.Fulfilled:
                _jobQueue.Enqueue(new PromiseReactionJob(fulfillReaction, promise.GetResultUnchecked(), DefaultPromiseRealmId));
                break;
            case PromiseState.Rejected:
                if (!promise.IsHandled)
                {
                    _promiseRejectionTracker.Track(JsValue.FromObject(promiseHandle), PromiseRejectionOperation.Handle);
                }
                _jobQueue.Enqueue(new PromiseReactionJob(rejectReaction, promise.GetResultUnchecked(), DefaultPromiseRealmId));
                break;
        }

        promise.IsHandled = true;
        return resultCapability.Promise;
    }

    private ObjectHandle AllocateReactionCell(PromiseReaction reaction)
    {
        return _heap.AllocateObject(new ReactionCell(reaction), AllocationSite.Current());
    }

    // Heap-resident wrapper for a PromiseReaction so it can live in PromiseObject's
    // pending reaction list as an ObjectHandle and be traced by the GC.
    private sealed class ReactionCell : JsObject
    {
        public ReactionCell(PromiseReaction reaction)
        {
            Reaction = reaction;
        }

        public PromiseReaction Reaction { get; }

        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            Reaction.Trace(tracer);
        }
    }

    // The interpreter's PromiseJob runner. Returns true to continue the
    // microtask checkpoint; false stops it (kept as a hook for future fatal-error
    // routing - today nothing short-circuits the checkpoint).
    private bool RunPromiseJob(PromiseJob job)
    {
        switch (job)
        {
            case PromiseReactionJob reactionJob:
                RunPromiseReactionJob(reactionJob);
                return true;
            case PromiseResolveThenableJob thenableJob:
                RunPromiseResolveThenableJob(thenableJob);
                return true;
            default:
                return true;
        }
    }

    // 27.2.2.1 NewPromiseReactionJob abstract closure. Invokes the handler if any,
    // and pushes the outcome through the capability so the chained promise settles.
    private void RunPromiseReactionJob(PromiseReactionJob job)
    {
        var reaction = job.Reaction;
        var capability = reaction.Capability;
        JsValue handlerResult;
        bool threw = false;

        if (!reaction.HasHandler)
        {
            // 27.2.2.1 step 5/6: missing handler passes the argument through.
            handlerResult = job.Argument;
            threw = reaction.Type == PromiseReactionType.Reject;
        }
        else
        {
            try
            {
                handlerResult = CallFunction(reaction.Handler, new[] { job.Argument }, JsValue.Undefined);
            }
            catch (JsThrownException ex)
            {
                handlerResult = ex.Value;
                threw = true;
            }
        }

        if (threw)
        {
            _ = CallFunction(capability.Reject, new[] { handlerResult }, JsValue.Undefined);
        }
        else
        {
            _ = CallFunction(capability.Resolve, new[] { handlerResult }, JsValue.Undefined);
        }
    }

    // 27.2.2.2 NewPromiseResolveThenableJob - thenable.then(resolve, reject).
    private void RunPromiseResolveThenableJob(PromiseResolveThenableJob job)
    {
        if (job.PromiseToResolve.Tag != JsValueTag.Object) return;
        var promiseHandle = job.PromiseToResolve.AsObjectHandle();
        if (_heap.GetObject(promiseHandle) is not PromiseInstance) return;

        var (resolveFn, rejectFn) = CreateResolvingFunctions(promiseHandle);
        try
        {
            _ = CallFunction(job.Then, new[] { resolveFn, rejectFn }, job.Thenable);
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(rejectFn, new[] { ex.Value }, JsValue.Undefined);
        }
    }
}
