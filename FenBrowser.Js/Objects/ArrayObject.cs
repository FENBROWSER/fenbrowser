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
    private JsValue[] _dense;
    private int _denseCount;
    private bool _denseAbandoned;

    /// <summary>Elements currently held in the dense vector.</summary>
    internal int DenseCount => _denseAbandoned ? 0 : _denseCount;

    internal bool IsDense => !_denseAbandoned;

    public ArrayObject(int initialCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _dense = initialCapacity == 0
            ? Array.Empty<JsValue>()
            : new JsValue[initialCapacity];
    }

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

        // "length" was answered from _denseCount while the vector was in use and
        // was never stored; it has to become a real property now, with the
        // attributes 10.4.2 requires of it.
        base.DefineOwnProperty(LengthKey, new JsPropertyDescriptor(
            JsValue.FromNumber(_denseCount),
            Writable: _lengthWritable,
            Enumerable: false,
            Configurable: false));

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
        if (!_denseAbandoned && string.Equals(key, LengthKey, StringComparison.Ordinal))
        {
            if (TryApplyDenseLength(descriptor, out var applied))
            {
                return applied;
            }

            Materialise();
            return base.DefineOwnProperty(key, descriptor);
        }

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

    // 10.4.2.1: an Array length is always present, writable by default, never
    // enumerable and never configurable. While the vector is in use its value is
    // exactly _denseCount - the vector is hole-free and holds 0..Count-1 - so
    // storing it as a property would mean a shape transition and a property-slot
    // allocation per array, and a slot write on every append. A fresh array
    // literal paid two array allocations and two Array.Copy calls for that one
    // property before holding a single element. Answer it from the count instead,
    // so a dense array carries no shape-tracked properties at all.
    private const string LengthKey = "length";

    // Object.defineProperty can make length non-writable while the array is still
    // dense; that is representable without giving the vector up, so it is kept
    // here rather than forcing a Materialise.
    private bool _lengthWritable = true;

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (!_denseAbandoned && string.Equals(key, LengthKey, StringComparison.Ordinal))
        {
            descriptor = new JsPropertyDescriptor(
                JsValue.FromNumber(_denseCount),
                Writable: _lengthWritable,
                Enumerable: false,
                Configurable: false);
            return true;
        }

        if (!_denseAbandoned && IsArrayIndexKey(key, out var index) && index < (uint)_denseCount)
        {
            descriptor = new JsPropertyDescriptor(
                _dense[index], Writable: true, Enumerable: true, Configurable: true);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    public override bool SetProperty(string key, JsValue value)
    {
        if (!_denseAbandoned && string.Equals(key, LengthKey, StringComparison.Ordinal))
        {
            if (!_lengthWritable)
            {
                return false;
            }

            if (TryApplyDenseLength(
                    new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false),
                    out var applied))
            {
                return applied;
            }

            Materialise();
            return base.SetProperty(key, value);
        }

        return base.SetProperty(key, value);
    }

    /// <summary>
    /// Applies a write to length that the vector can represent. Returns false
    /// when it cannot, leaving the caller to materialise: growing past the count
    /// would introduce holes, and an accessor or a configurable/enumerable
    /// length is not an array length at all.
    /// </summary>
    private bool TryApplyDenseLength(in JsPropertyDescriptor descriptor, out bool applied)
    {
        applied = false;

        if (descriptor.IsAccessor ||
            (descriptor.HasEnumerable && descriptor.Enumerable) ||
            (descriptor.HasConfigurable && descriptor.Configurable))
        {
            return false;
        }

        if (descriptor.HasWritable && !descriptor.Writable)
        {
            // Freezing the length is representable; the vector still describes
            // every element, it just may no longer be resized through length.
            _lengthWritable = false;
        }

        if (!descriptor.HasValue)
        {
            applied = true;
            return true;
        }

        var requested = descriptor.Value.Tag switch
        {
            JsValueTag.Int32 => descriptor.Value.AsInt32(),
            JsValueTag.Number => descriptor.Value.AsNumber(),
            _ => double.NaN,
        };

        if (double.IsNaN(requested) || requested < 0 || requested > uint.MaxValue ||
            Math.Truncate(requested) != requested)
        {
            // A non-numeric or out-of-range length is a RangeError the base
            // class already raises correctly.
            return false;
        }

        var target = (uint)requested;
        if (target == (uint)_denseCount)
        {
            applied = true;
            return true;
        }

        if (target < (uint)_denseCount)
        {
            applied = TryDenseTruncate(target);
            return applied;
        }

        // Growing leaves holes, which the vector cannot represent.
        return false;
    }

    public override bool DeleteProperty(string key)
    {
        if (!_denseAbandoned && string.Equals(key, LengthKey, StringComparison.Ordinal))
        {
            // 10.4.2: length is never configurable.
            return false;
        }

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

        // While dense, length is not in the base table but is still an own
        // property, and getOwnPropertyNames must see it. It sorts after the
        // index keys, which is where 10.1.11.1 puts a non-index string key.
        if (!_denseAbandoned)
        {
            yield return new KeyValuePair<string, JsPropertyDescriptor>(
                LengthKey,
                new JsPropertyDescriptor(
                    JsValue.FromNumber(_denseCount),
                    Writable: _lengthWritable,
                    Enumerable: false,
                    Configurable: false));
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
