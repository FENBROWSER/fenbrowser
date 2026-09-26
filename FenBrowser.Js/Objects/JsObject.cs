using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 ordinary object with shape-based property storage (plan §31).
//
// Properties are stored in a flat JsPropertyDescriptor[] array indexed by the
// slot number obtained from Shape.TryGetSlot(). Shapes form a parent-linked
// tree and only grow — deletion marks the slot as absent rather than shrinking
// the array, so existing inline caches stay valid.
//
// The older Dictionary<string, JsPropertyDescriptor> storage is replaced by
// Shape + array. For enumeration and legacy paths, EnumerateOwnProperties
// walks the array and Shape lineage simultaneously.
// Marks an object as carrying a spec internal slot that Object.prototype.toString
// (20.1.3.6) maps to a builtin tag: [[ParameterMap]] → "Arguments", [[ErrorData]] → "Error".
internal enum BuiltinTagSlot
{
    None,
    Arguments,
    Error,
}

public class JsObject : ITraceable
{
    // Current shape describing the property layout. Starts at the root shape
    // (empty) and transitions each time DefineOwnProperty adds a new property.
    private Shape _shape = Shape.Root;

    // Flat property storage indexed by Shape slot, split so that a data
    // property costs a value and a byte. A descriptor carries both accessor
    // halves and four "was this field supplied" flags as well as the value,
    // which made one property a little over a hundred bytes and an object of
    // four properties over four hundred -- for storage that is a value and
    // three attribute bits in almost every case. An accessor is rare enough to
    // keep its halves in a side table, and the flags say which slots have one.
    private Slot[] _slots = [];
    private Dictionary<int, AccessorPair>? _accessors;

    // ECMA-262 10.1.11.1: own string keys enumerate in property-creation order.
    // A deleted-then-readded property counts as a new creation and must move to
    // the end, and a shape reuses the original slot so inline caches stay valid,
    // so the shape chain alone stops reflecting creation order after a delete.
    // Sequence 0 means the slot never held a live property.
    private struct Slot
    {
        public JsValue Value;
        public int InsertionSeq;
        public PropertyFlags Flags;
    }

    [Flags]
    private enum PropertyFlags : byte
    {
        None = 0,
        Present = 1,
        Writable = 2,
        Enumerable = 4,
        Configurable = 8,
        Accessor = 16,
    }

    private readonly record struct AccessorPair(JsValue Get, JsValue Set);

    private int _nextSeq = 1;

    // Symbol-keyed own properties. Not shape-tracked, so they carry their own
    // key, but stored the same compact way: an object that has any is almost
    // always carrying one -- an arguments object's @@iterator, a collection's
    // @@toStringTag -- and a dictionary for a single entry cost more than the
    // object it hung off. A deleted entry keeps its place so that the accessor
    // table's indices stay valid; symbol deletion is rare enough to afford it.
    private SymbolProperty[]? _symbolProperties;
    private int _symbolCount;

    private struct SymbolProperty
    {
        public long Key;
        public JsValue Value;
        public PropertyFlags Flags;
    }

    // An accessor's two halves live in the same side table as a string
    // property's, under an index no slot can take.
    private static int AccessorKeyForSymbol(int index) => -1 - index;

    private bool TryFindSymbol(long symbolId, out int index)
    {
        var entries = _symbolProperties;
        if (entries is not null)
        {
            for (var i = 0; i < _symbolCount; i++)
            {
                if (entries[i].Key == symbolId && (entries[i].Flags & PropertyFlags.Present) != 0)
                {
                    index = i;
                    return true;
                }
            }
        }

        index = -1;
        return false;
    }

    private JsPropertyDescriptor ReadSymbol(int index)
    {
        var flags = _symbolProperties![index].Flags;
        if ((flags & PropertyFlags.Accessor) != 0)
        {
            var accessor = _accessors![AccessorKeyForSymbol(index)];
            return JsPropertyDescriptor.Accessor(
                accessor.Get,
                accessor.Set,
                (flags & PropertyFlags.Enumerable) != 0,
                (flags & PropertyFlags.Configurable) != 0);
        }

        return new JsPropertyDescriptor(
            _symbolProperties[index].Value,
            (flags & PropertyFlags.Writable) != 0,
            (flags & PropertyFlags.Enumerable) != 0,
            (flags & PropertyFlags.Configurable) != 0);
    }

