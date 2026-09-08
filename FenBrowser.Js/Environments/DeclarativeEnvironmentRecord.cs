using System.Runtime.CompilerServices;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.1 Declarative Environment Records.
//
// Each declarative Environment Record is associated with an ECMAScript program scope
// containing variable, constant, let, class, module, import, and/or function
// declarations. A declarative Environment Record binds the set of identifiers defined
// by the declarations contained within its scope.
public class DeclarativeEnvironmentRecord : EnvironmentRecord
{
    private Dictionary<string, Binding>? _bindings;

    // Slot storage. The compiler already numbers every variable a function
    // declares, and the bytecode already carries those numbers - LoadVar r6, 0
    // means "slot 0". The interpreter then threw the number away and looked the
    // variable up by name, hashing a string into this dictionary on every
    // declaration and every read. Where a name has a slot, the binding lives in
    // the array instead and the dictionary never sees it, so the two never
    // disagree. Names without a slot - eval-introduced, catch parameters,
    // anything dynamic - keep the dictionary exactly as before.
    private object? _slotOwner;
    private IReadOnlyDictionary<string, int>? _slotMap;
    private Binding[]? _slotBindings;
    private bool[]? _slotPresent;

    internal void AttachSlotStorage(object owner, IReadOnlyDictionary<string, int> slotMap, int slotCount)
        => AttachSlotStorage(owner, slotMap, slotCount, null, null);

    /// <summary>
    /// Attaches slot storage, optionally over arrays the caller already holds.
    /// A Binding is 40 bytes, so a function that declares 200 variables costs
    /// 8KB of slot storage per call whether or not it ever assigns them - and
    /// that allocation, not the register file beside it, is what a call to a
    /// machine-generated function actually spends. Supplied arrays must be
    /// cleared and at least <paramref name="slotCount"/> long.
    /// </summary>
    internal void AttachSlotStorage(
        object owner,
        IReadOnlyDictionary<string, int> slotMap,
        int slotCount,
        Binding[]? bindings,
        bool[]? present)
    {
        if (slotCount <= 0)
        {
            return;
        }

        _slotOwner = owner;
        _slotMap = slotMap;
        _slotBindings = bindings is not null && bindings.Length >= slotCount
            ? bindings
            : new Binding[slotCount];
        _slotPresent = present is not null && present.Length >= slotCount
            ? present
            : new bool[slotCount];
    }

    /// <summary>
    /// Hands the slot arrays back and leaves this record holding none, so a
    /// caller that knows the record is dead can reuse them. Returns false when
    /// the record does not own storage for <paramref name="owner"/>, which
    /// keeps a caller from taking arrays out from under a different frame.
    /// </summary>
    internal bool TryDetachSlotStorage(object owner, out Binding[]? bindings, out bool[]? present)
    {
        if (!ReferenceEquals(_slotOwner, owner) || _slotBindings is null || _slotPresent is null)
        {
            bindings = null;
            present = null;
            return false;
        }

        bindings = _slotBindings;
        present = _slotPresent;
        // Clearing the owner first means any later lookup misses the slot path
        // and falls back to the dictionary rather than reading arrays that now
        // belong to another call.
        _slotOwner = null;
        _slotMap = null;
        _slotBindings = null;
        _slotPresent = null;
        return true;
    }

    /// <summary>
    /// The slot array itself, when this record holds it for
    /// <paramref name="owner"/>. Compiled code hoists this once per loop rather
    /// than re-deriving it — a cast, an identity check and a call — on every
    /// variable it reads.
    /// </summary>
    internal Binding[]? SlotBindingsFor(object owner) =>
        ReferenceEquals(_slotOwner, owner) ? _slotBindings : null;

    /// <summary>Companion to <see cref="SlotBindingsFor"/>.</summary>
    internal bool[]? SlotPresenceFor(object owner) =>
        ReferenceEquals(_slotOwner, owner) ? _slotPresent : null;

