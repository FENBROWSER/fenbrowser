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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));

        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 27.2.4.4 get Promise [ @@species ] — returns the this value so
        // subclasses default to constructing instances of themselves.
        var speciesId = GetWellKnownSymbolId("species");
        if (speciesId != 0)
        {
            var speciesGetter = new NativeFunctionObject("get [Symbol.species]", (tv, _) => tv, length: 0);
            var speciesGetterHandle = _heap.AllocateObject(speciesGetter, AllocationSite.Current());
            _ = constructor.DefineOwnSymbolProperty(
                speciesId,
                JsPropertyDescriptor.Accessor(
                    JsValue.FromObject(speciesGetterHandle),
                    JsValue.Undefined,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(constructorHandle, speciesGetterHandle);
        }

        // 27.2.4.6 Promise.resolve(value): C is the this value; an existing
        // promise whose .constructor is C round-trips unchanged, anything else
        // goes through NewPromiseCapability(C).
        DefineIntrinsicFunction(constructorHandle, constructor, "resolve", (thisValue, args) =>
        {
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Promise.resolve called on a non-object receiver."));
            }

            if (value.Tag == JsValueTag.Object &&
                _heap.GetObject(value.AsObjectHandle()) is PromiseInstance &&
                GetReceiverProperty(value, "constructor") is { } valueCtor &&
                valueCtor.Tag == JsValueTag.Object &&
                valueCtor.AsObjectHandle() == thisValue.AsObjectHandle())
            {
                return value;
            }

            var capability = NewPromiseCapability(thisValue);
            _ = CallFunction(capability.Resolve, new[] { value }, JsValue.Undefined);
            return capability.Promise;
        }, length: 1);

        // 27.2.4.5 Promise.reject(reason) — uses NewPromiseCapability(this).
        DefineIntrinsicFunction(constructorHandle, constructor, "reject", (thisValue, args) =>
        {
            var reason = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Promise.reject called on a non-object receiver."));
            }

            var capability = NewPromiseCapability(thisValue);
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
        // Invoke the receiver's own "then" property so thenables work correctly.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "catch", (thisValue, args) =>
        {
            var onRejected = args.Count > 0 ? args[0] : JsValue.Undefined;
            return InvokeMethod(thisValue, "then", new[] { JsValue.Undefined, onRejected });
        }, length: 1);

        // 27.2.5.3 Promise.prototype.finally(onFinally).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "finally", (thisValue, args) =>
        {
            var onFinally = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromisePrototypeFinally(thisValue, onFinally);
        }, length: 1);

        // 27.2.4.1 Promise.all(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "all", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAll(thisValue, iterable);
        }, length: 1);

        // 27.2.4.2 Promise.allSettled(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "allSettled", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAllSettled(thisValue, iterable);
        }, length: 1);

        // 27.2.4.3 Promise.any(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "any", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAny(thisValue, iterable);
        }, length: 1);

        // 27.2.4.5 Promise.race(iterable).
        DefineIntrinsicFunction(constructorHandle, constructor, "race", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseRace(thisValue, iterable);
        }, length: 1);

        // Promise.allKeyed(iterable) — ES2025.
        // Like Promise.all but accepts [key, value] pairs and resolves to an object.
        DefineIntrinsicFunction(constructorHandle, constructor, "allKeyed", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAllKeyed(thisValue, iterable);
        }, length: 1);

        // Promise.allSettledKeyed(iterable) — ES2025.
        // Like Promise.allSettled but accepts [key, value] pairs and resolves to an object.
        DefineIntrinsicFunction(constructorHandle, constructor, "allSettledKeyed", (thisValue, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            return PromiseAllSettledKeyed(thisValue, iterable);
        }, length: 1);

        // 27.2.4.8 Promise.withResolvers() — ES2024.
        // Returns { promise, resolve, reject } for the receiver constructor C.
        DefineIntrinsicFunction(constructorHandle, constructor, "withResolvers", (thisValue, args) =>
        {
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Promise.withResolvers called on a non-object receiver."));
            }
            var capability = NewPromiseCapability(thisValue);
            var obj = CreateOrdinaryObject();
            // ECMA-262 27.2.4.8 step 3: OrdinaryObjectCreate(%Object.prototype%)
            var rootMark = _heap.RootCount;
            var objHandle = _heap.AllocateObject(obj, AllocationSite.Current());
            _heap.PushRoot(objHandle);
            try
            {
                CreateDataProperty(objHandle, obj, "promise", capability.Promise);
                CreateDataProperty(objHandle, obj, "resolve", capability.Resolve);
                CreateDataProperty(objHandle, obj, "reject", capability.Reject);
            }
            finally
            {
                _heap.PopRootsTo(rootMark);
            }
            return JsValue.FromObject(objHandle);
        }, length: 0);

        // 27.2.4.9 Promise.try(callbackfn) — ES2025.
        // Calls callbackfn with no arguments and wraps the result in a Promise.
        DefineIntrinsicFunction(constructorHandle, constructor, "try", (thisValue, args) =>
        {
            var callbackfn = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callbackfn))
            {
                throw new JsThrownException(CreateTypeError("Promise.try: argument is not callable."));
            }
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Promise.try called on a non-object receiver."));
            }
            var capability = NewPromiseCapability(thisValue);
            try
            {
                var result = CallFunction(callbackfn, Array.Empty<JsValue>(), JsValue.Undefined);
                _ = CallFunction(capability.Resolve, new[] { result }, JsValue.Undefined);
            }
            catch (JsThrownException ex)
            {
                _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            }
            return capability.Promise;
        }, length: 1);

        _promiseConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // Drain an iterable into a list of JsValues using the engine's existing
    // for-of iterator path so Symbol.iterator dispatch works for Arrays, Sets,
    // and user-defined iterables alike.
    private List<JsValue> DrainIterableToListCapped(JsValue iterable, string operation)
        => DrainIterableToListCapped(iterable, operation, cap: 100_000);

    // 27.2.4.1.1 PerformPromiseAll. Returns a promise that fulfills with an
    // Array of values once every input promise fulfills, or rejects with the
    // first rejection. Uses a capped eager drain to avoid infinite loops from
    // never-ending iterators; the per-element resolve interleaving required by
    // spec is handled via try/catch on each promiseResolve call.
    private JsValue PromiseAll(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
        {
            return capability.Promise;
        }

        try
        {
            // ECMA-262 27.2.4.1.1: drain iterable. The spec calls promiseResolve
            // between each IteratorStep; we drain first for architectural
            // simplicity but cap iterations to prevent infinite loops from
            // never-ending iterators.
            var sources = DrainIterableToListCapped(iterable, "Promise.all", cap: 100_000);
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
                var idx = i;
                var child = CallFunction(promiseResolve, new[] { sources[i] }, thisValue);
                var onFulfilled = AllocateNativeCallback((_, fnArgs) =>
                {
                    slots[idx] = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                    remaining[0]--;
                    if (remaining[0] == 0)
                    {
                        var arr = CreateArrayFromElements(slots);
                        var handle = _heap.AllocateObject(arr, AllocationSite.Current());
                        _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(handle) }, JsValue.Undefined);
                    }
                    return JsValue.Undefined;
                });
                InvokePromiseThen(child, onFulfilled, capability.Reject);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
        }

        return capability.Promise;
    }

    // Drains iterable into a List with a hard iteration cap to guard against
    // never-ending iterators. Unlike the general DrainIteratorIntoList, this
    // enforces the cap per-item so we bail before CreateForOfIterator's eager
    // drain eats unlimited memory/CPU.
    private List<JsValue> DrainIterableToListCapped(JsValue iterable, string operation, int cap)
    {
        if (iterable.Tag == JsValueTag.Undefined || iterable.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(operation + ": argument is not iterable."));
        }

        // Bypass CreateForOfIterator's eager drain — get the raw iterator and
        // iterate by hand with a hard cap to prevent infinite loops.
        var values = new List<JsValue>();
        if (iterable.Tag != JsValueTag.Object)
        {
            return values;
        }

        var obj = _heap.GetObject(iterable.AsObjectHandle());
        var iterId = GetWellKnownSymbolId("iterator");
        if (iterId == 0 || !obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) ||
            iterDesc.Value.Tag != JsValueTag.Object)
        {
            return values;
        }

        var rawIter = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), iterable);
        if (rawIter.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(operation + ": @@iterator did not return an object."));
        }

        var rootMark = _heap.RootCount;
        var savedYoung = _heap.YoungAllocationsPerMinorGc;
        _heap.YoungAllocationsPerMinorGc = -1;
        try
        {
            _heap.PushRoot(rawIter.AsObjectHandle());
            var iterObj = _heap.GetObject(rawIter.AsObjectHandle());
            for (var count = 0; count < cap; count++)
            {
                if (!TryGetPropertyValue(iterObj, rawIter, "next", out var nextFn) ||
                    nextFn.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(operation + ": iterator missing 'next'."));
                }

                var result = CallFunction(nextFn, Array.Empty<JsValue>(), rawIter);
                if (result.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(operation + ": IteratorResult is not an object."));
                }

                _heap.PushRoot(result.AsObjectHandle());
                var resultObj = _heap.GetObject(result.AsObjectHandle());
                TryGetPropertyValue(resultObj, result, "done", out var doneVal);
                if (IsTruthy(doneVal))
                {
                    return values;
                }

                TryGetPropertyValue(resultObj, result, "value", out var value);
                if (value.Tag == JsValueTag.Object)
                    _heap.PushRoot(value.AsObjectHandle());
                values.Add(value);
            }
            throw new JsThrownException(CreateRangeError(
                operation + ": iterable exceeds maximum size."));
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
            _heap.YoungAllocationsPerMinorGc = savedYoung;
        }
    }

    // 27.2.4.2.1 PerformPromiseAllSettled.
    private JsValue PromiseAllSettled(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
        {
            return capability.Promise;
        }

        try
        {
            var sources = DrainIterableToListCapped(iterable, "Promise.allSettled");
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
                var child = CallFunction(promiseResolve, new[] { sources[i] }, thisValue);
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
                InvokePromiseThen(child, onFulfilled, onRejected);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
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
    private JsValue PromiseAny(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
        {
            return capability.Promise;
        }

        try
        {
            var sources = DrainIterableToListCapped(iterable, "Promise.any");
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
                var child = CallFunction(promiseResolve, new[] { sources[i] }, thisValue);
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
                InvokePromiseThen(child, capability.Resolve, onRejected);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
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
    private JsValue PromiseRace(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
        {
            return capability.Promise;
        }

        try
        {
            var sources = DrainIterableToListCapped(iterable, "Promise.race");
            // Empty iterable returns a never-settling promise per spec.
            foreach (var source in sources)
            {
                var child = CallFunction(promiseResolve, new[] { source }, thisValue);
                InvokePromiseThen(child, capability.Resolve, capability.Reject);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
        }

        return capability.Promise;
    }

    // Promise.allKeyed(iterable): like Promise.all but entries are [key, value] pairs.
    // Resolves to an object with keys mapped to resolved values.
    private JsValue PromiseAllKeyed(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
            return capability.Promise;

        try
        {
            var sources = DrainIterableToListCapped(iterable, "Promise.allKeyed");
            if (sources.Count == 0)
            {
                var emptyObj = CreateOrdinaryObject();
                var emptyHandle = _heap.AllocateObject(emptyObj, AllocationSite.Current());
                _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(emptyHandle) }, JsValue.Undefined);
                return capability.Promise;
            }

            var resultObj = CreateOrdinaryObject();
            var resultHandle = _heap.AllocateObject(resultObj, AllocationSite.Current());
            var remaining = new[] { sources.Count };

            for (var i = 0; i < sources.Count; i++)
            {
                var entry = sources[i];
                var key = GetKeyFromEntry(entry, i);
                var child = CallFunction(promiseResolve, new[] { entry }, thisValue);
                var onFulfilled = AllocateNativeCallback((_, fnArgs) =>
                {
                    var value = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                    CreateDataProperty(resultHandle, resultObj, key, value);
                    remaining[0]--;
                    if (remaining[0] == 0)
                        _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(resultHandle) }, JsValue.Undefined);
                    return JsValue.Undefined;
                });
                InvokePromiseThen(child, onFulfilled, capability.Reject);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
        }

        return capability.Promise;
    }

    // Promise.allSettledKeyed(iterable): like Promise.allSettled but entries are
    // [key, value] pairs. Resolves to an object with keys mapped to {status, value|reason}.
    private JsValue PromiseAllSettledKeyed(JsValue thisValue, JsValue iterable)
    {
        if (!TryPreparePromiseCombinator(thisValue, out var capability, out var promiseResolve))
            return capability.Promise;

        try
        {
            var sources = DrainIterableToListCapped(iterable, "Promise.allSettledKeyed");
            if (sources.Count == 0)
            {
                var emptyObj = CreateOrdinaryObject();
                var emptyHandle = _heap.AllocateObject(emptyObj, AllocationSite.Current());
                _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(emptyHandle) }, JsValue.Undefined);
                return capability.Promise;
            }

            var resultObj = CreateOrdinaryObject();
            var resultHandle = _heap.AllocateObject(resultObj, AllocationSite.Current());
            var remaining = new[] { sources.Count };

            for (var i = 0; i < sources.Count; i++)
            {
                var entry = sources[i];
                var key = GetKeyFromEntry(entry, i);
                var child = CallFunction(promiseResolve, new[] { entry }, thisValue);
                var onFulfilled = AllocateNativeCallback((_, fnArgs) =>
                {
                    var value = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                    var record = MakeSettledRecord("fulfilled", "value", value);
                    CreateDataProperty(resultHandle, resultObj, key, record);
                    remaining[0]--;
                    if (remaining[0] == 0)
                        _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(resultHandle) }, JsValue.Undefined);
                    return JsValue.Undefined;
                });
                var onRejected = AllocateNativeCallback((_, fnArgs) =>
                {
                    var reason = fnArgs.Count > 0 ? fnArgs[0] : JsValue.Undefined;
                    var record = MakeSettledRecord("rejected", "reason", reason);
                    CreateDataProperty(resultHandle, resultObj, key, record);
                    remaining[0]--;
                    if (remaining[0] == 0)
                        _ = CallFunction(capability.Resolve, new[] { JsValue.FromObject(resultHandle) }, JsValue.Undefined);
                    return JsValue.Undefined;
                });
                InvokePromiseThen(child, onFulfilled, onRejected);
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
        }

        return capability.Promise;
    }

    // Extract the key from a [key, value] entry pair.
    private string GetKeyFromEntry(JsValue entry, int index)
    {
        if (entry.Tag == JsValueTag.Object)
        {
            var entryObj = _heap.GetObject(entry.AsObjectHandle());
            if (TryGetPropertyValue(entryObj, entry, "0", out var keyVal) && keyVal.Tag != JsValueTag.Undefined)
                return ToStringValue(keyVal);
        }
        return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    // Exposes NewPromiseCapability to native builtins through IBuiltinContext so
    // a builtin (e.g. AsyncDisposableStack.prototype.disposeAsync) can hand back
    // a promise it settles itself.
    (JsValue Promise, JsValue Resolve, JsValue Reject) Builtins.IBuiltinContext.CreatePromiseCapability()
    {
        var capability = NewPromiseCapability();
        return (capability.Promise, capability.Resolve, capability.Reject);
    }

    // 7.3.22 SpeciesConstructor(O, %Promise%): read O.constructor, then its
    // @@species; undefined/null fall back to the native %Promise%.
    private JsValue PromiseSpeciesConstructor(JsValue promiseValue)
    {
        var defaultCtor = JsValue.FromObject(EnsurePromiseConstructor());
        var ctor = GetReceiverProperty(promiseValue, "constructor");
        if (ctor.Tag == JsValueTag.Undefined)
        {
            return defaultCtor;
        }

        if (ctor.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Promise constructor property is not an object."));
        }

        var speciesId = GetWellKnownSymbolId("species");
        var species = speciesId != 0 ? GetReceiverSymbolProperty(ctor, speciesId) : JsValue.Undefined;
        if (species.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            return defaultCtor;
        }

        return species;
    }

    // 27.2.1.5 NewPromiseCapability(C) for an arbitrary constructor: run
    // `new C(executor)` with an executor that captures the (resolve, reject)
    // pair, so Promise subclasses observe their constructor and executor
    // exactly as the spec prescribes. The native %Promise% takes the fast path.
    private PromiseCapability NewPromiseCapability(JsValue constructor)
    {
        if (_promiseConstructorHandle is { } nativeCtor &&
            constructor.Tag == JsValueTag.Object &&
            constructor.AsObjectHandle() == nativeCtor)
        {
            return NewPromiseCapability();
        }

        if (constructor.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Promise capability requires a constructor."));
        }

        var captured = new JsValue[] { JsValue.Undefined, JsValue.Undefined };
        var executor = new NativeFunctionObject("", (_, exArgs) =>
        {
            // 27.2.1.5.1 GetCapabilitiesExecutor steps 3-4: throw if [[Resolve]]
            // or [[Reject]] is already set to a non-undefined value. A prior call
            // with no arguments (or with undefined args) leaves the slots
            // undefined and is therefore not considered "already invoked".
            if (captured[0].Tag != JsValueTag.Undefined)
            {
                throw new JsThrownException(CreateTypeError("Promise executor has already been invoked."));
            }

            if (captured[1].Tag != JsValueTag.Undefined)
            {
                throw new JsThrownException(CreateTypeError("Promise executor has already been invoked."));
            }

            captured[0] = exArgs.Count > 0 ? exArgs[0] : JsValue.Undefined;
            captured[1] = exArgs.Count > 1 ? exArgs[1] : JsValue.Undefined;
            return JsValue.Undefined;
        }, length: 2);
        var executorHandle = _heap.AllocateObject(executor, AllocationSite.Current());

        var promise = ConstructFunction(constructor, new[] { JsValue.FromObject(executorHandle) });
        if (!IsCallable(captured[0]) || !IsCallable(captured[1]))
        {
            throw new JsThrownException(CreateTypeError("Promise constructor did not supply callable resolve/reject functions."));
        }

        return new PromiseCapability(promise, captured[0], captured[1]);
    }

    // 27.2.4.1.1-style prologue shared by the combinators: validate the receiver,
    // build the capability from it, and fetch C.resolve (IfAbruptRejectPromise:
    // a bad C.resolve rejects the capability rather than throwing).
    private bool TryPreparePromiseCombinator(
        JsValue thisValue,
        out PromiseCapability capability,
        out JsValue promiseResolve)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Promise combinator called on a non-object receiver."));
        }

        capability = NewPromiseCapability(thisValue);
        promiseResolve = JsValue.Undefined;
        try
        {
            var ctorObj = _heap.GetObject(thisValue.AsObjectHandle());
            if (!TryGetPropertyValue(ctorObj, thisValue, "resolve", out promiseResolve) ||
                !IsCallable(promiseResolve))
            {
                throw new JsThrownException(CreateTypeError("Promise combinator requires a callable 'resolve'."));
            }
        }
        catch (JsThrownException ex)
        {
            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
            return false;
        }

        return true;
    }

    // Invoke receiver.methodName(args) — calls the receiver's own property,
    // throwing TypeError if the property is not callable.
    private JsValue InvokeMethod(JsValue receiver, string methodName, JsValue[] args)
    {
        var method = GetReceiverProperty(receiver, methodName);
        if (!IsCallable(method))
        {
            throw new JsThrownException(CreateTypeError($"'{methodName}' is not callable."));
        }
        return CallFunction(method, args, receiver);
    }

    // Invoke(nextPromise, "then", handlers) — the spec goes through the value's
    // own (possibly overridden) then, not %Promise.prototype.then% directly.
    private void InvokePromiseThen(JsValue nextPromise, JsValue onFulfilled, JsValue onRejected)
    {
        var then = GetReceiverProperty(nextPromise, "then");
        if (!IsCallable(then))
        {
            throw new JsThrownException(CreateTypeError("Promise combinator: resolved value has no callable 'then'."));
        }

        _ = CallFunction(then, new[] { onFulfilled, onRejected }, nextPromise);
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
        // 27.2.5.4 step 3: the result promise comes from
        // SpeciesConstructor(promise, %Promise%) so subclasses chain into their
        // own type.
        var resultCapability = NewPromiseCapability(PromiseSpeciesConstructor(thisValue));
        return PerformPromiseThen(thisValue.AsObjectHandle(), instance.Promise,
            fulfillHandler, rejectHandler, resultCapability);
    }

    // 27.2.5.3 Promise.prototype.finally(onFinally). Invokes the receiver's
    // own "then" property so thenables work correctly.
    private JsValue PromisePrototypeFinally(JsValue thisValue, JsValue onFinally)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Promise.prototype.finally called on non-object."));
        }

        if (!IsCallable(onFinally))
        {
            // 27.2.5.3 step 5 - non-callable onFinally degenerates to this.then(undefined, undefined).
            return InvokeMethod(thisValue, "then", new[] { JsValue.Undefined, JsValue.Undefined });
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
        return InvokeMethod(thisValue, "then", new[] { JsValue.FromObject(thenHandle), JsValue.FromObject(catchHandle) });
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
