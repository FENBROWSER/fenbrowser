using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1 Environment Records.
//
// An Environment Record is a specification type used to define the association of
// Identifiers to specific variables and functions, based upon the lexical nesting
// structure of ECMAScript code. Usually an Environment Record is associated with some
// specific syntactic structure of ECMAScript code such as a FunctionDeclaration, a
// BlockStatement, or a Catch clause of a TryStatement. Each time such code is
// evaluated, a new Environment Record is created to record the identifier bindings
// that are created by that code.
//
// Concrete subclasses (DeclarativeEnvironmentRecord, ObjectEnvironmentRecord,
// FunctionEnvironmentRecord, GlobalEnvironmentRecord, ModuleEnvironmentRecord)
// override the abstract operations.
public abstract class EnvironmentRecord
{
    protected EnvironmentRecord(EnvironmentRecord? outerEnv)
    {
        OuterEnv = outerEnv;
    }

    // The outer (enclosing) Environment Record in the lexical environment chain, or
    // null for the outermost global record.
    public EnvironmentRecord? OuterEnv { get; }

    // Generational-GC bookkeeping. Environment records are not heap cells, so
    // an object stored into a binding of a record reachable only through Old
    // (promoted) cells cannot dirty any remembered-set card. The owning heap
    // (stamped by the interpreter at record construction) registers such
    // records in its remembered-environment set instead.
    internal JsHeap? OwnerHeap;
    internal bool IsRememberedForMinorGc;

    // Attach hook so composite records (e.g. GlobalEnvironmentRecord's inner
    // declarative/object records) can stamp their children together with
    // themselves.
    internal virtual void AttachOwnerHeap(JsHeap heap)
    {
        OwnerHeap = heap;
    }

    internal void RememberBindingStore(ObjectHandle? storedObject = null)
    {
        if (OwnerHeap is null)
        {
            // A store into an unstamped record would silently miss the
            // remembered-environment set and corrupt the heap later. Fail at
            // the store so the missing stamp site is identified immediately.
            throw new FenBrowser.Js.Heap.JsEngineFatalException(
                $"Unstamped environment binding store: {GetType().Name}");
        }

        OwnerHeap.RememberEnvironment(this, storedObject);
    }

    // Reads a binding in one step where the record can report a miss itself.
    // Resolving an identifier walks the whole scope chain, and asking each
    // record first whether it has the binding and then for its value probes
    // the same dictionary twice per level. Records that already return
    // NotFound for an absent binding override this to probe once; the base
    // keeps the two-step form for object-backed records, whose GetBindingValue
    // reports Ok for a missing name in sloppy mode and so cannot be used to
    // decide whether to keep walking outwards.
    public virtual BindingOpResult TryLookupBinding(string name, bool strict, out JsValue value)
    {
        if (!HasBinding(name))
        {
            value = default;
            return BindingOpResult.NotFound;
        }

        return GetBindingValue(name, strict, out value);
    }

    // Declaring a binding and giving it its value is one act at a call site:
    // every parameter and every local of every function is created and then
    // initialised, immediately, on every call. Doing it as two operations
    // hashes the same name twice. Records that can do it in one override this.
    public virtual BindingOpResult CreateAndInitializeBinding(string name, JsValue value, bool deletable)
    {
        var created = CreateMutableBinding(name, deletable);
        if (created is not (BindingOpResult.Ok or BindingOpResult.AlreadyDeclared))
        {
            return created;
        }

        return InitializeBinding(name, value);
    }

    // Hoisting a var: create it holding undefined, but leave an existing
    // binding alone - `function f(a){ var a; }` must not blank the argument.
    // Every var of every function is hoisted on every call, and asking whether
    // it exists and then creating and initialising it hashes the name three
    // times. Records that can answer in one lookup override this.
    public virtual BindingOpResult EnsureVarBinding(string name, bool deletable)
    {
        if (HasBinding(name))
        {
            return BindingOpResult.Ok;
        }

        var created = CreateMutableBinding(name, deletable);
        return created != BindingOpResult.Ok ? created : InitializeBinding(name, JsValue.Undefined);
    }