    /// <summary>
    /// ECMA-262 14.7.4.9 CreatePerIterationEnvironment - a fresh record over
    /// <paramref name="outer"/> holding a copy of this record's bindings.
    ///
    /// A `for (let i ...)` head gets one of these per turn of the loop, so a
    /// function made on one turn keeps the value that turn had instead of
    /// seeing whatever the counter finished on.
    /// </summary>
    internal DeclarativeEnvironmentRecord CloneForNextIteration(EnvironmentRecord outer, out bool holdsObject)
    {
        var copy = new DeclarativeEnvironmentRecord(outer);
        holdsObject = false;

        if (_bindings is { Count: > 0 })
        {
            copy._bindings = new Dictionary<string, Binding>(_bindings, StringComparer.Ordinal);
            foreach (var binding in _bindings.Values)
            {
                if (binding.Value.Tag == JsValueTag.Object)
                {
                    holdsObject = true;
                    break;
                }
            }
        }

        if (_slotBindings is not null && _slotPresent is not null)
        {
            copy._slotOwner = _slotOwner;
            copy._slotMap = _slotMap;
            copy._slotBindings = (Binding[])_slotBindings.Clone();
            copy._slotPresent = (bool[])_slotPresent.Clone();
            for (var i = 0; i < _slotBindings.Length && !holdsObject; i++)
            {
                if (_slotPresent[i] && _slotBindings[i].Value.Tag == JsValueTag.Object)
                {
                    holdsObject = true;
                }
            }
        }

        return copy;
    }

    // Slots are numbered per function, so a slot only means anything to the
    // record built for that function's own call.
    internal bool OwnsSlotsOf(object owner) =>
        _slotOwner is not null && ReferenceEquals(_slotOwner, owner);

    private bool TryFindSlot(string name, out int slot)
    {
        if (_slotMap is not null && _slotMap.TryGetValue(name, out slot) &&
            (uint)slot < (uint)(_slotBindings?.Length ?? 0))
        {
            return true;
        }

        slot = -1;
        return false;
    }

    /// <summary>
    /// Reads a slot this record holds for <paramref name="owner"/>, in one call
    /// the caller can have inlined. The chain it replaces — an ownership test,
    /// then a bounds-and-presence test, then a status the caller has to
    /// re-examine — is four calls deep, and a variable read is far too common
    /// to pay that. Answers false for anything unusual so the caller can take
    /// the ordinary path: a slot that is absent, uninitialized, or belongs to
    /// some other function's numbering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadOwnSlot(object owner, int slot, out JsValue value)
    {
        var bindings = _slotBindings;
        if (bindings is not null &&
            ReferenceEquals(_slotOwner, owner) &&
            (uint)slot < (uint)bindings.Length)
        {
            // An absent slot is a default Binding, so it reads as
            // uninitialized: presence needs no array of its own here.
            ref var binding = ref bindings[slot];
            if (binding.IsInitialized)
            {
                value = binding.Value;
                return true;
            }
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// The write half of <see cref="TryReadOwnSlot"/>. Refuses an immutable or
    /// uninitialized binding so assignment errors keep going through the path
    /// that knows how to report them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWriteOwnSlot(object owner, int slot, JsValue value)
    {
        var bindings = _slotBindings;
        if (bindings is not null &&
            ReferenceEquals(_slotOwner, owner) &&
            (uint)slot < (uint)bindings.Length)
        {
            ref var binding = ref bindings[slot];
            if (binding.IsInitialized && binding.IsMutable)
            {
                binding = binding with { Value = value };
                if (value.Tag == JsValueTag.Object)
                {
                    // Same write barrier the name-keyed store takes. Skipping it
                    // hides the reference from the collector's remembered set,
                    // and the object goes away while the variable still names it.
                    RememberBindingStore(value.AsObjectHandle());
                }

                return true;
            }
        }

        return false;
    }

    // The fast paths: the caller already holds the slot, so no name is involved.
    internal bool TryGetAtSlot(int slot, out JsValue value, out BindingOpResult status)
    {
        if ((uint)slot >= (uint)(_slotBindings?.Length ?? 0) || !_slotPresent![slot])
        {
            value = JsValue.Undefined;
            status = BindingOpResult.NotFound;
            return false;
        }

        ref var binding = ref _slotBindings![slot];
        if (!binding.IsInitialized)
        {
            value = JsValue.Undefined;
            status = BindingOpResult.TdzAccess;
            return true;
        }

        value = binding.Value;
        status = BindingOpResult.Ok;
        return true;
    }

    internal bool TrySetAtSlot(int slot, JsValue value, bool strict, out BindingOpResult status)
    {
        if ((uint)slot >= (uint)(_slotBindings?.Length ?? 0) || !_slotPresent![slot])
        {
            status = BindingOpResult.NotFound;
            return false;
        }

        ref var binding = ref _slotBindings![slot];
        if (!binding.IsInitialized)
        {
            status = BindingOpResult.TdzAccess;
            return true;
        }

        if (!binding.IsMutable)
        {
            status = strict || binding.IsStrict ? BindingOpResult.ConstAssignment : BindingOpResult.Ok;
            return true;
        }

        binding = binding with { Value = value };
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore(value.AsObjectHandle());
        }

        status = BindingOpResult.Ok;
        return true;
    }

    // Declaring a parameter or a hoisted var at a known slot: an array write.
    /// <summary>
    /// Hoists a function's declared <c>var</c>s into their slots in one pass.
    /// ECMA-262 10.2.11 FunctionDeclarationInstantiation creates every declared
    /// var and sets it to undefined before the body runs, so this is on the
    /// entry path of every single call — and doing it one name at a time cost a
    /// call, two null tests and a bounds test each, which on a minified bundle
    /// (where functions declare dozens of vars and a call may touch none of
    /// them) was the largest single thing a call paid for.
    ///
    /// A slot a parameter already filled is left alone, which is what
    /// <c>function f(a) { var a; }</c> requires. The hoisted value is undefined
    /// and so never an object reference, so no store barrier is owed.
    ///
    /// Answers false when this record does not hold slot storage for
    /// <paramref name="owner"/>, leaving the caller its by-name path.
    /// </summary>
    internal bool TryDeclareHoistedVarsAtSlots(object owner, int[] slots)
    {
        if (!ReferenceEquals(_slotOwner, owner))
        {
            return false;
        }

        var bindings = _slotBindings;
        var present = _slotPresent;
        if (bindings is null || present is null)
        {
            return false;
        }

        var hoisted = new Binding(
            Value: JsValue.Undefined,
            IsMutable: true,
            IsInitialized: true,
            IsStrict: false,
            IsDeletable: false);

        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if ((uint)slot >= (uint)bindings.Length || present[slot])
            {
                continue;
            }

            bindings[slot] = hoisted;
            present[slot] = true;
        }

        return true;
    }

