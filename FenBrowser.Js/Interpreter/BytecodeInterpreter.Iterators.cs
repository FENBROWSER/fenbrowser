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
    private JsValue CreateForOfIterator(JsValue source)
    {
        var values = new List<JsValue>();

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
            for (var i = 0; i < s.Length; i++)
            {
                values.Add(JsValue.FromString(s[i].ToString()));
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

    // Drains a user iterator into a value list by calling .next() repeatedly until
    // the returned IteratorResult.done is true. Bounded by MaxCallDepth-friendly
    // semantics: each .next() goes through CallFunction so the recursion guard
    // applies.
    private void DrainIteratorIntoList(JsValue iter, List<JsValue> sink)
    {
        if (iter.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator @@iterator did not return an object."));
        }

        // Audit gap §1 #1/#2/#5: pin the iterator handle and every object-tag
        // value that lands in the sink for the duration of the drain. Without
        // these roots, a GC triggered inside user-code .next()/abrupt
        // completion reclaims cells we are about to read back, surfacing as
        // "Stale heap handle." crashes in
        //   annexB/.../for-of/iterator-close-return-emulates-undefined-throws-when-called.js
        //   built-ins/AggregateError/errors-iterabletolist-failures.js
        //   built-ins/Array/from/iter-map-fn-err.js
        // Plus: suspend auto-MinorCollect for the duration. CallFunction returns
        // a JsValue that briefly lives only in a C# local while the callee
        // frame is being popped — between those two beats an auto-trigger
        // would reclaim an object-tag result whose cell isn't yet rooted.
        var rootMark = _heap.RootCount;
        var savedYoungThreshold = _heap.YoungAllocationsPerMinorGc;
        _heap.YoungAllocationsPerMinorGc = -1;
        try
        {
            _heap.PushRoot(iter.AsObjectHandle());
            var iterObj = _heap.GetObject(iter.AsObjectHandle());
            while (true)
            {
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

    private sealed class ForOfIteratorObject : JsObject
    {
        private readonly IReadOnlyList<JsValue> _values;
        private int _index;

        public ForOfIteratorObject(IReadOnlyList<JsValue> values)
        {
            _values = values;
        }

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
        // surface "Stale heap handle." on the next consumer access.
        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            for (var i = _index; i < _values.Count; i++)
            {
                var v = _values[i];
                if (v.Tag == JsValueTag.Object)
                    tracer.Trace(v.AsObjectHandle());
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