    private void WriteSymbol(int index, long symbolId, in JsPropertyDescriptor descriptor)
    {
        var flags = PropertyFlags.Present;
        if (descriptor.Enumerable) flags |= PropertyFlags.Enumerable;
        if (descriptor.Configurable) flags |= PropertyFlags.Configurable;

        if (descriptor.IsAccessor)
        {
            flags |= PropertyFlags.Accessor;
            (_accessors ??= new Dictionary<int, AccessorPair>())[AccessorKeyForSymbol(index)] =
                new AccessorPair(descriptor.Get, descriptor.Set);
            _symbolProperties![index].Value = JsValue.Undefined;
        }
        else
        {
            if (descriptor.Writable) flags |= PropertyFlags.Writable;
            _symbolProperties![index].Value = descriptor.Value;
            _accessors?.Remove(AccessorKeyForSymbol(index));
        }

        _symbolProperties[index].Key = symbolId;
        _symbolProperties[index].Flags = flags;
    }

    private int AppendSymbolSlot()
    {
        if (_symbolProperties is null)
        {
            _symbolProperties = new SymbolProperty[2];
        }
        else if (_symbolCount == _symbolProperties.Length)
        {
            var bigger = new SymbolProperty[_symbolCount * 2];
            Array.Copy(_symbolProperties, bigger, _symbolCount);
            _symbolProperties = bigger;
        }

        return _symbolCount++;
    }

    // Plan H.5: private field brand. Each class with private members gets a unique
    // brand Symbol stored here. The constructor stamps it; private field access
    // checks it. Null means "no brand on this object."
    internal long PrivateBrand { get; set; }

    internal bool HasPrivateBrand(long brand) => PrivateBrand == brand;

    // ECMA-262 20.1.3.6 Object.prototype.toString uses internal-slot presence
    // ([[ParameterMap]], [[ErrorData]]) to pick the builtin tag. We model those
    // slots as a marker here; ordinary objects leave it None.
    internal BuiltinTagSlot ToStringTagSlot { get; set; } = BuiltinTagSlot.None;

    // Annex B [[IsHTMLDDA]] host-exotic marker. Ordinary objects leave this
    // false; browser hosts may mark their document.all-compatible object.
    internal bool IsHtmlDda { get; set; }

    // ECMA-262 immutable prototype exotic object (e.g. %Object.prototype%): its
    // [[SetPrototypeOf]] succeeds only when the new value equals the current one.
    internal bool ImmutablePrototype { get; set; }

    // True only for an object currently used as an ordinary script function's
    // instance prototype. This is diagnostic metadata, not an ECMAScript-visible
    // internal slot, and dies with the object rather than retaining a heap handle.
    internal bool IsFunctionInstancePrototype { get; set; }

    public ObjectHandle? PrototypeHandle { get; private set; }

    public bool Extensible { get; private set; } = true;

    // Tier 4 #22: GC bookkeeping. Set by JsHeap.AllocateObject when this
    // object is registered with the heap. WriteBarrier on every
    // object-valued property write keeps the generational remembered set
    // accurate without forcing every callsite to remember to barrier.
    internal ObjectHandle? OwnerHandle;
    internal JsHeap? OwnerHeap;