    internal void DeclareAtSlot(int slot, JsValue value, bool deletable, bool overwrite)
    {
        if ((uint)slot >= (uint)(_slotBindings?.Length ?? 0))
        {
            return;
        }

        if (_slotPresent![slot] && !overwrite)
        {
            return;
        }

        _slotBindings![slot] = new Binding(
            Value: value, IsMutable: true, IsInitialized: true, IsStrict: false, IsDeletable: deletable);
        _slotPresent[slot] = true;
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore(value.AsObjectHandle());
        }
    }


    public DeclarativeEnvironmentRecord(EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
    }

    internal void ResetDeclarativeState(EnvironmentRecord? outerEnv)
    {
        _bindings?.Clear();
        _slotOwner = null;
        _slotMap = null;
        _slotBindings = null;
        _slotPresent = null;
        ResetLifetime(outerEnv);
    }

    // GetBindingValue already answers NotFound for an absent binding here, so
    // one dictionary probe settles both questions. ModuleEnvironmentRecord and
    // FunctionEnvironmentRecord inherit this; their GetBindingValue overrides
    // fall through to this record's for a name they do not know.
    public override BindingOpResult TryLookupBinding(string name, bool strict, out JsValue value)
        => GetBindingValue(name, strict, out value);

    // One dictionary lookup instead of two. Call cost here scales with the
    // number of locals a function has - about 0.19us per local per call before
    // this - because each one was hashed once to create the binding and again
    // to initialise it.
    public override BindingOpResult CreateAndInitializeBinding(string name, JsValue value, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var declSlot))
        {
            if (_slotPresent![declSlot] && !_slotBindings![declSlot].IsMutable)
            {
                return BindingOpResult.AlreadyDeclared;
            }

            DeclareAtSlot(declSlot, value, deletable, overwrite: true);
            return BindingOpResult.Ok;
        }

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(bindings, name, out var existed);

        if (existed)
        {
            // Re-declaring keeps the binding's own flags; only the value is set.
            if (!slot.IsMutable)
            {
                return BindingOpResult.AlreadyDeclared;
            }

            slot = slot with { Value = value, IsInitialized = true };
        }
        else
        {
            slot = new Binding(value, IsMutable: true, IsInitialized: true, IsStrict: false, IsDeletable: deletable);
        }

        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore(value.AsObjectHandle());
        }

        return BindingOpResult.Ok;
    }

    // One lookup: present stays untouched, absent is created holding undefined.
    public override BindingOpResult EnsureVarBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var varDeclSlot))
        {
            DeclareAtSlot(varDeclSlot, JsValue.Undefined, deletable, overwrite: false);
            return BindingOpResult.Ok;
        }

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(bindings, name, out var existed);

        if (!existed)
        {
            slot = new Binding(
                JsValue.Undefined, IsMutable: true, IsInitialized: true, IsStrict: false, IsDeletable: deletable);
        }

        return BindingOpResult.Ok;
    }

    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (TryFindSlot(name, out var hasSlot)) return _slotPresent![hasSlot];
        return _bindings?.ContainsKey(name) == true;
    }

    // Returns true if the binding exists and is a lexical (non-deletable) binding.
    public bool HasLexicalBinding(string name)
    {
        if (TryFindSlot(name, out var lexSlot))
            return _slotPresent![lexSlot] && !_slotBindings![lexSlot].IsDeletable;
        return _bindings?.TryGetValue(name, out var b) == true && !b.IsDeletable;
    }

    public bool HasVarBinding(string name)
    {
        if (TryFindSlot(name, out var varSlot))
            return _slotPresent![varSlot] && _slotBindings![varSlot].IsDeletable;
        return _bindings?.TryGetValue(name, out var b) == true && b.IsDeletable;
    }

    public override BindingOpResult CreateMutableBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var newSlot))
        {
            if (_slotPresent![newSlot]) return BindingOpResult.AlreadyDeclared;
            _slotBindings![newSlot] = new Binding(
                Value: JsValue.Undefined,
                IsMutable: true,
                IsInitialized: false,
                IsStrict: false,
                IsDeletable: deletable);
            _slotPresent[newSlot] = true;
            return BindingOpResult.Ok;
        }

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        bindings[name] = new Binding(
            Value: JsValue.Undefined,
            IsMutable: true,
            IsInitialized: false,
            IsStrict: false,
            IsDeletable: deletable);
        return BindingOpResult.Ok;
    }

    public override BindingOpResult CreateImmutableBinding(string name, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var immutableSlot))
        {
            if (_slotPresent![immutableSlot]) return BindingOpResult.AlreadyDeclared;
            _slotBindings![immutableSlot] = new Binding(
                Value: JsValue.Undefined,
                IsMutable: false,
                IsInitialized: false,
                IsStrict: strict,
                IsDeletable: false);
            _slotPresent[immutableSlot] = true;
            return BindingOpResult.Ok;
        }

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        bindings[name] = new Binding(
            Value: JsValue.Undefined,
            IsMutable: false,
            IsInitialized: false,
            IsStrict: strict,
            IsDeletable: false);
        return BindingOpResult.Ok;
    }

    public override BindingOpResult InitializeBinding(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var initSlot))
        {
            if (!_slotPresent![initSlot]) return BindingOpResult.NotFound;
            ref var slotBinding = ref _slotBindings![initSlot];
            if (slotBinding.IsInitialized) return BindingOpResult.NotInitializable;
            slotBinding = slotBinding with { Value = value, IsInitialized = true };
            if (value.Tag == JsValueTag.Object) RememberBindingStore(value.AsObjectHandle());
            return BindingOpResult.Ok;
        }

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
        {
            return BindingOpResult.NotFound;
        }

        if (binding.IsInitialized)
        {
            // ECMA-262 step "Assert: envRec does not already have an initialized
            // binding for N." - re-initialization is a host bug, surface it instead of
            // silently overwriting.
            return BindingOpResult.NotInitializable;
        }

        _bindings[name] = binding with { Value = value, IsInitialized = true };
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore(value.AsObjectHandle());
        }
        return BindingOpResult.Ok;
    }

    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var setSlot))
        {
            if (TrySetAtSlot(setSlot, value, strict, out var slotStatus)) return slotStatus;
            _ = strict;
            return BindingOpResult.NotFound;
        }

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
        {
            // 9.1.1.1.5 step 1: "If envRec does not have a binding for N..." Both
            // strict and non-strict callers receive NotFound; the caller (interpreter)
            // turns strict NotFound into a ReferenceError, and non-strict NotFound into
            // an auto-created global binding.
            _ = strict;
            return BindingOpResult.NotFound;
        }

        if (!binding.IsInitialized)
        {
            return BindingOpResult.TdzAccess;
        }

        if (!binding.IsMutable)
        {
            if (strict || binding.IsStrict) return BindingOpResult.ConstAssignment;
            return BindingOpResult.Ok;
        }

        _bindings[name] = binding with { Value = value };
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore(value.AsObjectHandle());
        }
        return BindingOpResult.Ok;
    }

    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = strict;

        if (TryFindSlot(name, out var getSlot))
        {
            if (TryGetAtSlot(getSlot, out value, out var slotStatus)) return slotStatus;
            value = JsValue.Undefined;
            return BindingOpResult.NotFound;
        }

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
        {
            value = JsValue.Undefined;
            return BindingOpResult.NotFound;
        }

        if (!binding.IsInitialized)
        {
            // TDZ - ECMA-262 9.1.1.1.6 throws ReferenceError when the binding exists
            // but has not yet been initialized.
            value = JsValue.Undefined;
            return BindingOpResult.TdzAccess;
        }

        value = binding.Value;
        return BindingOpResult.Ok;
    }

    public override BindingOpResult DeleteBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryFindSlot(name, out var delSlot))
        {
            if (!_slotPresent![delSlot]) return BindingOpResult.NotFound;
            if (!_slotBindings![delSlot].IsDeletable) return BindingOpResult.ConstAssignment;
            _slotPresent[delSlot] = false;
            _slotBindings[delSlot] = default;
            return BindingOpResult.Ok;
        }

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
        {
            return BindingOpResult.NotFound;
        }

        if (!binding.IsDeletable)
        {
            return BindingOpResult.ConstAssignment;
        }

        _bindings.Remove(name);
        return BindingOpResult.Ok;
    }

    public int BindingCountForTest
    {
        get
        {
            var count = _bindings?.Count ?? 0;
            if (_slotPresent is not null)
            {
                foreach (var present in _slotPresent)
                {
                    if (present) count++;
                }
            }

            return count;
        }
    }

    public bool IsInitializedForTest(string name)
        => TryFindSlot(name, out var slot)
            ? _slotPresent![slot] && _slotBindings![slot].IsInitialized
            : _bindings?.TryGetValue(name, out var b) == true && b.IsInitialized;

    public bool IsMutableForTest(string name)
        => TryFindSlot(name, out var slot)
            ? _slotPresent![slot] && _slotBindings![slot].IsMutable
            : _bindings?.TryGetValue(name, out var b) == true && b.IsMutable;

    protected internal override void TraceOwnEdges(IHeapTracer tracer)
    {
        TraceDeclarativeBindings(tracer);
    }

    /// <summary>
    /// Traces object edges held by THIS record's bindings only. Shared with
    /// derived module records, which add their own import-target edges.
    /// </summary>
    protected void TraceDeclarativeBindings(IHeapTracer tracer)
    {
        // Slot-held bindings are as reachable as dictionary-held ones; missing
        // them here would let the collector sweep a live local.
        if (_slotBindings is not null)
        {
            for (var i = 0; i < _slotBindings.Length; i++)
            {
                if (_slotPresent![i] && _slotBindings[i].IsInitialized)
                {
                    TraceValue(tracer, _slotBindings[i].Value);
                }
            }
        }

        if (_bindings is null)
        {
            return;
        }

        foreach (var binding in _bindings.Values)
        {
            if (binding.IsInitialized)
            {
                TraceValue(tracer, binding.Value);
            }
        }
    }

    internal readonly record struct Binding(
        JsValue Value,
        bool IsMutable,
        bool IsInitialized,
        bool IsStrict,
        bool IsDeletable);
}
