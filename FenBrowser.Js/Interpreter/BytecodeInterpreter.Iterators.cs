using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Iterator helpers + nested iterator-state objects extracted from
// BytecodeInterpreter.cs as audit §2 slice 3 (interpreter monolith breakup).
// Pure file move — no semantic change.
public sealed partial class BytecodeInterpreter
{
    // Build a for-of iteration state. Tries the spec @@iterator dispatch first:
    // if the source object exposes a callable Symbol.iterator, call it and drive
    // the returned iterator via .next() until done. Strings, Arrays, and array-
    // likes fall through to fast in-place iteration over their indexed slots.
    private JsValue CreateForOfIterator(JsValue source, bool requireIterable = false)
    {
        var values = new List<JsValue>();

        if (source.Tag == JsValueTag.HostObject &&
            TryGetHostObjectIteratorMethod(source, out var hostIteratorMethod))
        {
            var iterator = CallFunction(hostIteratorMethod, Array.Empty<JsValue>(), source);
            DrainIteratorIntoList(iterator, values);
            var producer = new ForOfIteratorObject(values);
            return JsValue.FromObject(_heap.AllocateObject(producer, AllocationSite.Current()));
        }

        if (source.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(source.AsObjectHandle());
            // Spec @@iterator dispatch (7.4.2 GetIterator + 7.4.4 IteratorStep).
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId != 0 &&
                obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                iterDesc.Value.Tag == JsValueTag.Object)
            {
                var iter = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), source);
                DrainIteratorIntoList(iter, values);
                var producer = new ForOfIteratorObject(values);
                return JsValue.FromObject(_heap.AllocateObject(producer, AllocationSite.Current()));
            }

            // 7.4.3 GetIterator: no callable @@iterator means not iterable.
            // Destructuring (requireIterable) must throw; the for-of head keeps
            // the legacy array-like fallback below for engine-internal callers.
            if (requireIterable)
            {
                throw new JsThrownException(CreateTypeError(
                    "Value is not iterable (no Symbol.iterator method)."));
            }

            if (obj is ArrayObject)
            {
                var length = GetArrayLength(obj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    values.Add(TryGetPropertyValue(obj, source, key, out var v) ? v : JsValue.Undefined);
                }
            }
            else if (obj.TryGetOwnProperty("length", out var lenDesc) &&
                     (lenDesc.Value.Tag == JsValueTag.Number || lenDesc.Value.Tag == JsValueTag.Int32))
            {
                // Array-like fallback (arguments, NodeList-shape).
                var length = (int)lenDesc.Value.AsNumber();
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    values.Add(TryGetPropertyValue(obj, source, key, out var v) ? v : JsValue.Undefined);
                }
            }
            else
            {
                throw new JsThrownException(CreateTypeError(
                    "Value is not iterable (no @@iterator and not array-like)."));
            }
        }
        else if (source.Tag == JsValueTag.String)
        {
            var s = source.AsString();
            // Iterate over code points, not UTF-16 code units. Surrogate pairs
            // (emoji, supplementary chars) must stay as single characters.
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    values.Add(JsValue.FromString(s.Substring(i, 2)));
                    i++; // skip low surrogate
                }
                else
                {
                    values.Add(JsValue.FromString(s[i].ToString()));
                }
            }
        }
        else if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Cannot iterate over " + (source.Tag == JsValueTag.Null ? "null" : "undefined") + "."));
        }
        else
        {
            throw new JsThrownException(CreateTypeError("Value is not iterable."));
        }

        var fallbackIter = new ForOfIteratorObject(values);
        return JsValue.FromObject(_heap.AllocateObject(fallbackIter, AllocationSite.Current()));
    }

    // ECMA-262 13.7.5.12 ForIn/OfHeadEvaluation (iterate hint) + 7.4.1 GetIterator.
    // The for-of bytecode head uses this instead of the eager CreateForOfIterator
    // so that user iterators are pulled lazily: a user object with @@iterator
    // yields a live (lazy) ForOfIteratorObject; arrays / strings / array-likes
    // keep the bounded eager buffering (their length is finite and known, and
    // many direct callers still rely on TryMoveNext). Pulling lazily is what
    // makes `for (x of infiniteIterator) break;` terminate and lets the loop run
    // IteratorClose on the break.
    private JsValue CreateForOfIteratorState(JsValue source, bool requireIterable = false)
    {
        if (source.Tag == JsValueTag.HostObject &&
            TryGetHostObjectIteratorMethod(source, out var hostIteratorMethod))
        {
            var iterator = CallFunction(hostIteratorMethod, Array.Empty<JsValue>(), source);
            return CreateLazyForOfIteratorState(iterator);
        }

        if (source.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(source.AsObjectHandle());
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId != 0 &&
                obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                iterDesc.Value.Tag == JsValueTag.Object)
            {
                var iterator = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), source);
                return CreateLazyForOfIteratorState(iterator);
            }
        }

        // Arrays, strings, array-likes, and the not-iterable error paths keep the
        // existing eager buffering semantics.
        return CreateForOfIterator(source, requireIterable);
    }

    private bool TryGetHostObjectIteratorMethod(JsValue source, out JsValue iteratorMethod)
    {
        _ = RequireHostObject(source, "get Symbol.iterator");
        var handle = source.AsHostObjectHandle();
        var iteratorSymbolId = GetWellKnownSymbolId("iterator");
        if (iteratorSymbolId == 0 ||
            !TryGetHostObjectPrototypeSymbolProperty(
                handle,
                source,
                iteratorSymbolId,
                out iteratorMethod))
        {
            iteratorMethod = JsValue.Undefined;
            return false;
        }

        if (!IsCallable(iteratorMethod))
        {
            throw new JsThrownException(CreateTypeError(
                "Value is not iterable (Symbol.iterator is not callable)."));
        }

        return true;
    }

    private JsValue CreateLazyForOfIteratorState(JsValue iterator)
    {
        if (iterator.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Result of the Symbol.iterator method is not an object."));
        }

        var iterObj = _heap.GetObject(iterator.AsObjectHandle());
        if (!TryGetPropertyValue(iterObj, iterator, "next", out var nextFn) ||
            !IsCallable(nextFn))
        {
            throw new JsThrownException(CreateTypeError(
                "Iterator has no callable 'next' method."));
        }

        var lazy = new ForOfIteratorObject(iterator, nextFn);
        return JsValue.FromObject(_heap.AllocateObject(lazy, AllocationSite.Current()));
    }

    // Advances a for-of iterator state by one step. Returns true when the
    // iterator is exhausted (the caller jumps to the loop end). In lazy mode the
    // user .next() is invoked, the IteratorResult is read, and Done latches so a
    // subsequent IteratorClose becomes a no-op (the iterator already completed).
    private bool ForOfStepDone(ForOfIteratorObject iter, out JsValue value)
    {
        if (!iter.IsLazy)
        {
            return !iter.TryMoveNext(out value);
        }

        if (iter.Done)
        {
            value = JsValue.Undefined;
            return true;
        }

        var result = CallFunction(iter.NextMethod, Array.Empty<JsValue>(), iter.IteratorObject);
        if (result.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
        }

        // Pin the freshly allocated IteratorResult while its done/value are read:
        // a done/value getter running user code could trigger an auto-MinorCollect
        // that reclaims the result otherwise.
        var rootMark = _heap.RootCount;
        try
        {
            _heap.PushRoot(result.AsObjectHandle());
            var resultObj = _heap.GetObject(result.AsObjectHandle());
            TryGetPropertyValue(resultObj, result, "done", out var doneVal);
            if (IsTruthy(doneVal))
            {
                iter.Done = true;
                value = JsValue.Undefined;
                return true;
            }

            TryGetPropertyValue(resultObj, result, "value", out value);
            return false;
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
        }
    }

    // ECMA-262 7.4.11 IteratorClose for a normal (non-throw) completion such as a
    // for-of break. Calls the iterator's "return" method if present and throws a
    // TypeError when its result is not an object. No-op for eager (finite) states
    // and for already-completed lazy iterators.
    private void CloseForOfIteratorState(ForOfIteratorObject iter, bool suppressErrors = false)
    {
        if (!iter.IsLazy || iter.Done)
        {
            return;
        }

        iter.Done = true;
        var iterator = iter.IteratorObject;
        if (iterator.Tag != JsValueTag.Object)
        {
            return;
        }

        var iterObj = _heap.GetObject(iterator.AsObjectHandle());
        // 7.4.6 GetMethod: an absent or null/undefined "return" means there is
        // nothing to close.
        if (!TryGetPropertyValue(iterObj, iterator, "return", out var ret) ||
            ret.Tag == JsValueTag.Undefined ||
            ret.Tag == JsValueTag.Null)
        {
            return;
        }

        // A non-callable "return" surfaces as the TypeError thrown by CallFunction.
        // Suppress errors from `return()` — per ECMA-262 7.4.11 IteratorClose,
        // if the iterator close itself throws, the original completion (which may
        // be an abrupt throw) should still be the one that propagates. Throwing
        // here would replace a user-visible error with an internal TypeError.
        try
        {
            var result = CallFunction(ret, Array.Empty<JsValue>(), iterator);
            if (result.Tag != JsValueTag.Object && !suppressErrors)
            {
                throw new JsThrownException(CreateTypeError("Iterator return result is not an object."));
                // Non-object return — spec says throw, but during abrupt
                // completion this would mask the real error. Silently ignore.
            }
        }
        catch (JsThrownException)
        {
            if (!suppressErrors)
            {
                throw;
            }
            // If the `return()` method itself throws, the original completion
            // takes precedence. Do not replace it with a close failure.
        }
    }

    // Drains a user iterator into a value list by calling .next() repeatedly until
    // the returned IteratorResult.done is true. Bounded by MaxCallDepth-friendly
    // semantics: each .next() goes through CallFunction so the recursion guard
    // applies.
    // ECMA-262 13.2.4.1 ArrayAccumulation (SpreadElement) / 13.3.7.1 ArgumentList
    // spread: collect every value produced by source's iterator. Spreading requires
    // a real iterator (7.4.1 GetIterator) — unlike for-of's array-like fallback,
    // a plain object without @@iterator is a TypeError here.
    private List<JsValue> CollectSpreadValues(JsValue source)
    {
        var values = new List<JsValue>();

        if (source.Tag == JsValueTag.HostObject &&
            TryGetHostObjectIteratorMethod(source, out var hostIteratorMethod))
        {
            var iterator = CallFunction(hostIteratorMethod, Array.Empty<JsValue>(), source);
            DrainIteratorIntoList(iterator, values);
            return values;
        }

        if (source.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(source.AsObjectHandle());
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId != 0 &&
                obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc))
            {
                // ECMA-262 7.3.9 GetMethod: invoke getter (if accessor) via GetV.
                // This propagates any error thrown by the @@iterator accessor.
                var iterMethod = GetDescriptorValue(iterDesc, source);
                if (iterMethod.Tag != JsValueTag.Object || !IsCallable(iterMethod))
                {
                    throw new JsThrownException(CreateTypeError(
                        "Spread syntax requires an iterable with a callable @@iterator."));
                }
                var iter = CallFunction(iterMethod, Array.Empty<JsValue>(), source);
                DrainIteratorIntoList(iter, values);
                return values;
            }
        }
        else if (source.Tag == JsValueTag.String)
        {
            // The String iterator (22.1.5) walks by code point: a surrogate pair
            // contributes one element.
            var s = source.AsString();
            for (var i = 0; i < s.Length;)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    values.Add(JsValue.FromString(s.Substring(i, 2)));
                    i += 2;
                }
                else
                {
                    values.Add(JsValue.FromString(s[i].ToString()));
                    i++;
                }
            }

            return values;
        }

        throw new JsThrownException(CreateTypeError(
            source.Tag == JsValueTag.Null ? "Cannot spread null." :
            source.Tag == JsValueTag.Undefined ? "Cannot spread undefined." :
            "Spread syntax requires an iterable."));
    }

    // Safety cap: prevent infinite loops from never-ending iterators. ECMA-262
    // array length is bounded at 2^53-1, so 100M is a practical upper bound that
    // still allows legitimate large-but-finite iterables while preventing runaway
    // CPU from iterators whose next() never returns {done: true}.
    private const int MaxSafeDrainIterations = 100_000_000;

    private void DrainIteratorIntoList(JsValue iter, List<JsValue> sink)
    {
        DrainIteratorIntoList(iter, sink, maxIterations: MaxSafeDrainIterations);
    }

    // Same as DrainIteratorIntoList but with a safety cap: when maxIterations is
    // exceeded, throws a RangeError to prevent infinite loops from iterators
    // whose next() never returns {done: true} (spec: Promise combinators must not
    // pre-drain such iterators — they iterate lazily interleaving resolve calls).
    private void DrainIteratorIntoList(JsValue iter, List<JsValue> sink, int maxIterations)
    {
        if (iter.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator @@iterator did not return an object."));
        }

        var rootMark = _heap.RootCount;
        var savedYoungThreshold = _heap.YoungAllocationsPerMinorGc;
        _heap.YoungAllocationsPerMinorGc = -1;
        var count = 0;
        try
        {
            _heap.PushRoot(iter.AsObjectHandle());
            var iterObj = _heap.GetObject(iter.AsObjectHandle());
            while (true)
            {
                if (++count > maxIterations)
                {
                    throw new JsThrownException(CreateRangeError(
                        "Iterator exceeded maximum iteration limit."));
                }

                if (!TryGetPropertyValue(iterObj, iter, "next", out var nextFn) ||
                    nextFn.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError("Iterator result missing callable 'next'."));
                }

                var result = CallFunction(nextFn, Array.Empty<JsValue>(), iter);
                if (result.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
                }

                // Pin the freshly allocated IteratorResult — a follow-on
                // allocation (e.g. CreateTypeError, another .next() call) can
                // tick the auto-MinorCollect counter and reclaim it otherwise.
                _heap.PushRoot(result.AsObjectHandle());
                var resultObj = _heap.GetObject(result.AsObjectHandle());
                TryGetPropertyValue(resultObj, result, "done", out var doneVal);
                if (IsTruthy(doneVal))
                {
                    return;
                }

                TryGetPropertyValue(resultObj, result, "value", out var value);
                if (value.Tag == JsValueTag.Object)
                    _heap.PushRoot(value.AsObjectHandle());
                sink.Add(value);
            }
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
            _heap.YoungAllocationsPerMinorGc = savedYoungThreshold;
        }
    }

    // ECMA-262 7.4.11 IteratorClose ( iteratorRecord, completion ) used on the
    // abrupt-completion path: call the iterator's "return" method (if present)
    // so the producer can release resources, but never let a throw from
    // "return" mask the original completion that triggered the close — the
    // caller re-throws the original after this returns. This is the lazy
    // counterpart to the eager DrainIteratorIntoList path and is what lets
    // Array.from / for-of abort an infinite iterator when user code throws.
    private void IteratorCloseOnAbrupt(JsValue iterator)
    {
        if (iterator.Tag != JsValueTag.Object)
        {
            return;
        }

        var iterObj = _heap.GetObject(iterator.AsObjectHandle());
        if (!TryGetPropertyValue(iterObj, iterator, "return", out var ret) ||
            ret.Tag != JsValueTag.Object)
        {
            return;
        }

        try
        {
            _ = CallFunction(ret, Array.Empty<JsValue>(), iterator);
        }
        catch (JsThrownException)
        {
            // 7.4.11 step 6: an inner throw is discarded when the original
            // completion is itself a throw, which is the only path that calls
            // this helper. The original exception propagates from the caller.
        }
    }

    private JsValue CreateForInIterator(JsValue value)
    {
        var keys = new List<string>();
        if (value.Tag == JsValueTag.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            CollectEnumerableKeys(_heap.GetObject(value.AsObjectHandle()), keys, seen);
        }

        var iterator = new ForInIteratorObject(keys);
        return JsValue.FromObject(_heap.AllocateObject(iterator, AllocationSite.Current()));
    }

    private void CollectEnumerableKeys(JsObject obj, List<string> keys, HashSet<string> seen)
    {
        foreach (var property in obj.EnumerateOwnProperties())
        {
            if (seen.Add(property.Key) && property.Value.Enumerable)
            {
                keys.Add(property.Key);
            }
        }

        if (obj.PrototypeHandle is { } prototype)
        {
            CollectEnumerableKeys(_heap.GetObject(prototype), keys, seen);
        }
    }

    // ECMA-262 13.7.5 for-of iteration state. Two modes:
    //  - Eager (list-backed): used for arrays, strings and array-likes whose
    //    element count is finite and known. The values are buffered up front.
    //  - Lazy (live-iterator-backed): used for user objects with @@iterator.
    //    The state holds the live iterator object and its cached "next" method;
    //    the interpreter pulls one step at a time (ForOfStepDone). This is what
    //    lets for-of terminate on an infinite iterator when the body breaks and
    //    run IteratorClose correctly (7.4.11).
    private sealed class ForOfIteratorObject : JsObject
    {
        private readonly IReadOnlyList<JsValue> _values;
        private int _index;

        public ForOfIteratorObject(IReadOnlyList<JsValue> values)
        {
            _values = values;
        }

        public ForOfIteratorObject(JsValue iteratorObject, JsValue nextMethod)
        {
            _values = System.Array.Empty<JsValue>();
            IsLazy = true;
            IteratorObject = iteratorObject;
            NextMethod = nextMethod;
        }

        public bool IsLazy { get; }

        // Lazy mode only. The live iterator object and its "next" method
        // (read once at GetIterator time per 7.4.1). Done flips true once the
        // iterator reports done or has been closed, so neither ForOfStepDone
        // nor IteratorClose touches it again.
        public JsValue IteratorObject { get; }
        public JsValue NextMethod { get; }
        public bool Done { get; set; }

        public bool TryMoveNext(out JsValue value)
        {
            if (_index >= _values.Count)
            {
                value = JsValue.Undefined;
                return false;
            }

            value = _values[_index++];
            return true;
        }

        // Audit gap §1 #1/#2/#5: buffered iterator values must survive GC while
        // the iterator is reachable. Without this, GC during user code that
        // ran *after* this iterator was allocated (e.g. an inner abrupt
        // completion) could reclaim object cells referenced by _values and
        // surface "Stale heap handle." on the next consumer access. In lazy
        // mode the live iterator object and next method must likewise survive.
        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            for (var i = _index; i < _values.Count; i++)
            {
                var v = _values[i];
                if (v.Tag == JsValueTag.Object)
                    tracer.Trace(v.AsObjectHandle());
            }

            if (IsLazy)
            {
                if (IteratorObject.Tag == JsValueTag.Object)
                    tracer.Trace(IteratorObject.AsObjectHandle());
                if (NextMethod.Tag == JsValueTag.Object)
                    tracer.Trace(NextMethod.AsObjectHandle());
            }
        }
    }

    private sealed class ForInIteratorObject : JsObject
    {
        private readonly IReadOnlyList<string> _keys;
        private int _index;

        public ForInIteratorObject(IReadOnlyList<string> keys)
        {
            _keys = keys;
        }

        public bool TryMoveNext(out string key)
        {
            if (_index >= _keys.Count)
            {
                key = string.Empty;
                return false;
            }

            key = _keys[_index++];
            return true;
        }
    }
}
