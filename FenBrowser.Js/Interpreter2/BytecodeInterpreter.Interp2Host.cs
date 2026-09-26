using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

/// <summary>
/// The surface the register-window loop is allowed to reach through.
/// </summary>
/// <remarks>
/// <para>
/// The new loop decides where a value lives and how a frame is entered. It does
/// not decide what any operator, coercion or property lookup means: every one of
/// those goes back through the method the old loop already calls, so the two
/// cannot disagree about semantics no matter how far the new one is taken. This
/// file is that boundary, and it is the only place the two loops touch.
/// </para>
/// <para>
/// It is a partial of <see cref="BytecodeInterpreter"/> rather than an interface
/// because most of what it forwards to is private, and widening thirty members
/// to expose them would make the old loop's internals part of the engine's API.
/// Keeping the file beside the new loop instead means all of the new loop's
/// surface is in one folder and can be deleted in one commit if the direction
/// does not pay.
/// </para>
/// </remarks>
public sealed partial class BytecodeInterpreter
{
    private Interp2? _interp2;

    private Interp2 Interp2Loop => _interp2 ??= new Interp2(this);

    /// <summary>
    /// The register-window loop's live state, or null if it has never run. The
    /// collector needs this even when the loop is not currently executing,
    /// because a frame it left behind is unwound lazily.
    /// </summary>
    internal Interp2? Interp2State => _interp2;

    /// <summary>
    /// Entry from <c>CallFunctionCore</c>: run an eligible body on the new loop.
    /// </summary>
    /// <param name="newTarget">[[Construct]]'s newTarget; undefined for a call.</param>
    internal JsValue Interp2Execute(
        JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue, JsValue newTarget = default)
    {
        // Calls inside the loop stay on its own frame stack; only a re-entry
        // like this one, from a native or the old loop, spends CLR stack.
        EnsureNativeStack();
        return Interp2Loop.Execute(callee, layout, in args, thisValue, newTarget);
    }

    // ------------------------------------------------------------- guard rails

    /// <summary>
    /// Whether anything is watching this execution: an instruction budget, a
    /// wall-clock deadline or an embedder interrupt. When nothing is, the new
    /// loop skips its guard accounting entirely.
    /// </summary>
    internal bool Interp2HasGuards =>
        InstructionBudget > 0 || _wallClockDeadlineTicks != 0 || InterruptCallback is not null;

    /// <summary>
    /// Charge a block of dispatched instructions against the budget and sample
    /// the deadline and the interrupt.
    /// </summary>
    /// <remarks>
    /// The old loop tests the budget on every instruction and samples the clock
    /// every 8192. The new loop charges in blocks, so a script overruns its
    /// budget by at most one block before it is stopped. That is the same
    /// guarantee in substance - a runaway script halts - with an error raised a
    /// few thousand instructions later than the old loop would raise it. Both
    /// failures are uncatchable by script, so neither can be swallowed by a
    /// handler and turned into a hang.
    /// </remarks>
    internal void Interp2Guard(long dispatchedInstructions)
    {
        if (InstructionBudget > 0)
        {
            // The counter is an int and the block is bounded by the guard
            // interval, so this cannot overflow before the budget test below.
            _instructionCount += (int)dispatchedInstructions;
            if (_instructionCount > InstructionBudget)
            {
                throw new JsThrownException(CreateRangeError("Maximum instruction budget exceeded."))
                {
                    IsUncatchableByScript = true,
                };
            }
        }

        if (InterruptCallback is { } interrupt && !interrupt())
        {
            throw new JsThrownException(CreateRangeError("Execution interrupted."))
            {
                IsUncatchableByScript = true,
            };
        }

        if (_wallClockDeadlineTicks != 0 && System.Environment.TickCount64 >= _wallClockDeadlineTicks)
        {
            throw new JsThrownException(CreateRangeError("Script wall-clock timeout exceeded."))
            {
                IsUncatchableByScript = true,
            };
        }
    }

    internal JsThrownException Interp2CallStackOverflow()
        => new(CreateRangeError("Maximum call stack size exceeded."));

    // -------------------------------------------------------------- call entry

    /// <summary>
    /// Everything the new loop cannot enter itself: natives, bound functions,
    /// proxies, generators, async bodies, and any function its layout declined.
    /// </summary>
    internal JsValue Interp2Call(JsValue callee, in CallArgs args, JsValue thisValue)
        => CallFunction(callee, args, thisValue);

    /// <summary>
    /// A native called directly from a register window, skipping both the
    /// re-resolution of a callee this loop has already resolved and the pinning
    /// of arguments it is already holding somewhere the collector can see.
    /// </summary>
    internal JsValue Interp2CallNative(NativeFunctionObject native, in CallArgs args, JsValue thisValue)
        => FenBrowser.Js.Diagnostics.NativeCallStats.Enabled
            ? CallNativeFunctionBodyMeasured(native, JsValue.Undefined, args, thisValue)
            : CallNativeWithRootedArguments(native, args, thisValue);

    /// <summary>ECMA-262 10.2.1.3 OrdinaryCallBindThis, steps 6-7 (sloppy mode).</summary>
    internal JsValue Interp2CoerceReceiver(JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            return JsValue.FromObject(EnsureGlobalObject());
        }

