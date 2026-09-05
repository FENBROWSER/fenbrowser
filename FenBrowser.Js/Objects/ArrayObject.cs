using System;
using System.Collections.Generic;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 10.4.2 — Array exotic object marker. Used by instanceof checks
// and by JSON.parse / String.prototype.split to produce real arrays.
//
// Elements live in a dense vector rather than in the shape-tracked property
// table the base class uses for named properties. A shape transition per element
// is the wrong shape of cost for an array: building one of 50,000 entries used to
// create 50,000 shapes and 50,000 property slots, and every append formatted its
// index into a string to hash it. Appending measured ~9.6us per element, which is
// where a page like reCAPTCHA spent seconds of its budget.
//
// The vector holds indices 0..Count-1 and only ever holds plain data properties
// that are writable, enumerable and configurable, with no holes. Anything an
// array can legally do that breaks those invariants — a hole, a getter, a frozen
// element, a non-index key — makes the object give the fast path up: Materialise
// copies the elements into ordinary properties and everything afterwards runs on
// the base class exactly as it did before. Correctness never depends on staying
// dense, only speed does.
public sealed class ArrayObject : JsObject
{
    private JsValue[] _dense = Array.Empty<JsValue>();
    private int _denseCount;
    private bool _denseAbandoned;

    /// <summary>Elements currently held in the dense vector.</summary>
    internal int DenseCount => _denseAbandoned ? 0 : _denseCount;

    internal bool IsDense => !_denseAbandoned;

    /// <summary>
    /// Appends to the dense vector. Returns false when this array is no longer
    /// dense, in which case the caller must take the ordinary property path.
    /// </summary>
    internal bool TryDenseAppend(JsValue value)
    {
        if (_denseAbandoned)
        {
            return false;
        }

        if (_denseCount == _dense.Length)
        {
            Grow();
        }

        _dense[_denseCount++] = value;
        DenseBarrier(value);
        return true;
    }

    internal bool TryDenseGet(uint index, out JsValue value)
    {
        if (!_denseAbandoned && index < (uint)_denseCount)
        {
            value = _dense[index];
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// Writes an existing dense slot, or appends at exactly Count. Returns false
    /// when the index is out of dense range or the array is no longer dense.
    /// </summary>
    internal bool TryDenseSet(uint index, JsValue value)
    {
        if (_denseAbandoned)
        {
            return false;
        }

        if (index < (uint)_denseCount)
        {
            _dense[index] = value;
            DenseBarrier(value);
            return true;
        }

        return index == (uint)_denseCount && TryDenseAppend(value);
    }

    /// <summary>Drops the last dense element. False when there is nothing dense to drop.</summary>
    internal bool TryDensePopLast(out JsValue value)
    {
        if (!_denseAbandoned && _denseCount > 0)
        {
            value = _dense[--_denseCount];
            _dense[_denseCount] = JsValue.Undefined;
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// Truncates the dense vector to <paramref name="length"/> elements. Used by
    /// ArraySetLength, which is otherwise obliged to delete element keys one at a
    /// time. False when the array is not dense or the target is not a truncation.
    /// </summary>
    internal bool TryDenseTruncate(uint length)
    {
        if (_denseAbandoned || length > (uint)_denseCount)
        {
            return false;
        }

        for (var i = (int)length; i < _denseCount; i++)
        {
            _dense[i] = JsValue.Undefined;
        }

        _denseCount = (int)length;
        return true;
    }

    private void Grow()
    {
        var capacity = _dense.Length == 0 ? 8 : _dense.Length * 2;
        Array.Resize(ref _dense, capacity);
    }

    private void DenseBarrier(JsValue value)
    {
        // The base class barriers every descriptor store; dense elements bypass
        // that path entirely, so without this an Old array holding a Young element
        // never dirties its card and the element is collected out from under it.
        if (value.Tag == JsValueTag.Object)
        {
            BarrierInternalSlot(value);
        }
    }

    // Copies the dense elements into ordinary properties, in index order so their
    // creation order matches enumeration order, and stops using the fast path.
    private void Materialise()
    {
        if (_denseAbandoned)
        {
            return;
        }

        _denseAbandoned = true;
        for (var i = 0; i < _denseCount; i++)
        {
            base.DefineOwnProperty(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new JsPropertyDescriptor(_dense[i], Writable: true, Enumerable: true, Configurable: true));
        }

        _dense = Array.Empty<JsValue>();
        _denseCount = 0;
    }

    // A descriptor the dense vector can represent: a plain data property that is
    // writable, enumerable and configurable. Partial descriptors (from
    // Object.defineProperty with only some fields) are rejected so the base class
    // applies its own merge rules against the existing property.
    private static bool IsDenseRepresentable(in JsPropertyDescriptor descriptor)
    {
        return !descriptor.IsAccessor &&
               descriptor.HasValue &&
               descriptor.HasWritable && descriptor.Writable &&
               descriptor.HasEnumerable && descriptor.Enumerable &&
               descriptor.HasConfigurable && descriptor.Configurable;
    }

    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (!_denseAbandoned && IsArrayIndexKey(key, out var index))
        {
            if (IsDenseRepresentable(descriptor) && index <= (uint)_denseCount && Extensible)
            {
                return TryDenseSet(index, descriptor.Value);
            }

            // Writing past the end would leave a hole, and anything that is not a
            // plain writable/enumerable/configurable data property cannot be
            // represented by the vector. Fall back for good.
            Materialise();
        }

        return base.DefineOwnProperty(key, descriptor);
    }

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (!_denseAbandoned && IsArrayIndexKey(key, out var index) && index < (uint)_denseCount)
        {
            descriptor = new JsPropertyDescriptor(
                _dense[index], Writable: true, Enumerable: true, Configurable: true);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    public override bool DeleteProperty(string key)
    {
        if (!_denseAbandoned && IsArrayIndexKey(key, out var index) && index < (uint)_denseCount)
        {
            // Removing the last element keeps the vector hole-free; removing any
            // other element does not, so give the fast path up first.
            if (index == (uint)(_denseCount - 1))
            {
                _ = TryDensePopLast(out _);
                return true;
            }

            Materialise();
        }

        return base.DeleteProperty(key);
    }

    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        // ECMA-262 10.1.11.1: array-index keys ascending, then the remaining string
        // keys in creation order. While dense, the base class holds no index keys,
        // so emitting the vector first and deferring to the base for the rest gives
        // exactly that order.
        if (!_denseAbandoned)
        {
            for (var i = 0; i < _denseCount; i++)
            {
                yield return new KeyValuePair<string, JsPropertyDescriptor>(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new JsPropertyDescriptor(_dense[i], Writable: true, Enumerable: true, Configurable: true));
            }
        }

        foreach (var pair in base.EnumerateOwnProperties())
        {
            yield return pair;
        }
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);

        // Dense elements are live references the base class cannot see.
        for (var i = 0; i < _denseCount; i++)
        {
            if (_dense[i].Tag == JsValueTag.Object)
            {
                tracer.Trace(_dense[i].AsObjectHandle());
            }
        }
    }
}
