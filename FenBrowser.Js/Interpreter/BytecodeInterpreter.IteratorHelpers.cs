using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-262 27.1.4 Iterator Helpers (map/filter/take/drop/flatMap) + Iterator.concat.
//
// The helpers are LAZY: %Iterator.prototype%.map/filter/take/drop/flatMap each
// return an Iterator Helper object that pulls one value at a time from the
// underlying iterator on every .next() call, applying the per-method transform
// on demand. This is what the spec mandates (CreateIteratorFromClosure) and is
// observable: tests assert the underlying .next is read exactly once
// (GetIteratorDirect), the mapper/predicate runs lazily, abrupt completions
// close the underlying iterator (IfAbruptCloseIterator), and .return() forwards
// to the underlying iterator's return method. The previous eager
// DrainSelfAsList implementation hung on infinite iterators and broke every
// laziness / return-forwarding assertion.
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle? _iteratorHelperPrototypeHandle;

    private enum IteratorHelperKind
    {
        Map,
        Filter,
        Take,
        Drop,
        FlatMap,
        Concat,
        Zip,
    }

    // The Iterator Helper exotic object. Instead of capturing per-helper closure
    // state in C# delegates (which the GC cannot trace), every piece of mutable
    // state lives in explicit, traced slots so the helper survives collection.
    private sealed class IteratorHelperObject : JsObject
    {
        public IteratorHelperKind Kind;

        // The underlying Iterator Record (27.1.4 GetIteratorDirect): the iterator
        // object and its "next" method, captured once at helper creation.
        public JsValue Underlying;
        public JsValue UnderlyingNext;

        // map/filter/flatMap callback; counter passed to it as the 2nd argument.
        public JsValue Callback;
        public long Counter;

        // take/drop remaining count (may be +Infinity). Drop primes once.
        public double Remaining;
        public bool DropPrimed;

        // flatMap / concat inner Iterator Record currently being drained.
        public JsValue Inner;
        public JsValue InnerNext;
        public bool HasInner;

        // concat: the validated (iterable, @@iterator method) records + cursor.
        public List<(JsValue Iterable, JsValue Method)>? ConcatRecords;
        public int ConcatIndex;

        // Iterator.zip / zipKeyed (Joint Iteration). Per-input iterator records,
        // per-input done flags (longest mode), padding values, mode, and—for
        // zipKeyed—the result keys (the result is then a null-proto object).
        public List<JsValue>? ZipIters;
        public List<JsValue>? ZipNexts;
        public bool[]? ZipDone;
        public List<JsValue>? ZipPadding;
        public string? ZipMode;
        public bool ZipKeyed;
        public List<string>? ZipKeys;

        // [[GeneratorState]] guard: a helper whose body is mid-step rejects a
        // re-entrant next()/return() with a TypeError (generator-is-running).
        public bool Running;
        public bool Done;

        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            TraceValue(tracer, Underlying);
            TraceValue(tracer, UnderlyingNext);
            TraceValue(tracer, Callback);
            TraceValue(tracer, Inner);
            TraceValue(tracer, InnerNext);
            if (ConcatRecords is not null)
            {
                foreach (var (iterable, method) in ConcatRecords)
                {
                    TraceValue(tracer, iterable);
                    TraceValue(tracer, method);
                }
            }

            TraceValueList(tracer, ZipIters);
            TraceValueList(tracer, ZipNexts);
            TraceValueList(tracer, ZipPadding);
        }

        private static void TraceValueList(IHeapTracer tracer, List<JsValue>? list)
        {
            if (list is null) return;
            foreach (var v in list) TraceValue(tracer, v);
        }

        private static void TraceValue(IHeapTracer tracer, JsValue value)
        {
            if (value.Tag == JsValueTag.Object)
            {
                tracer.Trace(value.AsObjectHandle());
            }
        }
    }

    // ECMA-262 7.4.2 GetIteratorDirect ( obj ). Reads obj.next exactly once and
    // returns the (iterator, nextMethod) record. The spec does NOT require next
    // to be callable here; that surfaces lazily when the record is stepped.
    private (JsValue Iterator, JsValue Next) GetIteratorDirect(JsValue obj)
    {
        if (obj.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator method called on a non-object."));
        }

        var target = _heap.GetObject(obj.AsObjectHandle());
        TryGetPropertyValue(target, obj, "next", out var next);
        return (obj, next);
    }

    // ECMA-262 7.4.5 IteratorStepValue against a raw record: call next, validate
    // the result is an object, and return its value (or signal done). The freshly
    // allocated IteratorResult is pinned while done/value getters (user code) run.
    private bool IteratorRecordStepValue(JsValue iterator, JsValue next, out JsValue value)
    {
        var result = CallFunction(next, System.Array.Empty<JsValue>(), iterator);
        if (result.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
        }

        var rootMark = _heap.RootCount;
        try
        {
            _heap.PushRoot(result.AsObjectHandle());
            var resultObj = _heap.GetObject(result.AsObjectHandle());
            TryGetPropertyValue(resultObj, result, "done", out var doneVal);
            if (IsTruthy(doneVal))
            {
                value = JsValue.Undefined;
                return false;
            }

            TryGetPropertyValue(resultObj, result, "value", out value);
            return true;
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
        }
    }

    // ECMA-262 7.4.11 IteratorClose for a NORMAL completion: call the iterator's
    // "return" method (if present) and require its result to be an object. Used
    // when a helper terminates early (take exhausted, some/every/find short-
    // circuit). Throws propagate.
    private void IteratorRecordCloseNormal(JsValue iterator)
    {
        if (iterator.Tag != JsValueTag.Object)
        {
            return;
        }

        var iterObj = _heap.GetObject(iterator.AsObjectHandle());
        if (!TryGetPropertyValue(iterObj, iterator, "return", out var ret) ||
            ret.Tag == JsValueTag.Undefined || ret.Tag == JsValueTag.Null)
        {
            return;
        }

        var result = CallFunction(ret, System.Array.Empty<JsValue>(), iterator);
        if (result.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator 'return' method did not return an object."));
        }
    }

    // %IteratorHelperPrototype% (27.1.4.x): inherits %Iterator.prototype% (so the
    // chained helpers + @@iterator-returns-this are available) and provides next,
    // return, and @@toStringTag = "Iterator Helper".
    private ObjectHandle EnsureIteratorHelperPrototype()
    {
        if (_iteratorHelperPrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        proto.SetPrototype(EnsureIteratorPrototype());
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        DefineNativePrototypeMethod(protoHandle, proto, "next", (thisValue, _) =>
        {
            var helper = RequireIteratorHelper(thisValue, "next");
            return IteratorHelperNext(helper);
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "return", (thisValue, _) =>
        {
            var helper = RequireIteratorHelper(thisValue, "return");
            if (helper.Running)
            {
                throw new JsThrownException(CreateTypeError("Iterator helper is already running."));
            }

            if (!helper.Done)
            {
                helper.Done = true;
                // %IteratorHelperPrototype%.return performs IteratorClose with a
                // NORMAL completion: the underlying iterator's `return` result (and
                // any throw it raises) PROPAGATES to the caller.
                CloseIteratorHelperReturn(helper);
            }

            return BuildIteratorResult(JsValue.Undefined, done: true);
        }, length: 0);

        DefineBuiltinToStringTag(proto, "Iterator Helper");

        _iteratorHelperPrototypeHandle = protoHandle;
        return protoHandle;
    }

    private IteratorHelperObject RequireIteratorHelper(JsValue thisValue, string method)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is IteratorHelperObject helper)
        {
            return helper;
        }

        throw new JsThrownException(CreateTypeError(
            $"Iterator Helper.prototype.{method} called on an incompatible receiver."));
    }

    // Closes the underlying iterator (and the current flatMap/concat inner, if
    // any) for a .return() normal completion: the underlying `return` result is
    // validated and its throw PROPAGATES. Inner is closed before outer to match
    // the nested closure unwind order.
    private void CloseIteratorHelperReturn(IteratorHelperObject helper)
    {
        if (helper.Kind == IteratorHelperKind.Zip)
        {
            CloseZipIters(helper, exceptIndex: -1, normalCompletion: true);
            return;
        }

        if (helper.HasInner)
        {
            helper.HasInner = false;
            var inner = helper.Inner;
            helper.Inner = JsValue.Undefined;
            helper.InnerNext = JsValue.Undefined;
            IteratorRecordCloseNormal(inner);
        }

        IteratorRecordCloseNormal(helper.Underlying);
    }

    private JsValue IteratorHelperNext(IteratorHelperObject helper)
    {
        if (helper.Running)
        {
            throw new JsThrownException(CreateTypeError("Iterator helper is already running."));
        }

        if (helper.Done)
        {
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        helper.Running = true;
        try
        {
            return helper.Kind switch
            {
                IteratorHelperKind.Map => StepMap(helper),
                IteratorHelperKind.Filter => StepFilter(helper),
                IteratorHelperKind.Take => StepTake(helper),
                IteratorHelperKind.Drop => StepDrop(helper),
                IteratorHelperKind.FlatMap => StepFlatMap(helper),
                IteratorHelperKind.Concat => StepConcat(helper),
                IteratorHelperKind.Zip => StepZip(helper),
                _ => throw new JsThrownException(CreateTypeError("Unknown iterator helper.")),
            };
        }
        catch
        {
            // Any abrupt completion from the helper body completes the helper.
            helper.Done = true;
            throw;
        }
        finally
        {
            helper.Running = false;
        }
    }

    private JsValue StepMap(IteratorHelperObject helper)
    {
        if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out var value))
        {
            helper.Done = true;
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        JsValue mapped;
        try
        {
            mapped = CallFunction(helper.Callback,
                new[] { value, JsValue.FromNumber(helper.Counter) }, JsValue.Undefined);
        }
        catch (JsThrownException)
        {
            IteratorCloseOnAbrupt(helper.Underlying);
            throw;
        }

        helper.Counter++;
        return BuildIteratorResult(mapped, done: false);
    }

    private JsValue StepFilter(IteratorHelperObject helper)
    {
        while (true)
        {
            if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out var value))
            {
                helper.Done = true;
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }

            bool selected;
            try
            {
                selected = IsTruthy(CallFunction(helper.Callback,
                    new[] { value, JsValue.FromNumber(helper.Counter) }, JsValue.Undefined));
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(helper.Underlying);
                throw;
            }

            helper.Counter++;
            if (selected)
            {
                return BuildIteratorResult(value, done: false);
            }
        }
    }

    private JsValue StepTake(IteratorHelperObject helper)
    {
        if (helper.Remaining <= 0)
        {
            helper.Done = true;
            IteratorRecordCloseNormal(helper.Underlying);
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        if (!double.IsPositiveInfinity(helper.Remaining))
        {
            helper.Remaining -= 1;
        }

        if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out var value))
        {
            helper.Done = true;
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        return BuildIteratorResult(value, done: false);
    }

    private JsValue StepDrop(IteratorHelperObject helper)
    {
        if (!helper.DropPrimed)
        {
            helper.DropPrimed = true;
            while (helper.Remaining > 0)
            {
                if (!double.IsPositiveInfinity(helper.Remaining))
                {
                    helper.Remaining -= 1;
                }

                if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out _))
                {
                    helper.Done = true;
                    return BuildIteratorResult(JsValue.Undefined, done: true);
                }
            }
        }

        if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out var value))
        {
            helper.Done = true;
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        return BuildIteratorResult(value, done: false);
    }

    private JsValue StepFlatMap(IteratorHelperObject helper)
    {
        while (true)
        {
            if (helper.HasInner)
            {
                if (IteratorRecordStepValue(helper.Inner, helper.InnerNext, out var innerValue))
                {
                    return BuildIteratorResult(innerValue, done: false);
                }

                helper.HasInner = false;
                helper.Inner = JsValue.Undefined;
                helper.InnerNext = JsValue.Undefined;
            }

            if (!IteratorRecordStepValue(helper.Underlying, helper.UnderlyingNext, out var value))
            {
                helper.Done = true;
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }

            JsValue mapped;
            try
            {
                mapped = CallFunction(helper.Callback,
                    new[] { value, JsValue.FromNumber(helper.Counter) }, JsValue.Undefined);
                helper.Counter++;
                var (innerIter, innerNext) = GetIteratorFlattenable(mapped, rejectPrimitives: true);
                helper.Inner = innerIter;
                helper.InnerNext = innerNext;
                helper.HasInner = true;
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(helper.Underlying);
                throw;
            }
        }
    }

    private JsValue StepConcat(IteratorHelperObject helper)
    {
        while (true)
        {
            if (helper.HasInner)
            {
                if (IteratorRecordStepValue(helper.Inner, helper.InnerNext, out var innerValue))
                {
                    return BuildIteratorResult(innerValue, done: false);
                }

                helper.HasInner = false;
                helper.Inner = JsValue.Undefined;
                helper.InnerNext = JsValue.Undefined;
            }

            var records = helper.ConcatRecords!;
            if (helper.ConcatIndex >= records.Count)
            {
                helper.Done = true;
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }

            var (iterable, method) = records[helper.ConcatIndex++];
            var iter = CallFunction(method, System.Array.Empty<JsValue>(), iterable);
            if (iter.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator.concat: @@iterator did not return an object."));
            }

            var (innerIter, innerNext) = GetIteratorDirect(iter);
            helper.Inner = innerIter;
            helper.InnerNext = innerNext;
            helper.HasInner = true;
        }
    }

    // ───────── Iterator.from / %WrapForValidIteratorPrototype% ─────────

    private ObjectHandle? _wrapForValidIteratorPrototypeHandle;

    private sealed class WrapForValidIteratorObject : JsObject
    {
        public JsValue Iterated;
        public JsValue NextMethod;

        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            if (Iterated.Tag == JsValueTag.Object) tracer.Trace(Iterated.AsObjectHandle());
            if (NextMethod.Tag == JsValueTag.Object) tracer.Trace(NextMethod.AsObjectHandle());
        }
    }

    // ECMA-262 27.1.4.1 Iterator.from ( O ). Strings are boxed; the flattened
    // iterator record is reused directly when it already inherits
    // %Iterator.prototype% (e.g. a generator), otherwise wrapped.
    private JsValue IteratorFrom(JsValue source)
    {
        if (source.Tag == JsValueTag.String)
        {
            source = ToObjectValue(source);
        }

        var (iter, next) = GetIteratorFlattenable(source, rejectPrimitives: true);
        if (iter.Tag == JsValueTag.Object && InheritsIteratorPrototype(iter.AsObjectHandle()))
        {
            return iter;
        }

        var wrap = new WrapForValidIteratorObject { Iterated = iter, NextMethod = next };
        wrap.SetPrototype(EnsureWrapForValidIteratorPrototype());
        var handle = _heap.AllocateObject(wrap, AllocationSite.Current());
        if (iter.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, iter.AsObjectHandle());
        return JsValue.FromObject(handle);
    }

    private bool InheritsIteratorPrototype(ObjectHandle handle)
    {
        var iterProto = EnsureIteratorPrototype();
        var proto = _heap.GetObject(handle).PrototypeHandle;
        while (proto is { } p)
        {
            if (p == iterProto) return true;
            proto = _heap.GetObject(p).PrototypeHandle;
        }

        return false;
    }

    private ObjectHandle EnsureWrapForValidIteratorPrototype()
    {
        if (_wrapForValidIteratorPrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        proto.SetPrototype(EnsureIteratorPrototype());
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        DefineNativePrototypeMethod(protoHandle, proto, "next", (thisValue, _) =>
        {
            var wrap = RequireWrap(thisValue, "next");
            var result = CallFunction(wrap.NextMethod, System.Array.Empty<JsValue>(), wrap.Iterated);
            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
            }

            return result;
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "return", (thisValue, _) =>
        {
            var wrap = RequireWrap(thisValue, "return");
            var iterator = wrap.Iterated;
            if (iterator.Tag == JsValueTag.Object)
            {
                var iterObj = _heap.GetObject(iterator.AsObjectHandle());
                if (TryGetPropertyValue(iterObj, iterator, "return", out var ret) &&
                    ret.Tag != JsValueTag.Undefined && ret.Tag != JsValueTag.Null)
                {
                    return CallFunction(ret, System.Array.Empty<JsValue>(), iterator);
                }
            }

            return BuildIteratorResult(JsValue.Undefined, done: true);
        }, length: 0);

        DefineBuiltinToStringTag(proto, "Iterator Helper");

        _wrapForValidIteratorPrototypeHandle = protoHandle;
        return protoHandle;
    }

    private WrapForValidIteratorObject RequireWrap(JsValue thisValue, string member)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is WrapForValidIteratorObject wrap)
        {
            return wrap;
        }

        throw new JsThrownException(CreateTypeError(
            $"%WrapForValidIteratorPrototype%.{member} called on an incompatible receiver."));
    }

    // ───────── Iterator.zip / Iterator.zipKeyed (Joint Iteration) ─────────

    // Iterator.zip ( iterables [ , options ] ): iterables is an iterable of
    // iterables. Iterator.zipKeyed: iterables is an object whose own enumerable
    // values are iterables, and results are null-prototype objects keyed by name.
    private JsValue IteratorZip(IReadOnlyList<JsValue> args, bool keyed)
    {
        var iterablesArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (iterablesArg.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                (keyed ? "Iterator.zipKeyed" : "Iterator.zip") + " requires an object argument."));
        }

        var (optionsObj, optionsVal) = GetOptionsObject(args.Count > 1 ? args[1] : JsValue.Undefined);

        // mode (default "shortest"); strict membership, no coercion.
        var mode = "shortest";
        if (TryGetPropertyValue(optionsObj, optionsVal, "mode", out var modeVal) &&
            modeVal.Tag != JsValueTag.Undefined)
        {
            if (modeVal.Tag != JsValueTag.String ||
                modeVal.AsString() is not ("shortest" or "longest" or "strict"))
            {
                throw new JsThrownException(CreateTypeError("Iterator.zip: invalid 'mode' option."));
            }

            mode = modeVal.AsString();
        }

        // padding (only consulted for "longest"): must be undefined or an object.
        var paddingOption = JsValue.Undefined;
        if (mode == "longest" &&
            TryGetPropertyValue(optionsObj, optionsVal, "padding", out var pad) &&
            pad.Tag != JsValueTag.Undefined)
        {
            if (pad.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator.zip: 'padding' must be an object."));
            }

            paddingOption = pad;
        }

        var iters = new List<JsValue>();
        var nexts = new List<JsValue>();
        var zipKeys = keyed ? new List<string>() : null;

        // Suspend young GC while collecting the input iterator records + padding:
        // these live only in C# locals/lists until the helper object is allocated.
        var savedYoung = _heap.YoungAllocationsPerMinorGc;
        _heap.YoungAllocationsPerMinorGc = -1;
        try
        {

        if (keyed)
        {
            var obj = _heap.GetObject(iterablesArg.AsObjectHandle());
            var keyList = new List<string>();
            foreach (var prop in obj.EnumerateOwnProperties())
            {
                if (obj.TryGetOwnProperty(prop.Key, out var d) && d.Enumerable)
                {
                    keyList.Add(prop.Key);
                }
            }

            foreach (var key in keyList)
            {
                TryGetPropertyValue(obj, iterablesArg, key, out var value);
                // A key whose value is undefined is omitted from the result.
                if (value.Tag == JsValueTag.Undefined)
                {
                    continue;
                }

                try
                {
                    var (it, nx) = GetIteratorFlattenable(value, rejectPrimitives: true);
                    iters.Add(it);
                    nexts.Add(nx);
                    zipKeys!.Add(key);
                }
                catch (JsThrownException)
                {
                    CloseIteratorListAbrupt(iters);
                    throw;
                }
            }
        }
        else
        {
            var (outerIt, outerNext) = GetIteratorFlattenable(iterablesArg, rejectPrimitives: true);
            while (true)
            {
                bool got;
                JsValue elem;
                try
                {
                    got = IteratorRecordStepValue(outerIt, outerNext, out elem);
                }
                catch (JsThrownException)
                {
                    CloseIteratorListAbrupt(iters);
                    throw;
                }

                if (!got)
                {
                    break;
                }

                try
                {
                    var (it, nx) = GetIteratorFlattenable(elem, rejectPrimitives: true);
                    iters.Add(it);
                    nexts.Add(nx);
                }
                catch (JsThrownException)
                {
                    IteratorCloseOnAbrupt(outerIt);
                    CloseIteratorListAbrupt(iters);
                    throw;
                }
            }
        }

        var iterCount = iters.Count;
        var padding = new List<JsValue>(iterCount);
        for (var i = 0; i < iterCount; i++)
        {
            padding.Add(JsValue.Undefined);
        }

        if (mode == "longest" && paddingOption.Tag == JsValueTag.Object)
        {
            var (padIt, padNext) = GetIteratorFlattenable(paddingOption, rejectPrimitives: true);
            for (var i = 0; i < iterCount; i++)
            {
                if (!IteratorRecordStepValue(padIt, padNext, out var padValue))
                {
                    break;
                }

                padding[i] = padValue;
            }
        }

        var helper = new IteratorHelperObject
        {
            Kind = IteratorHelperKind.Zip,
            Underlying = JsValue.Undefined,
            UnderlyingNext = JsValue.Undefined,
            Callback = JsValue.Undefined,
            Inner = JsValue.Undefined,
            InnerNext = JsValue.Undefined,
            ZipIters = iters,
            ZipNexts = nexts,
            ZipDone = new bool[iterCount],
            ZipPadding = padding,
            ZipMode = mode,
            ZipKeyed = keyed,
            ZipKeys = zipKeys,
        };
        helper.SetPrototype(EnsureIteratorHelperPrototype());
        return JsValue.FromObject(_heap.AllocateObject(helper, AllocationSite.Current()));

        }
        finally
        {
            _heap.YoungAllocationsPerMinorGc = savedYoung;
        }
    }

    // ECMA-262 GetOptionsObject: undefined → fresh null-proto object; Object →
    // itself; anything else → TypeError.
    private (JsObject Obj, JsValue Val) GetOptionsObject(JsValue options)
    {
        if (options.Tag == JsValueTag.Undefined)
        {
            var empty = new JsObject();
            var handle = _heap.AllocateObject(empty, AllocationSite.Current());
            return (empty, JsValue.FromObject(handle));
        }

        if (options.Tag == JsValueTag.Object)
        {
            return (_heap.GetObject(options.AsObjectHandle()), options);
        }

        throw new JsThrownException(CreateTypeError("Iterator.zip options must be an object or undefined."));
    }

    private JsValue StepZip(IteratorHelperObject h)
    {
        // Pin accumulating per-input values: each IteratorStepValue (and the
        // result-object allocation) can trigger a young GC that would otherwise
        // reclaim a results[] entry that is not yet referenced by a live object.
        var savedYoung = _heap.YoungAllocationsPerMinorGc;
        _heap.YoungAllocationsPerMinorGc = -1;
        try
        {
            return StepZipInner(h);
        }
        finally
        {
            _heap.YoungAllocationsPerMinorGc = savedYoung;
        }
    }

    private JsValue StepZipInner(IteratorHelperObject h)
    {
        var iters = h.ZipIters!;
        var nexts = h.ZipNexts!;
        var done = h.ZipDone!;
        var count = iters.Count;
        if (count == 0)
        {
            h.Done = true;
            return BuildIteratorResult(JsValue.Undefined, done: true);
        }

        var results = new JsValue[count];
        for (var i = 0; i < count; i++)
        {
            if (done[i])
            {
                results[i] = h.ZipPadding![i];
                continue;
            }

            bool got;
            JsValue value;
            try
            {
                got = IteratorRecordStepValue(iters[i], nexts[i], out value);
            }
            catch (JsThrownException)
            {
                done[i] = true;
                h.Done = true;
                CloseZipIters(h, exceptIndex: -1, normalCompletion: false);
                throw;
            }

            if (got)
            {
                results[i] = value;
                continue;
            }

            // Input i is exhausted.
            switch (h.ZipMode)
            {
                case "shortest":
                    done[i] = true;
                    h.Done = true;
                    CloseZipIters(h, exceptIndex: -1, normalCompletion: true);
                    return BuildIteratorResult(JsValue.Undefined, done: true);

                case "strict":
                    done[i] = true;
                    if (i != 0)
                    {
                        h.Done = true;
                        CloseZipIters(h, exceptIndex: -1, normalCompletion: false);
                        throw new JsThrownException(CreateTypeError(
                            "Iterator.zip strict: inputs have different lengths."));
                    }

                    // i == 0: every remaining input must also be done now.
                    for (var j = 1; j < count; j++)
                    {
                        bool gotj;
                        try
                        {
                            gotj = IteratorRecordStepValue(iters[j], nexts[j], out _);
                        }
                        catch (JsThrownException)
                        {
                            done[j] = true;
                            h.Done = true;
                            CloseZipIters(h, exceptIndex: -1, normalCompletion: false);
                            throw;
                        }

                        done[j] = true;
                        if (gotj)
                        {
                            h.Done = true;
                            CloseZipIters(h, exceptIndex: -1, normalCompletion: false);
                            throw new JsThrownException(CreateTypeError(
                                "Iterator.zip strict: inputs have different lengths."));
                        }
                    }

                    h.Done = true;
                    return BuildIteratorResult(JsValue.Undefined, done: true);

                default: // longest
                    done[i] = true;
                    results[i] = h.ZipPadding![i];
                    break;
            }
        }

        if (h.ZipMode == "longest")
        {
            var allDone = true;
            for (var i = 0; i < count; i++)
            {
                if (!done[i]) { allDone = false; break; }
            }

            if (allDone)
            {
                h.Done = true;
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }
        }

        return BuildIteratorResult(BuildZipResult(h, results), done: false);
    }

    private JsValue BuildZipResult(IteratorHelperObject h, JsValue[] results)
    {
        if (!h.ZipKeyed)
        {
            var arr = CreateArrayFromElements(results);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }

        // zipKeyed yields a fresh null-prototype object keyed by the input names.
        var obj = new JsObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        var keys = h.ZipKeys!;
        for (var i = 0; i < keys.Count; i++)
        {
            obj.SetProperty(keys[i], results[i]);
            if (results[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(handle, results[i].AsObjectHandle());
            }
        }

        return JsValue.FromObject(handle);
    }

    // Closes the still-open zip inputs. With a normal completion the first
    // `return` throw propagates (subsequent closes swallow); with an abrupt
    // completion every close swallows so the original throw is preserved.
    private void CloseZipIters(IteratorHelperObject h, int exceptIndex, bool normalCompletion)
    {
        var iters = h.ZipIters!;
        var done = h.ZipDone!;
        JsThrownException? pending = null;
        // CloseAllIterators closes the still-open inputs in DESCENDING index order.
        for (var j = iters.Count - 1; j >= 0; j--)
        {
            if (j == exceptIndex || done[j])
            {
                continue;
            }

            done[j] = true;
            try
            {
                if (normalCompletion && pending is null)
                {
                    IteratorRecordCloseNormal(iters[j]);
                }
                else
                {
                    IteratorCloseOnAbrupt(iters[j]);
                }
            }
            catch (JsThrownException e)
            {
                pending ??= e;
            }
        }

        if (pending is not null)
        {
            throw pending;
        }
    }

    private void CloseIteratorListAbrupt(List<JsValue> iters)
    {
        foreach (var iter in iters)
        {
            IteratorCloseOnAbrupt(iter);
        }
    }

    // ECMA-262 27.1.4.x GetIteratorFlattenable ( obj, primitiveHandling ).
    // For flatMap, primitives are rejected (reject-primitives). If obj exposes a
    // callable @@iterator it is invoked; otherwise obj is used directly as the
    // iterator (GetIteratorDirect).
    private (JsValue Iterator, JsValue Next) GetIteratorFlattenable(JsValue obj, bool rejectPrimitives)
    {
        if (obj.Tag != JsValueTag.Object)
        {
            if (rejectPrimitives || obj.Tag != JsValueTag.String)
            {
                throw new JsThrownException(CreateTypeError("Iterator flatMap: value is not an object."));
            }
        }

        JsValue iterator;
        if (obj.Tag == JsValueTag.Object)
        {
            var target = _heap.GetObject(obj.AsObjectHandle());
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId != 0 &&
                target.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                iterDesc.Value.Tag == JsValueTag.Object)
            {
                iterator = CallFunction(iterDesc.Value, System.Array.Empty<JsValue>(), obj);
                if (iterator.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError("@@iterator method did not return an object."));
                }
            }
            else
            {
                iterator = obj;
            }
        }
        else
        {
            // String primitive (only reachable when rejectPrimitives is false).
            iterator = CreateForOfIterator(obj);
        }

        return GetIteratorDirect(iterator);
    }

    // Constructs an Iterator Helper object with the given kind + state, parented
    // to %IteratorHelperPrototype%.
    private JsValue MakeIteratorHelper(IteratorHelperKind kind, JsValue iterator, JsValue next,
        JsValue callback, double remaining)
    {
        var helper = new IteratorHelperObject
        {
            Kind = kind,
            Underlying = iterator,
            UnderlyingNext = next,
            Callback = callback,
            Remaining = remaining,
            Inner = JsValue.Undefined,
            InnerNext = JsValue.Undefined,
        };
        helper.SetPrototype(EnsureIteratorHelperPrototype());
        var handle = _heap.AllocateObject(helper, AllocationSite.Current());
        if (iterator.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(handle, iterator.AsObjectHandle());
        }

        return JsValue.FromObject(handle);
    }

    // Shared limit coercion for take/drop: ToNumber, reject NaN with a RangeError,
    // then ToIntegerOrInfinity on the already-computed Number (no double-coerce),
    // and reject negatives.
    private double CoerceIteratorLimit(IReadOnlyList<JsValue> args, string method)
    {
        var arg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var numLimit = ToNumber(arg);
        if (double.IsNaN(numLimit))
        {
            throw new JsThrownException(CreateRangeError(method + " limit must not be NaN."));
        }

        var integerLimit = double.IsInfinity(numLimit) ? numLimit : System.Math.Truncate(numLimit);
        if (integerLimit < 0)
        {
            throw new JsThrownException(CreateRangeError(method + " limit must be a non-negative integer."));
        }

        return integerLimit;
    }

    // ───────── %Iterator.prototype% helper method bodies ─────────
    // Each validates the receiver is an Object, validates the callback / limit in
    // spec argument order, captures the underlying record once (GetIteratorDirect),
    // then returns a lazy Iterator Helper.

    private JsValue IteratorProtoMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "map");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.map");
        var (iter, next) = GetIteratorDirect(thisValue);
        return MakeIteratorHelper(IteratorHelperKind.Map, iter, next, fn, 0);
    }

    private JsValue IteratorProtoFilter(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "filter");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.filter");
        var (iter, next) = GetIteratorDirect(thisValue);
        return MakeIteratorHelper(IteratorHelperKind.Filter, iter, next, fn, 0);
    }

    private JsValue IteratorProtoTake(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "take");
        var limit = CoerceIteratorLimitOrClose(thisValue, args, "Iterator.prototype.take");
        var (iter, next) = GetIteratorDirect(thisValue);
        return MakeIteratorHelper(IteratorHelperKind.Take, iter, next, JsValue.Undefined, limit);
    }

    private JsValue IteratorProtoDrop(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "drop");
        var limit = CoerceIteratorLimitOrClose(thisValue, args, "Iterator.prototype.drop");
        var (iter, next) = GetIteratorDirect(thisValue);
        return MakeIteratorHelper(IteratorHelperKind.Drop, iter, next, JsValue.Undefined, limit);
    }

    private JsValue IteratorProtoFlatMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "flatMap");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.flatMap");
        var (iter, next) = GetIteratorDirect(thisValue);
        return MakeIteratorHelper(IteratorHelperKind.FlatMap, iter, next, fn, 0);
    }

    private void EnsureIteratorReceiver(JsValue thisValue, string method)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Iterator.prototype." + method + " called on a non-object."));
        }
    }

    // ECMA-262 27.2.1.x — when the callback argument is not callable, the spec
    // closes the underlying iterator (IteratorClose, calling its `return`) and
    // THEN throws a TypeError, without ever reading `next`.
    private JsValue RequireCallableOrClose(JsValue thisValue, IReadOnlyList<JsValue> args, string name)
    {
        var fn = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (!IsCallable(fn))
        {
            IteratorCloseOnAbrupt(thisValue);
            throw new JsThrownException(CreateTypeError(name + " callback is not a function."));
        }

        return fn;
    }

    // take/drop: coerce + validate the limit; any abrupt completion (a throwing
    // ToNumber, NaN, or a negative limit) closes the underlying iterator first.
    private double CoerceIteratorLimitOrClose(JsValue thisValue, IReadOnlyList<JsValue> args, string name)
    {
        try
        {
            return CoerceIteratorLimit(args, name);
        }
        catch (JsThrownException)
        {
            IteratorCloseOnAbrupt(thisValue);
            throw;
        }
    }

    // ───────── terminal %Iterator.prototype% consumers ─────────
    // These drive the underlying iterator lazily one value at a time and forward
    // abrupt completions through IteratorClose (7.4.11) where the spec requires.

    private JsValue IteratorProtoToArray(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "toArray");
        var (iter, next) = GetIteratorDirect(thisValue);
        var items = new List<JsValue>();
        while (IteratorRecordStepValue(iter, next, out var value))
        {
            items.Add(value);
        }

        var arr = CreateArrayFromElements(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue IteratorProtoForEach(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "forEach");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.forEach");
        var (iter, next) = GetIteratorDirect(thisValue);
        long counter = 0;
        while (IteratorRecordStepValue(iter, next, out var value))
        {
            try
            {
                CallFunction(fn, new[] { value, JsValue.FromNumber(counter) }, JsValue.Undefined);
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(iter);
                throw;
            }

            counter++;
        }

        return JsValue.Undefined;
    }

    private JsValue IteratorProtoReduce(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "reduce");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.reduce");
        var (iter, next) = GetIteratorDirect(thisValue);
        var hasInitial = args.Count > 1;
        long counter = 0;
        JsValue accumulator;
        if (hasInitial)
        {
            accumulator = args[1];
        }
        else
        {
            if (!IteratorRecordStepValue(iter, next, out var first))
            {
                throw new JsThrownException(CreateTypeError("Reduce of empty iterator with no initial value."));
            }

            accumulator = first;
            counter = 1;
        }

        while (IteratorRecordStepValue(iter, next, out var value))
        {
            try
            {
                accumulator = CallFunction(fn,
                    new[] { accumulator, value, JsValue.FromNumber(counter) }, JsValue.Undefined);
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(iter);
                throw;
            }

            counter++;
        }

        return accumulator;
    }

    private JsValue IteratorProtoSome(JsValue thisValue, IReadOnlyList<JsValue> args)
        => IteratorPredicateConsume(thisValue, args, "some", stopWhen: true, resultWhenStopped: true, exhaustedResult: false);

    private JsValue IteratorProtoEvery(JsValue thisValue, IReadOnlyList<JsValue> args)
        => IteratorPredicateConsume(thisValue, args, "every", stopWhen: false, resultWhenStopped: false, exhaustedResult: true);

    private JsValue IteratorProtoFind(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        EnsureIteratorReceiver(thisValue, "find");
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype.find");
        var (iter, next) = GetIteratorDirect(thisValue);
        long counter = 0;
        while (IteratorRecordStepValue(iter, next, out var value))
        {
            bool matched;
            try
            {
                matched = IsTruthy(CallFunction(fn,
                    new[] { value, JsValue.FromNumber(counter) }, JsValue.Undefined));
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(iter);
                throw;
            }

            if (matched)
            {
                IteratorRecordCloseNormal(iter);
                return value;
            }

            counter++;
        }

        return JsValue.Undefined;
    }

    private JsValue IteratorPredicateConsume(JsValue thisValue, IReadOnlyList<JsValue> args,
        string method, bool stopWhen, bool resultWhenStopped, bool exhaustedResult)
    {
        EnsureIteratorReceiver(thisValue, method);
        var fn = RequireCallableOrClose(thisValue, args, "Iterator.prototype." + method);
        var (iter, next) = GetIteratorDirect(thisValue);
        long counter = 0;
        while (IteratorRecordStepValue(iter, next, out var value))
        {
            bool predicate;
            try
            {
                predicate = IsTruthy(CallFunction(fn,
                    new[] { value, JsValue.FromNumber(counter) }, JsValue.Undefined));
            }
            catch (JsThrownException)
            {
                IteratorCloseOnAbrupt(iter);
                throw;
            }

            if (predicate == stopWhen)
            {
                IteratorRecordCloseNormal(iter);
                return JsValue.FromBoolean(resultWhenStopped);
            }

            counter++;
        }

        return JsValue.FromBoolean(exhaustedResult);
    }

    // ECMA-262 27.1.4.1 Iterator.concat ( ...items ). Validates every argument up
    // front (each must be an object exposing a callable @@iterator), then lazily
    // opens and drains them in order.
    private JsValue IteratorConcat(IReadOnlyList<JsValue> args)
    {
        var records = new List<(JsValue Iterable, JsValue Method)>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var item = args[i];
            if (item.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator.concat: argument is not an object."));
            }

            var itemObj = _heap.GetObject(item.AsObjectHandle());
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId == 0 ||
                !itemObj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) ||
                iterDesc.Value.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator.concat: argument is not iterable."));
            }

            records.Add((item, iterDesc.Value));
        }

        var helper = new IteratorHelperObject
        {
            Kind = IteratorHelperKind.Concat,
            Underlying = JsValue.Undefined,
            UnderlyingNext = JsValue.Undefined,
            Callback = JsValue.Undefined,
            Inner = JsValue.Undefined,
            InnerNext = JsValue.Undefined,
            ConcatRecords = records,
        };
        helper.SetPrototype(EnsureIteratorHelperPrototype());
        return JsValue.FromObject(_heap.AllocateObject(helper, AllocationSite.Current()));
    }
}