    // 9.1.1.1.1 HasBinding ( N ) - true if the record has a binding for N.
    public abstract bool HasBinding(string name);

    // 9.1.1.1.2 CreateMutableBinding ( N, D ) - create a new mutable binding; D
    // indicates whether the binding may later be deleted by a delete operator.
    public abstract BindingOpResult CreateMutableBinding(string name, bool deletable);

    // 9.1.1.1.3 CreateImmutableBinding ( N, S ) - create a new immutable binding; S
    // indicates the binding is a strict binding (assignment after init is a TypeError).
    public abstract BindingOpResult CreateImmutableBinding(string name, bool strict);

    // 9.1.1.1.4 InitializeBinding ( N, V ) - set the bound value for an existing
    // uninitialized binding (lifts the TDZ).
    public abstract BindingOpResult InitializeBinding(string name, JsValue value);

    // 9.1.1.1.5 SetMutableBinding ( N, V, S ) - update an existing binding; S indicates
    // whether the caller is strict (affects the error returned when the binding is
    // immutable or missing).
    public abstract BindingOpResult SetMutableBinding(string name, JsValue value, bool strict);

    // 9.1.1.1.6 GetBindingValue ( N, S ) - retrieve the value bound to N. When the
    // binding is still in its TDZ the result is BindingOpResult.TdzAccess.
    public abstract BindingOpResult GetBindingValue(string name, bool strict, out JsValue value);

    // 9.1.1.1.7 DeleteBinding ( N ) - remove a binding if it is deletable.
    public abstract BindingOpResult DeleteBinding(string name);

    // 9.1.1.1.8 HasThisBinding ( ) - whether the record provides a `this` binding.
    public virtual bool HasThisBinding => false;

    // 9.1.1.1.9 HasSuperBinding ( ) - whether the record provides a `super` binding.
    public virtual bool HasSuperBinding => false;

    // 9.1.1.1.10 WithBaseObject ( ) - the object an ObjectEnvironmentRecord wraps when
    // it was produced by a `with` statement; null otherwise.
    public virtual ObjectHandle? WithBaseObject => null;

    // GetThisBinding ( ) - shared shape across function/global/module records. The
    // declarative + object env records do not expose a `this` binding and default to
    // NotInitializable so callers know to walk the outer chain.
    public virtual BindingOpResult GetThisBinding(out JsValue value)
    {
        value = JsValue.Undefined;
        return BindingOpResult.NotInitializable;
    }

    // Environment records are not heap cells, but closure function objects retain
    // them through [[Environment]]. Trace the chain so captured object values remain
    // live after captured-cell snapshots are removed.
    //
    // The outer-environment chain is a plain linked list of non-heap-cell records
    // carrying no mark bits, so a recursive walk cannot terminate through the heap's
    // Marked checks and overflows the native stack on deep closure graphs built by
    // real-world bundles. Walk the chain iteratively here and dispatch each record's
    // own edges through <see cref="TraceOwnEdges"/>; subclasses must not re-walk
    // <see cref="OuterEnv"/> themselves.
    public void Trace(IHeapTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        for (var current = this; current is not null; current = current.OuterEnv)
        {
            if (!tracer.BeginEnvironment(current))
            {
                // Already traced this collection, and so was the rest of the
                // chain beyond it.
                return;
            }

            current.TraceOwnEdges(tracer);
        }
    }

    /// <summary>
    /// Traces the edges owned by THIS record only (bindings, backing objects,
    /// import targets). The outer-environment walk is handled once by
    /// <see cref="Trace"/>; overrides must not recurse into it.
    /// </summary>
    protected internal virtual void TraceOwnEdges(IHeapTracer tracer)
    {
    }

    protected static void TraceValue(IHeapTracer tracer, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            tracer.Trace(value.AsObjectHandle());
        }
    }
}