        return thisValue.Tag == JsValueTag.Object ? thisValue : CreateObjectFromValue(thisValue);
    }

    internal EnvironmentRecord? Interp2OuterEnvironment(JsFunctionObject callee)
        => ResolveFunctionOuterEnvironment(callee);

    // ---------------------------------------------------- captured variables

    /// <summary>
    /// The heap record that holds the variables of one activation that some
    /// closure made inside it can read.
    /// </summary>
    /// <remarks>
    /// It is a record for this function's slot numbering, so a variable that
    /// lives here is reached by the same slot index the bytecode already
    /// carries, and a closure resolving the name by walking the chain finds it
    /// where it would have found it before. Only the captured slots are
    /// declared in it; every other name falls straight through to the
    /// environment this body itself closed over.
    ///
    /// It is a FunctionEnvironmentRecord rather than a plain declarative one
    /// because it is this call's *variable* environment, not a block inside it,
    /// and code that walks the chain asks which it is: a direct eval declaring
    /// `var x` walks outwards for the nearest variable environment and treats
    /// every declarative record before it as a block, so a plain record here
    /// made `var x` collide with the enclosing function's own x.
    ///
    /// The storage is not taken from the slot pool. A pooled file is handed back
    /// when the frame that rented it tears down, and this record is created
    /// precisely because it outlives the frame.
    /// </remarks>
    internal DeclarativeEnvironmentRecord Interp2CreateContext(
        BytecodeFunction function,
        JsFunctionObject callee,
        JsValue thisValue,
        EnvironmentRecord? outerEnvironment,
        JsValue newTarget)
    {
        // ECMA-262 9.1.1.3: an arrow has no `this` of its own, so its record
        // must be one that `this` resolves straight through. Giving it a
        // function environment would stop the walk at the arrow and hand back
        // the wrong receiver.
        if (function.IsArrow)
        {
            var arrowContext = StampEnvironment(new DeclarativeEnvironmentRecord(outerEnvironment));
            // The arrow's variable environment, as on the old loop.
            arrowContext.IsVariableScope = true;
            arrowContext.AttachSlotStorage(function, function.VariableSlots, function.SlotNames.Length);
            return arrowContext;
        }

        var context = StampEnvironment(new FunctionEnvironmentRecord(
            ThisBindingStatus.Uninitialized,
            callee.SelfHandle is { } selfHandle ? JsValue.FromObject(selfHandle) : JsValue.Undefined,
            newTarget,
            callee.HomeObject,
            outerEnvironment));
        context.AttachSlotStorage(function, function.VariableSlots, function.SlotNames.Length);

        // A derived constructor's `this` stays uninitialized until super().
        if (!function.IsDerivedConstructor)
        {
            _ = context.BindThisValue(thisValue);
        }

        return context;
    }

    /// <summary>
    /// ECMA-262 9.1.2.5 GetThisEnvironment: the receiver an arrow sees, which is
    /// the nearest enclosing record that provides one.
    /// </summary>
    internal JsValue Interp2ResolveThis(EnvironmentRecord? environment, JsValue frameReceiver)
    {
        var status = ResolveThisBinding(environment, out var value);

        // ECMA-262 9.2.2 step 9: a derived constructor's `this` stays
        // uninitialized until super() runs, and an arrow inside it that reads
        // `this` first sees the same ReferenceError the constructor would.
        if (status == BindingOpResult.TdzAccess)
        {
            throw new JsThrownException(CreateReferenceError(
                "Must call super constructor in derived class before accessing 'this'."));
        }

        // No record in the chain provides one - fall back to the receiver the
        // call supplied, which is what the dispatch loop does.
        return status == BindingOpResult.Ok ? value : frameReceiver;
    }

    /// <summary>The {value, done} object a yield hands back.</summary>
    internal JsValue Interp2CreateIteratorResult(JsValue value, bool done)
        => CreateIteratorResult(value, done);

    /// <summary>
    /// ECMA-262 27.7.5.2 Await, up to the point where the frame has to be put
    /// away: true when the await suspends, and <paramref name="awaitedPromise"/>
    /// is what the resumption attaches to.
    /// </summary>
    /// <param name="hasContext">
    /// Whether the frame has an async activation to suspend into. An async
    /// generator's does not - see <see cref="AwaitWithNothingToSuspendInto"/>.
    /// </param>
    internal bool Interp2AwaitPrepare(
        JsValue value, bool hasContext, out JsValue awaitedPromise, out JsValue inlineResult)
    {
        awaitedPromise = PromiseResolveStatic(value);
        inlineResult = JsValue.Undefined;
        if (awaitedPromise.Tag != JsValueTag.Object ||
            _heap.GetObject(awaitedPromise.AsObjectHandle()) is not Promises.PromiseInstance instance)
        {
            // Not something with reactions to hang the resumption on, so the
            // value is its own result - as it is on the old loop.
            inlineResult = value;
            return false;
        }

        if (!hasContext)
        {
            inlineResult = AwaitWithNothingToSuspendInto(instance);
            return false;
        }

        return true;
    }

    /// <summary>The rest of it: the resumption this suspended frame waits on.</summary>
    internal void Interp2AwaitAttach(Objects.AsyncContext context, JsValue awaitedPromise)
    {
        var instance = (Promises.PromiseInstance)_heap.GetObject(awaitedPromise.AsObjectHandle());
        var onFulfilled = GetOrCreateAsyncResumeCallback(isReject: false, context);
        var onRejected = GetOrCreateAsyncResumeCallback(isReject: true, context);
        var onFulfilledHandle = _heap.AllocateObject(onFulfilled, AllocationSite.Current());
        var onRejectedHandle = _heap.AllocateObject(onRejected, AllocationSite.Current());
        PerformPromiseThen(
            awaitedPromise.AsObjectHandle(),
            instance.Promise,
            JsValue.FromObject(onFulfilledHandle),
            JsValue.FromObject(onRejectedHandle),
            GetDummyCapability());
        instance.Promise.IsHandled = true;
    }

    /// <summary>Run an async function body on the register-window loop.</summary>
    internal JsValue Interp2RunAsync(
        Objects.AsyncContext context, JsFunctionObject callee, JsValue[] args, JsValue thisValue)
    {
        EnsureNativeStack();
        return Interp2Loop.RunAsync(context, callee, args, thisValue);
    }

    /// <summary>Resume one when the promise it awaited settles.</summary>
    internal JsValue Interp2ResumeAsync(Objects.AsyncContext context)
    {
        EnsureNativeStack();
        return Interp2Loop.ResumeAsync(context);
    }

    /// <summary>Start or resume a generator body on the register-window loop.</summary>
    internal JsValue Interp2RunGenerator(Objects.GeneratorObject generator)
    {
        // A chain of generators driving one another re-enters here without
        // any call in between, so this entry needs its own native check.
        EnsureNativeStack();
        return Interp2Loop.RunGenerator(generator);
    }

    internal static void Interp2DeclareContextSlot(
        DeclarativeEnvironmentRecord context, int slot, JsValue value)
        => context.DeclareAtSlot(slot, value, deletable: false, overwrite: true);

    /// <summary>A captured let or const, before its declaration has run.</summary>
    internal static void Interp2DeclareUninitializedContextSlot(
        DeclarativeEnvironmentRecord context, int slot, bool isConst)
        => context.DeclareUninitializedAtSlot(slot, immutable: isConst);

    /// <summary>That binding's declaration running.</summary>
    internal static void Interp2InitializeContextSlot(
        DeclarativeEnvironmentRecord context, int slot, JsValue value, bool isConst)
        => context.InitializeAtSlot(slot, value, immutable: isConst);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowThisBeforeSuper()
        => throw new JsThrownException(CreateReferenceError(
            "Must call super constructor in derived class before accessing 'this' or returning from derived constructor."));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowSuperCalledTwice()
        => throw new JsThrownException(CreateReferenceError("super() called twice in derived class constructor."));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowDerivedReturn()
        => throw new JsThrownException(CreateTypeError("Derived constructors may only return object or undefined."));

    /// <summary>What [[Construct]] accepts as a constructor's own result.</summary>
    internal bool Interp2IsConstructorResult(JsValue value) => IsConstructorReturnObject(value);

    /// <summary>ECMA-262 10.2.9 SetFunctionName from a runtime key.</summary>
    internal void Interp2SetFunctionName(JsValue function, JsValue key) => ApplyFunctionName(function, key, prefix: null);

    /// <summary>ECMA-262 9.4.5 GetNewTarget for an arrow: the enclosing function's.</summary>
    internal JsValue Interp2ResolveNewTarget(EnvironmentRecord? environment)
        => ResolveLexicalNewTarget(environment);

    /// <summary>
    /// The [[HomeObject]] of the environment GetThisEnvironment finds, which is
    /// where `super` in an arrow resolves (ECMA-262 9.1.2 GetSuperBase).
    /// </summary>
    private static Runtime.ObjectHandle? ResolveLexicalHomeObject(EnvironmentRecord? environment)
    {
        for (var env = environment; env is not null; env = env.OuterEnv)
        {
            if (env.HasThisBinding)
            {
                return (env as FunctionEnvironmentRecord)?.HomeObject;
            }
        }

        return null;
    }

    /// <summary>
    /// A function's arguments object bound by name in its record, for an arrow
    /// inside that reads it (the body's own bytecode gave it no slot).
    /// </summary>
    internal void Interp2BindArgumentsByName(DeclarativeEnvironmentRecord context, JsValue argumentsObject)
        => _ = context.CreateAndInitializeBinding("arguments", argumentsObject, deletable: false);

    /// <summary>
    /// A named function expression's own name bound by name in its record
    /// (ECMA-262 15.2.5: CreateImmutableBinding(name, false), then initialized).
    /// </summary>
    internal static void Interp2BindFunctionNameByName(DeclarativeEnvironmentRecord context, string name, JsValue value)
    {
        _ = context.CreateImmutableBinding(name, strict: false);
        _ = context.InitializeBinding(name, value);
    }

    /// <summary>A named function expression's own name, captured by a closure.</summary>
    internal static void Interp2DeclareFunctionNameSlot(DeclarativeEnvironmentRecord context, int slot, JsValue value)
        => context.DeclareFunctionNameAtSlot(slot, value);

    /// <summary>A block binding's declaration running, in the record that holds it.</summary>
    internal void Interp2InitializeBinding(EnvironmentRecord record, string name, JsValue value)
    {
        var status = record.InitializeBinding(name, value);
        if (status != BindingOpResult.Ok)
        {
            throw Interp2BindingFailure(status, name, assignment: true);
        }
    }

    /// <summary>
    /// ECMA-262 9.1.1.1.4 InitializeBinding on the record
    /// <paramref name="name"/> resolves to from <paramref name="environment"/>:
    /// a declaration running in a body whose scope is only known by walking it.
    /// </summary>
    internal void Interp2InitializeName(EnvironmentRecord? environment, string? name, JsValue value)
    {
        if (name is not null)
        {
            for (var env = environment; env is not null; env = env.OuterEnv)
            {
                if (env.HasBinding(name))
                {
                    Interp2InitializeBinding(env, name, value);
                    return;
                }
            }
        }

        throw new JsThrownException(CreateReferenceError(
            name is null ? "Invalid variable slot." : $"{name} is not defined."));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowDeadZoneAccess(string? name)
        => throw new JsThrownException(
            CreateReferenceError($"Cannot access '{name ?? "?"}' before initialization."));

    internal JsValue Interp2LoadContext(
        DeclarativeEnvironmentRecord context, BytecodeFunction function, int slot, string? name, bool strict)
    {
        if (context.TryReadOwnSlot(function, slot, out var value))
        {
            return value;
        }

        if (context.TryGetAtSlot(slot, out var slotValue, out var status))
        {
            if (status == BindingOpResult.Ok)
            {
                return slotValue;
            }

            throw Interp2BindingFailure(status, name ?? "?", assignment: false);
        }

        // Defensive: every context slot is declared when the frame is entered,
        // so this is unreachable unless the layout and the entry path disagree.
        return Interp2LoadFree(context.OuterEnv, name, strict);
    }

    internal void Interp2StoreContext(
        DeclarativeEnvironmentRecord context, BytecodeFunction function, int slot, JsValue value, string? name, bool strict)
    {
        if (context.TryWriteOwnSlot(function, slot, value))
        {
            return;
        }

        if (context.TrySetAtSlot(slot, value, strict, out var status))
        {
            if (status == BindingOpResult.Ok)
            {
                return;
            }

            throw Interp2BindingFailure(status, name ?? "?", assignment: true);
        }

        Interp2StoreFree(context.OuterEnv, name, value, strict);
    }

    // ------------------------------------------------------- free identifiers

    /// <summary>
    /// ECMA-262 9.1.2.1 GetIdentifierReference for a name this body does not
    /// declare, through the site cache that remembers where it resolved last
    /// time.
    /// </summary>
    /// <remarks>
    /// Both cached shapes are re-verified rather than trusted: the slot form
    /// re-checks that the record at the cached depth still numbers slots for the
    /// same function, and the global form checks the lexical version and then
    /// runs a shape guard. A stale entry misses and takes the walk; it never
    /// answers.
    /// </remarks>
    internal JsValue Interp2LoadFreeCached(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment)
    {
        var name = slot < layout.SlotNames.Length ? layout.SlotNames[slot] : null;
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        var sites = layout.FreeSites;
        if (slot < sites.Length && sites[slot] is { IsCurrent: true } site &&
            TryReadThroughSite(site, outerEnvironment, layout.Function, icOffset, name, out var cached))
        {
            return cached;
        }

        return Interp2LoadFreeAndCache(layout, slot, icOffset, outerEnvironment, name);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue Interp2LoadFreeAndCache(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment, string name)
    {
        var strict = layout.IsStrict;
        var walkStartTicks = FenBrowser.Js.Diagnostics.InterpreterProfiler.Enabled
            ? FenBrowser.Js.Diagnostics.InterpreterProfiler.StartSample()
            : 0L;
        var hops = 0;
        var cacheable = true;
        for (var env = outerEnvironment; env is not null; env = env.OuterEnv, hops++)
        {
            var status = env.TryLookupBinding(name, strict, out var value);
            if (status == BindingOpResult.NotFound)
            {
                cacheable &= env is not ObjectEnvironmentRecord;
                continue;
            }

            if (status != BindingOpResult.Ok)
            {
                throw Interp2BindingFailure(status, name, assignment: false);
            }

            // Only the reads that missed the site cache are counted, which is
            // the point: the cache is meant to make this rare, so the number
            // reported is how often it failed to.
            if (FenBrowser.Js.Diagnostics.InterpreterProfiler.Enabled)
            {
                FenBrowser.Js.Diagnostics.InterpreterProfiler.RecordVarResolve(
                    env is GlobalEnvironmentRecord or ObjectEnvironmentRecord, hops);
                FenBrowser.Js.Diagnostics.InterpreterProfiler.RecordVarSlowPath(walkStartTicks);
            }

            if (cacheable && (uint)slot < (uint)layout.FreeSites.Length &&
                CreateReadSite(layout.Function, icOffset, hops, env, name) is { } created)
            {
                layout.FreeSites[slot] = created;
            }

            return value;
        }

        throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
    }

    /// <summary>
    /// ECMA-262 9.1.2.1 GetIdentifierReference for a name this body does not
    /// declare. The walk starts at the closure's environment because a frame on
    /// the new loop has none of its own - everything it declares is a register,
    /// so there is nothing between it and its closure to shadow the name.
    /// </summary>
    internal JsValue Interp2LoadFree(EnvironmentRecord? outerEnvironment, string? name, bool strict)
    {
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            var status = env.TryLookupBinding(name, strict, out var value);
            if (status == BindingOpResult.NotFound)
            {
                continue;
            }

            if (status == BindingOpResult.Ok)
            {
                return value;
            }

            throw Interp2BindingFailure(status, name, assignment: false);
        }

        throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
    }

    /// <summary>
    /// <see cref="Interp2LoadFree"/> for a callee: also the with object the
    /// name resolved on, which a call passes as `this` (ECMA-262 13.3.6.2 step
    /// 1.b), or undefined when it resolved in any other record.
    /// </summary>
    internal JsValue Interp2LoadFreeWithBase(
        EnvironmentRecord? environment, string? name, bool strict, out JsValue withBase)
    {
        withBase = JsValue.Undefined;
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        for (var env = environment; env is not null; env = env.OuterEnv)
        {
            var status = env.TryLookupBinding(name, strict, out var value);
            if (status == BindingOpResult.NotFound)
            {
                continue;
            }

            if (status != BindingOpResult.Ok)
            {
                throw Interp2BindingFailure(status, name, assignment: false);
            }

            if (env.WithBaseObject is { } baseObject)
            {
                withBase = JsValue.FromObject(baseObject);
            }

            return value;
        }

        throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
    }

    /// <summary>ECMA-262 9.1.1.1.5 SetMutableBinding through the closure chain.</summary>
    /// <summary>
    /// ECMA-262 13.4: an update expression resolves its reference once, then
    /// evaluates and writes back through that same reference. Records which
    /// environment holds the name so the matching store can use it.
    /// </summary>
    /// <remarks>
    /// The gap between the two is not empty: <c>x++</c> runs ToNumeric, which
    /// can call a <c>valueOf</c>, which can delete the very property the name
    /// resolved to. Walking again then answers differently - a ReferenceError in
    /// strict code where the spec says the write lands. Held on the interpreter
    /// rather than the frame, and matched by name at the store, exactly as the
    /// old loop holds it, so both loops answer the same.
    /// </remarks>
    /// <summary>
    /// <see cref="Interp2PreResolveFree"/> through the slot's site, when the
    /// LoadVar that `x++` also performs has recorded one.
    /// </summary>
    internal void Interp2PreResolveFreeCached(FrameLayout layout, int slot, EnvironmentRecord? outerEnvironment)
    {
        var name = (uint)slot < (uint)layout.SlotNames.Length ? layout.SlotNames[slot] : null;
        var sites = layout.FreeSites;
        if (name is not null && (uint)slot < (uint)sites.Length && sites[slot] is { IsCurrent: true } site &&
            TryPreResolveThroughSite(site, outerEnvironment, name))
        {
            return;
        }

        Interp2PreResolveFree(outerEnvironment, name);
    }

    internal void Interp2PreResolveFree(EnvironmentRecord? outerEnvironment, string? name)
    {
        _preResolvedEnv = null;
        _preResolvedName = name;
        _preResolvedSite = null;
        if (name is null) return;

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            if (env.HasBinding(name))
            {
                _preResolvedEnv = env;
                return;
            }
        }
    }

    /// <summary>
    /// Writes through the binding <see cref="Interp2PreResolveFree"/> found, or
    /// walks the chain when there was none to find or something in between
    /// resolved a different name.
    /// </summary>
    internal void Interp2StoreResolvedFree(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment, JsValue value)
    {
        var name = (uint)slot < (uint)layout.SlotNames.Length ? layout.SlotNames[slot] : null;
        if (name is not null && TryStoreResolvedThroughSite(layout.Function, icOffset, name, value))
        {
            return;
        }

        var strict = layout.IsStrict;
        var resolved = _preResolvedEnv;
        var resolvedName = _preResolvedName;
        _preResolvedEnv = null;
        _preResolvedName = null;
        _preResolvedSite = null;

        if (resolved is not null && name is not null &&
            string.Equals(resolvedName, name, StringComparison.Ordinal))
        {
            var status = resolved.SetMutableBinding(name, value, strict);
            if (status == BindingOpResult.Ok)
            {
                NoteResolvedGlobalStore(layout.Function, icOffset, resolved, name);
                return;
            }

            throw Interp2BindingFailure(status, name, assignment: true);
        }

        Interp2StoreFree(outerEnvironment, name, value, strict);
    }

    /// <summary>
    /// ECMA-262 13.2.5.5 / 15.4: defines an own data property from a computed
    /// key - an object literal's <c>{ [k]: v }</c> and a computed method name.
    /// </summary>
    /// <remarks>
    /// Defining rather than setting is the point: a computed key must not run a
    /// setter the prototype happens to carry, and B.3.1 says a computed
    /// <c>__proto__</c> makes a plain own property instead of reparenting.
    /// <paramref name="namesFunction"/> carries the NamedEvaluation the compiler
    /// asked for on an anonymous method.
    /// </remarks>
    // { value, writable: true, enumerable: true, configurable: true } as an
    // object, for a proxy's defineProperty trap.
    private JsValue CreateDataDescriptorObject(JsValue value)
    {
        var descriptor = CreateOrdinaryObject();
        descriptor.DefineOwnProperty("value", new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        descriptor.DefineOwnProperty("writable", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
        descriptor.DefineOwnProperty("enumerable", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
        descriptor.DefineOwnProperty("configurable", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(descriptor, AllocationSite.Current()));
    }

    /// <param name="flags">SetElemDefine's D: bit 0 names an anonymous function from the key, bit 1 throws when the define fails.</param>
    internal void Interp2DefineElement(JsValue target, JsValue key, JsValue value, int flags)
    {
        var namesFunction = (flags & 1) != 0;
        var orThrow = (flags & 2) != 0;
        var obj = _heap.GetObject(ResolveObjectHandle(target));
        if (obj is ProxyObject proxy)
        {
            // A class field's `this` can be a proxy a base constructor returned;
            // CreateDataProperty is its [[DefineOwnProperty]], so the trap runs.
            var propertyKeyValue = key.Tag == JsValueTag.Symbol ? key : JsValue.FromString(ToPropertyKey(key));
            if (!ProxyDefineProperty(proxy, propertyKeyValue, CreateDataDescriptorObject(value)) && orThrow)
                throw new JsThrownException(CreateTypeError("Cannot define a class field on this object."));
            if (namesFunction) ApplyFunctionName(value, propertyKeyValue, prefix: null);
            return;
        }

        if (key.Tag == JsValueTag.Symbol)
        {
            var definedSymbol = obj.DefineOwnSymbolProperty(
                key.AsSymbolId(),
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
            if (!definedSymbol && orThrow)
                throw new JsThrownException(CreateTypeError("Cannot define a class field over a non-configurable property."));
            if (namesFunction) ApplyFunctionName(value, key, prefix: null);
            return;
        }

        var propertyKey = ToPropertyKey(key);
        var defined = obj.DefineOwnProperty(
            propertyKey,
            new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        if (!defined && orThrow)
            throw new JsThrownException(CreateTypeError("Cannot define class field '" + propertyKey + "' over a non-configurable property."));
        if (namesFunction) ApplyFunctionName(value, JsValue.FromString(propertyKey), prefix: null);
    }

    /// <summary>
    /// ECMA-262 13.2.4.1 / 13.3.7.1: expands an iterable into an array literal
    /// or an argument list, returning the index after the last element written.
    /// </summary>
    internal JsValue Interp2SpreadAppend(JsValue target, JsValue startIndexValue, JsValue source)
    {
        var targetHandle = ResolveObjectHandle(target);
        var targetObj = _heap.GetObject(targetHandle);
        var startIndex = (int)startIndexValue.AsNumber();
        var values = CollectSpreadValues(source);

        // The iterable's values are only reachable from a CLR list until they
        // land in the array, and writing one can collect.
        var rootMark = _heap.RootCount;
        try
        {
            _heap.PushRoot(targetHandle);
            foreach (var v in values)
            {
                if (v.Tag == JsValueTag.Object) _heap.PushRoot(v.AsObjectHandle());
            }

            for (var k = 0; k < values.Count; k++)
            {
                var key = (startIndex + k).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _ = SetPropertyValue(targetHandle, targetObj, key, values[k], target);
            }
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
        }

        return JsValue.FromNumber(startIndex + values.Count);
    }

    /// <summary>
    /// ECMA-262 10.2.7 MakeMethod: records the object a method was defined on,
    /// which is what `super` in its body resolves against.
    /// </summary>
    internal void Interp2SetHomeObject(JsValue methodValue, JsValue homeValue)
    {
        if (methodValue.Tag != JsValueTag.Object || homeValue.Tag != JsValueTag.Object) return;
        if (_heap.GetObject(methodValue.AsObjectHandle()) is not JsFunctionObject method) return;

        var home = homeValue.AsObjectHandle();
        method.HomeObject = home;
        method.BarrierInternalSlot(home);
        _heap.WriteBarrier(methodValue.AsObjectHandle(), home);
    }

    /// <summary>
    /// ECMA-262 13.3.7.3 MakeSuperPropertyReference and 9.1.2 GetSuperBase:
    /// `super.x` and `super[k]`, read off the home object's prototype with the
    /// current receiver as the `this` an accessor would see.
    /// </summary>
    /// <remarks>
    /// The old loop finds the home object on the frame's callee, and failing
    /// that walks the environment chain for an arrow that closed over one. Only
    /// the first case can arise here: a body whose nested function reaches for
    /// the enclosing `super` is refused by the capture analysis before it runs.
    /// </remarks>
    /// <summary>
    /// ECMA-262 13.3.7.1 `super.x` / `super[k]`. A method carries its home
    /// object; an arrow inside one has none of its own and reads the enclosing
    /// method's through <paramref name="lexicalEnvironment"/> (9.1.2
    /// GetSuperBase via GetThisEnvironment).
    /// </summary>
    internal JsValue Interp2LoadSuper(
        BytecodeFunction function, JsFunctionObject? callee, JsValue thisValue, EnvironmentRecord? lexicalEnvironment,
        JsValue key, bool keyIsName)
    {
        if ((callee?.HomeObject ?? ResolveLexicalHomeObject(lexicalEnvironment)) is not { } home)
        {
            throw new JsThrownException(CreateReferenceError(
                "super reference requires a class method context."));
        }

        if (!TryGetSuperPropertyBase(function, home, out var baseProtoHandle))
        {
            throw new JsThrownException(CreateTypeError("Cannot read properties of null."));
        }

        var baseProto = _heap.GetObject(baseProtoHandle);
        if (!keyIsName && key.Tag == JsValueTag.Symbol)
        {
            return TryGetSymbolPropertyValue(baseProto, thisValue, key.AsSymbolId(), out var symbolValue)
                ? symbolValue
                : JsValue.Undefined;
        }

        var name = keyIsName ? key.AsString() : ToPropertyKey(key);
        return TryGetPropertyValue(baseProto, thisValue, name, out var value) ? value : JsValue.Undefined;
    }

    /// <summary>
    /// An accessor on an object literal or a class - <c>get x() {}</c>,
    /// <c>set [k](v) {}</c> - through the core the old loop's handlers wrap.
    /// <paramref name="name"/> is the constant name, or null when the key is in
    /// <paramref name="key"/>.
    /// </summary>
    internal void Interp2DefineAccessor(
        JsValue target, string? name, JsValue key, JsValue accessor, bool isGetter, bool enumerable)
        => DefineAccessorCore(target, name, key, accessor, isGetter, enumerable);

    /// <summary>A class or object-literal method, through the old loop's core.</summary>
    internal void Interp2DefineMethod(JsValue target, string? name, JsValue key, JsValue method)
        => DefineMethodCore(target, name, key, method);

    /// <summary>ECMA-262 7.3.5 CreateDataPropertyOrThrow, shared with the old loop.</summary>
    internal void Interp2DefineOwnDataProperty(JsValue target, string key, JsValue value)
        => DefineOwnDataProperty(target, key, value);

    /// <summary>ECMA-262 13.2.5.5 object spread: <c>{ ...src }</c>.</summary>
    internal void Interp2CopyDataProperties(JsValue target, JsValue source)
        => CopyDataPropertiesInto(target, source);

    /// <summary>ECMA-262 13.2.8.3 GetTemplateObject, cached on the function.</summary>
    internal JsValue Interp2GetTemplateObject(BytecodeFunction function, int index)
        => GetTemplateObject(function, index);

    /// <summary>
    /// ECMA-262 7.3.31 PrivateGet. A private name is not a property key user
    /// code can forge, so the whole check is the brand: an object carries the
    /// one its class stamped on it, and a body may only read the fields of the
    /// class it was declared in.
    /// </summary>
    internal JsValue Interp2GetPrivateField(BytecodeFunction function, JsValue receiver, string name)
    {
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;

        if (receiver.Tag == JsValueTag.HostObject)
        {
            if (!TryGetHostPrivateField(receiver, name, brand, out var hostValue))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot read private field from an object whose class did not declare it."));
            }

            return hostValue;
        }

        if (receiver.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Cannot read private field from non-object."));
        }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand ||
            !TryGetPropertyValue(obj, receiver, name, out var value))
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot read private field from an object whose class did not declare it."));
        }

        return value;
    }

    /// <summary>ECMA-262 7.3.32 PrivateSet, guarded by the same brand.</summary>
    internal void Interp2SetPrivateField(
        BytecodeFunction function, JsValue receiver, string name, JsValue value)
    {
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;

        if (receiver.Tag == JsValueTag.HostObject)
        {
            if (!TrySetHostPrivateField(receiver, name, value, brand))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot write private field to an object whose class did not declare it."));
            }

            return;
        }

        if (receiver.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Cannot write private field to non-object."));
        }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand)
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot write private field to an object whose class did not declare it."));
        }

        WritePrivateField(obj, receiver, name, value);
    }

    /// <summary>
    /// The write half of <see cref="Interp2LoadFreeCached"/>, sharing its site:
    /// a store and a load of one name in one function are the same slot, so
    /// whichever runs first records where the binding lives. A const or a
    /// binding still in its dead zone is refused by
    /// <see cref="DeclarativeEnvironmentRecord.TryWriteOwnSlot"/>, and a global
    /// property that is not a plain writable data property by the store cache's
    /// guard, and the walk reports them.
    /// </summary>
    /// <remarks>
    /// Assignments to outer variables used to walk the chain by name every
    /// time: a closure variable write cost ~35ns more than a local one, a
    /// global ~110ns more.
    /// </remarks>
    internal void Interp2StoreFreeCached(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment, JsValue value)
    {
        var sites = layout.FreeSites;
        if ((uint)slot < (uint)sites.Length && sites[slot] is { IsCurrent: true } site &&
            layout.SlotNames[slot] is { } siteName &&
            TryWriteThroughSite(site, outerEnvironment, layout.Function, icOffset, siteName, value))
        {
            return;
        }

        Interp2StoreFreeAndCache(layout, slot, icOffset, outerEnvironment, value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void Interp2StoreFreeAndCache(FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment, JsValue value)
    {
        var name = slot < layout.SlotNames.Length ? layout.SlotNames[slot] : null;
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        // Interp2StoreFree's walk, noting on the way where the binding lives:
        // a record with slot numbering, or a property of the global object.
        var hops = 0;
        var cacheable = true;
        for (var env = outerEnvironment; env is not null; env = env.OuterEnv, hops++)
        {
            if (!env.HasBinding(name))
            {
                // A `with` object passed over here could gain the name later.
                cacheable &= env is not ObjectEnvironmentRecord;
                continue;
            }

            var status = env.SetMutableBinding(name, value, layout.IsStrict);
            if (status != BindingOpResult.Ok)
            {
                throw Interp2BindingFailure(status, name, assignment: true);
            }

            if (cacheable && (uint)slot < (uint)layout.FreeSites.Length &&
                CreateWriteSite(layout.Function, icOffset, hops, env, name) is { } created)
            {
                layout.FreeSites[slot] = created;
            }

            return;
        }

        Interp2StoreFree(outerEnvironment: null, name, value, layout.IsStrict);
    }

    internal void Interp2StoreFree(EnvironmentRecord? outerEnvironment, string? name, JsValue value, bool strict)
    {
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.SetMutableBinding(name, value, strict);
            if (status == BindingOpResult.Ok)
            {
                return;
            }

            throw Interp2BindingFailure(status, name, assignment: true);
        }

        // ECMA-262 9.1.1.4.4: an unresolvable reference is a ReferenceError in
        // strict code and creates a global property in sloppy code.
        if (strict)
        {
            throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
        }

        SetImplicitGlobalProperty(name, value);
    }

    /// <summary>
    /// ECMA-262 13.5.3.1: <c>typeof</c> applied to an identifier is the one
    /// read that does not throw for a name that resolves nowhere - it answers
    /// "undefined". A name in the temporal dead zone still throws.
    /// </summary>
    internal string Interp2TypeOfFree(EnvironmentRecord? outerEnvironment, string? name, bool strict)
    {
        if (name is null)
        {
            return "undefined";
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.GetBindingValue(name, strict, out var value);
            if (status == BindingOpResult.Ok)
            {
                return TypeOfValue(value);
            }

            throw Interp2BindingFailure(status, name, assignment: false);
        }

        return "undefined";
    }

    /// <summary>
    /// ECMA-262 9.1.1.1.5: assigning to a named function expression's own name.
    /// Its binding is immutable but not strict unless the function is, so the
    /// write is a TypeError in strict code and is dropped in sloppy code.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowSelfNameAssignment(string? name)
        => throw new JsThrownException(CreateTypeError($"Assignment to constant variable '{name ?? "?"}'."));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void Interp2ThrowConstAssignment(string? name)
        => throw new JsThrownException(CreateTypeError($"Assignment to constant variable '{name ?? "?"}'."));

    private JsThrownException Interp2BindingFailure(BindingOpResult status, string name, bool assignment)
        => status switch
        {
            BindingOpResult.TdzAccess =>
                new JsThrownException(CreateReferenceError($"Cannot access '{name}' before initialization.")),
            BindingOpResult.ConstAssignment => new JsThrownException(CreateTypeError(assignment
                ? $"Assignment to constant variable '{name}'."
                : $"Cannot read immutable binding '{name}'.")),
            BindingOpResult.NotInitializable or BindingOpResult.AlreadyDeclared =>
                new JsThrownException(CreateTypeError($"Cannot initialize binding '{name}'.")),
            _ => new JsThrownException(CreateReferenceError($"{name} is not defined.")),
        };

    // ------------------------------------------------------------- operators

    /// <summary>
    /// The two-operand numeric fast path, shared verbatim with the old loop and
    /// the JIT so all three agree on what a Number-Number operation produces.
    /// </summary>
    internal static bool Interp2TryFastBinary(OpCode op, in JsValue left, in JsValue right, out JsValue result)
        => TryFastBinop(op, in left, in right, out result);

    /// <summary>
    /// The generic form of every binary operator the new loop dispatches, each
    /// forwarding to the same helper the old loop's case forwards to.
    /// </summary>
    internal JsValue Interp2SlowBinary(OpCode op, JsValue left, JsValue right) => op switch
    {
        OpCode.Add => Add(left, right),
        OpCode.Sub => BigIntArith(left, right, "subtraction", static (a, b) => a - b, static (a, b) => a - b),
        OpCode.Mul => BigIntArith(left, right, "multiplication", static (a, b) => a * b, static (a, b) => a * b),
        OpCode.Div => BigIntArith(left, right, "division", static (a, b) => a / b, static (a, b) => a / b),
        OpCode.Mod => BigIntArith(left, right, "modulo", static (a, b) => a % b, static (a, b) => a % b),
        OpCode.Exp => ExponentiationOp(left, right),
        OpCode.Lt => JsValue.FromBoolean(IsLessThan(left, right)),
        OpCode.Gt => JsValue.FromBoolean(IsGreaterThan(left, right)),
        OpCode.Le => JsValue.FromBoolean(IsLessThanOrEqual(left, right)),
        OpCode.Ge => JsValue.FromBoolean(IsGreaterThanOrEqual(left, right)),
        OpCode.Eq => JsValue.FromBoolean(AreEqual(left, right)),
        OpCode.Neq => JsValue.FromBoolean(!AreEqual(left, right)),
        OpCode.StrictEq => JsValue.FromBoolean(AreStrictlyEqual(left, right)),
        OpCode.StrictNeq => JsValue.FromBoolean(!AreStrictlyEqual(left, right)),
        OpCode.BitAnd => BitwiseAndOp(left, right),
        OpCode.BitOr => BitwiseOrOp(left, right),
        OpCode.BitXor => BitwiseXorOp(left, right),
        OpCode.ShiftLeft => LeftShiftOp(left, right),
        OpCode.ShiftRight => RightShiftOp(left, right),
        OpCode.UnsignedShiftRight => UnsignedRightShiftOp(left, right),
        _ => throw new JsEngineFatalException($"Interp2SlowBinary: unsupported opcode {op}."),
    };

    internal JsValue Interp2Unary(OpCode op, JsValue operand)
    {
        switch (op)
        {
            case OpCode.Pos:
                return JsValue.FromNumberCompact(ToNumber(operand));
            case OpCode.Neg:
            {
                var numeric = ToNumericValue(operand);
                return numeric.Tag == JsValueTag.BigInt
                    ? JsValue.FromBigInt(-numeric.AsBigInt())
                    : JsValue.FromNumberCompact(-numeric.AsNumber());
            }

            case OpCode.BitNot:
                return BitwiseNotOp(operand);
            case OpCode.TypeOf:
                return JsValue.FromString(TypeOfValue(operand));
            case OpCode.ToNumeric:
                return ToNumericValue(operand);
            case OpCode.ToStringCoerce:
                return JsValue.FromString(ToStringValue(operand));
            case OpCode.Increment:
                return StepNumeric(operand, +1);
            case OpCode.Decrement:
                return StepNumeric(operand, -1);
            default:
                throw new JsEngineFatalException($"Interp2Unary: unsupported opcode {op}.");
        }
    }

    internal bool Interp2IsTruthy(JsValue value) => IsTruthy(value);

    internal string Interp2TypeOfValue(JsValue value) => TypeOfValue(value);

    // -------------------------------------------------------------- properties

    /// <summary>
    /// ECMA-262 13.3.2 property access by literal name, through the same
    /// per-call-site inline cache the old loop populates. The caches are keyed
    /// by (function, instruction offset), which both loops agree on because they
    /// execute the same bytecode - so a site warmed on one loop is warm on the
    /// other.
    /// </summary>
    /// <param name="getter">
    /// Set, with the result undefined, when the read resolves through this
    /// site's accessor stub to a getter: the loop calls it as it calls any
    /// function - pushing a window when it can - rather than this re-entering
    /// the loop from outside.
    /// </param>
    internal JsValue Interp2GetPropertyByName(BytecodeFunction function, int icOffset, JsValue receiver, string key, out JsValue getter)
    {
        getter = JsValue.Undefined;
        if (TryGetLoadIC(function, icOffset, receiver, key, out var cached))
        {
            if (Interpreter2.Interp2Options.Log) Interpreter2.Interp2Stats.RecordPropertyRead(cached: true);
            return cached;
        }

        if (TryAccessorStub(function, icOffset, receiver, out getter))
        {
            return JsValue.Undefined;
        }

        if (Interpreter2.Interp2Options.Log)
        {
            Interpreter2.Interp2Stats.RecordPropertyRead(cached: false);
            Interpreter2.Interp2Stats.RecordPropertyMiss(ClassifyPropertyMiss(receiver, key));
        }

        var value = GetReceiverProperty(receiver, key);
        PopulateLoadIC(function, icOffset, receiver, key);
        NoteAccessor(function, icOffset, receiver, key, forSetter: false);
        return value;
    }

    /// <summary>
    /// Where a name that missed its cache site actually lives. Diagnostic only,
    /// and it repeats the lookup - so it runs under the coverage switch and
    /// nowhere else.
    /// </summary>
    private Interpreter2.PropertyMissKind ClassifyPropertyMiss(JsValue receiver, string key)
    {
        if (receiver.Tag == JsValueTag.String)
        {
            Interpreter2.Interp2Stats.RecordStringReceiverKey(key);
            return Interpreter2.PropertyMissKind.StringReceiver;
        }

        if (receiver.Tag == JsValueTag.HostObject)
        {
            return Interpreter2.PropertyMissKind.HostReceiver;
        }

        if (receiver.Tag != JsValueTag.Object)
        {
            return Interpreter2.PropertyMissKind.OtherPrimitiveReceiver;
        }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj is ProxyObject or ModuleNamespaceObject)
        {
            return Interpreter2.PropertyMissKind.ExoticReceiver;
        }

        if (obj.TryGetOwnProperty(key, out var own))
        {
            // Only a property the shape describes and that reads as a plain
            // data slot is one a site could have cached. An accessor never can
            // be - reading it calls user code. A data property the shape does
            // not describe is neither: it is a gap.
            if (obj.CurrentShape.TryGetSlot(key, out var slot) && obj.TryReadDataSlot(slot, out _))
            {
                return Interpreter2.PropertyMissKind.OwnDataSlot;
            }

            if (own.IsAccessor)
            {
                return Interpreter2.PropertyMissKind.OwnAccessor;
            }

            Interpreter2.Interp2Stats.RecordUncacheableKey(key);
            return Interpreter2.PropertyMissKind.OwnNotInShape;
        }

        for (var proto = obj.PrototypeHandle; proto is { } handle; )
        {
            var protoObj = _heap.GetObject(handle);
            if (protoObj.TryGetOwnProperty(key, out _))
            {
                return Interpreter2.PropertyMissKind.OnPrototype;
            }

            proto = protoObj.PrototypeHandle;
        }

        return Interpreter2.PropertyMissKind.Absent;
    }

    /// <summary>ECMA-262 13.3.3 computed member access.</summary>
    internal JsValue Interp2GetElement(BytecodeFunction function, int icOffset, JsValue receiver, JsValue key)
    {
        // 13.3.2.1 GetValue requires the base to be object-coercible before the
        // key is coerced, so `null[obj]` throws before obj's toString runs.
        if (receiver.Tag is JsValueTag.Null or JsValueTag.Undefined)
        {
            var kind = receiver.Tag == JsValueTag.Null ? "null" : "undefined";
            throw new JsThrownException(CreateTypeError(key.Tag == JsValueTag.String
                ? $"Cannot read properties of {kind} (reading '{key.AsString()}')."
                : $"Cannot read properties of {kind}."));
        }

        if (key.Tag == JsValueTag.Symbol)
        {
            return GetReceiverSymbolProperty(receiver, key.AsSymbolId());
        }

        if (TryGetDenseElement(receiver, key, out var element))
        {
            if (Interpreter2.Interp2Options.Log) Interpreter2.Interp2Stats.RecordElementRead(cached: true);
            // A dense array element is an own data property: the slot itself is
            // the answer, with no key to build and no prototype chain to walk.
            return element;
        }

        if (key.Tag == JsValueTag.String &&
            TryGetElemStringIC(function, icOffset, receiver, key.AsString(), out var cached))
        {
            if (Interpreter2.Interp2Options.Log) Interpreter2.Interp2Stats.RecordElementRead(cached: true);
            return cached;
        }

        if (Interpreter2.Interp2Options.Log)
        {
            Interpreter2.Interp2Stats.RecordElementRead(cached: false);
            var missKind = ClassifyElementMiss(receiver, key);
            Interpreter2.Interp2Stats.RecordElementMiss(missKind);
            Interpreter2.Interp2Stats.RecordElementMissSite(
                DescribeLoadSite(function, icOffset, out var megamorphic), megamorphic);
            if (missKind == Interpreter2.ElementMissKind.ObjectNameKey)
            {
                Interpreter2.Interp2Stats.RecordElementNameKey(key.AsString());
                Interpreter2.Interp2Stats.RecordElementSite(
                    function.GetHashCode(),
                    icOffset,
                    key.AsString(),
                    receiver.Tag == JsValueTag.Object
                        ? _heap.GetObject(receiver.AsObjectHandle()).CurrentShape
                        : null);
            }
        }

        var propertyKey = ToPropertyKey(key);
        var value = GetReceiverProperty(receiver, propertyKey);
        if (key.Tag == JsValueTag.String)
        {
            PopulateGetElemStringIC(function, icOffset, receiver, propertyKey);
        }

        return value;
    }

    /// <summary>
    /// What an element read was, when neither the dense path nor a site could
    /// answer it. Diagnostic only, so it runs under the coverage switch and
    /// nowhere else - and it reads nothing user code could observe.
    /// </summary>
    private Interpreter2.ElementMissKind ClassifyElementMiss(JsValue receiver, JsValue key)
    {
        var keyIsIndex = key.Tag switch
        {
            JsValueTag.Int32 => key.AsInt32() >= 0,
            JsValueTag.Number => key.AsNumber() >= 0 && key.AsNumber() == Math.Floor(key.AsNumber()),
            JsValueTag.String => IsCanonicalIntegerIndex(key.AsString(), out _),
            _ => false,
        };

        switch (receiver.Tag)
        {
            case JsValueTag.String:
                return keyIsIndex
                    ? Interpreter2.ElementMissKind.StringIndex
                    : Interpreter2.ElementMissKind.StringName;

            case JsValueTag.HostObject:
                return Interpreter2.ElementMissKind.HostReceiver;

            case JsValueTag.Object:
                break;

            default:
                return Interpreter2.ElementMissKind.OtherPrimitiveReceiver;
        }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj is ProxyObject or ModuleNamespaceObject) return Interpreter2.ElementMissKind.ExoticReceiver;
        if (obj is TypedArrayObject) return Interpreter2.ElementMissKind.TypedArrayIndex;

        if (obj is ArrayObject array && keyIsIndex)
        {
            return array.IsDense
                ? Interpreter2.ElementMissKind.DenseArrayOutOfRange
                : Interpreter2.ElementMissKind.SparseArrayIndex;
        }

        if (keyIsIndex) return Interpreter2.ElementMissKind.ObjectIndexKey;

        return key.Tag == JsValueTag.String
            ? Interpreter2.ElementMissKind.ObjectNameKey
            : Interpreter2.ElementMissKind.OtherKey;
    }

    /// <summary>ECMA-262 13.15.2 assignment to a literal property name.</summary>
    internal void Interp2SetPropertyByName(
        BytecodeFunction function, int icOffset, JsValue receiver, string key, JsValue value, bool strict)
        => SetPropertyByNameCore(function, icOffset, receiver, key, value, strict);

    /// <summary>ECMA-262 13.15.2 assignment to a computed member.</summary>
    internal void Interp2SetElement(JsValue receiver, JsValue key, JsValue value, bool strict)
        => SetElementCore(receiver, key, value, strict);

    /// <summary>Array-literal element store at a compiler-known index.</summary>
    internal void Interp2SetElementByIndex(JsValue receiver, int index, JsValue value, bool strict)
        => SetElementByIndexCore(receiver, index, value, strict);

    /// <summary>ECMA-262 13.3.5 EvaluateNew: <c>new F(...)</c>.</summary>
    /// <remarks>
    /// newTarget is the constructor itself. A derived class's <c>super()</c> is
    /// a different path and this loop declines the bodies that can reach it.
    /// </remarks>
    internal JsValue Interp2Construct(JsValue constructor, in CallArgs args)
    {
        // An ordinary function or a class goes straight to [[Construct]] with
        // the arguments as they are; the general entry takes a list, and
        // handing it this struct boxed it on every `new`.
        if (constructor.Tag == JsValueTag.Object &&
            _heap.GetObject(constructor.AsObjectHandle()) is JsFunctionObject fn &&
            fn.Kind is FunctionKind.Ordinary or FunctionKind.Constructor &&
            !fn.Function.IsArrow)
        {
            return ExecuteConstruct(fn, args, constructor);
        }

        return ConstructFunction(constructor, args, constructor);
    }

    /// <summary>
    /// ECMA-262 10.2.3 OrdinaryFunctionCreate for a nested function.
    /// </summary>
    /// <remarks>
    /// The captured environment is the enclosing frame's own closure rather than
    /// a record for the frame itself, which the layout has already established
    /// no function created here can tell apart: none of them reaches for a name
    /// this body declares, so the record would have been a link they resolve
    /// straight through.
    /// </remarks>
    internal JsValue Interp2CreateFunction(BytecodeFunction nested, EnvironmentRecord? outerEnvironment)
        => CreateFunctionObject(nested, outerEnvironment);

    /// <summary>
    /// ECMA-262 10.4.4 CreateUnmappedArgumentsObject. This engine builds the
    /// mapped form the same way - as a snapshot of the arguments rather than as
    /// an alias of the parameter bindings - which is what lets a parameter stay
    /// in a register in a body that has one.
    /// </summary>
    internal JsValue Interp2CreateArguments(in CallArgs args, bool restricted, JsFunctionObject callee)
        => CreateArgumentsObject(args, restricted, callee);

    /// <summary>ECMA-262 13.10.2 `instanceof`.</summary>
    internal JsValue Interp2InstanceOf(JsValue left, JsValue right)
        => JsValue.FromBoolean(InstanceOfCore(left, right));

    /// <summary>ECMA-262 13.10.1 `in`.</summary>
    internal JsValue Interp2In(JsValue key, JsValue rhs)
        => JsValue.FromBoolean(HasPropertyCore(key, rhs));

    /// <summary>ECMA-262 13.5.1.2 `delete obj.name`.</summary>
    internal JsValue Interp2DeletePropertyByName(JsValue receiver, string prop, bool strict)
        => JsValue.FromBoolean(DeletePropertyByNameCore(receiver, prop, strict));

    /// <summary>ECMA-262 13.5.1.2 `delete obj[key]`.</summary>
    internal JsValue Interp2DeleteElement(JsValue receiver, JsValue key, bool strict)
        => JsValue.FromBoolean(DeleteElementCore(receiver, key, strict));

    // ------------------------------------------------------------ iteration

    /// <summary>ECMA-262 14.7.5.6 EnumerateObjectProperties, as a for-in state.</summary>
    internal JsValue Interp2CreateForInIterator(JsValue source) => CreateForInIterator(source);

    /// <summary>Advance a for-in state; false when the enumeration is finished.</summary>
    internal bool Interp2ForInNext(JsValue iterator, out JsValue key)
    {
        var state = ResolveObject(iterator) as ForInIteratorObject
            ?? throw new JsEngineFatalException("Interp2ForInNext: register does not hold a for-in iterator.");
        if (!state.TryMoveNext(out var name))
        {
            key = JsValue.Undefined;
            return false;
        }

        key = JsValue.FromString(name);
        return true;
    }

    /// <summary>
    /// ECMA-262 7.4.2 GetIterator for a for-of head. <paramref name="requireIterable"/>
    /// is the destructuring form, where an array-like without @@iterator throws
    /// rather than falling back.
    /// </summary>
    internal JsValue Interp2CreateForOfIterator(JsValue source, bool requireIterable)
        => CreateForOfIteratorState(source, requireIterable);

    /// <summary>Advance a for-of state; true when the iterator reported done.</summary>
    internal bool Interp2ForOfNext(JsValue iterator, out JsValue value)
    {
        var state = ResolveObject(iterator) as ForOfIteratorObject
            ?? throw new JsEngineFatalException("Interp2ForOfNext: register does not hold a for-of iterator.");
        return ForOfStepDone(state, out value);
    }

    /// <summary>
    /// ECMA-262 7.4.11 IteratorClose. <paramref name="suppressErrors"/> is the
    /// throw-completion form: closing because something already went wrong, so
    /// the original exception wins over anything return() raises.
    /// </summary>
    internal void Interp2IteratorClose(JsValue iterator, bool suppressErrors)
    {
        try
        {
            if (ResolveObject(iterator) is ForOfIteratorObject closing)
            {
                CloseForOfIteratorState(closing, suppressErrors);
            }
        }
        catch (JsThrownException) when (suppressErrors)
        {
        }
    }

    /// <summary>ECMA-262 13.2.7 regular expression literal.</summary>
    internal JsValue Interp2NewRegExp(string rawText) => NewRegExpLiteral(rawText);

    internal JsValue Interp2NewObject()
        => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()));

    internal JsValue Interp2NewArray(int length)
        => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(length), AllocationSite.Current()));
}