    private void BarrierIfObject(JsValue value)
    {
        if (value.Tag == JsValueTag.Object &&
            OwnerHandle is { } owner &&
            OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, value.AsObjectHandle());
        }
    }

    // Write barrier for internal-slot stores that bypass SetProperty /
    // DefineOwnProperty (async context, generator, function and promise
    // internals). Keeps the generational remembered set precise: an Old owner
    // receiving a Young child dirties its card, so the heap never needs blanket
    // sticky rescans of non-plain object payloads.
    internal void BarrierInternalSlot(JsValue value)
    {
        if (value.Tag == JsValueTag.Object &&
            OwnerHandle is { } owner &&
            OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, value.AsObjectHandle());
        }
    }

    internal void BarrierInternalSlot(ObjectHandle? handle)
    {
        if (handle is { } child &&
            OwnerHandle is { } owner &&
            OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, child);
        }
    }

    // Internal accessors for inline caches. A cache never wants a descriptor --
    // it wants the value, and to know that reading it is unobservable -- so
    // these answer without materialising one.
    internal Shape CurrentShape => _shape;

    /// <summary>Reads a slot holding a plain data property.</summary>
    internal bool TryReadDataSlot(int slot, out JsValue value)
    {
        if ((uint)slot < (uint)_slots.Length &&
            (_slots[slot].Flags & (PropertyFlags.Present | PropertyFlags.Accessor)) == PropertyFlags.Present)
        {
            value = _slots[slot].Value;
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>Whether a slot holds a data property a store may write through.</summary>
    /// <summary>The getter and setter of an accessor slot; false when the slot is not an accessor.</summary>
    internal bool TryReadAccessorSlot(int slot, out JsValue getter, out JsValue setter)
    {
        if ((uint)slot < (uint)_slots.Length &&
            (_slots[slot].Flags & (PropertyFlags.Present | PropertyFlags.Accessor)) == (PropertyFlags.Present | PropertyFlags.Accessor))
        {
            getter = _accessors![slot].Get;
            setter = _accessors[slot].Set;
            return true;
        }

        getter = JsValue.Undefined;
        setter = JsValue.Undefined;
        return false;
    }

    internal bool IsWritableDataSlot(int slot) =>
        (uint)slot < (uint)_slots.Length &&
        (_slots[slot].Flags & (PropertyFlags.Present | PropertyFlags.Accessor | PropertyFlags.Writable)) ==
            (PropertyFlags.Present | PropertyFlags.Writable);

    /// <summary>
    /// Writes a slot the caller has already established is a writable data
    /// property. The write barrier belongs to the caller, which holds the
    /// object's handle.
    /// </summary>
    internal void WriteDataSlot(int slot, JsValue value) => _slots[slot].Value = value;

    private bool TryReadSlot(int slot, out JsPropertyDescriptor descriptor)
    {
        if ((uint)slot >= (uint)_slots.Length || (_slots[slot].Flags & PropertyFlags.Present) == 0)
        {
            descriptor = default;
            return false;
        }

        var flags = _slots[slot].Flags;
        descriptor = (flags & PropertyFlags.Accessor) != 0
            ? JsPropertyDescriptor.Accessor(
                _accessors![slot].Get,
                _accessors[slot].Set,
                (flags & PropertyFlags.Enumerable) != 0,
                (flags & PropertyFlags.Configurable) != 0)
            : new JsPropertyDescriptor(
                _slots[slot].Value,
                (flags & PropertyFlags.Writable) != 0,
                (flags & PropertyFlags.Enumerable) != 0,
                (flags & PropertyFlags.Configurable) != 0);
        return true;
    }

    private void WriteSlot(int slot, in JsPropertyDescriptor descriptor)
    {
        var flags = PropertyFlags.Present;
        if (descriptor.Enumerable) flags |= PropertyFlags.Enumerable;
        if (descriptor.Configurable) flags |= PropertyFlags.Configurable;

        if (descriptor.IsAccessor)
        {
            flags |= PropertyFlags.Accessor;
            (_accessors ??= new Dictionary<int, AccessorPair>())[slot] =
                new AccessorPair(descriptor.Get, descriptor.Set);
            _slots[slot].Value = JsValue.Undefined;
        }
        else
        {
            if (descriptor.Writable) flags |= PropertyFlags.Writable;
            _slots[slot].Value = descriptor.Value;
            _accessors?.Remove(slot);
        }

        _slots[slot].Flags = flags;
    }

    private void ClearSlot(int slot)
    {
        _slots[slot].Flags = PropertyFlags.None;
        _slots[slot].Value = JsValue.Undefined;
        _accessors?.Remove(slot);
    }

    public void PreventExtensions()
    {
        Extensible = false;
    }

    // ECMA-262 9.1.6 [[DefineOwnProperty]].
    public virtual bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
        => OrdinaryDefineOwnProperty(key, descriptor);

    /// <summary>
    /// Puts a property an exotic object has been answering from outside its
    /// shape into the shape, as it stands. The property already exists as far
    /// as the program can tell, so the object having been made non-extensible
    /// since does not stop it (ECMA-262 10.1.6.3 applies to new properties).
    /// </summary>
    private protected bool MoveIntoShape(string key, JsPropertyDescriptor descriptor)
    {
        var extensible = Extensible;
        Extensible = true;
        try
        {
            return OrdinaryDefineOwnProperty(key, descriptor);
        }
        finally
        {
            Extensible = extensible;
        }
    }

    // ECMA-262 10.1.6.1 OrdinaryDefineOwnProperty.
    private protected bool OrdinaryDefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var existingSlot) &&
            TryReadSlot(existingSlot, out var current))
        {
            // ECMA-262 10.1.11.2 ValidateAndApplyPropertyDescriptor
            // Step 4: current.[[Configurable]] is false
            if (!current.Configurable)
            {
                // 4.a: Desc.[[Configurable]] is true → return false
                if (descriptor.Configurable)
                    return false;
                // 4.b: Desc.[[Enumerable]] differs → return false
                if (descriptor.Enumerable != current.Enumerable)
                    return false;
            }

            // Step 6: data ↔ accessor type change when non-configurable
            if (!current.Configurable && current.IsAccessor != descriptor.IsAccessor)
                return false;

            // Step 7: both data descriptors
            if (!current.IsAccessor && !descriptor.IsAccessor)
            {
                // 7.a: current non-configurable and non-writable
                if (!current.Configurable && !current.Writable)
                {
                    // 7.a.i: Desc.[[Writable]] is true → return false
                    if (descriptor.Writable)
                        return false;
                    // 7.a.ii: Desc.[[Value]] differs → return false
                    if (!JsValueSameValue(descriptor.Value, current.Value))
                        return false;
                }
            }
            // Step 8: both accessor descriptors, non-configurable
            else if (!current.Configurable && current.IsAccessor && descriptor.IsAccessor)
            {
                if (!JsValueSameValue(descriptor.Get, current.Get) ||
                    !JsValueSameValue(descriptor.Set, current.Set))
                    return false;
            }

            // Step 9: apply the descriptor
            WriteSlot(existingSlot, descriptor);
            BarrierIfObject(descriptor.Value);
            if (descriptor.IsAccessor)
            {
                BarrierIfObject(descriptor.Get);
                BarrierIfObject(descriptor.Set);
            }
            return true;
        }

        // Re-adding a previously deleted property (slot exists but value is null)
        if (_shape.TryGetSlot(key, out var deletedSlot))
        {
            EnsurePropertyStorage(deletedSlot);
            _slots[deletedSlot].InsertionSeq = _nextSeq++;
            WriteSlot(deletedSlot, descriptor);
            BarrierIfObject(descriptor.Value);
            if (descriptor.IsAccessor)
            {
                BarrierIfObject(descriptor.Get);
                BarrierIfObject(descriptor.Set);
            }
            return true;
        }

        // New property: object must be extensible (ECMA-262 9.1.6.3 step 3.b).
        if (!Extensible)
            return false;

        // New property: transition shape and grow array.
        _shape = _shape.TransitionTo(key);
        var slot = _shape.PropertyCount - 1;
        EnsurePropertyStorage(slot);
        WriteSlot(slot, descriptor);
        _slots[slot].InsertionSeq = _nextSeq++;
        BarrierIfObject(descriptor.Value);
        if (descriptor.IsAccessor)
        {
            BarrierIfObject(descriptor.Get);
            BarrierIfObject(descriptor.Set);
        }
        return true;
    }

    /// <summary>
    /// The new-property path of <see cref="DefineOwnProperty"/> for a writable,
    /// enumerable, configurable data property whose shape transition is already
    /// known - an add-property cache replaying a [[Set]] it has proved safe.
    /// </summary>
    internal void AppendDataProperty(Shape next, JsValue value)
    {
        _shape = next;
        var slot = next.PropertyCount - 1;
        EnsurePropertyStorage(slot);
        WriteSlot(slot, new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        _slots[slot].InsertionSeq = _nextSeq++;
        BarrierIfObject(value);
    }

    // A property table that starts empty and doubles reaches four slots by way
    // of one, two and four, so an object with three properties allocated three
    // descriptor arrays and three sequence arrays and copied between them twice
    // — before holding anything. A descriptor is a little over a hundred bytes
    // (a value and both accessor halves are JsValues), which made those
    // discarded steps the larger part of what a small object cost: an object of
    // three properties allocated about 1.2KB, most of it thrown away on the way.
    // Starting at four covers most objects in one allocation and costs nothing
    // for the ones that stay smaller, since the array is sized in slots the
    // object was always going to grow into.
    private const int InitialPropertyCapacity = 4;

    private void EnsurePropertyStorage(int slot)
    {
        if (slot < _slots.Length)
        {
            return;
        }

        var newLen = Math.Max(
            Math.Max(_slots.Length * 2, slot + 1),
            InitialPropertyCapacity);
        var bigger = new Slot[newLen];
        Array.Copy(_slots, bigger, _slots.Length);
        _slots = bigger;
    }

    // Own property lookup via Shape → slot → array. Null slot = deleted.
    // Virtual so exotic objects (String) can synthesise computed properties
    // (indexed character access) on demand.
    /// <summary>
    /// Whether an own property could appear under this name without the shape
    /// changing to say so.
    /// </summary>
    /// <remarks>
    /// True only for the names an exotic object answers from somewhere the
    /// shape does not describe - an array's dense vector, a string's
    /// characters, a typed array's buffer. A property cache guards a receiver by
    /// its shape, so for those names the guard could not see one appear, and a
    /// program that assumed the receiver does not have the name would keep
    /// answering from the prototype after it did.
    ///
    /// It is asked per name rather than per object because the distinction is
    /// per name: an array can grow a `2`, and it can grow a `length`, but it can
    /// never grow a `push` outside its shape - and `push` is the read that
    /// matters, because it is on the prototype.
    /// </remarks>
    public virtual bool MayGainOwnPropertyOutsideShape(string key) => false;

    public virtual bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var slot) && TryReadSlot(slot, out descriptor))
        {
            return true;
        }

        descriptor = default;
        return false;
    }

    // Enumerate own string-keyed properties. ECMA-262 10.1.11.1 OrdinaryOwnPropertyKeys:
    // array-index keys first in ascending numeric order, then the remaining string keys
    // in insertion (property-creation) order. Virtual so exotic objects (String) can
    // yield their synthesised indexed properties.
    public virtual IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        var chain = new List<(string key, int slot)>();
        for (Shape? s = _shape; s != null && s != Shape.Root; s = s.Parent)
            chain.Add((s.AddedProperty!, s.AddedSlot));
        chain.Reverse();

        List<(uint idx, string key, int slot)>? integerKeys = null;
        List<(int seq, string key, int slot)>? stringKeys = null;
        var deleteReordered = false;
        foreach (var (key, slot) in chain)
        {
            if (IsArrayIndexKey(key, out var idx))
            {
                (integerKeys ??= new List<(uint, string, int)>()).Add((idx, key, slot));
            }
            else
            {
                // Chain order is shape-transition order; the slot sequence is the true
                // creation order. They diverge only after a delete+re-add, in which
                // case the seq for this slot won't match its chain position.
                if (stringKeys is { Count: > 0 } && _slots[slot].InsertionSeq < stringKeys[^1].seq)
                    deleteReordered = true;
                (stringKeys ??= new List<(int, string, int)>()).Add((_slots[slot].InsertionSeq, key, slot));
            }
        }

        // Fast path: no array-index keys and no delete-induced reordering, so the
        // shape-chain order already matches the spec enumeration order.
        if (integerKeys is null && !deleteReordered)
        {
            foreach (var (key, slot) in chain)
            {
                if (TryReadSlot(slot, out var desc))
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }

            yield break;
        }

        if (integerKeys is not null)
        {
            integerKeys.Sort((a, b) => a.idx.CompareTo(b.idx));
            foreach (var (_, key, slot) in integerKeys)
            {
                if (TryReadSlot(slot, out var desc))
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }
        }

        if (stringKeys is not null)
        {
            if (deleteReordered)
                stringKeys.Sort((a, b) => a.seq.CompareTo(b.seq));
            foreach (var (_, key, slot) in stringKeys)
            {
                if (TryReadSlot(slot, out var desc))
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }
        }
    }

    // ECMA-262 6.1.7: an array index is a canonical numeric string whose value is an
    // integer in [0, 2^32 - 1). Used to order own keys (integer indices come first).
    internal static bool IsArrayIndexKey(string key, out uint index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key) || key.Length > 10)
            return false;
        if (key.Length > 1 && key[0] == '0')
            return false;   // no leading zeros — not a canonical numeric string

        ulong result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
                return false;
            result = (result * 10) + (ulong)(c - '0');
        }

        if (result >= 4294967295UL)   // 2^32 - 1 is not itself an array index
            return false;

        index = (uint)result;
        return true;
    }

    // Symbol-keyed property access.
    public bool DefineOwnSymbolProperty(long symbolId, JsPropertyDescriptor descriptor)
    {
        if (TryFindSymbol(symbolId, out var index))
        {
            var existing = ReadSymbol(index);
            if (!existing.Configurable)
            {
                if (descriptor.Configurable) return false;
                if (descriptor.Enumerable != existing.Enumerable) return false;
            }
            if (!existing.Configurable && existing.IsAccessor != descriptor.IsAccessor) return false;
            if (!existing.IsAccessor && !descriptor.IsAccessor)
            {
                if (!existing.Configurable && !existing.Writable)
                {
                    if (descriptor.Writable) return false;
                    if (!JsValueSameValue(descriptor.Value, existing.Value)) return false;
                }
            }
            else if (!existing.Configurable && existing.IsAccessor && descriptor.IsAccessor)
            {
                if (!JsValueSameValue(descriptor.Get, existing.Get) ||
                    !JsValueSameValue(descriptor.Set, existing.Set))
                    return false;
            }
            WriteSymbol(index, symbolId, descriptor);
            BarrierIfObject(descriptor.Value);
            if (descriptor.IsAccessor)
            {
                BarrierIfObject(descriptor.Get);
                BarrierIfObject(descriptor.Set);
            }
            return true;
        }

        if (!Extensible) return false;

        WriteSymbol(AppendSymbolSlot(), symbolId, descriptor);
        BarrierIfObject(descriptor.Value);
        if (descriptor.IsAccessor)
        {
            BarrierIfObject(descriptor.Get);
            BarrierIfObject(descriptor.Set);
        }
        return true;
    }

    public bool TryGetOwnSymbolProperty(long symbolId, out JsPropertyDescriptor descriptor)
    {
        if (TryFindSymbol(symbolId, out var index))
        {
            descriptor = ReadSymbol(index);
            return true;
        }

        descriptor = default;
        return false;
    }

    public IEnumerable<KeyValuePair<long, JsPropertyDescriptor>> EnumerateOwnSymbolProperties()
    {
        if (_symbolProperties is null) yield break;
        for (var i = 0; i < _symbolCount; i++)
        {
            if ((_symbolProperties[i].Flags & PropertyFlags.Present) != 0)
            {
                yield return new KeyValuePair<long, JsPropertyDescriptor>(_symbolProperties[i].Key, ReadSymbol(i));
            }
        }
    }

    public bool TryGetSymbolProperty(long symbolId, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (TryGetOwnSymbolProperty(symbolId, out descriptor))
            return true;
        if (PrototypeHandle is { } proto)
            return prototypeResolver(proto).TryGetSymbolProperty(symbolId, prototypeResolver, out descriptor);
        descriptor = default;
        return false;
    }

    public bool DeleteSymbolProperty(long symbolId)
    {
        if (!TryFindSymbol(symbolId, out var index)) return false;
        if (!ReadSymbol(index).Configurable) return false;

        _symbolProperties![index].Flags = PropertyFlags.None;
        _symbolProperties[index].Value = JsValue.Undefined;
        _accessors?.Remove(AccessorKeyForSymbol(index));
        return true;
    }

    // Full property lookup: own + prototype chain walk.
    public bool TryGetProperty(string key, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (TryGetOwnProperty(key, out descriptor))
            return true;
        if (PrototypeHandle is { } proto)
            return prototypeResolver(proto).TryGetProperty(key, prototypeResolver, out descriptor);
        descriptor = default;
        return false;
    }

    public virtual bool SetProperty(string key, JsValue value)
    {
        if (_shape.TryGetSlot(key, out var slot) && TryReadSlot(slot, out var existing))
        {
            if (existing.IsAccessor || !existing.Writable) return false;
            _slots[slot].Value = value;
            BarrierIfObject(value);
            return true;
        }
        return DefineOwnProperty(key, new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
    }

    public virtual bool DeleteProperty(string key)
    {
        if (_shape.TryGetSlot(key, out var slot) && TryReadSlot(slot, out var existing))
        {
            if (!existing.Configurable) return false;
            ClearSlot(slot);
            return true;
        }
        return true;
    }

    public void SetPrototype(ObjectHandle? prototypeHandle)
    {
        PrototypeHandle = prototypeHandle;
        if (prototypeHandle is { } proto && OwnerHandle is { } owner && OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, proto);
        }
    }

    /// <summary>
    /// Property slots this object carries, live or emptied. The collector walks
    /// every one of them, so this is what marking actually costs.
    /// </summary>
    internal int PropertySlotCount => _slots.Length;

    public virtual void Trace(IHeapTracer tracer)
    {
        if (PrototypeHandle is { } proto)
            tracer.Trace(proto);

        for (var slot = 0; slot < _slots.Length; slot++)
        {
            var flags = _slots[slot].Flags;
            if ((flags & PropertyFlags.Present) == 0) continue;
            if ((flags & PropertyFlags.Accessor) != 0)
            {
                var accessor = _accessors![slot];
                if (accessor.Get.Tag == JsValueTag.Object) tracer.Trace(accessor.Get.AsObjectHandle());
                if (accessor.Set.Tag == JsValueTag.Object) tracer.Trace(accessor.Set.AsObjectHandle());
            }
            else if (_slots[slot].Value.Tag == JsValueTag.Object)
            {
                tracer.Trace(_slots[slot].Value.AsObjectHandle());
            }
        }

        // Symbol-keyed properties hold live references too — e.g. an object's
        // [Symbol.iterator] method. Omitting them let the GC reclaim the target
        // (a generator's @@iterator function, say) and surfaced as a "Stale heap
        // handle." on the next for-of over that object.
        for (var i = 0; i < _symbolCount; i++)
        {
            var flags = _symbolProperties![i].Flags;
            if ((flags & PropertyFlags.Present) == 0) continue;
            if ((flags & PropertyFlags.Accessor) != 0)
            {
                var accessor = _accessors![AccessorKeyForSymbol(i)];
                if (accessor.Get.Tag == JsValueTag.Object) tracer.Trace(accessor.Get.AsObjectHandle());
                if (accessor.Set.Tag == JsValueTag.Object) tracer.Trace(accessor.Set.AsObjectHandle());
            }
            else if (_symbolProperties[i].Value.Tag == JsValueTag.Object)
            {
                tracer.Trace(_symbolProperties[i].Value.AsObjectHandle());
            }
        }
    }

    private static bool JsValueSameValue(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            var a = left.AsNumber();
            var b = right.AsNumber();
            if (double.IsNaN(a) && double.IsNaN(b))
                return true;
            if (a == 0d && b == 0d)
                return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
            return a == b;
        }
        if (left.Tag != right.Tag)
            return false;
        return left.Tag switch
        {
            JsValueTag.Undefined => true,
            JsValueTag.Null => true,
            JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
            JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
            JsValueTag.BigInt => left.AsBigInt() == right.AsBigInt(),
            JsValueTag.String => string.Equals(left.AsString(), right.AsString(), System.StringComparison.Ordinal),
            JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
            JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
            _ => false
        };
    }

    private static void TraceDescriptor(IHeapTracer tracer, JsPropertyDescriptor d)
    {
        if (d.IsAccessor)
        {
            if (d.Get.Tag == JsValueTag.Object)
                tracer.Trace(d.Get.AsObjectHandle());
            if (d.Set.Tag == JsValueTag.Object)
                tracer.Trace(d.Set.AsObjectHandle());
        }
        else if (d.Value.Tag == JsValueTag.Object)
        {
            tracer.Trace(d.Value.AsObjectHandle());
        }
    }
}
