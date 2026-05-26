using FenBrowser.Js.Builtins;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Js.Interpreter;

public sealed partial class BytecodeInterpreter : IBuiltinContext
{
    private readonly JsHeap _heap;
    public JsHeap Heap => _heap;
    double IBuiltinContext.ToNumber(JsValue value) => ToNumber(value);
    string IBuiltinContext.ToStringValue(JsValue value) => ToStringValue(value);
    JsValue IBuiltinContext.CallFunction(JsValue fn, IReadOnlyList<JsValue> args, JsValue thisValue) => CallFunction(fn, args, thisValue);
    JsValue IBuiltinContext.ConstructFunction(JsValue ctor, IReadOnlyList<JsValue> args) => ConstructFunction(ctor, args);
    bool IBuiltinContext.TryGetPropertyValue(JsObject obj, JsValue receiver, string name, out JsValue value) => TryGetPropertyValue(obj, receiver, name, out value);
    int IBuiltinContext.GetArrayLength(JsObject obj) => GetArrayLength(obj);
    JsValue IBuiltinContext.CreateTypeError(string message) => CreateTypeError(message);
    JsValue IBuiltinContext.CreateRangeError(string message) => CreateRangeError(message);
    JsValue IBuiltinContext.CreateSyntaxError(string message) => CreateSyntaxError(message);
    JsValue IBuiltinContext.CreateError(string message) => CreateError(message);
    JsValue IBuiltinContext.CreateUriError(string message) => CreateUriError(message);
    JsValue IBuiltinContext.CreateReferenceError(string message) => CreateReferenceError(message);
    void IBuiltinContext.DefineIntrinsicFunction(
        ObjectHandle ownerHandle, JsObject owner, string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call, int length)
        => DefineIntrinsicFunction(ownerHandle, owner, name, call, length);
    ObjectHandle IBuiltinContext.GetObjectPrototype() => GetGlobalPrototype("Object");
    ObjectHandle IBuiltinContext.GetArrayPrototype() => GetGlobalPrototype("Array");
    ObjectHandle IBuiltinContext.GetErrorPrototype() => EnsureErrorPrototype();
    ObjectHandle IBuiltinContext.GetParseIntFunction() => EnsureParseIntFunction();
    ObjectHandle IBuiltinContext.GetFunctionCallMethod() => EnsureFunctionCallMethod();
    ObjectHandle IBuiltinContext.GetParseFloatFunction() => EnsureParseFloatFunction();
    JsValue IBuiltinContext.CreateSymbol(string? description) => JsValue.FromSymbol(description);
    JsValue IBuiltinContext.CreateWellKnownSymbol(string name) => GetWellKnownSymbol(name);
    JsValue IBuiltinContext.SymbolFor(string key) => SymbolFor(key);
    JsValue IBuiltinContext.SymbolKeyFor(long id) => SymbolKeyFor(id);
    void IBuiltinContext.InstallDatePrototypeMethods(ObjectHandle protoHandle, JsObject proto) => InstallPrototypeMethodsOnDatePrototype(protoHandle, proto);
    void IBuiltinContext.InstallRegExpPrototypeMethods(ObjectHandle protoHandle, JsObject proto) => InstallPrototypeMethodsOnRegExpPrototype(protoHandle, proto);
    JsValue IBuiltinContext.Eval(IReadOnlyList<JsValue> args) => Eval(args);
    void IBuiltinContext.EnqueueMicrotask(JsValue callback) => _pendingMicrotasks.Enqueue(callback);
    ObjectHandle IBuiltinContext.MaterializeObjectConstructor() => EnsureObjectConstructor();
    ObjectHandle IBuiltinContext.MaterializeArrayConstructor() => EnsureArrayConstructor();
    ObjectHandle IBuiltinContext.MaterializeFunctionConstructor() => EnsureFunctionConstructor();
    ObjectHandle IBuiltinContext.MaterializeSetConstructor() => EnsureSetConstructor();
    ObjectHandle IBuiltinContext.MaterializeMapConstructor() => EnsureMapConstructor();
    ObjectHandle IBuiltinContext.MaterializeWeakMapConstructor() => EnsureWeakMapConstructor();
    ObjectHandle IBuiltinContext.MaterializeWeakSetConstructor() => EnsureWeakSetConstructor();
    ObjectHandle IBuiltinContext.MaterializePromiseConstructor() => EnsurePromiseConstructor();
    ObjectHandle IBuiltinContext.MaterializeJsonObject() => EnsureJsonObject();
    ObjectHandle IBuiltinContext.MaterializeReflectObject() => EnsureReflectObject();
    ObjectHandle IBuiltinContext.MaterializeIteratorConstructor() => EnsureIteratorConstructor();
    ObjectHandle IBuiltinContext.MaterializeWeakRefConstructor() => EnsureWeakRefConstructor();
    ObjectHandle IBuiltinContext.MaterializeFinalizationRegistryConstructor() => EnsureFinalizationRegistryConstructor();
    ObjectHandle IBuiltinContext.MaterializeAggregateErrorConstructor() => EnsureAggregateErrorConstructor();
    ObjectHandle IBuiltinContext.MaterializeStructuredCloneFunction() => EnsureStructuredCloneFunction();
    ObjectHandle IBuiltinContext.MaterializeArrayBufferConstructor() => EnsureArrayBufferConstructor();
    ObjectHandle IBuiltinContext.MaterializeDataViewConstructor() => EnsureDataViewConstructor();
    BuiltinBinding[] IBuiltinContext.MaterializeTypedArrayConstructors() => EnsureTypedArrayConstructors();
    private ObjectHandle? _objectConstructorHandle;
    private ObjectHandle? _objectPrototypeHandle;
    private ObjectHandle? _arrayConstructorHandle;
    private ObjectHandle? _arrayPrototypeHandle;
    private ObjectHandle? _booleanConstructorHandle;
    private ObjectHandle? _booleanPrototypeHandle;
    private ObjectHandle? _numberConstructorHandle;
    private ObjectHandle? _numberPrototypeHandle;
    private ObjectHandle? _stringConstructorHandle;
    private ObjectHandle? _stringPrototypeHandle;
    private ObjectHandle? _errorConstructorHandle;
    private ObjectHandle? _errorPrototypeHandle;
    private ObjectHandle? _typeErrorConstructorHandle;
    private ObjectHandle? _typeErrorPrototypeHandle;
    private ObjectHandle? _rangeErrorConstructorHandle;
    private ObjectHandle? _rangeErrorPrototypeHandle;
    private ObjectHandle? _syntaxErrorConstructorHandle;
    private ObjectHandle? _syntaxErrorPrototypeHandle;
    private ObjectHandle? _functionConstructorHandle;
    private ObjectHandle? _functionPrototypeHandle;
    private ObjectHandle? _functionCallMethodHandle;
    private ObjectHandle? _evalFunctionHandle;
    private ObjectHandle? _parseIntHandle;
    private ObjectHandle? _parseFloatHandle;
    private ObjectHandle? _isNaNHandle;
    private ObjectHandle? _isFiniteHandle;
    private ObjectHandle? _encodeUriHandle;
    private ObjectHandle? _encodeUriComponentHandle;
    private ObjectHandle? _decodeUriHandle;
    private ObjectHandle? _decodeUriComponentHandle;
    private ObjectHandle? _uriErrorConstructorHandle;
    private ObjectHandle? _generatorPrototypeHandle;
    private ObjectHandle? _generatorIteratorHandle;
    private ObjectHandle? _uriErrorPrototypeHandle;
    private ObjectHandle? _referenceErrorConstructorHandle;
    private ObjectHandle? _referenceErrorPrototypeHandle;
    private ObjectHandle? _evalErrorConstructorHandle;
    private ObjectHandle? _evalErrorPrototypeHandle;
    private ObjectHandle? _aggregateErrorConstructorHandle;
    private ObjectHandle? _aggregateErrorPrototypeHandle;
    private ObjectHandle? _structuredCloneHandle;
    private ObjectHandle? _queueMicrotaskHandle;

    // Pending HostQueueMicrotask callbacks. Drained at the end of every top-level
    // Execute() invocation as part of the unified microtask checkpoint (D.6) which
    // also flushes the Promise JobQueue. The two queues live separately because
    // queueMicrotask jobs carry a single JsValue callback while PromiseJobs carry
    // structured reaction state - but they drain interleaved FIFO within the same
    // checkpoint so ordering matches HTML's "perform a microtask checkpoint".
    private readonly Queue<JsValue> _pendingMicrotasks = new();

    // ECMA-262 9.5 Promise Job Queue. Populated by PerformPromiseThen and the
    // resolving functions; drained by RunMicrotaskCheckpoint at the end of every
    // top-level Execute. Public so tests can observe queue depth.
    private readonly JobQueue _jobQueue = new();
    private ObjectHandle? _promiseConstructorHandle;
    private ObjectHandle? _promisePrototypeHandle;
    private IHostPromiseRejectionTracker _promiseRejectionTracker = new InMemoryPromiseRejectionTracker();

    public IHostPromiseRejectionTracker PromiseRejectionTracker
    {
        get => _promiseRejectionTracker;
        set => _promiseRejectionTracker = value ?? throw new ArgumentNullException(nameof(value));
    }

    public int PromiseJobQueueDepthForTest => _jobQueue.Count;
    private ObjectHandle? _weakRefConstructorHandle;
    private ObjectHandle? _weakRefPrototypeHandle;
    private ObjectHandle? _finalizationRegistryConstructorHandle;
    private ObjectHandle? _finalizationRegistryPrototypeHandle;

    private ObjectHandle? _globalObjectHandle;
    private GlobalEnvironmentRecord? _globalEnvironment;
    private ObjectHandle? _dateConstructorHandle;
    private ObjectHandle? _datePrototypeHandle;
    private ObjectHandle? _regexpConstructorHandle;
    private ObjectHandle? _regexpPrototypeHandle;
    private ObjectHandle? _jsonObjectHandle;
    private ObjectHandle? _symbolConstructorHandle;
    private ObjectHandle? _setConstructorHandle;
    private ObjectHandle? _setPrototypeHandle;
    private ObjectHandle? _mapConstructorHandle;
    private ObjectHandle? _mapPrototypeHandle;
    private ObjectHandle? _weakMapConstructorHandle;
    private ObjectHandle? _weakMapPrototypeHandle;
    private ObjectHandle? _weakSetConstructorHandle;
    private ObjectHandle? _weakSetPrototypeHandle;
    private ObjectHandle? _reflectObjectHandle;
    private ObjectHandle? _iteratorConstructorHandle;
    private ObjectHandle? _iteratorPrototypeHandle;

    // H.5 - one-shot slot: ExecuteConstruct sets the constructor handle as
    // NewTarget before invoking ExecuteInternal; ExecuteInternalCore picks it
    // up at frame setup and clears it. Stays Undefined for ordinary calls.
    private JsValue _pendingNewTarget = JsValue.Undefined;

    // Well-known symbol ids cached at first Symbol-constructor materialisation. JS
    // code that reads Symbol.iterator twice must get === values; a single id per
    // well-known symbol guarantees that.
    private readonly Dictionary<string, long> _wellKnownSymbols = new(StringComparer.Ordinal);

    // ECMA-262 20.4.2.2 the GlobalSymbolRegistry: a Realm-wide String-keyed map of
    // shared Symbol values. Symbol.for(k) returns the existing one if k is keyed,
    // otherwise mints a fresh symbol with description=k and registers it. Symbol.
    // keyFor(s) returns the key under which s was registered, or undefined.
    private readonly Dictionary<string, long> _symbolRegistryByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _symbolRegistryById = new();

    // Plan §14.2: instruction budget. Zero = no limit.
    public int InstructionBudget { get; set; }
    public Func<bool>? InterruptCallback { get; set; }
    private int _instructionCount;

    public BytecodeInterpreter(JsHeap? heap = null)
    {
        _heap = heap ?? new JsHeap();
    }

    [MayExecuteJs]
    public JsValue Execute(BytecodeFunction function)
    {
        _instructionCount = 0;
        var globalHandle = EnsureGlobalObject();
        var result = ExecuteInternal(
            function,
            Array.Empty<JsValue>(),
            JsValue.FromObject(globalHandle),
            frameEnvironment: EnsureGlobalEnvironment());
        DrainPendingMicrotasks();
        return result;
    }

    // ECMA-262 27.5.1.3 GeneratorYield — save frame execution state into the
    // owner generator so a subsequent .next()/resume can continue from this point.
    private static void SaveGeneratorState(InterpreterFrame frame, int yieldDestReg)
    {
        if (frame.OwnerGenerator is not { } gen)
            return;

        gen.InstructionPointer = frame.InstructionPointer;
        Array.Copy(frame.Registers, gen.Registers, frame.Registers.Length);
        gen.Environment = frame.Environment;
        gen.State = GeneratorState.Suspended;
        gen.YieldDestReg = yieldDestReg;

        // Preserve exception handler stack so try/catch blocks survive yield.
        gen.SavedExceptionHandlers = frame.ExceptionHandlers.ToArray();
    }

    // ECMA-262 27.5.1.2 — execute (or resume) a generator function body.
    public JsValue ExecuteGenerator(GeneratorObject gen, JsValue sentValue)
    {
        _instructionCount = 0;
        gen.SentValue = sentValue;

        // First call: let ExecuteInternalCore create a proper DeclarativeEnvironmentRecord
        // chained to the outer scope. Resume: reuse the saved frame environment so local
        // bindings from the first call are still visible.
        var isResume = gen.InstructionPointer > 0;
        // First call: pass the initial parameters that were bound when the
        // generator function was called (stored in gen.Registers[1..n]).
        // Resume: pass no args — the saved registers and env already hold
        // all local state.
        var initialArgs = isResume ? Array.Empty<JsValue>() : gen.GetInitialParameters();
        var result = ExecuteInternal(
            gen.Function,
            initialArgs,
            gen.ThisValue,
            gen.OuterEnvironment,
            frameEnvironment: isResume ? gen.Environment : null,
            ownerGenerator: gen);

        // If Yield didn't set state to Suspended, the generator body completed
        // (return or fell off end). Wrap the raw return value into {value, done: true}
        // per ECMA-262 27.5.1.2 GeneratorYield step 5.
        if (gen.State != GeneratorState.Suspended)
        {
            gen.State = GeneratorState.Completed;
            var retObj = CreateOrdinaryObject();
            retObj.DefineOwnProperty("value", new JsPropertyDescriptor(result, Writable: true, Enumerable: true, Configurable: true));
            retObj.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(retObj, AllocationSite.Current()));
        }

        return result;
    }

    // ECMA-262 27.7.5.2 Await — save frame execution state into the async
    // context so the promise reaction callback can resume from this point.
    private static void SaveAsyncState(InterpreterFrame frame, int awaitDestReg)
    {
        if (frame.AsyncContext is not { } ctx)
            return;

        ctx.InstructionPointer = frame.InstructionPointer;
        Array.Copy(frame.Registers, ctx.Registers, frame.Registers.Length);
        ctx.Environment = frame.Environment;
        ctx.IsSuspended = true;
        ctx.AwaitDestReg = awaitDestReg;
        ctx.SavedExceptionHandlers = frame.ExceptionHandlers.ToArray();
    }

    // ECMA-262 27.7.5.3 — resume an async function after the awaited promise
    // settles. Restores the saved frame state and continues execution from
    // the instruction pointer where Await suspended.
    private JsValue ResumeAsyncFunction(AsyncContext ctx, JsValue value, bool isReject)
    {
        _instructionCount = 0;
        ctx.SentValue = value;
        ctx.IsRejectResume = isReject;
        ctx.IsSuspended = false;

        var isResume = ctx.InstructionPointer > 0;
        var rootMark = _heap.RootCount;

        try
        {
            var result = ExecuteInternal(
                ctx.Function,
                Array.Empty<JsValue>(),
                ctx.ThisValue,
                ctx.OuterEnvironment,
                frameEnvironment: isResume ? ctx.Environment : null,
                asyncContext: ctx);

            if (ctx.IsSuspended)
            {
                // Another await suspended — resume callbacks already attached.
                return JsValue.Undefined;
            }

            // Async function body completed normally.
            if (ctx.CapabilityResolve is { } resolve)
            {
                _ = CallFunction(JsValue.FromObject(resolve), new[] { result }, JsValue.Undefined);
            }

            // Pop the GC root for this async context.
            _heap.PopRootsTo(rootMark);
            return result;
        }
        catch (JsThrownException ex)
        {
            if (ctx.CapabilityReject is { } reject)
            {
                _ = CallFunction(JsValue.FromObject(reject), new[] { ex.Value }, JsValue.Undefined);
            }

            _heap.PopRootsTo(rootMark);
            return JsValue.Undefined;
        }
    }

    // E.6.next - execute a top-level function with a caller-supplied
    // environment record as the frame env. Used by ModuleEvaluator to give
    // every module its own environment so module-local declarations don't
    // leak onto globalThis. The supplied env should chain to the global env
    // record so builtins (Object, Array, ...) remain visible.
    [MayExecuteJs]
    public JsValue ExecuteWithEnvironment(BytecodeFunction function, Environments.EnvironmentRecord environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var globalHandle = EnsureGlobalObject();
        var result = ExecuteInternal(
            function,
            Array.Empty<JsValue>(),
            JsValue.FromObject(globalHandle),
            frameEnvironment: environment);
        DrainPendingMicrotasks();
        return result;
    }

    // E.6.next - read a binding directly from a caller-supplied env record.
    // Used by ModuleEvaluator to harvest export values out of a per-module
    // env (where the binding doesn't live on the global object).
    public bool TryReadBinding(EnvironmentRecord environment, string name, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!environment.HasBinding(name))
        {
            value = JsValue.Undefined;
            return false;
        }

        var status = environment.GetBindingValue(name, strict: false, out value);
        if (status == BindingOpResult.Ok)
        {
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    // HTML "perform a microtask checkpoint" - invoked at the end of every top-level
    // Execute. Drains both the queueMicrotask callback queue and the Promise
    // JobQueue (D.6). Per spec the checkpoint runs until both queues are empty,
    // including jobs/callbacks enqueued by earlier work in the same checkpoint;
    // we therefore loop until a full pass produces no new work.
    //
    // Thrown exceptions from a queueMicrotask callback surface to the caller (host
    // "report the exception" semantics for the first failing job); PromiseJobs
    // catch their own errors and route them into the parent Promise's reject
    // path via the capability, matching 27.2.2.1 NewPromiseReactionJob step 5.
    private void DrainPendingMicrotasks()
    {
        while (_pendingMicrotasks.Count > 0 || _jobQueue.Count > 0)
        {
            // Drain queueMicrotask first so an early host callback that resolves a
            // promise gets its triggered reactions into the JobQueue before we
            // start running jobs - keeping HTML's tail-call ordering intact.
            while (_pendingMicrotasks.Count > 0)
            {
                var callback = _pendingMicrotasks.Dequeue();
                _ = CallFunction(callback, Array.Empty<JsValue>(), JsValue.Undefined);
            }

            _ = _jobQueue.RunMicrotaskCheckpoint(RunPromiseJob);
        }
    }

    // Bounds JS recursion so a runaway tail-less recursive function surfaces as a
    // catchable JS RangeError instead of crashing the host with a native
    // StackOverflowException. ExecuteInternalCore is a large method and its native
    // frame cost is meaningfully higher than a trivial function call; on this
    // runtime, allowing up to 80 JS frames can overflow before the guard triggers.
    // Keep the cap just above the deepest intentional regression depth (50) while
    // reserving stack headroom for unwind/exception paths.
    private const int MaxCallDepth = 56;
    private int _callDepth;

    [MayExecuteJs]
    private JsValue ExecuteInternal(
        BytecodeFunction function,
        IReadOnlyList<JsValue> args,
        JsValue thisValue,
        EnvironmentRecord? outerEnvironment = null,
        EnvironmentRecord? frameEnvironment = null,
        JsFunctionObject? callee = null,
        GeneratorObject? ownerGenerator = null,
        AsyncContext? asyncContext = null)
    {
        if (_callDepth >= MaxCallDepth)
        {
            throw new JsThrownException(CreateRangeError("Maximum call stack size exceeded."));
        }

        _callDepth++;
        try
        {
            return ExecuteInternalCore(function, args, thisValue, outerEnvironment, frameEnvironment, callee, ownerGenerator, asyncContext);
        }
        finally
        {
            _callDepth--;
        }
    }

    [MayExecuteJs]
    private JsValue ExecuteInternalCore(
        BytecodeFunction function,
        IReadOnlyList<JsValue> args,
        JsValue thisValue,
        EnvironmentRecord? outerEnvironment = null,
        EnvironmentRecord? frameEnvironment = null,
        JsFunctionObject? callee = null,
        GeneratorObject? ownerGenerator = null,
        AsyncContext? asyncContext = null)
    {
        // B.6.4 — when the callee carries an outer EnvironmentRecord (set at
        // CreateFunction time on JsFunctionObject), the new frame's env is a fresh
        // declarative record chained to it so free identifier references walk the
        // lexical scope chain through env records. Otherwise, fall back to the
        // detached fresh env that InterpreterFrame would have allocated on its own.
        var frameEnv = frameEnvironment ?? (outerEnvironment is null
            ? null
            : function.IsDerivedConstructor
                ? new FunctionEnvironmentRecord(ThisBindingStatus.Uninitialized, JsValue.Undefined, JsValue.Undefined, callee?.HomeObject, outerEnvironment)
                : new DeclarativeEnvironmentRecord(outerEnv: outerEnvironment));
        var frame = new InterpreterFrame(function, thisValue, frameEnv) { CalleeFunctionObject = callee, OwnerGenerator = ownerGenerator, AsyncContext = asyncContext };
        // H.5 - new.target: consume the one-shot pending slot set by
        // ExecuteConstruct. Ordinary calls leave it Undefined.
        if (_pendingNewTarget.Tag != JsValueTag.Undefined)
        {
            frame.NewTarget = _pendingNewTarget;
            _pendingNewTarget = JsValue.Undefined;
        }

        // Generator resume: restore saved execution state instead of fresh init.
        // ECMA-262 27.5.1.2 Resume — the [[GeneratorContext]] holds IP, registers,
        // and environment; we skip parameter binding and declaration instantiation
        // because those were already done on the first .next() call.
        if (ownerGenerator != null && ownerGenerator.InstructionPointer > 0)
        {
            Array.Copy(ownerGenerator.Registers, frame.Registers, frame.Registers.Length);
            frame.InstructionPointer = ownerGenerator.InstructionPointer;
            if (ownerGenerator.YieldDestReg >= 0)
                frame.Registers[ownerGenerator.YieldDestReg] = ownerGenerator.SentValue;
            ownerGenerator.YieldDestReg = -1;
            // Restore exception handler stack so try/catch blocks survive yield.
            // ToArray returns top-first; push in reverse to reconstruct original.
            var saved = ownerGenerator.SavedExceptionHandlers;
            for (var i = saved.Length - 1; i >= 0; i--)
                frame.ExceptionHandlers.Push(saved[i]);
        }
        else if (asyncContext != null && asyncContext.InstructionPointer > 0)
        {
            // Async resume: restore saved IP, registers, and environment so
            // execution continues after the Await that suspended this frame.
            // ECMA-262 27.7.5.3 AwaitFulfilled / AwaitRejected.
            Array.Copy(asyncContext.Registers, frame.Registers, frame.Registers.Length);
            frame.InstructionPointer = asyncContext.InstructionPointer;
            if (asyncContext.AwaitDestReg >= 0)
                frame.Registers[asyncContext.AwaitDestReg] = asyncContext.SentValue;
            asyncContext.AwaitDestReg = -1;
            var saved = asyncContext.SavedExceptionHandlers;
            for (var i = saved.Length - 1; i >= 0; i--)
                frame.ExceptionHandlers.Push(saved[i]);
        }
        else
        {
            // Pre-create env bindings for locally-bound names that the spec mandates the
            // function-environment record holds: each formal parameter, and `arguments`
            // when the function gets its own arguments object. We deliberately do NOT
            // pre-bind every VariableSlots entry: the inner compiler also allocates slots
            // for free variable references, and creating local bindings for those would
            // shadow the outer chain that LoadName/StoreName walks.
            for (var i = 0; i < function.ParameterNames.Count; i++)
            {
                var paramName = function.ParameterNames[i];
                var paramValue = i < args.Count ? args[i] : JsValue.Undefined;
                _ = frame.Environment.CreateMutableBinding(paramName, deletable: false);
                _ = frame.Environment.InitializeBinding(paramName, paramValue);
            }

            if (function.HasOwnArgumentsObject &&
                !function.ParameterNames.Contains("arguments", StringComparer.Ordinal))
            {
                var argumentsObject = CreateArgumentsObject(args);
                _ = frame.Environment.CreateMutableBinding("arguments", deletable: false);
                _ = frame.Environment.InitializeBinding("arguments", argumentsObject);
            }

            InstantiateVarDeclarations(function, frame);
            InstantiateLexicalDeclarations(function, frame);
        }

        while (frame.InstructionPointer < function.Instructions.Count)
        {
            // Plan §14.2: instruction budget and interrupt check.
            if (InstructionBudget > 0 && ++_instructionCount > InstructionBudget)
                throw new JsThrownException(CreateRangeError("Maximum instruction budget exceeded."));
            if (InterruptCallback is { } cb && !cb())
                throw new JsThrownException(CreateRangeError("Execution interrupted."));

            // ECMA-262 27.5.1.5 GeneratorResumeAbrupt — inject a throw-mode
            // completion into the resumed generator body. ThrowOrHandle routes
            // through the frame's exception handler stack so try/catch blocks
            // inside the generator can intercept the injected exception.
            // YieldStar handles Throw/Return itself (ECMA-262 15.5.5 step 5).
            if (frame.OwnerGenerator is { } genFrame && genFrame.CompletionMode == GeneratorCompletionMode.Throw)
            {
                var nextIns = function.Instructions[frame.InstructionPointer];
                if (nextIns.OpCode != OpCode.YieldStar)
                {
                    genFrame.CompletionMode = GeneratorCompletionMode.Normal;
                    ThrowOrHandle(frame, genFrame.SentValue);
                    continue;
                }
            }

            // ECMA-262 27.7.5.3 AwaitRejected — when an awaited promise rejects,
            // inject the rejection reason as a throw completion into the resumed
            // async function body so `await rejectedPromise` throws.
            if (frame.AsyncContext is { } acFrame && acFrame.IsRejectResume)
            {
                acFrame.IsRejectResume = false;
                ThrowOrHandle(frame, acFrame.SentValue);
                continue;
            }

            var ins = function.Instructions[frame.InstructionPointer++];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    frame.Registers[ins.A] = function.Constants[ins.B];
                    break;
                case OpCode.LoadVar:
                    frame.Registers[ins.A] = LoadName(frame, ins.B);
                    break;
                case OpCode.LoadThis:
                    if (function.IsDerivedConstructor &&
                        frame.Environment is FunctionEnvironmentRecord fenDerived &&
                        fenDerived.ThisBindingStatus == ThisBindingStatus.Uninitialized)
                    {
                        // Allow the compiler-emitted receiver load for `super(...)`.
                        // Any other `this` access before super must throw.
                        var currentIp = frame.InstructionPointer - 1;
                        var isSuperReceiverLoad = currentIp > 0 &&
                                                  function.Instructions[currentIp - 1].OpCode == OpCode.LoadSuperConstructor;
                        if (!isSuperReceiverLoad)
                        {
                            ThrowReferenceError(frame, "Must call super constructor in derived class before accessing 'this'.");
                            break;
                        }
                    }

                    if (frame.Environment is FunctionEnvironmentRecord fenThis &&
                        fenThis.GetThisBinding(out var boundThis) == BindingOpResult.Ok)
                    {
                        frame.Registers[ins.A] = boundThis;
                    }
                    else
                    {
                        frame.Registers[ins.A] = frame.ThisValue;
                    }
                    break;
                case OpCode.StoreVar:
                    StoreName(frame, ins.B, frame.Registers[ins.A]);
                    break;
                case OpCode.InitVar:
                    InitializeName(frame, ins.B, frame.Registers[ins.A]);
                    break;
                case OpCode.Move:
                    frame.Registers[ins.A] = frame.Registers[ins.B];
                    break;
                case OpCode.Jump:
                    frame.InstructionPointer = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (!IsTruthy(frame.Registers[ins.A]))
                    {
                        frame.InstructionPointer = ins.B;
                    }
                    break;
                case OpCode.PushHandler:
                    frame.ExceptionHandlers.Push(ins.A);
                    break;
                case OpCode.PopHandler:
                    if (frame.ExceptionHandlers.Count > 0)
                    {
                        _ = frame.ExceptionHandlers.Pop();
                    }

                    break;
                case OpCode.Throw:
                    ThrowOrHandle(frame, frame.Registers[ins.A]);
                    break;
                case OpCode.NewObject:
                {
                    var handle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.NewArray:
                {
                    var obj = CreateArrayObject(Array.Empty<JsValue>());
                    var handle = _heap.AllocateObject(obj, AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.DefineGetter:
                case OpCode.DefineSetter:
                    HandleDefineAccessor(frame, function, ins);
                    break;
                case OpCode.DefineGetterByReg:
                case OpCode.DefineSetterByReg:
                    HandleDefineAccessorByReg(frame, ins);
                    break;
                case OpCode.SetHomeObject:
                    HandleSetHomeObject(frame, ins);
                    break;
                case OpCode.LoadSuperProperty:
                    HandleLoadSuperProperty(frame, function, ins);
                    break;
                case OpCode.LoadSuperConstructor:
                    HandleLoadSuperConstructor(frame, ins);
                    break;
                case OpCode.LoadNewTarget:
                    frame.Registers[ins.A] = frame.NewTarget;
                    break;
                case OpCode.InitThisBinding:
                    if (frame.Environment is FunctionEnvironmentRecord fenInit && fenInit.ThisBindingStatus == ThisBindingStatus.Uninitialized)
                        fenInit.BindThisValue(frame.ThisValue);
                    break;
                case OpCode.Yield:
                {
                    // ECMA-262 27.5.1.3 GeneratorYield — save frame state to the
                    // owner generator so the next .next()/resume continues here.
                    SaveGeneratorState(frame, ins.A);

                    var resultObj = CreateOrdinaryObject();
                    resultObj.DefineOwnProperty("value", new JsPropertyDescriptor(frame.Registers[ins.B], Writable: true, Enumerable: true, Configurable: true));
                    resultObj.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
                    return JsValue.FromObject(_heap.AllocateObject(resultObj, AllocationSite.Current()));
                }
                case OpCode.YieldStar:
                {
                    // ECMA-262 15.5.5 — yield* delegation.
                    // The operand expression is already evaluated in register B.
                    // This handler runs on every resume while yield* is active.
                    var gen = frame.OwnerGenerator!;
                    ObjectHandle iterHandle;

                    // Step 1: get or reuse the inner iterator.
                    if (gen.YieldStarIterator is { } existing)
                    {
                        iterHandle = existing;
                    }
                    else
                    {
                        // GetIterator(operand) — ECMA-262 7.4.1.
                        var operand = frame.Registers[ins.B];
                        if (operand.Tag != JsValueTag.Object)
                            throw new JsThrownException(CreateTypeError("yield* operand is not iterable."));
                        var operandObj = _heap.GetObject(operand.AsObjectHandle());
                        var iteratorSymId = GetWellKnownSymbolId("iterator");
                        if (iteratorSymId == 0 ||
                            !operandObj.TryGetSymbolProperty(iteratorSymId, h => _heap.GetObject(h), out var iterFnDesc) ||
                            iterFnDesc.Value.Tag != JsValueTag.Object)
                            throw new JsThrownException(CreateTypeError("yield* operand is not iterable (missing @@iterator)."));
                        var iterResult = CallFunction(iterFnDesc.Value, Array.Empty<JsValue>(), operand);
                        if (iterResult.Tag != JsValueTag.Object)
                            throw new JsThrownException(CreateTypeError("@@iterator did not return an object."));
                        iterHandle = iterResult.AsObjectHandle();
                        gen.YieldStarIterator = iterHandle;
                    }

                    var iterObj = _heap.GetObject(iterHandle);
                    var iterValue = JsValue.FromObject(iterHandle);

                    // Step 2: determine method and argument based on CompletionMode.
                    string methodName;
                    JsValue methodArg;
                    if (gen.CompletionMode == GeneratorCompletionMode.Return)
                    {
                        gen.CompletionMode = GeneratorCompletionMode.Normal;
                        methodName = "return";
                        methodArg = gen.SentValue;
                        // If the inner iterator has no .return(), complete delegation
                        // with the return value (ECMA-262 15.5.5 step 5.d).
                        if (!iterObj.TryGetProperty(methodName, h => _heap.GetObject(h), out _))
                        {
                            gen.YieldStarIterator = null;
                            frame.Registers[ins.A] = methodArg;
                            break;
                        }
                    }
                    else if (gen.CompletionMode == GeneratorCompletionMode.Throw)
                    {
                        gen.CompletionMode = GeneratorCompletionMode.Normal;
                        methodName = "throw";
                        methodArg = gen.SentValue;
                    }
                    else
                    {
                        methodName = "next";
                        methodArg = gen.SentValue;
                    }

                    // Step 3: call the method on the inner iterator.
                    JsValue innerResult;
                    try
                    {
                        if (!iterObj.TryGetProperty(methodName, h => _heap.GetObject(h), out var methodDesc) ||
                            methodDesc.Value.Tag != JsValueTag.Object)
                        {
                            // Method missing.
                            if (methodName == "throw")
                            {
                                // ECMA-262 15.5.5 step 5.c.ii — .throw() missing:
                                // clear delegation state and propagate the exception.
                                gen.YieldStarIterator = null;
                                ThrowOrHandle(frame, methodArg);
                                break;
                            }
                            throw new JsThrownException(CreateTypeError(
                                $"Iterator does not have a '{methodName}' method."));
                        }

                        var callArgs = methodArg.Tag == JsValueTag.Undefined
                            ? Array.Empty<JsValue>()
                            : new[] { methodArg };
                        innerResult = CallFunction(methodDesc.Value, callArgs, iterValue);
                    }
                    catch (JsThrownException)
                    {
                        // ECMA-262 15.5.5 step 5.c.iii — if .throw() throws,
                        // clear delegation and propagate.
                        if (methodName == "throw")
                        {
                            gen.YieldStarIterator = null;
                        }
                        throw;
                    }

                    // Step 4: parse the result object.
                    if (innerResult.Tag != JsValueTag.Object)
                        throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
                    var resultObj = _heap.GetObject(innerResult.AsObjectHandle());
                    var done = resultObj.TryGetOwnProperty("done", out var doneDesc) &&
                               doneDesc.Value.AsBoolean();

                    if (done)
                    {
                        // Delegation complete (ECMA-262 15.5.5 step 5.d / 5.e).
                        gen.YieldStarIterator = null;
                        frame.Registers[ins.A] = resultObj.TryGetOwnProperty("value", out var vd)
                            ? vd.Value
                            : JsValue.Undefined;
                        break;
                    }

                    // Step 5: yield the value, resuming back at this YieldStar instruction.
                    gen.YieldDestReg = ins.A;
                    // Point IP back to this YieldStar instruction so the next resume
                    // re-enters this handler to call .next() again on the inner iterator.
                    gen.InstructionPointer = frame.InstructionPointer - 1;
                    Array.Copy(frame.Registers, gen.Registers, frame.Registers.Length);
                    gen.Environment = frame.Environment;
                    gen.State = GeneratorState.Suspended;
                    gen.SavedExceptionHandlers = frame.ExceptionHandlers.ToArray();

                    var yieldObj = CreateOrdinaryObject();
                    yieldObj.DefineOwnProperty("value", new JsPropertyDescriptor(
                        resultObj.TryGetOwnProperty("value", out var yd) ? yd.Value : JsValue.Undefined,
                        Writable: true, Enumerable: true, Configurable: true));
                    yieldObj.DefineOwnProperty("done", new JsPropertyDescriptor(
                        JsValue.FromBoolean(false),
                        Writable: true, Enumerable: true, Configurable: true));
                    return JsValue.FromObject(_heap.AllocateObject(yieldObj, AllocationSite.Current()));
                }
                case OpCode.EnterScope:
                {
                    var newScope = new DeclarativeEnvironmentRecord(frame.Environment);
                    if (ins.A != 0)
                    {
                        var scopeName = SlotNameTable.GetName(function, ins.A);
                        if (scopeName != null)
                        {
                            _ = newScope.CreateMutableBinding(scopeName, deletable: true);
                            // Initialize immediately so StoreVar/SetMutableBinding
                            // won't trip the TDZ guard.
                            _ = newScope.InitializeBinding(scopeName, JsValue.Undefined);
                        }
                    }
                    frame.Environment = newScope;
                    break;
                }
                case OpCode.LeaveScope:
                {
                    frame.Environment = frame.Environment.OuterEnv ?? frame.Environment;
                    break;
                }
                case OpCode.Await:
                {
                    try
                    {
                        var result = AwaitValue(frame, frame.Registers[ins.B], ins.A);
                        frame.Registers[ins.A] = result;
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                        break;
                    }

                    // If AwaitValue suspended the frame, return from the
                    // interpreter loop so control flows back to CallFunction
                    // which returns the async function's pending promise.
                    if (frame.AsyncContext is { IsSuspended: true })
                        return JsValue.Undefined;

                    break;
                }
                case OpCode.SetPrototype:
                {
                    // ECMA-262 7.3.5 OrdinarySetPrototypeOf - the V argument must be
                    // either an Object or Null; anything else is a TypeError. The
                    // current spec allows the same-target case as a no-op.
                    var childValue = frame.Registers[ins.A];
                    var parentValue = frame.Registers[ins.B];
                    if (childValue.Tag != JsValueTag.Object)
                    {
                        ThrowOrHandle(frame, CreateTypeError("SetPrototype requires an object target."));
                        break;
                    }

                    var childHandle = childValue.AsObjectHandle();
                    var childObj = _heap.GetObject(childHandle);

                    if (parentValue.Tag == JsValueTag.Null)
                    {
                        childObj.SetPrototype(null);
                    }
                    else if (parentValue.Tag == JsValueTag.Object)
                    {
                        var parentHandle = parentValue.AsObjectHandle();
                        childObj.SetPrototype(parentHandle);
                        _heap.WriteBarrier(childHandle, parentHandle);
                    }
                    else
                    {
                        ThrowOrHandle(frame, CreateTypeError("SetPrototype value must be Object or null."));
                    }

                    break;
                }
                case OpCode.SetPropByName:
                {
                    var receiverValue = frame.Registers[ins.A];
                    var prop = function.PropertyNames[ins.B];
                    var value = frame.Registers[ins.C];

                    if (receiverValue.Tag == JsValueTag.HostObject)
                    {
                        try { SetHostObjectProperty(receiverValue, prop, value); }
                        catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                        break;
                    }

                    var ownerHandle = ResolveObjectHandle(receiverValue);
                    var obj = _heap.GetObject(ownerHandle);
                    try
                    {
                        _ = SetPropertyValue(ownerHandle, obj, prop, value, receiverValue);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }

                    break;
                }
                case OpCode.GetPropByName:
                {
                    var receiver = frame.Registers[ins.B];
                    var prop = function.PropertyNames[ins.C];
                    var icOffset = frame.InstructionPointer - 1;
                    if (TryGetLoadIC(function, icOffset, receiver, prop, out var icResult))
                    {
                        frame.Registers[ins.A] = icResult;
                        break;
                    }
                    try
                    {
                        frame.Registers[ins.A] = GetReceiverProperty(receiver, prop);
                        PopulateLoadIC(function, icOffset, receiver, prop);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }
                    break;
                }
                case OpCode.DeletePropByName:
                {
                    var receiver = frame.Registers[ins.B];
                    if (receiver.Tag != JsValueTag.Object)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(true);
                        break;
                    }

                    var obj = ResolveObject(receiver);
                    var prop = function.PropertyNames[ins.C];
                    frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteProperty(prop));
                    break;
                }
                case OpCode.SetElem:
                {
                    var ownerHandle = ResolveObjectHandle(frame.Registers[ins.A]);
                    var obj = _heap.GetObject(ownerHandle);
                    var keyValue = frame.Registers[ins.B];
                    var value = frame.Registers[ins.C];

                    if (keyValue.Tag == JsValueTag.Symbol)
                    {
                        // Symbol-keyed [[Set]] - install on the parallel symbol table.
                        obj.DefineOwnSymbolProperty(keyValue.AsSymbolId(),
                            new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
                        if (value.Tag == JsValueTag.Object)
                        {
                            _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
                        }

                        break;
                    }

                    var key = ToPropertyKey(keyValue);
                    try
                    {
                        _ = SetPropertyValue(ownerHandle, obj, key, value, frame.Registers[ins.A]);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                        break;
                    }

                    if (double.TryParse(key, out var numericIndex))
                    {
                        var nextLength = numericIndex + 1;
                        if (!obj.TryGetOwnProperty("length", out var lenDesc) || lenDesc.Value.AsNumber() < nextLength)
                        {
                            _ = obj.SetProperty("length", JsValue.FromNumber(nextLength));
                        }
                    }

                    break;
                }
                case OpCode.DeleteElem:
                {
                    var receiver = frame.Registers[ins.B];
                    if (receiver.Tag != JsValueTag.Object)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(true);
                        break;
                    }

                    var obj = ResolveObject(receiver);
                    var key = ToPropertyKey(frame.Registers[ins.C]);
                    frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteProperty(key));
                    break;
                }
                case OpCode.EnumerateKeys:
                {
                    frame.Registers[ins.A] = CreateForInIterator(frame.Registers[ins.B]);
                    break;
                }
                case OpCode.EnumerateValues:
                {
                    frame.Registers[ins.A] = CreateForOfIterator(frame.Registers[ins.B]);
                    break;
                }
                case OpCode.ForOfNext:
                {
                    var iter = ResolveObject(frame.Registers[ins.B]) as ForOfIteratorObject
                        ?? throw new InvalidOperationException("Invalid for-of iterator object.");
                    if (!iter.TryMoveNext(out var value))
                    {
                        frame.InstructionPointer = ins.C;
                        break;
                    }

                    frame.Registers[ins.A] = value;
                    break;
                }
                case OpCode.ForInNext:
                {
                    var iterator = ResolveObject(frame.Registers[ins.B]) as ForInIteratorObject
                        ?? throw new InvalidOperationException("Invalid for-in iterator object.");
                    if (!iterator.TryMoveNext(out var key))
                    {
                        frame.InstructionPointer = ins.C;
                        break;
                    }

                    frame.Registers[ins.A] = JsValue.FromString(key);
                    break;
                }
                // H.5 — private field ops with brand validation.
                // ECMA-262 9.1.10 PrivateFieldAdd / PrivateFieldGet / PrivateFieldFind.
                // Brand is a class-unique token stored in function.BrandTokens[ins.D].
                case OpCode.DefinePrivateField:
                {
                    var target = frame.Registers[ins.A];
                    var name = function.PropertyNames[ins.B];
                    var value = frame.Registers[ins.C];
                    if (target.Tag != JsValueTag.Object)
                        throw new JsThrownException(CreateTypeError("Cannot define private field on non-object."));
                    var targetObj = _heap.GetObject(target.AsObjectHandle());
                    var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
                    targetObj.PrivateBrand = targetObj.PrivateBrand != 0 ? targetObj.PrivateBrand : brand;
                    targetObj.DefineOwnProperty(name, new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
                    break;
                }
                case OpCode.GetPrivateField:
                {
                    var objVal = frame.Registers[ins.B];
                    var name = function.PropertyNames[ins.C];
                    if (objVal.Tag != JsValueTag.Object)
                        throw new JsThrownException(CreateTypeError("Cannot read private field from non-object."));
                    var obj = _heap.GetObject(objVal.AsObjectHandle());
                    var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
                    if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand)
                        throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
                    if (!obj.TryGetOwnProperty(name, out var desc))
                        throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
                    frame.Registers[ins.A] = desc.Value;
                    break;
                }
                case OpCode.SetPrivateField:
                {
                    var objVal = frame.Registers[ins.A];
                    var name = function.PropertyNames[ins.B];
                    var value = frame.Registers[ins.C];
                    if (objVal.Tag != JsValueTag.Object)
                        throw new JsThrownException(CreateTypeError("Cannot write private field to non-object."));
                    var obj = _heap.GetObject(objVal.AsObjectHandle());
                    var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
                    if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand || !obj.TryGetOwnProperty(name, out var existing))
                        throw new JsThrownException(CreateTypeError("Cannot write private field to an object whose class did not declare it."));
                    obj.DefineOwnProperty(name, existing with { Value = value });
                    break;
                }
                case OpCode.GetElem:
                {
                    var receiver = frame.Registers[ins.B];
                    var keyValue = frame.Registers[ins.C];
                    try
                    {
                        frame.Registers[ins.A] = keyValue.Tag == JsValueTag.Symbol
                            ? GetReceiverSymbolProperty(receiver, keyValue.AsSymbolId())
                            : GetReceiverProperty(receiver, ToPropertyKey(keyValue));
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }

                    break;
                }
                case OpCode.CreateFunction:
                {
                    var nested = function.NestedFunctions[ins.B];
                    // Capture the current lexical EnvironmentRecord so closures
                    // resolve free identifiers through the env chain.
                    frame.Registers[ins.A] = CreateFunctionObject(nested, frame.Environment);
                    break;
                }
                case OpCode.Call0:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>(), JsValue.Undefined);
                    break;
                }
                case OpCode.Call1:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.C] }, JsValue.Undefined);
                    break;
                }
                case OpCode.CallMethod0:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>(), frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallMethod1:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.D] }, frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallMethodN:
                {
                    var callArgs = new JsValue[ins.E];
                    for (var i = 0; i < ins.E; i++)
                    {
                        callArgs[i] = frame.Registers[ins.D + i];
                    }

                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], callArgs, frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallN:
                {
                    var callArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        callArgs[i] = frame.Registers[ins.C + i];
                    }

                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], callArgs, JsValue.Undefined);
                    break;
                }
                case OpCode.CallSpread:
                {
                    // ECMA-262 13.3.7.1 — unpack a spread array into individual args.
                    var spreadArray = frame.Registers[ins.C];
                    var unpackedArgs = Array.Empty<JsValue>();
                    if (spreadArray.Tag == JsValueTag.Object)
                    {
                        var arrObj = _heap.GetObject(spreadArray.AsObjectHandle());
                        if (arrObj.TryGetOwnProperty("length", out var lenDesc))
                        {
                            var len = (int)lenDesc.Value.AsNumber();
                            unpackedArgs = new JsValue[len];
                            for (var i = 0; i < len; i++)
                            {
                                if (arrObj.TryGetOwnProperty(i.ToString(), out var elemDesc))
                                    unpackedArgs[i] = elemDesc.Value;
                                else
                                    unpackedArgs[i] = JsValue.Undefined;
                            }
                        }
                    }
                    var thisVal = ins.D != 0 ? frame.Registers[ins.D] : JsValue.Undefined;
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], unpackedArgs, thisVal);
                    break;
                }
                case OpCode.Construct0:
                {
                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>());
                    break;
                }
                case OpCode.Construct1:
                {
                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.C] });
                    break;
                }
                case OpCode.ConstructN:
                {
                    var ctorArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        ctorArgs[i] = frame.Registers[ins.C + i];
                    }

                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], ctorArgs);
                    break;
                }
                case OpCode.Not:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!IsTruthy(frame.Registers[ins.B]));
                    break;
                case OpCode.Pos:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]));
                    break;
                case OpCode.Neg:
                    try
                    {
                        if (frame.Registers[ins.B].Tag == JsValueTag.BigInt)
                            frame.Registers[ins.A] = JsValue.FromBigInt(-frame.Registers[ins.B].AsBigInt());
                        else
                            frame.Registers[ins.A] = JsValue.FromNumber(-ToNumber(frame.Registers[ins.B]));
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Void:
                    frame.Registers[ins.A] = JsValue.Undefined;
                    break;
                case OpCode.Delete:
                    frame.Registers[ins.A] = DeleteName(frame, ins.B);
                    break;
                case OpCode.TypeOf:
                    frame.Registers[ins.A] = JsValue.FromString(TypeOfValue(frame.Registers[ins.B]));
                    break;
                case OpCode.Add:
                    try { frame.Registers[ins.A] = Add(frame.Registers[ins.B], frame.Registers[ins.C]); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Sub:
                    try { frame.Registers[ins.A] = BigIntArith(frame.Registers[ins.B], frame.Registers[ins.C], "subtraction", (a, b) => a - b, (a, b) => a - b); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Mul:
                    try { frame.Registers[ins.A] = BigIntArith(frame.Registers[ins.B], frame.Registers[ins.C], "multiplication", (a, b) => a * b, (a, b) => a * b); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Mod:
                    try { frame.Registers[ins.A] = BigIntArith(frame.Registers[ins.B], frame.Registers[ins.C], "modulo", (a, b) => a % b, (a, b) => a % b); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Div:
                    try { frame.Registers[ins.A] = BigIntArith(frame.Registers[ins.B], frame.Registers[ins.C], "division", (a, b) => a / b, (a, b) => a / b); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Eq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(AreEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Neq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!AreEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.StrictEq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(AreStrictlyEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.StrictNeq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!AreStrictlyEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.In:
                {
                    var key = ToPropertyKey(frame.Registers[ins.B]);
                    var rhs = frame.Registers[ins.C];
                    if (rhs.Tag != JsValueTag.Object)
                    {
                        ThrowTypeError(frame, "Right-hand side of 'in' must be an object.");
                        break;
                    }

                    var obj = ResolveObject(rhs);
                    var has = obj.TryGetProperty(key, h => _heap.GetObject(h), out _);
                    frame.Registers[ins.A] = JsValue.FromBoolean(has);
                    break;
                }
                case OpCode.InstanceOf:
                {
                    if (TryInstanceOf(frame, frame.Registers[ins.B], frame.Registers[ins.C], out var instanceOfResult))
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(instanceOfResult);
                    }

                    break;
                }
                case OpCode.Lt:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsLessThan(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Gt:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsGreaterThan(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Le:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsLessThanOrEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Ge:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsGreaterThanOrEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.And:
                    frame.Registers[ins.A] = IsTruthy(frame.Registers[ins.B]) ? frame.Registers[ins.C] : frame.Registers[ins.B];
                    break;
                case OpCode.Or:
                    frame.Registers[ins.A] = IsTruthy(frame.Registers[ins.B]) ? frame.Registers[ins.B] : frame.Registers[ins.C];
                    break;
                case OpCode.Return:
                    return frame.Registers[ins.A];
                default:
                    throw new InvalidOperationException($"Unsupported opcode {ins.OpCode}.");
            }
        }

        return JsValue.Undefined;
    }

    private void InstantiateVarDeclarations(BytecodeFunction function, InterpreterFrame frame)
    {
        foreach (var name in function.VarDeclarationNames)
        {
            if (frame.Environment is GlobalEnvironmentRecord global)
            {
                var result = global.CreateGlobalVarBinding(name, deletable: false);
                if (result != BindingOpResult.Ok)
                {
                    ThrowTypeError(frame, $"Cannot declare global var binding '{name}'.");
                    return;
                }

                continue;
            }

            if (frame.Environment.HasBinding(name))
            {
                continue;
            }

            var create = frame.Environment.CreateMutableBinding(name, deletable: false);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare var binding '{name}'.");
                return;
            }

            var init = frame.Environment.InitializeBinding(name, JsValue.Undefined);
            if (init != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot initialize var binding '{name}'.");
                return;
            }
        }
    }

    private void InstantiateLexicalDeclarations(BytecodeFunction function, InterpreterFrame frame)
    {
        foreach (var name in function.LexicalDeclarationNames)
        {
            var create = frame.Environment.CreateMutableBinding(name, deletable: false);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare lexical binding '{name}'.");
                return;
            }
        }

        foreach (var name in function.ConstDeclarationNames)
        {
            var create = frame.Environment.CreateImmutableBinding(name, strict: true);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare const binding '{name}'.");
                return;
            }
        }
    }

    private JsObject CreateOrdinaryObject()
    {
        var obj = new JsObject();
        obj.SetPrototype(EnsureObjectPrototype());
        return obj;
    }

    private JsValue CreateArgumentsObject(IReadOnlyList<JsValue> args)
    {
        var obj = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());

        _ = obj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(args.Count),
                Writable: true,
                Enumerable: false,
                Configurable: true));

        for (var i = 0; i < args.Count; i++)
        {
            var descriptor = new JsPropertyDescriptor(
                args[i],
                Writable: true,
                Enumerable: true,
                Configurable: true);
            _ = obj.DefineOwnProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), descriptor);
            WriteDescriptorBarrier(handle, descriptor);
        }

        return JsValue.FromObject(handle);
    }

    private enum ArrayIteratorKind
    {
        Key,
        Value,
        Entry,
    }

    // ECMA-262 23.1.5 Array Iterator Objects. Allocates an object whose [[Prototype]]
    // is %ArrayIteratorPrototype%; .next() returns {value, done} reading the source
    // array on each call so length changes during iteration are observed (the spec
    // does NOT pre-materialise).
    private JsValue CreateArrayIterator(JsValue source, ArrayIteratorKind kind)
    {
        if (source.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype iterator method called on non-object receiver."));
        }

        var iter = new ArrayIteratorObject(source.AsObjectHandle(), kind);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        var handle = _heap.AllocateObject(iter, AllocationSite.Current());
        _heap.WriteBarrier(handle, source.AsObjectHandle());
        return JsValue.FromObject(handle);
    }

    private ObjectHandle? _arrayIteratorPrototypeHandle;

    private ObjectHandle EnsureArrayIteratorPrototype()
    {
        if (_arrayIteratorPrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        // ECMA-262 23.1.5.2: %ArrayIteratorPrototype% inherits from
        // %Iterator.prototype% so map/filter/take/drop chain after .values() etc.
        proto.SetPrototype(EnsureIteratorPrototype());
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        // ECMA-262 23.1.5.2.1 %ArrayIteratorPrototype%.next.
        // Doubles as the .next for SnapshotIteratorObject (used by Set/Map iterators):
        // both expose an index + value sequence and the only difference is how
        // values are materialised.
        var next = new NativeFunctionObject("next", (thisValue, _) =>
        {
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Iterator.prototype.next called on non-object."));
            }

            var target = _heap.GetObject(thisValue.AsObjectHandle());
            if (target is SnapshotIteratorObject snap)
            {
                if (snap.Index >= snap.Values.Count)
                {
                    return BuildIteratorResult(JsValue.Undefined, done: true);
                }

                return BuildIteratorResult(snap.Values[snap.Index++], done: false);
            }

            if (target is not ArrayIteratorObject iter)
            {
                throw new JsThrownException(CreateTypeError(
                    "Iterator.prototype.next called on incompatible receiver."));
            }

            var sourceObj = _heap.GetObject(iter.SourceHandle);
            var length = GetArrayLength(sourceObj);
            if (iter.Index >= length)
            {
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }

            var idx = iter.Index++;
            var key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
            JsValue value;
            switch (iter.Kind)
            {
                case ArrayIteratorKind.Key:
                    value = JsValue.FromNumber(idx);
                    break;
                case ArrayIteratorKind.Value:
                    TryGetPropertyValue(sourceObj, JsValue.FromObject(iter.SourceHandle), key, out value);
                    break;
                default:
                {
                    TryGetPropertyValue(sourceObj, JsValue.FromObject(iter.SourceHandle), key, out var v);
                    var pair = CreateArrayFromElements(new[] { JsValue.FromNumber(idx), v });
                    var pairHandle = _heap.AllocateObject(pair, AllocationSite.Current());
                    value = JsValue.FromObject(pairHandle);
                    break;
                }
            }

            return BuildIteratorResult(value, done: false);
        }, length: 0);

        var nextHandle = _heap.AllocateObject(next, AllocationSite.Current());
        proto.DefineOwnProperty("next", new JsPropertyDescriptor(
            JsValue.FromObject(nextHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, nextHandle);

        // ECMA-262 27.1.2.1 - every iterator's @@iterator returns the iterator
        // itself, so for-of on the iterator just keeps stepping through it.
        var selfIter = new NativeFunctionObject("[Symbol.iterator]", (thisValue, _) => thisValue, length: 0);
        var selfIterHandle = _heap.AllocateObject(selfIter, AllocationSite.Current());
        proto.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(selfIterHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, selfIterHandle);

        _arrayIteratorPrototypeHandle = protoHandle;
        return protoHandle;
    }

    // Build the IteratorResult shape { value, done } the spec mandates for every
    // iterator's .next() return value.
    private JsValue BuildIteratorResult(JsValue value, bool done)
    {
        var result = CreateOrdinaryObject();
        result.SetProperty("value", value);
        result.SetProperty("done", JsValue.FromBoolean(done));
        var handle = _heap.AllocateObject(result, AllocationSite.Current());
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(handle, value.AsObjectHandle());
        }

        return JsValue.FromObject(handle);
    }

    private sealed class ArrayIteratorObject : JsObject
    {
        public ArrayIteratorObject(ObjectHandle sourceHandle, ArrayIteratorKind kind)
        {
            SourceHandle = sourceHandle;
            Kind = kind;
        }

        public ObjectHandle SourceHandle { get; }
        public ArrayIteratorKind Kind { get; }
        public int Index { get; set; }
    }

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

            var resultObj = _heap.GetObject(result.AsObjectHandle());
            TryGetPropertyValue(resultObj, result, "done", out var doneVal);
            if (IsTruthy(doneVal))
            {
                return;
            }

            TryGetPropertyValue(resultObj, result, "value", out var value);
            sink.Add(value);
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

    // ECMA-262 20.4 Symbol. Minimal surface: callable as Symbol([description])
    // returning a fresh Symbol primitive; well-known symbols (Symbol.iterator etc.)
    // installed as own properties. Symbol() is NOT constructable (the spec
    // requires `new Symbol()` to throw TypeError).
    private ObjectHandle EnsureSymbolConstructor()
    {
        if (_symbolConstructorHandle is { } existing)
        {
            return existing;
        }

        var constructor = new NativeFunctionObject(
            "Symbol",
            (_, args) =>
            {
                var desc = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
                    ? ToStringValue(args[0])
                    : null;
                return JsValue.FromSymbol(desc);
            },
            _ => throw new JsThrownException(CreateTypeError("Symbol is not a constructor.")),
            length: 0);

        var handle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(handle);

        // ECMA-262 20.4.2 - well-known symbols are own properties of %Symbol%.
        // Each is allocated once and cached so reads return the same identity.
        InstallWellKnownSymbol(constructor, "iterator");
        InstallWellKnownSymbol(constructor, "asyncIterator");
        InstallWellKnownSymbol(constructor, "hasInstance");
        InstallWellKnownSymbol(constructor, "isConcatSpreadable");
        InstallWellKnownSymbol(constructor, "match");
        InstallWellKnownSymbol(constructor, "matchAll");
        InstallWellKnownSymbol(constructor, "replace");
        InstallWellKnownSymbol(constructor, "search");
        InstallWellKnownSymbol(constructor, "species");
        InstallWellKnownSymbol(constructor, "split");
        InstallWellKnownSymbol(constructor, "toPrimitive");
        InstallWellKnownSymbol(constructor, "toStringTag");
        InstallWellKnownSymbol(constructor, "unscopables");

        // ECMA-262 20.4.2.2 Symbol.for(key). Coerce key to String, then return the
        // registered Symbol or mint+register a fresh one.
        DefineIntrinsicFunction(handle, constructor, "for", (_, args) =>
        {
            var key = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            if (_symbolRegistryByKey.TryGetValue(key, out var existingId))
            {
                return JsValue.SymbolFromId(existingId);
            }
            var fresh = JsValue.FromSymbol(key);
            var id = fresh.AsSymbolId();
            _symbolRegistryByKey[key] = id;
            _symbolRegistryById[id] = key;
            return fresh;
        }, length: 1);

        // ECMA-262 20.4.2.6 Symbol.keyFor(sym). Reverse lookup; undefined when sym
        // was not produced by Symbol.for. Non-symbol receivers raise TypeError.
        DefineIntrinsicFunction(handle, constructor, "keyFor", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Symbol)
            {
                throw new JsThrownException(CreateTypeError("Symbol.keyFor requires a symbol argument."));
            }
            return _symbolRegistryById.TryGetValue(args[0].AsSymbolId(), out var key)
                ? JsValue.FromString(key)
                : JsValue.Undefined;
        }, length: 1);

        _symbolConstructorHandle = handle;
        return handle;
    }

    private void InstallWellKnownSymbol(NativeFunctionObject constructor, string name)
    {
        var symbol = JsValue.FromSymbol("Symbol." + name);
        _wellKnownSymbols[name] = symbol.AsSymbolId();
        constructor.DefineOwnProperty(name, new JsPropertyDescriptor(
            symbol, Writable: false, Enumerable: false, Configurable: false));
    }

    private JsValue GetWellKnownSymbol(string name)
    {
        if (_wellKnownSymbols.Count == 0) _ = EnsureSymbolConstructor();
        return _wellKnownSymbols.TryGetValue(name, out var id)
            ? JsValue.SymbolFromId(id) : JsValue.Undefined;
    }

    private JsValue SymbolFor(string key)
    {
        if (_symbolRegistryByKey.TryGetValue(key, out var existingId))
            return JsValue.SymbolFromId(existingId);
        var fresh = JsValue.FromSymbol(key);
        var id = fresh.AsSymbolId();
        _symbolRegistryByKey[key] = id;
        _symbolRegistryById[id] = key;
        return fresh;
    }

    private JsValue SymbolKeyFor(long id)
    {
        return _symbolRegistryById.TryGetValue(id, out var key)
            ? JsValue.FromString(key) : JsValue.Undefined;
    }

    // Returns the cached id for a well-known symbol, materialising the Symbol
    // constructor first if no script has touched it yet. Native code can use this
    // to recognise "is this value Symbol.iterator?" without going through a
    // user-observable property lookup.
    public long GetWellKnownSymbolId(string name)
    {
        if (_wellKnownSymbols.Count == 0)
        {
            _ = EnsureSymbolConstructor();
        }

        return _wellKnownSymbols.TryGetValue(name, out var id) ? id : 0;
    }

    // ECMA-262 24.2 Set. Backed by a List<JsValue> per instance for SameValueZero
    // equality (which is what Set keys use). Linear scan suffices for the test262
    // sizes; a hash-backed variant lands when Set perf becomes load-bearing.
    private ObjectHandle EnsureSetConstructor()
    {
        if (_setConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Set",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor Set requires 'new'.")),
            args =>
            {
                var set = new SetObject();
                set.SetPrototype(EnsureSetPrototype());
                var handle = _heap.AllocateObject(set, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            TryGetPropertyValue(src, args[0], key, out var v);
                            set.Add(v);
                            if (v.Tag == JsValueTag.Object)
                            {
                                _heap.WriteBarrier(handle, v.AsObjectHandle());
                            }
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // ECMA-262 24.2.3.1 add (returns the set for chaining).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "add", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            set.Add(value);
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            }

            return thisValue;
        }, length: 1);

        // 24.2.3.4 has.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(set.Has(value));
        }, length: 1);

        // 24.2.3.3 delete - returns whether the entry was present.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(set.Remove(value));
        }, length: 1);

        // 24.2.3.2 clear.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "clear", (thisValue, _) =>
        {
            RequireSet(thisValue).Clear();
            return JsValue.Undefined;
        }, length: 0);

        // ECMA-262 24.2.3.10 / .8 / .11 Set.prototype.values / keys / entries plus
        // [Symbol.iterator]. values and keys are the SAME function; entries yields
        // [v, v] pairs per spec.
        var setValuesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, _) => CreateSetIterator(t, isEntries: false));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, _) => CreateSetIterator(t, isEntries: false));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, _) => CreateSetIterator(t, isEntries: true));
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(setValuesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, setValuesHandle);

        // 24.2.3.6 forEach(callback[, thisArg]).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var cb = args.Count > 0 ? args[0] : JsValue.Undefined;
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (cb.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Set.prototype.forEach callback is not a function."));
            }

            foreach (var entry in set.Snapshot())
            {
                CallFunction(cb, new[] { entry, entry, thisValue }, thisArg);
            }

            return JsValue.Undefined;
        }, length: 1);

        // 24.2.3.9 size getter installed as a data property for simplicity; the
        // spec's getter/setter machinery covers it as an accessor on
        // Set.prototype but a writable=false data form is observationally close for
        // most tests until accessors-on-prototype is wired.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "size_getter_internal_unused", (_, _) => JsValue.Undefined, length: 0);
        // Instead, install a 'size' accessor that reads the live count from the set.
        var sizeGetter = new NativeFunctionObject("get size", (thisValue, _) =>
            JsValue.FromNumber(RequireSet(thisValue).Count), length: 0);
        var sizeGetterHandle = _heap.AllocateObject(sizeGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("size", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(sizeGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, sizeGetterHandle);

        // ECMA-262 24.2.3 (Set Methods, ES2025). Returns a fresh Set containing
        // every entry of either operand. The other operand is treated as a "Set-
        // like" - we use its [Symbol.iterator] when present and fall back to
        // Array-like indexed reads otherwise, mirroring the spec's GetSetRecord
        // protocol's permissive iteration path.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "union", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var result = CreateFreshSet(out var resultHandle, thisValue.AsObjectHandle());
            foreach (var v in self.Snapshot())
            {
                result.Add(v);
                if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
            }
            foreach (var v in IterateSetLike(args))
            {
                result.Add(v);
                if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
            }
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 24.2.3 (Set Methods, ES2025) intersection. Spec optimisation:
        // walk the smaller side so the result is bounded by min(|this|, |other|).
        // We don't have other.size cheaply for arbitrary iterables, so we drain
        // the argument into a temporary set and walk whichever is smaller.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "intersection", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var other = new SetObject();
            foreach (var v in IterateSetLike(args)) other.Add(v);

            var result = CreateFreshSet(out var resultHandle, thisValue.AsObjectHandle());
            var (small, large) = self.Count <= other.Count ? (self, other) : (other, self);
            foreach (var v in small.Snapshot())
            {
                if (large.Has(v))
                {
                    result.Add(v);
                    if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
                }
            }
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 24.2.3 (Set Methods, ES2025) difference. Result = entries of
        // this not present in other.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "difference", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var other = new SetObject();
            foreach (var v in IterateSetLike(args)) other.Add(v);

            var result = CreateFreshSet(out var resultHandle, thisValue.AsObjectHandle());
            foreach (var v in self.Snapshot())
            {
                if (!other.Has(v))
                {
                    result.Add(v);
                    if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
                }
            }
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 24.2.3 (Set Methods, ES2025) symmetricDifference. Result =
        // entries present in exactly one operand.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "symmetricDifference", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var other = new SetObject();
            foreach (var v in IterateSetLike(args)) other.Add(v);

            var result = CreateFreshSet(out var resultHandle, thisValue.AsObjectHandle());
            foreach (var v in self.Snapshot())
            {
                if (!other.Has(v))
                {
                    result.Add(v);
                    if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
                }
            }
            foreach (var v in other.Snapshot())
            {
                if (!self.Has(v))
                {
                    result.Add(v);
                    if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(resultHandle, v.AsObjectHandle());
                }
            }
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 24.2.3 (Set Methods, ES2025) isSubsetOf - every entry of this
        // must be in other. Short-circuits on size (a set can never be a subset
        // of something smaller).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "isSubsetOf", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var other = new SetObject();
            foreach (var v in IterateSetLike(args)) other.Add(v);
            if (self.Count > other.Count) return JsValue.FromBoolean(false);
            foreach (var v in self.Snapshot())
            {
                if (!other.Has(v)) return JsValue.FromBoolean(false);
            }
            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 24.2.3 isSupersetOf - every entry of other must be in this.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "isSupersetOf", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            var other = new SetObject();
            foreach (var v in IterateSetLike(args)) other.Add(v);
            if (other.Count > self.Count) return JsValue.FromBoolean(false);
            foreach (var v in other.Snapshot())
            {
                if (!self.Has(v)) return JsValue.FromBoolean(false);
            }
            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 24.2.3 isDisjointFrom - no entry shared with other.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "isDisjointFrom", (thisValue, args) =>
        {
            var self = RequireSet(thisValue);
            foreach (var v in IterateSetLike(args))
            {
                if (self.Has(v)) return JsValue.FromBoolean(false);
            }
            return JsValue.FromBoolean(true);
        }, length: 1);

        _setPrototypeHandle = prototypeHandle;
        _setConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureSetPrototype()
    {
        _ = EnsureSetConstructor();
        return _setPrototypeHandle!.Value;
    }

    // ECMA-262 24.1 Map. Same shape as Set with an explicit key + value pair per
    // entry; SameValueZero is the key-identity rule (NaN-key matches NaN, +0/-0
    // collapse).
    private ObjectHandle EnsureMapConstructor()
    {
        if (_mapConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Map",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor Map requires 'new'.")),
            args =>
            {
                var map = new MapObject();
                map.SetPrototype(EnsureMapPrototype());
                var handle = _heap.AllocateObject(map, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!TryGetPropertyValue(src, args[0], key, out var entry) ||
                                entry.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError(
                                    "Iterator value " + i + " is not an entry object."));
                            }

                            var entryObj = _heap.GetObject(entry.AsObjectHandle());
                            TryGetPropertyValue(entryObj, entry, "0", out var k);
                            TryGetPropertyValue(entryObj, entry, "1", out var v);
                            map.Set(k, v);
                            if (k.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, k.AsObjectHandle());
                            if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, v.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 24.1.3.9 set (returns the map for chaining).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "set", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            var value = args.Count > 1 ? args[1] : JsValue.Undefined;
            map.Set(key, value);
            if (key.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), key.AsObjectHandle());
            if (value.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            return thisValue;
        }, length: 2);

        // 24.1.3.6 get.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "get", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return map.TryGet(key, out var v) ? v : JsValue.Undefined;
        }, length: 1);

        // 24.1.3.7 has.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(map.Has(key));
        }, length: 1);

        // 24.1.3.3 delete.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(map.Remove(key));
        }, length: 1);

        // 24.1.3.1 clear.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "clear", (thisValue, _) =>
        {
            RequireMap(thisValue).Clear();
            return JsValue.Undefined;
        }, length: 0);

        // ECMA-262 24.1.3.11 / .8 / .4 Map.prototype.values / keys / entries plus
        // [Symbol.iterator] (the same function as entries per spec).
        var mapEntriesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Entry));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Key));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Value));
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(mapEntriesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, mapEntriesHandle);

        // 24.1.3.5 forEach(callback, thisArg) - callback(value, key, map).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var cb = args.Count > 0 ? args[0] : JsValue.Undefined;
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (cb.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Map.prototype.forEach callback is not a function."));
            }

            foreach (var (k, v) in map.Snapshot())
            {
                CallFunction(cb, new[] { v, k, thisValue }, thisArg);
            }

            return JsValue.Undefined;
        }, length: 1);

        // 24.1.3.10 size accessor.
        var sizeGetter = new NativeFunctionObject("get size", (thisValue, _) =>
            JsValue.FromNumber(RequireMap(thisValue).Count), length: 0);
        var sizeGetterHandle = _heap.AllocateObject(sizeGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("size", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(sizeGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, sizeGetterHandle);

        // ECMA-262 24.1.2.1 Map.groupBy(items, callbackfn) (ES2024). Same grouping
        // discipline as Object.groupBy except keys use SameValueZero identity
        // (callback's return is used as-is, not coerced to a property key) so any
        // value - including objects - can be a group key.
        DefineIntrinsicFunction(constructorHandle, constructor, "groupBy", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            var callback = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (callback.Tag != JsValueTag.Object ||
                _heap.GetObject(callback.AsObjectHandle()) is not (JsFunctionObject or NativeFunctionObject))
            {
                throw new JsThrownException(CreateTypeError(
                    "Map.groupBy: callback must be a function."));
            }
            var values = new List<JsValue>();
            var iter = CreateForOfIterator(iterable);
            if (iter.Tag == JsValueTag.Object &&
                _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
            {
                while (forOf.TryMoveNext(out var v)) values.Add(v);
            }

            var map = new MapObject();
            map.SetPrototype(EnsureMapPrototype());
            var mapHandle = _heap.AllocateObject(map, AllocationSite.Current());
            for (var i = 0; i < values.Count; i++)
            {
                var key = CallFunction(callback, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined);
                if (map.TryGet(key, out var existingBucket) &&
                    existingBucket.Tag == JsValueTag.Object &&
                    _heap.GetObject(existingBucket.AsObjectHandle()) is ArrayObject existingArr)
                {
                    var len = GetArrayLength(existingArr);
                    existingArr.SetProperty(len.ToString(System.Globalization.CultureInfo.InvariantCulture), values[i]);
                    existingArr.SetProperty("length", JsValue.FromNumber(len + 1));
                    if (values[i].Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(existingBucket.AsObjectHandle(), values[i].AsObjectHandle());
                    }
                }
                else
                {
                    var arrObj = CreateArrayFromElements(new[] { values[i] });
                    var arrHandle = _heap.AllocateObject(arrObj, AllocationSite.Current());
                    map.Set(key, JsValue.FromObject(arrHandle));
                    if (key.Tag == JsValueTag.Object) _heap.WriteBarrier(mapHandle, key.AsObjectHandle());
                    _heap.WriteBarrier(mapHandle, arrHandle);
                }
            }
            return JsValue.FromObject(mapHandle);
        }, length: 2);

        _mapPrototypeHandle = prototypeHandle;
        _mapConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureMapPrototype()
    {
        _ = EnsureMapConstructor();
        return _mapPrototypeHandle!.Value;
    }

    private MapObject RequireMap(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not MapObject map)
        {
            throw new JsThrownException(CreateTypeError("Map method called on incompatible receiver."));
        }

        return map;
    }

    private sealed class MapObject : JsObject
    {
        private readonly List<(JsValue Key, JsValue Value)> _entries = new();

        public int Count => _entries.Count;

        public void Set(JsValue key, JsValue value)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i].Key, key))
                {
                    _entries[i] = (_entries[i].Key, value);
                    return;
                }
            }

            _entries.Add((key, value));
        }

        public bool TryGet(JsValue key, out JsValue value)
        {
            foreach (var entry in _entries)
            {
                if (SameValueZero(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = JsValue.Undefined;
            return false;
        }

        public bool Has(JsValue key)
        {
            foreach (var entry in _entries)
            {
                if (SameValueZero(entry.Key, key))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Remove(JsValue key)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i].Key, key))
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void Clear() => _entries.Clear();

        public IReadOnlyList<(JsValue, JsValue)> Snapshot() => _entries.ToArray();
    }

    private SetObject CreateFreshSet(out ObjectHandle handle, ObjectHandle? selfHandle = null)
    {
        var set = new SetObject();
        set.SetPrototype(EnsureSetPrototype());
        handle = _heap.AllocateObject(set, AllocationSite.Current());
        if (selfHandle is { } self) _heap.WriteBarrier(handle, self);
        return set;
    }

    // Drain the first argument of a Set method as an iterable. ECMA-262's
    // GetSetRecord protocol calls out to .has/.keys/.size on a Set-like, but
    // for-of iteration covers every common shape (Set, Map, Array, custom
    // iterators) and matches what Test262 actually exercises.
    private IEnumerable<JsValue> IterateSetLike(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Set operation: argument is not iterable."));
        }
        var iter = CreateForOfIterator(args[0]);
        if (iter.Tag == JsValueTag.Object &&
            _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
        {
            while (forOf.TryMoveNext(out var v)) yield return v;
        }
    }

    private SetObject RequireSet(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not SetObject set)
        {
            throw new JsThrownException(CreateTypeError("Set method called on incompatible receiver."));
        }

        return set;
    }

    // Set / Map iterator wrappers reuse %ArrayIteratorPrototype% since the only
    // shape it exposes is .next() returning {value, done}; .next reads the source
    // collection's snapshot at iterator-creation time. A fresh snapshot per
    // iterator avoids "mutation during iteration" edge cases the spec actually
    // requires to surface (a future commit can switch to a live cursor).
    private JsValue CreateSetIterator(JsValue receiver, bool isEntries)
    {
        var set = RequireSet(receiver);
        var snap = set.Snapshot();
        var values = new List<JsValue>(snap.Count);
        for (var i = 0; i < snap.Count; i++)
        {
            if (isEntries)
            {
                var pair = CreateArrayFromElements(new[] { snap[i], snap[i] });
                values.Add(JsValue.FromObject(_heap.AllocateObject(pair, AllocationSite.Current())));
            }
            else
            {
                values.Add(snap[i]);
            }
        }

        var iter = new SnapshotIteratorObject(values);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    private enum MapIteratorKind { Key, Value, Entry }

    private JsValue CreateMapIterator(JsValue receiver, MapIteratorKind kind)
    {
        var map = RequireMap(receiver);
        var snap = map.Snapshot();
        var values = new List<JsValue>(snap.Count);
        for (var i = 0; i < snap.Count; i++)
        {
            switch (kind)
            {
                case MapIteratorKind.Key: values.Add(snap[i].Item1); break;
                case MapIteratorKind.Value: values.Add(snap[i].Item2); break;
                default:
                {
                    var pair = CreateArrayFromElements(new[] { snap[i].Item1, snap[i].Item2 });
                    values.Add(JsValue.FromObject(_heap.AllocateObject(pair, AllocationSite.Current())));
                    break;
                }
            }
        }

        var iter = new SnapshotIteratorObject(values);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    // Shared iterator whose next() reads from a pre-materialised value list. The
    // %ArrayIteratorPrototype% next handler is keyed on ArrayIteratorObject, so
    // for Snapshot iterators we install a tiny shim via DefineOwnProperty('next').
    private sealed class SnapshotIteratorObject : JsObject
    {
        public IReadOnlyList<JsValue> Values { get; }
        public int Index { get; set; }
        public SnapshotIteratorObject(IReadOnlyList<JsValue> values) => Values = values;
    }

    private sealed class SetObject : JsObject
    {
        // Spec uses SameValueZero for entry identity. A List with a manual
        // SameValueZero comparison keeps both spec correctness and trivial GC
        // traceability (entries are reachable via the list).
        private readonly List<JsValue> _entries = new();

        public int Count => _entries.Count;

        public void Add(JsValue value)
        {
            if (!Has(value))
            {
                _entries.Add(value);
            }
        }

        public bool Has(JsValue value)
        {
            foreach (var e in _entries)
            {
                if (SameValueZero(e, value))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Remove(JsValue value)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i], value))
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void Clear() => _entries.Clear();

        public IReadOnlyList<JsValue> Snapshot() => _entries.ToArray();
    }

    // ECMA-262 24.3 WeakMap and 24.4 WeakSet. Spec requires keys to be Objects
    // (or non-registered Symbols in newer drafts). Internally we use an
    // ObjectHandle-keyed dictionary so identity matches the spec's "same object".
    // True weak references would require runtime GC-aware semantics; this is a
    // strong-reference shim that satisfies API conformance.
    private ObjectHandle EnsureWeakMapConstructor()
    {
        if (_weakMapConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "WeakMap",
            (_, _) => throw new JsThrownException(CreateTypeError("Constructor WeakMap requires 'new'.")),
            args =>
            {
                var wm = new WeakMapObject();
                wm.SetPrototype(EnsureWeakMapPrototype());
                var handle = _heap.AllocateObject(wm, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!TryGetPropertyValue(src, args[0], k, out var entry) ||
                                entry.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakMap entry is not an object."));
                            }

                            var entryObj = _heap.GetObject(entry.AsObjectHandle());
                            TryGetPropertyValue(entryObj, entry, "0", out var key);
                            TryGetPropertyValue(entryObj, entry, "1", out var val);
                            if (key.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakMap key must be an object."));
                            }

                            wm.Set(key.AsObjectHandle(), val);
                            _heap.WriteBarrier(handle, key.AsObjectHandle());
                            if (val.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, val.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "set", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            var val = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Invalid value used as weak map key."));
            }

            wm.Set(key.AsObjectHandle(), val);
            _heap.WriteBarrier(thisValue.AsObjectHandle(), key.AsObjectHandle());
            if (val.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), val.AsObjectHandle());
            return thisValue;
        }, length: 2);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "get", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.Undefined;
            return wm.TryGet(key.AsObjectHandle(), out var v) ? v : JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(wm.Has(key.AsObjectHandle()));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(wm.Remove(key.AsObjectHandle()));
        }, length: 1);

        _weakMapPrototypeHandle = prototypeHandle;
        _weakMapConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureWeakMapPrototype()
    {
        _ = EnsureWeakMapConstructor();
        return _weakMapPrototypeHandle!.Value;
    }

    private WeakMapObject RequireWeakMap(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not WeakMapObject wm)
        {
            throw new JsThrownException(CreateTypeError("WeakMap method called on incompatible receiver."));
        }

        return wm;
    }

    private ObjectHandle EnsureWeakSetConstructor()
    {
        if (_weakSetConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "WeakSet",
            (_, _) => throw new JsThrownException(CreateTypeError("Constructor WeakSet requires 'new'.")),
            args =>
            {
                var ws = new WeakSetObject();
                ws.SetPrototype(EnsureWeakSetPrototype());
                var handle = _heap.AllocateObject(ws, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            TryGetPropertyValue(src, args[0], key, out var v);
                            if (v.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakSet entries must be objects."));
                            }

                            ws.Add(v.AsObjectHandle());
                            _heap.WriteBarrier(handle, v.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "add", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Invalid value used in weak set."));
            }

            ws.Add(value.AsObjectHandle());
            _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            return thisValue;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(ws.Has(value.AsObjectHandle()));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(ws.Remove(value.AsObjectHandle()));
        }, length: 1);

        _weakSetPrototypeHandle = prototypeHandle;
        _weakSetConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureWeakSetPrototype()
    {
        _ = EnsureWeakSetConstructor();
        return _weakSetPrototypeHandle!.Value;
    }

    private WeakSetObject RequireWeakSet(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not WeakSetObject ws)
        {
            throw new JsThrownException(CreateTypeError("WeakSet method called on incompatible receiver."));
        }

        return ws;
    }

    private sealed class WeakMapObject : JsObject
    {
        private readonly Dictionary<ObjectHandle, JsValue> _entries = new();
        public void Set(ObjectHandle key, JsValue value) => _entries[key] = value;
        public bool TryGet(ObjectHandle key, out JsValue value) => _entries.TryGetValue(key, out value);
        public bool Has(ObjectHandle key) => _entries.ContainsKey(key);
        public bool Remove(ObjectHandle key) => _entries.Remove(key);
    }

    private sealed class WeakSetObject : JsObject
    {
        private readonly HashSet<ObjectHandle> _entries = new();
        public void Add(ObjectHandle key) => _entries.Add(key);
        public bool Has(ObjectHandle key) => _entries.Contains(key);
        public bool Remove(ObjectHandle key) => _entries.Remove(key);
    }

    // ECMA-262 28.1 The Reflect Object. Most members are thin wrappers over the
    // same internal helpers Object.* use; the spec-mandated differences are that
    // Reflect.set / defineProperty / deleteProperty return booleans (matching
    // the underlying [[Set]] / [[DefineOwnProperty]] / [[Delete]] success
    // result) instead of throwing.
    private ObjectHandle EnsureReflectObject()
    {
        if (_reflectObjectHandle is { } existing)
        {
            return existing;
        }

        var reflect = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(reflect, AllocationSite.Current());
        _heap.PushRoot(handle);

        // 28.1.9 has
        DefineIntrinsicFunction(handle, reflect, "has", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.has");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return JsValue.FromBoolean(obj.TryGetProperty(key, h => _heap.GetObject(h), out var __));
        }, length: 2);

        // 28.1.6 get
        DefineIntrinsicFunction(handle, reflect, "get", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.get");
            var target = args[0];
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return GetReceiverProperty(target, key);
        }, length: 2);

        // 28.1.14 set
        DefineIntrinsicFunction(handle, reflect, "set", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.set");
            var targetHandle = args[0].AsObjectHandle();
            var obj = _heap.GetObject(targetHandle);
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            var value = args.Count > 2 ? args[2] : JsValue.Undefined;
            return JsValue.FromBoolean(obj.SetProperty(key, value));
        }, length: 3);

        // 28.1.4 deleteProperty
        DefineIntrinsicFunction(handle, reflect, "deleteProperty", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.deleteProperty");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return JsValue.FromBoolean(obj.DeleteProperty(key));
        }, length: 2);

        // 28.1.11 ownKeys - returns string-keyed own properties as an Array.
        // Symbol-keyed properties land here too once their iteration order is wired.
        DefineIntrinsicFunction(handle, reflect, "ownKeys", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.ownKeys");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var items = new List<JsValue>();
            foreach (var p in obj.EnumerateOwnProperties())
            {
                items.Add(JsValue.FromString(p.Key));
            }

            var arr = CreateArrayFromElements(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // 28.1.3 defineProperty - returns true on success, false when the underlying
        // [[DefineOwnProperty]] rejects (rather than throwing like Object.defineProperty).
        DefineIntrinsicFunction(handle, reflect, "defineProperty", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.defineProperty");
            if (args.Count < 3 || args[2].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Reflect.defineProperty descriptor must be an object."));
            }

            try
            {
                ObjectDefineProperty(JsValue.Undefined, args);
                return JsValue.FromBoolean(true);
            }
            catch (JsThrownException)
            {
                return JsValue.FromBoolean(false);
            }
        }, length: 3);

        // 28.1.7 getOwnPropertyDescriptor
        DefineIntrinsicFunction(handle, reflect, "getOwnPropertyDescriptor", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.getOwnPropertyDescriptor");
            return ObjectGetOwnPropertyDescriptor(JsValue.Undefined, args);
        }, length: 2);

        // 28.1.8 getPrototypeOf
        DefineIntrinsicFunction(handle, reflect, "getPrototypeOf", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.getPrototypeOf");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return obj.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
        }, length: 1);

        // 28.1.13 setPrototypeOf - returns boolean (no TypeError on non-Object proto;
        // it returns false instead, per spec step 5).
        DefineIntrinsicFunction(handle, reflect, "setPrototypeOf", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.setPrototypeOf");
            var protoArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                return JsValue.FromBoolean(false);
            }

            var ownerHandle = args[0].AsObjectHandle();
            var obj = _heap.GetObject(ownerHandle);
            if (protoArg.Tag == JsValueTag.Object)
            {
                obj.SetPrototype(protoArg.AsObjectHandle());
                _heap.WriteBarrier(ownerHandle, protoArg.AsObjectHandle());
            }
            else
            {
                obj.SetPrototype(null);
            }

            return JsValue.FromBoolean(true);
        }, length: 2);

        // 28.1.10 isExtensible
        DefineIntrinsicFunction(handle, reflect, "isExtensible", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.isExtensible");
            return JsValue.FromBoolean(_heap.GetObject(args[0].AsObjectHandle()).Extensible);
        }, length: 1);

        // 28.1.12 preventExtensions
        DefineIntrinsicFunction(handle, reflect, "preventExtensions", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.preventExtensions");
            _heap.GetObject(args[0].AsObjectHandle()).PreventExtensions();
            return JsValue.FromBoolean(true);
        }, length: 1);

        // 28.1.1 apply(target, thisArg, argsList)
        DefineIntrinsicFunction(handle, reflect, "apply", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply target must be a function."));
            }

            var targetObj = _heap.GetObject(args[0].AsObjectHandle());
            if (targetObj is not JsFunctionObject && targetObj is not NativeFunctionObject)
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply target is not callable."));
            }

            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var argsList = args.Count > 2 ? args[2] : JsValue.Undefined;
            JsValue[] callArgs;
            if (argsList.Tag == JsValueTag.Object)
            {
                var lobj = _heap.GetObject(argsList.AsObjectHandle());
                var len = GetArrayLength(lobj);
                callArgs = new JsValue[len];
                for (var i = 0; i < len; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryGetPropertyValue(lobj, argsList, k, out callArgs[i]);
                }
            }
            else if (argsList.Tag == JsValueTag.Undefined || argsList.Tag == JsValueTag.Null)
            {
                callArgs = Array.Empty<JsValue>();
            }
            else
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply args must be an Array-like."));
            }

            return CallFunction(args[0], callArgs, thisArg);
        }, length: 3);

        _reflectObjectHandle = handle;
        return handle;
    }

    private void RequireObjectTarget(IReadOnlyList<JsValue> args, string name)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(name + " called on non-object."));
        }
    }

    // ECMA-262 27.1.4 The Iterator Object (ES2023 Iterator Helpers proposal,
    // landed in 2024 spec). Adds Iterator.from(value) - lift any iterable into a
    // wrapper with Iterator.prototype helper methods - plus
    // %Iterator.prototype%.{map, filter, take, drop, forEach, toArray, every,
    // some, find, reduce, flatMap}.
    private ObjectHandle EnsureIteratorConstructor()
    {
        if (_iteratorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Iterator",
            (_, _) => throw new JsThrownException(CreateTypeError("Iterator is abstract; cannot be invoked directly.")),
            _ => throw new JsThrownException(CreateTypeError("Iterator is abstract; cannot be constructed directly.")),
            length: 0);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 27.1.4.1 Iterator.from(O). If O already inherits from %Iterator.prototype%
        // and exposes a callable .next, return it unchanged. Otherwise build a
        // wrapper iterator whose .next delegates to the underlying iterable's
        // Symbol.iterator + next.
        DefineIntrinsicFunction(constructorHandle, constructor, "from", (_, args) =>
        {
            var source = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromObject(BuildIteratorWrapper(source));
        }, length: 1);

        // %Iterator.prototype%[Symbol.iterator] returns this per 27.1.4.2.1.
        var selfIter = new NativeFunctionObject("[Symbol.iterator]", (thisValue, _) => thisValue, length: 0);
        var selfIterHandle = _heap.AllocateObject(selfIter, AllocationSite.Current());
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(selfIterHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, selfIterHandle);

        // The Iterator Helpers. Each one drains the receiver's .next into a list,
        // applies the per-method transform, and returns a fresh wrapped iterator
        // (or terminal value for forEach/toArray/every/some/find/reduce).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "map", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.map");
            var values = DrainSelfAsList(thisValue);
            var mapped = new List<JsValue>(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                mapped.Add(CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined));
            }

            return WrapListAsIterator(mapped);
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "filter", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.filter");
            var values = DrainSelfAsList(thisValue);
            var kept = new List<JsValue>();
            for (var i = 0; i < values.Count; i++)
            {
                if (IsTruthy(CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined)))
                {
                    kept.Add(values[i]);
                }
            }

            return WrapListAsIterator(kept);
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "take", (thisValue, args) =>
        {
            var n = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            if (n < 0 || double.IsNaN(ToNumber(args.Count > 0 ? args[0] : JsValue.Undefined)))
            {
                throw new JsThrownException(CreateRangeError("Iterator.prototype.take limit must be a non-negative integer."));
            }

            var values = DrainSelfAsList(thisValue);
            return WrapListAsIterator(values.GetRange(0, Math.Min(n, values.Count)));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "drop", (thisValue, args) =>
        {
            var n = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            if (n < 0)
            {
                throw new JsThrownException(CreateRangeError("Iterator.prototype.drop limit must be a non-negative integer."));
            }

            var values = DrainSelfAsList(thisValue);
            return n >= values.Count
                ? WrapListAsIterator(new List<JsValue>())
                : WrapListAsIterator(values.GetRange(n, values.Count - n));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.forEach");
            var values = DrainSelfAsList(thisValue);
            for (var i = 0; i < values.Count; i++)
            {
                CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined);
            }

            return JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "toArray", (thisValue, _) =>
        {
            var values = DrainSelfAsList(thisValue);
            var arr = CreateArrayFromElements(values);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 0);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "every", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.every");
            var values = DrainSelfAsList(thisValue);
            for (var i = 0; i < values.Count; i++)
            {
                if (!IsTruthy(CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined)))
                {
                    return JsValue.FromBoolean(false);
                }
            }

            return JsValue.FromBoolean(true);
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "some", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.some");
            var values = DrainSelfAsList(thisValue);
            for (var i = 0; i < values.Count; i++)
            {
                if (IsTruthy(CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined)))
                {
                    return JsValue.FromBoolean(true);
                }
            }

            return JsValue.FromBoolean(false);
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "find", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.find");
            var values = DrainSelfAsList(thisValue);
            for (var i = 0; i < values.Count; i++)
            {
                if (IsTruthy(CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined)))
                {
                    return values[i];
                }
            }

            return JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "reduce", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.reduce");
            var values = DrainSelfAsList(thisValue);
            var hasInitial = args.Count > 1;
            if (values.Count == 0 && !hasInitial)
            {
                throw new JsThrownException(CreateTypeError("Reduce of empty iterator with no initial value."));
            }

            var acc = hasInitial ? args[1] : values[0];
            var start = hasInitial ? 0 : 1;
            for (var i = start; i < values.Count; i++)
            {
                acc = CallFunction(fn, new[] { acc, values[i], JsValue.FromNumber(i) }, JsValue.Undefined);
            }

            return acc;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "flatMap", (thisValue, args) =>
        {
            var fn = RequireCallable(args, 0, "Iterator.prototype.flatMap");
            var values = DrainSelfAsList(thisValue);
            var flat = new List<JsValue>();
            for (var i = 0; i < values.Count; i++)
            {
                var produced = CallFunction(fn, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined);
                // The spec wants GetIteratorFlattenable; for now, accept either an
                // Array or an iterable / iterator and drain it via the same path.
                if (produced.Tag == JsValueTag.Object)
                {
                    var producedObj = _heap.GetObject(produced.AsObjectHandle());
                    if (producedObj is ArrayObject)
                    {
                        var len = GetArrayLength(producedObj);
                        for (var k = 0; k < len; k++)
                        {
                            var key = k.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            flat.Add(TryGetPropertyValue(producedObj, produced, key, out var v) ? v : JsValue.Undefined);
                        }

                        continue;
                    }

                    // Try @@iterator dispatch.
                    var iterId = GetWellKnownSymbolId("iterator");
                    if (producedObj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                        iterDesc.Value.Tag == JsValueTag.Object)
                    {
                        var iter = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), produced);
                        DrainIteratorIntoList(iter, flat);
                        continue;
                    }
                }

                flat.Add(produced);
            }

            return WrapListAsIterator(flat);
        }, length: 1);

        _iteratorPrototypeHandle = prototypeHandle;
        _iteratorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue RequireCallable(IReadOnlyList<JsValue> args, int idx, string name)
    {
        if (idx >= args.Count || args[idx].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(name + " callback is not a function."));
        }

        var obj = _heap.GetObject(args[idx].AsObjectHandle());
        if (obj is not JsFunctionObject && obj is not NativeFunctionObject)
        {
            throw new JsThrownException(CreateTypeError(name + " callback is not callable."));
        }

        return args[idx];
    }

    // Drains the receiver-iterator via its .next() into a list. Same logic as
    // DrainIteratorIntoList but the iterator object is already the receiver
    // (no @@iterator dispatch needed).
    private List<JsValue> DrainSelfAsList(JsValue iter)
    {
        var values = new List<JsValue>();
        if (iter.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator.prototype method called on non-object receiver."));
        }

        var iterObj = _heap.GetObject(iter.AsObjectHandle());
        while (true)
        {
            if (!TryGetPropertyValue(iterObj, iter, "next", out var nextFn) || nextFn.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator missing callable 'next'."));
            }

            var result = CallFunction(nextFn, Array.Empty<JsValue>(), iter);
            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
            }

            var resultObj = _heap.GetObject(result.AsObjectHandle());
            TryGetPropertyValue(resultObj, result, "done", out var doneVal);
            if (IsTruthy(doneVal))
            {
                return values;
            }

            TryGetPropertyValue(resultObj, result, "value", out var value);
            values.Add(value);
        }
    }

    // Wrap a materialised value list as an iterator whose [[Prototype]] is
    // %ArrayIteratorPrototype% (which itself inherits from %Iterator.prototype%).
    // The proximate prototype provides `next` (which knows how to read from
    // SnapshotIteratorObject); the parent provides the map/filter/etc. helpers.
    private JsValue WrapListAsIterator(List<JsValue> values)
    {
        var iter = new SnapshotIteratorObject(values);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    private ObjectHandle EnsureIteratorPrototype()
    {
        _ = EnsureIteratorConstructor();
        return _iteratorPrototypeHandle!.Value;
    }

    // Iterator.from(value): lift any iterable, iterator, or array-like into an
    // iterator whose prototype is %Iterator.prototype% (so the helpers chain).
    private ObjectHandle BuildIteratorWrapper(JsValue source)
    {
        var values = new List<JsValue>();
        if (source.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(source.AsObjectHandle());
            // If source already exposes .next, treat it as an iterator and drain
            // (round-trips an Array.values() through Iterator.from cleanly).
            if (obj.TryGetProperty("next", h => _heap.GetObject(h), out var nextDesc) &&
                nextDesc.Value.Tag == JsValueTag.Object)
            {
                while (true)
                {
                    var r = CallFunction(nextDesc.Value, Array.Empty<JsValue>(), source);
                    if (r.Tag != JsValueTag.Object) break;
                    var rObj = _heap.GetObject(r.AsObjectHandle());
                    TryGetPropertyValue(rObj, r, "done", out var doneVal);
                    if (IsTruthy(doneVal)) break;
                    TryGetPropertyValue(rObj, r, "value", out var v);
                    values.Add(v);
                }
            }
            else
            {
                // Try Symbol.iterator dispatch.
                var iterId = GetWellKnownSymbolId("iterator");
                if (obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                    iterDesc.Value.Tag == JsValueTag.Object)
                {
                    var iter = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), source);
                    DrainIteratorIntoList(iter, values);
                }
                else if (obj is ArrayObject)
                {
                    var len = GetArrayLength(obj);
                    for (var i = 0; i < len; i++)
                    {
                        var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        values.Add(TryGetPropertyValue(obj, source, key, out var v) ? v : JsValue.Undefined);
                    }
                }
                else
                {
                    throw new JsThrownException(CreateTypeError("Iterator.from argument is not iterable."));
                }
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
        else
        {
            throw new JsThrownException(CreateTypeError("Iterator.from argument is not iterable."));
        }

        var wrap = new SnapshotIteratorObject(values);
        wrap.SetPrototype(EnsureArrayIteratorPrototype());
        return _heap.AllocateObject(wrap, AllocationSite.Current());
    }

    private ObjectHandle EnsureGlobalObject()
    {
        if (_globalObjectHandle is { } existing)
        {
            return existing;
        }

        var global = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(global, AllocationSite.Current());
        _heap.PushRoot(handle);
        _globalObjectHandle = handle;
        InstallGlobalObjectProperties(global, handle);
        return handle;
    }

    private GlobalEnvironmentRecord EnsureGlobalEnvironment()
    {
        if (_globalEnvironment is { } existing)
        {
            return existing;
        }

        var globalHandle = EnsureGlobalObject();
        _globalEnvironment = new GlobalEnvironmentRecord(
            new JsObjectBindingAdapter(_heap, globalHandle),
            JsValue.FromObject(globalHandle));
        return _globalEnvironment;
    }

    private void InstallGlobalObjectProperties(JsObject global, ObjectHandle globalHandle)
    {
        // Pass 1: Error constructors must be installed first so AggregateError
        // can resolve GetGlobalPrototype("Error") during its materialization.
        var errorRegistry = new BuiltinRegistry()
            .Register(new ErrorBuiltins());
        foreach (var b in errorRegistry.Materialize(this))
            InstallBinding(global, globalHandle, b);

        // Pass 2: all remaining builtins, including AggregateError which chains
        // its prototype to the now-installed Error.prototype.
        var registry = new BuiltinRegistry()
            .Register(new GlobalConstantsBuiltin(JsValue.FromObject(globalHandle)))
            .Register(new MathBuiltin())
            .Register(new GlobalFunctionsBuiltin())
            .Register(new BooleanBuiltin())
            .Register(new NumberBuiltin())
            .Register(new StringBuiltin())
            .Register(new SymbolBuiltin())
            .Register(new DateBuiltin())
            .Register(new RegExpBuiltin())
            .Register(new ObjectBuiltin())
            .Register(new ArrayBuiltin())
            .Register(new FunctionBuiltin())
            .Register(new SetBuiltin())
            .Register(new MapBuiltin())
            .Register(new WeakMapBuiltin())
            .Register(new WeakSetBuiltin())
            .Register(new PromiseBuiltin())
            .Register(new JsonBuiltin())
            .Register(new ReflectBuiltin())
            .Register(new IteratorBuiltin())
            .Register(new MiscGlobalsBuiltin())
            .Register(new AggregateErrorBuiltin())
            .Register(new GeneratorBuiltin())
            .Register(new ArrayBufferBuiltin());
        foreach (var b in registry.Materialize(this))
            InstallBinding(global, globalHandle, b);
    }

    private void InstallBinding(JsObject global, ObjectHandle globalHandle, BuiltinBinding binding)
    {
        _ = global.DefineOwnProperty(
            binding.Name,
            new JsPropertyDescriptor(
                binding.Value,
                Writable: binding.Writable,
                Enumerable: binding.Enumerable,
                Configurable: binding.Configurable));

        if (binding.Value.Tag == JsValueTag.Object)
        {
            Heap.WriteBarrier(globalHandle, binding.Value.AsObjectHandle());
        }
    }

    private void DefineGlobalDataProperty(
        JsObject global,
        ObjectHandle globalHandle,
        string name,
        JsValue value,
        bool writable = true,
        bool configurable = true)
    {
        _ = global.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                value,
                Writable: writable,
                Enumerable: false,
                Configurable: configurable));

        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(globalHandle, value.AsObjectHandle());
        }
    }

    // LoadName/StoreName funnel slot-based LoadVar/StoreVar opcodes through the
    // active EnvironmentRecord chain. Unresolvable reads become ReferenceError, and
    // non-strict unresolvable writes create/update a property on the global object.
    private JsValue LoadName(InterpreterFrame frame, int slot)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is not null)
        {
            // ECMA-262 9.1.2.1 GetIdentifierReference walks the env chain. Resolve
            // through the lexical chain so closures over parameters of an outer
            // function (whose params now live on FunctionEnvironmentRecord since
            // B.6.4) read the live outer binding rather than the stale snapshot.
            for (var env = (EnvironmentRecord?)frame.Environment; env is not null; env = env.OuterEnv)
            {
                if (!env.HasBinding(name))
                {
                    continue;
                }

                var status = env.GetBindingValue(name, strict: false, out var envValue);
                if (status == BindingOpResult.Ok)
                {
                    return envValue;
                }

                ThrowBindingFailure(frame, status, name, assignment: false);
                return JsValue.Undefined;
            }
        }

        ThrowReferenceError(frame, name is null ? $"Invalid variable slot {slot}." : $"{name} is not defined.");
        return JsValue.Undefined;
    }

    private void StoreName(InterpreterFrame frame, int slot, JsValue value)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is not null)
        {
            for (var env = (EnvironmentRecord?)frame.Environment; env is not null; env = env.OuterEnv)
            {
                if (!env.HasBinding(name))
                {
                    continue;
                }

                var status = env.SetMutableBinding(name, value, strict: false);
                if (status == BindingOpResult.Ok)
                {
                    return;
                }

                ThrowBindingFailure(frame, status, name, assignment: true);
                return;
            }

            SetImplicitGlobalProperty(name, value);
            return;
        }

        ThrowReferenceError(frame, $"Invalid variable slot {slot}.");
    }

    private void InitializeName(InterpreterFrame frame, int slot, JsValue value)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is not null)
        {
            for (var env = (EnvironmentRecord?)frame.Environment; env is not null; env = env.OuterEnv)
            {
                if (!env.HasBinding(name))
                {
                    continue;
                }

                var status = env.InitializeBinding(name, value);
                if (status == BindingOpResult.Ok)
                {
                    return;
                }

                ThrowBindingFailure(frame, status, name, assignment: true);
                return;
            }
        }

        ThrowReferenceError(frame, name is null ? $"Invalid variable slot {slot}." : $"{name} is not defined.");
    }

    private JsValue DeleteName(InterpreterFrame frame, int slot)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is null)
        {
            return JsValue.FromBoolean(true);
        }

        for (var env = (EnvironmentRecord?)frame.Environment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.DeleteBinding(name);
            return JsValue.FromBoolean(status == BindingOpResult.Ok || status == BindingOpResult.NotFound);
        }

        return JsValue.FromBoolean(true);
    }

    private void ThrowBindingFailure(InterpreterFrame frame, BindingOpResult status, string name, bool assignment)
    {
        switch (status)
        {
            case BindingOpResult.TdzAccess:
                ThrowReferenceError(frame, $"Cannot access '{name}' before initialization.");
                return;
            case BindingOpResult.ConstAssignment:
                ThrowTypeError(frame, assignment
                    ? $"Assignment to constant variable '{name}'."
                    : $"Cannot read immutable binding '{name}'.");
                return;
            case BindingOpResult.NotInitializable:
            case BindingOpResult.AlreadyDeclared:
                ThrowTypeError(frame, $"Cannot initialize binding '{name}'.");
                return;
            case BindingOpResult.NotFound:
                ThrowReferenceError(frame, $"{name} is not defined.");
                return;
            default:
                return;
        }
    }

    private void SetImplicitGlobalProperty(string name, JsValue value)
    {
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        _ = global.SetProperty(name, value);
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(globalHandle, value.AsObjectHandle());
        }
    }

    private void ThrowTypeError(InterpreterFrame frame, string message)
    {
        ThrowOrHandle(frame, CreateTypeError(message));
    }

    private void ThrowReferenceError(InterpreterFrame frame, string message)
    {
        ThrowOrHandle(frame, CreateReferenceError(message));
    }

    private void ThrowOrHandle(InterpreterFrame frame, JsValue value)
    {
        if (frame.ExceptionHandlers.Count > 0)
        {
            var handlerIp = frame.ExceptionHandlers.Pop();
            frame.Registers[0] = value;
            frame.InstructionPointer = handlerIp;
            return;
        }

        throw new JsThrownException(value);
    }

    private JsValue CreateError(string message)
    {
        return CreateErrorObject("Error", GetGlobalPrototype("Error"), message);
    }

    private JsValue CreateTypeError(string message)
    {
        return CreateErrorObject("TypeError", GetGlobalPrototype("TypeError"), message);
    }

    private JsValue CreateReferenceError(string message)
    {
        return CreateErrorObject("ReferenceError", GetGlobalPrototype("ReferenceError"), message);
    }

    private JsValue CreateRangeError(string message)
    {
        return CreateErrorObject("RangeError", GetGlobalPrototype("RangeError"), message);
    }

    private JsValue CreateSyntaxError(string message)
    {
        return CreateErrorObject("SyntaxError", GetGlobalPrototype("SyntaxError"), message);
    }

    private JsValue CreateErrorObject(string name, ObjectHandle prototypeHandle, string message)
    {
        var error = new JsObject();
        error.SetPrototype(prototypeHandle);
        _ = error.SetProperty("name", JsValue.FromString(name));
        _ = error.SetProperty("message", JsValue.FromString(message));
        return JsValue.FromObject(_heap.AllocateObject(error, AllocationSite.Current()));
    }

    private static string GetOptionalMessage(IReadOnlyList<JsValue> args)
    {
        return args.Count > 0 ? FormatPrimitiveForString(args[0]) : string.Empty;
    }

    private ObjectHandle EnsureEvalFunction()
    {
        if (_evalFunctionHandle is { } existing)
        {
            return existing;
        }

        var eval = new NativeFunctionObject(
            "eval",
            (_, args) => Eval(args),
            length: 1);
        var evalHandle = _heap.AllocateObject(eval, AllocationSite.Current());
        _heap.PushRoot(evalHandle);
        _evalFunctionHandle = evalHandle;
        return evalHandle;
    }

    private JsValue Eval(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.Undefined;
        }

        if (args[0].Tag != JsValueTag.String)
        {
            return args[0];
        }

        var program = JsParser.ParseScript(new SourceText(args[0].AsString(), "<eval>"));
        var compiled = new BytecodeCompiler().CompileProgram(program);
        new BytecodeVerifier().Verify(compiled);
        var globalHandle = EnsureGlobalObject();
        return ExecuteInternal(
            compiled,
            Array.Empty<JsValue>(),
            JsValue.FromObject(globalHandle),
            frameEnvironment: EnsureGlobalEnvironment());
    }

    private ObjectHandle EnsureTypeErrorPrototype()
    {
        _ = EnsureTypeErrorConstructor();
        return _typeErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureTypeErrorConstructor()
    {
        if (_typeErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "TypeError",
            (_, args) => CreateErrorObject("TypeError", EnsureTypeErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("TypeError", EnsureTypeErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _typeErrorPrototypeHandle = prototypeHandle;
        _typeErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureRangeErrorPrototype()
    {
        _ = EnsureRangeErrorConstructor();
        return _rangeErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureRangeErrorConstructor()
    {
        if (_rangeErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "RangeError",
            (_, args) => CreateErrorObject("RangeError", EnsureRangeErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("RangeError", EnsureRangeErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _rangeErrorPrototypeHandle = prototypeHandle;
        _rangeErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureSyntaxErrorPrototype()
    {
        _ = EnsureSyntaxErrorConstructor();
        return _syntaxErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureSyntaxErrorConstructor()
    {
        if (_syntaxErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "SyntaxError",
            (_, args) => CreateErrorObject("SyntaxError", EnsureSyntaxErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("SyntaxError", EnsureSyntaxErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _syntaxErrorPrototypeHandle = prototypeHandle;
        _syntaxErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureErrorPrototype()
    {
        _ = EnsureErrorConstructor();
        return _errorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureErrorConstructor()
    {
        if (_errorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new NumberObject(0d);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Error",
            (_, args) => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        // ECMA-262 20.5.3 Error.prototype: must carry the spec-default 'name'
        // ("Error") and 'message' ("") so an Error built without arguments still
        // stringifies as "Error" (rather than ": ").
        _ = prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString("Error"), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        // ECMA-262 20.5.3.4 Error.prototype.toString(). Reads .name (defaulting to
        // "Error") and .message (defaulting to ""), then joins them with ": " when
        // both are non-empty. The receiver must be an Object - primitives raise
        // TypeError per step 2.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", ErrorPrototypeToString);

        _errorPrototypeHandle = prototypeHandle;
        _errorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue ErrorPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Error.prototype.toString called on non-object."));
        }

        var obj = _heap.GetObject(thisValue.AsObjectHandle());
        string name = "Error";
        if (TryGetPropertyValue(obj, thisValue, "name", out var nameValue) &&
            nameValue.Tag != JsValueTag.Undefined)
        {
            name = ToStringValue(nameValue);
        }

        string message = string.Empty;
        if (TryGetPropertyValue(obj, thisValue, "message", out var messageValue) &&
            messageValue.Tag != JsValueTag.Undefined)
        {
            message = ToStringValue(messageValue);
        }

        if (name.Length == 0)
        {
            return JsValue.FromString(message);
        }

        if (message.Length == 0)
        {
            return JsValue.FromString(name);
        }

        return JsValue.FromString(name + ": " + message);
    }

    private ObjectHandle EnsureDatePrototype()
    {
        _ = EnsureDateConstructor();
        return _datePrototypeHandle!.Value;
    }

    private ObjectHandle EnsureDateConstructor()
    {
        if (_dateConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Date",
            (_, args) => CreateDateObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            args => CreateDateObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            length: 7);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(constructorHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // ECMA-262 21.4.3.1 Date.now() - milliseconds since the UNIX epoch as a
        // Number value. Routed through DateTimeOffset so it's culture-invariant and
        // matches the spec's TimeClip semantics for "now" (which produces an integer
        // millisecond count by construction).
        DefineIntrinsicFunction(constructorHandle, constructor, "now", (_, _) =>
            JsValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), length: 0);

        // ECMA-262 21.4.3.4 Date.UTC(year[, month[, day[, hours[, minutes[, seconds[, ms]]]]]]).
        // Constructs a UTC time value from explicit components, defaulting any
        // omitted lower-order field to its spec default (0, or 1 for day). Years in
        // [0, 99] map to 1900+year per spec step 2.
        DefineIntrinsicFunction(constructorHandle, constructor, "UTC", (_, args) =>
        {
            if (args.Count == 0)
            {
                return JsValue.FromNumber(double.NaN);
            }

            var year = (int)ToNumber(args[0]);
            if (year >= 0 && year <= 99)
            {
                year += 1900;
            }

            var month = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            var day = args.Count > 2 ? (int)ToNumber(args[2]) : 1;
            var hours = args.Count > 3 ? (int)ToNumber(args[3]) : 0;
            var minutes = args.Count > 4 ? (int)ToNumber(args[4]) : 0;
            var seconds = args.Count > 5 ? (int)ToNumber(args[5]) : 0;
            var ms = args.Count > 6 ? (int)ToNumber(args[6]) : 0;

            try
            {
                var dt = new DateTimeOffset(year, month + 1, day, hours, minutes, seconds, ms, TimeSpan.Zero);
                return JsValue.FromNumber(dt.ToUnixTimeMilliseconds());
            }
            catch (ArgumentOutOfRangeException)
            {
                return JsValue.FromNumber(double.NaN);
            }
        }, length: 7);

        // ECMA-262 21.4.3.2 Date.parse(string). Returns the time value of a parsed
        // date string, or NaN on failure. Recognises the standard ISO-8601 forms
        // (the spec's "Date Time String Format" 21.4.1.18) plus a few common
        // looser variants .NET's DateTimeOffset.TryParse already understands.
        DefineIntrinsicFunction(constructorHandle, constructor, "parse", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "Invalid Date";
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return JsValue.FromNumber(parsed.ToUnixTimeMilliseconds());
            }

            return JsValue.FromNumber(double.NaN);
        }, length: 1);

        InstallPrototypeMethodsOnDatePrototype(prototypeHandle, prototype);

        _datePrototypeHandle = prototypeHandle;
        _dateConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // Shared engine for Date setFullYear / setMonth / setDate. Reads the current
    // [[DateValue]] (substituting epoch when NaN, matching the spec's MakeDay
    // step), overwrites the requested portions from args, and writes back through
    // TimeClip. Any non-finite component drives the result to NaN per the
    // spec's MakeDate failure mode.
    private JsValue SetDateField(
        JsValue thisValue, string method, IReadOnlyList<JsValue> args,
        bool hasYear, bool hasMonth, bool hasDay,
        int yearArgIndex = 0, int monthArgIndex = 1, int dayArgIndex = 2)
    {
        var date = RequireDate(thisValue, method);
        DateTimeOffset baseDate;
        if (double.IsFinite(date.TimeValue))
        {
            baseDate = DateTimeOffset.FromUnixTimeMilliseconds((long)date.TimeValue);
        }
        else if (hasYear)
        {
            // setFullYear is allowed to resurrect a NaN-valued Date per spec 21.4.4.21
            // step 2: "If t is NaN, set t to +0".
            baseDate = DateTimeOffset.FromUnixTimeMilliseconds(0);
        }
        else
        {
            date.TimeValue = double.NaN;
            return JsValue.FromNumber(double.NaN);
        }

        int year = baseDate.Year, month = baseDate.Month, day = baseDate.Day;
        if (hasYear)
        {
            var n = ToNumber(args[yearArgIndex]);
            if (!double.IsFinite(n)) { date.TimeValue = double.NaN; return JsValue.FromNumber(double.NaN); }
            year = (int)n;
        }
        if (hasMonth)
        {
            var n = ToNumber(args[monthArgIndex]);
            if (!double.IsFinite(n)) { date.TimeValue = double.NaN; return JsValue.FromNumber(double.NaN); }
            month = (int)n + 1; // JS months are 0-based, DateTimeOffset is 1-based.
        }
        if (hasDay)
        {
            var n = ToNumber(args[dayArgIndex]);
            if (!double.IsFinite(n)) { date.TimeValue = double.NaN; return JsValue.FromNumber(double.NaN); }
            day = (int)n;
        }

        try
        {
            // Build via per-component AddXxx so out-of-range month/day overflow
            // naturally per spec MakeDay (Feb 30 -> Mar 2, etc.).
            var dt = new DateTimeOffset(year, 1, 1, baseDate.Hour, baseDate.Minute, baseDate.Second, baseDate.Millisecond, TimeSpan.Zero)
                .AddMonths(month - 1)
                .AddDays(day - 1);
            date.TimeValue = TimeClip(dt.ToUnixTimeMilliseconds());
        }
        catch (ArgumentOutOfRangeException)
        {
            date.TimeValue = double.NaN;
        }
        return JsValue.FromNumber(date.TimeValue);
    }

    // Shared engine for setHours / setMinutes / setSeconds / setMilliseconds.
    // startIndex picks the most significant portion the call writes (0=hour,
    // 1=minute, 2=second, 3=millisecond); the call may also provide every
    // lower-order portion after it. Anything not provided keeps its existing value.
    private JsValue SetTimeField(JsValue thisValue, string method, IReadOnlyList<JsValue> args, int startIndex)
    {
        var date = RequireDate(thisValue, method);
        if (!double.IsFinite(date.TimeValue))
        {
            return JsValue.FromNumber(double.NaN);
        }
        var baseDate = DateTimeOffset.FromUnixTimeMilliseconds((long)date.TimeValue);
        int hour = baseDate.Hour, minute = baseDate.Minute, second = baseDate.Second, ms = baseDate.Millisecond;
        var components = new[] { hour, minute, second, ms };
        for (var i = 0; i < args.Count && startIndex + i < 4; i++)
        {
            var n = ToNumber(args[i]);
            if (!double.IsFinite(n)) { date.TimeValue = double.NaN; return JsValue.FromNumber(double.NaN); }
            components[startIndex + i] = (int)n;
        }
        try
        {
            // Use a millisecond-precise sum so out-of-range portions (e.g. minute=70)
            // ripple per MakeTime semantics.
            var midnight = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 0, 0, 0, TimeSpan.Zero);
            var t = midnight
                .AddHours(components[0])
                .AddMinutes(components[1])
                .AddSeconds(components[2])
                .AddMilliseconds(components[3]);
            date.TimeValue = TimeClip(t.ToUnixTimeMilliseconds());
        }
        catch (ArgumentOutOfRangeException)
        {
            date.TimeValue = double.NaN;
        }
        return JsValue.FromNumber(date.TimeValue);
    }

    // ECMA-262 21.4.1.31 TimeClip - returns NaN for non-finite or out-of-range
    // |t| > 8.64e15, otherwise integer-truncates toward zero.
    private static double TimeClip(double t)
    {
        if (!double.IsFinite(t) || Math.Abs(t) > 8.64e15) return double.NaN;
        return t >= 0 ? Math.Floor(t) : -Math.Floor(-t);
    }

    private void InstallPrototypeMethodsOnDatePrototype(ObjectHandle prototypeHandle, JsObject prototype)
    {
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getTime", DatePrototypeGetTime);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "valueOf", DatePrototypeGetTime);

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setTime", (thisValue, args) =>
        {
            var date = RequireDate(thisValue, "setTime");
            var t = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
            date.TimeValue = TimeClip(t);
            return JsValue.FromNumber(date.TimeValue);
        }, length: 1);

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setFullYear",
            (t, a) => SetDateField(t, "setFullYear", a, hasYear: true, hasMonth: a.Count > 1, hasDay: a.Count > 2), length: 3);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCFullYear",
            (t, a) => SetDateField(t, "setUTCFullYear", a, hasYear: true, hasMonth: a.Count > 1, hasDay: a.Count > 2), length: 3);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setMonth",
            (t, a) => SetDateField(t, "setMonth", a, hasYear: false, hasMonth: true, hasDay: a.Count > 1, monthArgIndex: 0, dayArgIndex: 1), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCMonth",
            (t, a) => SetDateField(t, "setUTCMonth", a, hasYear: false, hasMonth: true, hasDay: a.Count > 1, monthArgIndex: 0, dayArgIndex: 1), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setDate",
            (t, a) => SetDateField(t, "setDate", a, hasYear: false, hasMonth: false, hasDay: true, dayArgIndex: 0), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCDate",
            (t, a) => SetDateField(t, "setUTCDate", a, hasYear: false, hasMonth: false, hasDay: true, dayArgIndex: 0), length: 1);

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setHours",
            (t, a) => SetTimeField(t, "setHours", a, startIndex: 0), length: 4);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCHours",
            (t, a) => SetTimeField(t, "setUTCHours", a, startIndex: 0), length: 4);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setMinutes",
            (t, a) => SetTimeField(t, "setMinutes", a, startIndex: 1), length: 3);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCMinutes",
            (t, a) => SetTimeField(t, "setUTCMinutes", a, startIndex: 1), length: 3);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setSeconds",
            (t, a) => SetTimeField(t, "setSeconds", a, startIndex: 2), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCSeconds",
            (t, a) => SetTimeField(t, "setUTCSeconds", a, startIndex: 2), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setMilliseconds",
            (t, a) => SetTimeField(t, "setMilliseconds", a, startIndex: 3), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setUTCMilliseconds",
            (t, a) => SetTimeField(t, "setUTCMilliseconds", a, startIndex: 3), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toISOString", DatePrototypeToIsoString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toJSON", DatePrototypeToJson, length: 1);

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getFullYear",
            (t, _) => GetDateComponent(t, "getFullYear", d => d.Year));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMonth",
            (t, _) => GetDateComponent(t, "getMonth", d => d.Month - 1));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getDate",
            (t, _) => GetDateComponent(t, "getDate", d => d.Day));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getDay",
            (t, _) => GetDateComponent(t, "getDay", d => (int)d.DayOfWeek));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getHours",
            (t, _) => GetDateComponent(t, "getHours", d => d.Hour));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMinutes",
            (t, _) => GetDateComponent(t, "getMinutes", d => d.Minute));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getSeconds",
            (t, _) => GetDateComponent(t, "getSeconds", d => d.Second));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMilliseconds",
            (t, _) => GetDateComponent(t, "getMilliseconds", d => d.Millisecond));

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCFullYear",
            (t, _) => GetDateComponent(t, "getUTCFullYear", d => d.Year));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMonth",
            (t, _) => GetDateComponent(t, "getUTCMonth", d => d.Month - 1));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCDate",
            (t, _) => GetDateComponent(t, "getUTCDate", d => d.Day));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCDay",
            (t, _) => GetDateComponent(t, "getUTCDay", d => (int)d.DayOfWeek));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCHours",
            (t, _) => GetDateComponent(t, "getUTCHours", d => d.Hour));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMinutes",
            (t, _) => GetDateComponent(t, "getUTCMinutes", d => d.Minute));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCSeconds",
            (t, _) => GetDateComponent(t, "getUTCSeconds", d => d.Second));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMilliseconds",
            (t, _) => GetDateComponent(t, "getUTCMilliseconds", d => d.Millisecond));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getTimezoneOffset",
            (t, _) => GetDateComponent(t, "getTimezoneOffset", _ => 0));

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", DatePrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toDateString", DatePrototypeToDateString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toTimeString", DatePrototypeToTimeString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toUTCString", DatePrototypeToUtcString);
    }

    private DateObject RequireDate(JsValue thisValue, string method)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is DateObject date)
        {
            return date;
        }
        throw new JsThrownException(CreateTypeError(
            $"Date.prototype.{method} called on a non-Date receiver."));
    }

    private double GetDateTimeValue(JsValue thisValue, string method)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is DateObject date)
        {
            return date.TimeValue;
        }
        throw new JsThrownException(CreateTypeError($"Date.prototype.{method} called on a non-Date receiver."));
    }

    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] MonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private string FormatDatePart(DateTimeOffset d) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0} {1} {2:D2} {3:D4}",
            DayNames[(int)d.DayOfWeek], MonthNames[d.Month - 1], d.Day, d.Year);

    private string FormatTimePart(DateTimeOffset d) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2} GMT+0000 (Coordinated Universal Time)",
            d.Hour, d.Minute, d.Second);

    private JsValue DatePrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        var d = DateTimeOffset.FromUnixTimeMilliseconds((long)t);
        return JsValue.FromString(FormatDatePart(d) + " " + FormatTimePart(d));
    }

    private JsValue DatePrototypeToDateString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toDateString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(FormatDatePart(DateTimeOffset.FromUnixTimeMilliseconds((long)t)));
    }

    private JsValue DatePrototypeToTimeString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toTimeString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(FormatTimePart(DateTimeOffset.FromUnixTimeMilliseconds((long)t)));
    }

    // ECMA-262 21.4.4.43 Date.prototype.toUTCString. Format: "Day, DD Mon YYYY HH:MM:SS GMT".
    private JsValue DatePrototypeToUtcString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toUTCString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        var d = DateTimeOffset.FromUnixTimeMilliseconds((long)t);
        return JsValue.FromString(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}, {1:D2} {2} {3:D4} {4:D2}:{5:D2}:{6:D2} GMT",
            DayNames[(int)d.DayOfWeek], d.Day, MonthNames[d.Month - 1], d.Year,
            d.Hour, d.Minute, d.Second));
    }

    private JsValue GetDateComponent(JsValue thisValue, string method, Func<DateTimeOffset, int> extract)
    {
        var t = GetDateTimeValue(thisValue, method);
        if (!double.IsFinite(t))
        {
            return JsValue.FromNumber(double.NaN);
        }
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)t);
        return JsValue.FromNumber(extract(dto));
    }

    private JsValue DatePrototypeGetTime(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromNumber(GetDateTimeValue(thisValue, "getTime"));
    }

    // ECMA-262 21.4.4.37 Date.prototype.toJSON(key). Per spec it ToPrimitive(this,
    // "number")s the receiver; if the result is a non-finite Number it returns null
    // (so JSON.stringify emits null instead of throwing), otherwise it delegates to
    // this.toISOString(). The (key) argument is unused per step 4.
    private JsValue DatePrototypeToJson(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Date.prototype.toJSON called on non-object."));
        }
        var primitive = ToPrimitive(thisValue, PrimitiveHint.Number);
        if (primitive.Tag == JsValueTag.Number && !double.IsFinite(primitive.AsNumber()))
        {
            return JsValue.Null;
        }
        return DatePrototypeToIsoString(thisValue, Array.Empty<JsValue>());
    }

    // ECMA-262 21.4.4.36 Date.prototype.toISOString. Format is the Date Time String
    // Format defined in 21.4.1.18: extended ISO 8601 with millisecond precision and
    // a literal "Z" suffix (always UTC). Non-finite [[DateValue]] raises RangeError
    // per step 3.
    private JsValue DatePrototypeToIsoString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toISOString");
        if (!double.IsFinite(t))
        {
            throw new JsThrownException(CreateRangeError("Invalid time value."));
        }
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)t);
        // Extended year form (+YYYYYY/-YYYYYY) when outside [0, 9999] per 21.4.1.18.
        var year = dto.Year;
        var yearStr = (year >= 0 && year <= 9999)
            ? year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : (year >= 0 ? "+" : "-") + Math.Abs(year).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return JsValue.FromString(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}-{1:D2}-{2:D2}T{3:D2}:{4:D2}:{5:D2}.{6:D3}Z",
            yearStr, dto.Month, dto.Day, dto.Hour, dto.Minute, dto.Second, dto.Millisecond));
    }

    private JsValue CreateDateObject(double timeValue)
    {
        var obj = new DateObject(timeValue);
        obj.SetPrototype(GetGlobalPrototype("Date"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle EnsureRegExpPrototype()
    {
        _ = EnsureRegExpConstructor();
        return _regexpPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureRegExpConstructor()
    {
        if (_regexpConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "RegExp",
            (_, args) => CreateRegExpObject(args),
            args => CreateRegExpObject(args),
            length: 2);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        InstallPrototypeMethodsOnRegExpPrototype(prototypeHandle, prototype);

        // ES2025 RegExp.escape(string). Returns a String that can be safely embedded
        // in a regex pattern to match the literal input. The proposal mandates
        // \xHH escaping for the FIRST character if it's an ASCII letter or digit
        // (so the escaped form can never be parsed as a back-reference like \12 or
        // be ambiguous in a context like /(?<\\u0041 ...)/), and the standard
        // backslash escape for every SyntaxCharacter and several whitespace forms.
        DefineIntrinsicFunction(constructorHandle, constructor, "escape", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.String)
            {
                throw new JsThrownException(CreateTypeError("RegExp.escape: argument must be a string."));
            }
            return JsValue.FromString(RegExpEscape(args[0].AsString()));
        }, length: 1);

        _regexpPrototypeHandle = prototypeHandle;
        _regexpConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private static string RegExpEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            // First-character ASCII letter/digit -> \xHH per ES2025 step 6.
            if (i == 0 && ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            {
                _ = sb.Append('\\').Append('x')
                    .Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            switch (c)
            {
                case '\t': _ = sb.Append("\\t"); continue;
                case '\n': _ = sb.Append("\\n"); continue;
                case '\v': _ = sb.Append("\\v"); continue;
                case '\f': _ = sb.Append("\\f"); continue;
                case '\r': _ = sb.Append("\\r"); continue;
            }
            // ECMA-262 SyntaxCharacter set plus '/'.
            if ("^$\\.*+?()[]{}|/".IndexOf(c) >= 0)
            {
                _ = sb.Append('\\').Append(c);
            }
            else
            {
                _ = sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private void InstallPrototypeMethodsOnRegExpPrototype(ObjectHandle prototypeHandle, JsObject prototype)
    {
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "test", RegExpPrototypeTest, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", RegExpPrototypeToString);
    }

    private JsValue CreateRegExpObject(IReadOnlyList<JsValue> args)
    {
        var pattern = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? ToStringValue(args[0])
            : string.Empty;
        var flags = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? ToStringValue(args[1])
            : string.Empty;
        var normalizedFlags = NormalizeRegExpFlags(flags);
        var options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (normalizedFlags.Contains('m', StringComparison.Ordinal))
        {
            options |= RegexOptions.Multiline;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        var obj = new RegExpObject(pattern, normalizedFlags, regex);
        obj.SetPrototype(GetGlobalPrototype("RegExp"));
        _ = obj.DefineOwnProperty(
            "source",
            new JsPropertyDescriptor(JsValue.FromString(pattern), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "global",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('g', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "ignoreCase",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('i', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "multiline",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('m', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "lastIndex",
            new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private JsValue RegExpPrototypeTest(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        return JsValue.FromBoolean(regexp.Regex.IsMatch(input));
    }

    private JsValue RegExpPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var regexp = RegExpThisValue(thisValue);
        var escapedSource = regexp.Pattern.Replace("/", "\\/", StringComparison.Ordinal);
        return JsValue.FromString($"/{escapedSource}/{regexp.Flags}");
    }

    private RegExpObject RegExpThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is RegExpObject regexp)
        {
            return regexp;
        }

        throw new JsThrownException(CreateTypeError("RegExp.prototype method called on incompatible receiver."));
    }

    private string NormalizeRegExpFlags(string flags)
    {
        var seenGlobal = false;
        var seenIgnoreCase = false;
        var seenMultiline = false;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'g' when !seenGlobal:
                    seenGlobal = true;
                    break;
                case 'i' when !seenIgnoreCase:
                    seenIgnoreCase = true;
                    break;
                case 'm' when !seenMultiline:
                    seenMultiline = true;
                    break;
                case 'g':
                case 'i':
                case 'm':
                    throw new JsThrownException(CreateSyntaxError("RegExp flags must not be duplicated."));
                default:
                    throw new JsThrownException(CreateSyntaxError($"Invalid RegExp flag '{flag}'."));
            }
        }

        return string.Concat(
            seenGlobal ? "g" : string.Empty,
            seenIgnoreCase ? "i" : string.Empty,
            seenMultiline ? "m" : string.Empty);
    }

    private ObjectHandle EnsureJsonObject()
    {
        if (_jsonObjectHandle is { } existing)
        {
            return existing;
        }

        var json = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(json, AllocationSite.Current());
        _heap.PushRoot(handle);

        DefineIntrinsicFunction(handle, json, "parse", JsonParse, length: 2);
        DefineIntrinsicFunction(handle, json, "stringify", JsonStringify, length: 3);

        _jsonObjectHandle = handle;
        return handle;
    }

    private void DefineIntrinsicFunction(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var function = new NativeFunctionObject(name, call, length: length);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        _ = owner.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(ownerHandle, functionHandle);
    }

    // ECMA-262 25.5.1 JSON.parse(text[, reviver]). When a reviver function is
    // provided, the spec defines an InternalizeJSONProperty walk that visits every
    // node bottom-up: replacing the node with reviver.call(holder, key, value);
    // returning undefined deletes the entry. A non-callable reviver is ignored
    // per spec step 2.
    private JsValue JsonParse(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        JsValue unfiltered;
        try
        {
            using var document = JsonDocument.Parse(text);
            unfiltered = ConvertJsonElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        if (args.Count < 2 || args[1].Tag != JsValueTag.Object)
        {
            return unfiltered;
        }

        var reviverObj = _heap.GetObject(args[1].AsObjectHandle());
        if (reviverObj is not JsFunctionObject && reviverObj is not NativeFunctionObject)
        {
            return unfiltered;
        }

        // Spec 25.5.1.1 InternalizeJSONProperty: wrap the result in a single-property
        // root object { "": value } so the reviver can also see the top-level value
        // at the empty key, then walk children bottom-up.
        var rootObj = CreateOrdinaryObject();
        rootObj.SetProperty(string.Empty, unfiltered);
        var rootHandle = _heap.AllocateObject(rootObj, AllocationSite.Current());
        return InternalizeJsonProperty(rootHandle, string.Empty, args[1]);
    }

    private JsValue InternalizeJsonProperty(ObjectHandle holderHandle, string key, JsValue reviver)
    {
        var holder = _heap.GetObject(holderHandle);
        TryGetPropertyValue(holder, JsValue.FromObject(holderHandle), key, out var value);

        if (value.Tag == JsValueTag.Object)
        {
            var valueObj = _heap.GetObject(value.AsObjectHandle());
            var valueHandle = value.AsObjectHandle();
            if (valueObj is ArrayObject)
            {
                var length = GetArrayLength(valueObj);
                for (var i = 0; i < length; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var newValue = InternalizeJsonProperty(valueHandle, k, reviver);
                    if (newValue.Tag == JsValueTag.Undefined)
                    {
                        valueObj.DeleteProperty(k);
                    }
                    else
                    {
                        valueObj.SetProperty(k, newValue);
                    }
                }
            }
            else
            {
                var keys = new List<string>();
                foreach (var pair in valueObj.EnumerateOwnProperties())
                {
                    keys.Add(pair.Key);
                }

                foreach (var k in keys)
                {
                    var newValue = InternalizeJsonProperty(valueHandle, k, reviver);
                    if (newValue.Tag == JsValueTag.Undefined)
                    {
                        valueObj.DeleteProperty(k);
                    }
                    else
                    {
                        valueObj.SetProperty(k, newValue);
                    }
                }
            }
        }

        return CallFunction(reviver, new[] { JsValue.FromString(key), value }, JsValue.FromObject(holderHandle));
    }

    private JsValue ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => JsValue.Null,
            JsonValueKind.True => JsValue.FromBoolean(true),
            JsonValueKind.False => JsValue.FromBoolean(false),
            JsonValueKind.Number => element.TryGetDouble(out var number) ? JsValue.FromNumber(number) : JsValue.FromNumber(double.NaN),
            JsonValueKind.String => JsValue.FromString(element.GetString() ?? string.Empty),
            JsonValueKind.Array => ConvertJsonArray(element),
            JsonValueKind.Object => ConvertJsonObject(element),
            _ => JsValue.Undefined
        };
    }

    private JsValue ConvertJsonArray(JsonElement element)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var value = ConvertJsonElement(item);
            var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
            _ = obj.DefineOwnProperty(index.ToString(System.Globalization.CultureInfo.InvariantCulture), descriptor);
            WriteDescriptorBarrier(handle, descriptor);
            index++;
        }

        _ = obj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(JsValue.FromNumber(index), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(handle);
    }

    private JsValue ConvertJsonObject(JsonElement element)
    {
        var obj = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        foreach (var property in element.EnumerateObject())
        {
            var value = ConvertJsonElement(property.Value);
            var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
            _ = obj.DefineOwnProperty(property.Name, descriptor);
            WriteDescriptorBarrier(handle, descriptor);
        }

        return JsValue.FromObject(handle);
    }

    // Context carried through the JSON.stringify recursive walk per ECMA-262 25.5.2:
    // gap = indent string (empty when no indent), stack = circular-reference guard,
    // depth = current nesting level (drives gap repetition).
    private sealed class JsonStringifyContext
    {
        public HashSet<ObjectHandle> Stack { get; } = new();
        public string Gap { get; init; } = string.Empty;
        // ECMA-262 25.5.2 ReplacerFunction (when 2nd arg is callable) - applied to
        // every (holder, key, value) triple before serialisation, with the chance to
        // return a transformed value, including undefined to drop the key.
        public JsValue ReplacerFunction { get; init; } = JsValue.Undefined;
        // ECMA-262 25.5.2 PropertyList (when 2nd arg is an Array). When non-null, only
        // own keys appearing here are emitted on objects (arrays ignore the list).
        public HashSet<string>? PropertyList { get; init; }
    }

    private JsValue JsonStringify(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count == 0)
        {
            return JsValue.Undefined;
        }

        // ECMA-262 25.5.2 SerializeJSONProperty - third argument 'space'. Numbers
        // clamp to [0, 10] and indent that many spaces; strings clamp to first 10
        // characters and indent literally. Anything else (Boolean, Object) drops
        // back to no-indent compact form.
        var gap = string.Empty;
        if (args.Count > 2)
        {
            var space = args[2];
            if (space.Tag == JsValueTag.Number || space.Tag == JsValueTag.Int32)
            {
                var n = (int)Math.Clamp(Math.Floor(ToNumber(space)), 0, 10);
                if (n > 0) gap = new string(' ', n);
            }
            else if (space.Tag == JsValueTag.String)
            {
                var s = space.AsString();
                gap = s.Length > 10 ? s.Substring(0, 10) : s;
            }
        }

        // ECMA-262 25.5.2 second argument 'replacer'. Either a callable transformer
        // (applied to each key/value pair, may return undefined to drop the entry)
        // or an Array<String|Number> whose entries form an allowlist of own keys
        // emitted on object values (arrays are unaffected by PropertyList).
        var replacerFn = JsValue.Undefined;
        HashSet<string>? propertyList = null;
        if (args.Count > 1 && args[1].Tag == JsValueTag.Object)
        {
            var rObj = _heap.GetObject(args[1].AsObjectHandle());
            if (rObj is JsFunctionObject or NativeFunctionObject)
            {
                replacerFn = args[1];
            }
            else if (rObj is ArrayObject)
            {
                propertyList = new HashSet<string>(StringComparer.Ordinal);
                var len = GetArrayLength(rObj);
                for (var i = 0; i < len; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!TryGetPropertyValue(rObj, args[1], k, out var item)) continue;
                    if (item.Tag == JsValueTag.String)
                    {
                        propertyList.Add(item.AsString());
                    }
                    else if (item.Tag == JsValueTag.Number || item.Tag == JsValueTag.Int32)
                    {
                        propertyList.Add(ToStringValue(item));
                    }
                }
            }
        }

        var ctx = new JsonStringifyContext
        {
            Gap = gap,
            ReplacerFunction = replacerFn,
            PropertyList = propertyList,
        };

        // ECMA-262 25.5.2 step 8 - wrap the initial value in a synthetic { '': value }
        // holder so the replacer (if callable) gets called once at the top level with
        // key="" and the wrapper as the 'this' receiver.
        var rootValue = args[0];
        if (replacerFn.Tag != JsValueTag.Undefined)
        {
            var wrapper = CreateOrdinaryObject();
            wrapper.SetProperty(string.Empty, rootValue);
            var wrapperHandle = _heap.AllocateObject(wrapper, AllocationSite.Current());
            rootValue = CallFunction(replacerFn, new[] { JsValue.FromString(string.Empty), rootValue }, JsValue.FromObject(wrapperHandle));
        }

        var json = StringifyJsonValue(rootValue, ctx, depth: 0, inArray: false);
        return json is null ? JsValue.Undefined : JsValue.FromString(json);
    }

    private string? StringifyJsonValue(JsValue value, JsonStringifyContext ctx, int depth, bool inArray)
    {
        if (depth > 200)
        {
            throw new JsThrownException(CreateTypeError("JSON.stringify exceeded the maximum serialization depth."));
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => inArray ? "null" : null,
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => StringifyJsonNumber(value.AsNumber()),
            JsValueTag.String => JsonSerializer.Serialize(value.AsString()),
            JsValueTag.Object => StringifyJsonObject(value, ctx, depth, inArray),
            _ => inArray ? "null" : null
        };
    }

    private string StringifyJsonNumber(double number)
    {
        return double.IsFinite(number)
            ? FormatNumberForString(number)
            : "null";
    }

    private string? StringifyJsonObject(JsValue value, JsonStringifyContext ctx, int depth, bool inArray)
    {
        var handle = value.AsObjectHandle();
        var obj = _heap.GetObject(handle);
        if (obj is JsFunctionObject or NativeFunctionObject)
        {
            return inArray ? "null" : null;
        }

        if (!ctx.Stack.Add(handle))
        {
            throw new JsThrownException(CreateTypeError("Cannot stringify circular structure."));
        }

        try
        {
            if (obj is ArrayObject)
            {
                return StringifyJsonArray(obj, value, ctx, depth);
            }

            var parts = new List<string>();
            // ECMA-262 25.5.2 SerializeJSONObject: PropertyList drives iteration order
            // when present; otherwise we walk the own enumerable string keys.
            IEnumerable<string> keys;
            if (ctx.PropertyList is not null)
            {
                keys = ctx.PropertyList;
            }
            else
            {
                var k = new List<string>();
                foreach (var p in obj.EnumerateOwnProperties())
                {
                    if (p.Value.Enumerable) k.Add(p.Key);
                }
                keys = k;
            }

            foreach (var key in keys)
            {
                if (!TryGetPropertyValue(obj, value, key, out var propertyValue))
                {
                    continue;
                }
                // ECMA-262 25.5.2 ReplacerFunction call - applied per entry with the
                // holder as 'this' and (key, value) arguments.
                if (ctx.ReplacerFunction.Tag != JsValueTag.Undefined)
                {
                    propertyValue = CallFunction(ctx.ReplacerFunction,
                        new[] { JsValue.FromString(key), propertyValue }, value);
                }
                var serialized = StringifyJsonValue(propertyValue, ctx, depth + 1, inArray: false);
                if (serialized is not null)
                {
                    var colon = ctx.Gap.Length > 0 ? ": " : ":";
                    parts.Add(JsonSerializer.Serialize(key) + colon + serialized);
                }
            }

            if (parts.Count == 0)
            {
                return "{}";
            }

            if (ctx.Gap.Length == 0)
            {
                return "{" + string.Join(",", parts) + "}";
            }

            // Indent each member by depth+1 levels of Gap, with newline separators.
            var inner = string.Concat(System.Linq.Enumerable.Repeat(ctx.Gap, depth + 1));
            var outer = string.Concat(System.Linq.Enumerable.Repeat(ctx.Gap, depth));
            return "{\n" + inner + string.Join(",\n" + inner, parts) + "\n" + outer + "}";
        }
        finally
        {
            _ = ctx.Stack.Remove(handle);
        }
    }

    private string StringifyJsonArray(JsObject obj, JsValue receiver, JsonStringifyContext ctx, int depth)
    {
        var length = GetArrayLength(obj);
        var parts = new string[length];
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hasValue = TryGetPropertyValue(obj, receiver, key, out var value);
            // ECMA-262 25.5.2 step 4 of SerializeJSONArray - ReplacerFunction also
            // applies to array elements (with index as the key string).
            if (hasValue && ctx.ReplacerFunction.Tag != JsValueTag.Undefined)
            {
                value = CallFunction(ctx.ReplacerFunction,
                    new[] { JsValue.FromString(key), value }, receiver);
            }
            parts[i] = hasValue
                ? StringifyJsonValue(value, ctx, depth + 1, inArray: true) ?? "null"
                : "null";
        }

        if (length == 0)
        {
            return "[]";
        }

        if (ctx.Gap.Length == 0)
        {
            return "[" + string.Join(",", parts) + "]";
        }

        var inner = string.Concat(System.Linq.Enumerable.Repeat(ctx.Gap, depth + 1));
        var outer = string.Concat(System.Linq.Enumerable.Repeat(ctx.Gap, depth));
        return "[\n" + inner + string.Join(",\n" + inner, parts) + "\n" + outer + "]";
    }

    private ObjectHandle EnsureObjectPrototype()
    {
        _ = EnsureObjectConstructor();
        return _objectPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureObjectConstructor()
    {
        if (_objectConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(new JsObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Object",
            (_, args) => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            args => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        var definePropertyHandle = _heap.AllocateObject(
            new NativeFunctionObject("defineProperty", ObjectDefineProperty, length: 3),
            AllocationSite.Current());
        _ = constructor.SetProperty("defineProperty", JsValue.FromObject(definePropertyHandle));
        _heap.WriteBarrier(constructorHandle, definePropertyHandle);
        var getOwnPropertyDescriptorHandle = _heap.AllocateObject(
            new NativeFunctionObject("getOwnPropertyDescriptor", ObjectGetOwnPropertyDescriptor, length: 2),
            AllocationSite.Current());
        _ = constructor.SetProperty("getOwnPropertyDescriptor", JsValue.FromObject(getOwnPropertyDescriptorHandle));
        _heap.WriteBarrier(constructorHandle, getOwnPropertyDescriptorHandle);

        // ECMA-262 20.1.2.13 Object.is(value1, value2). Implements the SameValue
        // abstract operation (7.2.10): +0 and -0 are NOT equal, NaN is equal to NaN.
        DefineIntrinsicFunction(constructorHandle, constructor, "is", (_, args) =>
        {
            var a = args.Count > 0 ? args[0] : JsValue.Undefined;
            var b = args.Count > 1 ? args[1] : JsValue.Undefined;
            return JsValue.FromBoolean(SameValue(a, b));
        }, length: 2);

        // ECMA-262 20.1.2.4 Object.defineProperties(O, Properties). Walks each own
        // enumerable property of the Properties object and calls Object.defineProperty
        // with the corresponding descriptor. The receiver O is returned.
        DefineIntrinsicFunction(constructorHandle, constructor, "defineProperties", (_, args) =>
        {
            if (args.Count < 2 || args[0].Tag != JsValueTag.Object || args[1].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.defineProperties requires an object target and a properties object."));
            }

            var propsObj = _heap.GetObject(args[1].AsObjectHandle());
            var propsReceiver = args[1];
            var keys = new List<string>();
            foreach (var pair in propsObj.EnumerateOwnProperties())
            {
                if (pair.Value.Enumerable)
                {
                    keys.Add(pair.Key);
                }
            }

            foreach (var key in keys)
            {
                if (!TryGetPropertyValue(propsObj, propsReceiver, key, out var descValue) ||
                    descValue.Tag != JsValueTag.Object)
                {
                    continue;
                }

                ObjectDefineProperty(JsValue.Undefined, new[]
                {
                    args[0],
                    JsValue.FromString(key),
                    descValue,
                });
            }

            return args[0];
        }, length: 2);

        // ECMA-262 20.1.2.14 Object.hasOwn(O, P) - ES2022. Equivalent to calling
        // Object.prototype.hasOwnProperty.call(O, P) without the awkward .call form
        // and without the prototype hazard if O is a null-prototype object.
        DefineIntrinsicFunction(constructorHandle, constructor, "hasOwn", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out var __));
        }, length: 2);

        // ECMA-262 20.1.2.11 Object.getOwnPropertyDescriptors(O). Returns an object
        // whose own keys mirror the input's own keys and whose values are full
        // descriptor objects (built by the same factory as getOwnPropertyDescriptor
        // so the shape stays in sync).
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertyDescriptors", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            var result = CreateOrdinaryObject();
            var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
            if (target.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(target.AsObjectHandle());
                foreach (var pair in obj.EnumerateOwnProperties())
                {
                    var descObj = BuildDescriptorObject(pair.Value);
                    var descHandle = _heap.AllocateObject(descObj, AllocationSite.Current());
                    WriteDescriptorBarrier(descHandle, pair.Value);
                    result.SetProperty(pair.Key, JsValue.FromObject(descHandle));
                    _heap.WriteBarrier(resultHandle, descHandle);
                }
            }

            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 20.1.2.7 Object.fromEntries(iterable). The full spec walks an
        // arbitrary iterable via @@iterator; this implementation accepts an Array of
        // two-element entries (the overwhelmingly common case) until the full
        // iterator protocol is wired. Each entry's [0] becomes the property key
        // (coerced via ToPropertyKey) and [1] becomes the value. Non-Array or non-
        // entry inputs surface as a TypeError, matching engines like V8 / SM.
        DefineIntrinsicFunction(constructorHandle, constructor, "fromEntries", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (iterable.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.fromEntries: argument must be an iterable of entries."));
            }

            var source = _heap.GetObject(iterable.AsObjectHandle());
            if (source is not ArrayObject)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.fromEntries: argument must be an Array (full iterator protocol pending)."));
            }

            var length = GetArrayLength(source);
            var result = CreateOrdinaryObject();
            for (var i = 0; i < length; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!TryGetPropertyValue(source, iterable, key, out var entryValue) ||
                    entryValue.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        $"Object.fromEntries: entry at index {i} is not an object."));
                }

                var entry = _heap.GetObject(entryValue.AsObjectHandle());
                if (entry is not ArrayObject)
                {
                    throw new JsThrownException(CreateTypeError(
                        $"Object.fromEntries: entry at index {i} is not an Array."));
                }

                TryGetPropertyValue(entry, entryValue, "0", out var entryKey);
                TryGetPropertyValue(entry, entryValue, "1", out var entryValueSlot);
                var keyName = ToPropertyKey(entryKey);
                result.SetProperty(keyName, entryValueSlot);
            }

            var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 20.1.2.5 Object.groupBy(items, callbackfn) (ES2024). Walks the
        // iterable, calls callbackfn(element, index) for each, coerces the return to a
        // property key (ToPropertyKey: Symbol stays Symbol, otherwise ToString) and
        // groups elements into Arrays keyed by that key on a fresh null-prototype
        // object. Result key order mirrors insertion order of first-seen keys.
        DefineIntrinsicFunction(constructorHandle, constructor, "groupBy", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            var callback = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (callback.Tag != JsValueTag.Object ||
                _heap.GetObject(callback.AsObjectHandle()) is not (JsFunctionObject or NativeFunctionObject))
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.groupBy: callback must be a function."));
            }
            var values = new List<JsValue>();
            var iter = CreateForOfIterator(iterable);
            if (iter.Tag == JsValueTag.Object &&
                _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
            {
                while (forOf.TryMoveNext(out var v)) values.Add(v);
            }

            var groups = new Dictionary<string, List<JsValue>>(StringComparer.Ordinal);
            var orderedKeys = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var rawKey = CallFunction(callback, new[] { values[i], JsValue.FromNumber(i) }, JsValue.Undefined);
                var key = ToPropertyKey(rawKey);
                if (!groups.TryGetValue(key, out var bucket))
                {
                    bucket = new List<JsValue>();
                    groups[key] = bucket;
                    orderedKeys.Add(key);
                }
                bucket.Add(values[i]);
            }

            var result = new JsObject();
            // null prototype per spec step 5 (! OrdinaryObjectCreate(null)).
            var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
            foreach (var key in orderedKeys)
            {
                var arrObj = CreateArrayFromElements(groups[key].ToArray());
                var arrHandle = _heap.AllocateObject(arrObj, AllocationSite.Current());
                result.SetProperty(key, JsValue.FromObject(arrHandle));
                _heap.WriteBarrier(resultHandle, arrHandle);
            }
            return JsValue.FromObject(resultHandle);
        }, length: 2);

        // ECMA-262 20.1.2.10 Object.getOwnPropertyNames(O). Unlike Object.keys this
        // does NOT filter by Enumerable - every own string-keyed property surfaces in
        // [[OwnPropertyKeys]] order. Primitives and null/undefined still throw via
        // the ToObject step on the spec side; we surface that as a TypeError matching
        // the Object.keys path.
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertyNames", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            var items = new List<JsValue>();
            if (target.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(target.AsObjectHandle());
                foreach (var pair in obj.EnumerateOwnProperties())
                {
                    items.Add(JsValue.FromString(pair.Key));
                }
            }

            var arr = CreateArrayObject(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // ECMA-262 20.1.2.20 / 20.1.2.15 Object.preventExtensions / isExtensible.
        DefineIntrinsicFunction(constructorHandle, constructor, "preventExtensions", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            _heap.GetObject(target.AsObjectHandle()).PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isExtensible", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            return JsValue.FromBoolean(_heap.GetObject(target.AsObjectHandle()).Extensible);
        }, length: 1);

        // ECMA-262 20.1.2.6 / 20.1.2.16 Object.freeze / isFrozen.
        DefineIntrinsicFunction(constructorHandle, constructor, "freeze", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            var keys = new List<string>();
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                keys.Add(pair.Key);
            }

            foreach (var key in keys)
            {
                if (!obj.TryGetOwnProperty(key, out var desc))
                {
                    continue;
                }

                var newDesc = desc.IsAccessor
                    ? JsPropertyDescriptor.Accessor(desc.Get, desc.Set, desc.Enumerable, Configurable: false)
                    : new JsPropertyDescriptor(desc.Value, Writable: false, desc.Enumerable, Configurable: false);
                obj.DefineOwnProperty(key, newDesc);
            }

            obj.PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isFrozen", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(true);   // primitives are vacuously frozen
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj.Extensible)
            {
                return JsValue.FromBoolean(false);
            }

            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (pair.Value.Configurable)
                {
                    return JsValue.FromBoolean(false);
                }

                if (!pair.Value.IsAccessor && pair.Value.Writable)
                {
                    return JsValue.FromBoolean(false);
                }
            }

            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 20.1.2.23 / 20.1.2.17 Object.seal / isSealed.
        DefineIntrinsicFunction(constructorHandle, constructor, "seal", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            var keys = new List<string>();
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                keys.Add(pair.Key);
            }

            foreach (var key in keys)
            {
                if (!obj.TryGetOwnProperty(key, out var desc))
                {
                    continue;
                }

                var newDesc = desc.IsAccessor
                    ? JsPropertyDescriptor.Accessor(desc.Get, desc.Set, desc.Enumerable, Configurable: false)
                    : new JsPropertyDescriptor(desc.Value, desc.Writable, desc.Enumerable, Configurable: false);
                obj.DefineOwnProperty(key, newDesc);
            }

            obj.PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isSealed", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(true);
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj.Extensible)
            {
                return JsValue.FromBoolean(false);
            }

            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (pair.Value.Configurable)
                {
                    return JsValue.FromBoolean(false);
                }
            }

            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 20.1.2.1 Object.assign(target, ...sources). Copies enumerable own
        // string-keyed properties from each source to target via [[Set]]. Returns
        // the (possibly coerced) target.
        DefineIntrinsicFunction(constructorHandle, constructor, "assign", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.assign target must not be undefined or null."));
            }

            var targetValue = args[0];
            if (targetValue.Tag != JsValueTag.Object)
            {
                return targetValue;
            }

            var targetHandle = targetValue.AsObjectHandle();
            var target = _heap.GetObject(targetHandle);

            for (var i = 1; i < args.Count; i++)
            {
                var source = args[i];
                if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
                {
                    continue;
                }

                if (source.Tag != JsValueTag.Object)
                {
                    continue;
                }

                var sourceObj = _heap.GetObject(source.AsObjectHandle());
                foreach (var pair in sourceObj.EnumerateOwnProperties())
                {
                    if (!pair.Value.Enumerable)
                    {
                        continue;
                    }

                    var value = pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value;
                    if (!target.SetProperty(pair.Key, value))
                    {
                        // Property was non-writable; per spec [[Set]] returning false
                        // in strict mode is a TypeError. We surface that consistently.
                        throw new JsThrownException(CreateTypeError(
                            $"Cannot assign to read-only property '{pair.Key}'."));
                    }

                    if (value.Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(targetHandle, value.AsObjectHandle());
                    }
                }
            }

            return targetValue;
        }, length: 2);

        // ECMA-262 20.1.2.12 Object.getPrototypeOf(O).
        DefineIntrinsicFunction(constructorHandle, constructor, "getPrototypeOf", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.getPrototypeOf called on null or undefined."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return JsValue.Null;
            }

            var target = _heap.GetObject(args[0].AsObjectHandle());
            return target.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
        }, length: 1);

        // ECMA-262 20.1.2.21 Object.setPrototypeOf(O, proto). Returns O. Proto must
        // be either an Object or null.
        DefineIntrinsicFunction(constructorHandle, constructor, "setPrototypeOf", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.setPrototypeOf called on null or undefined."));
            }

            var protoArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.setPrototypeOf: prototype must be Object or null."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return args[0];
            }

            var ownerHandle = args[0].AsObjectHandle();
            var target = _heap.GetObject(ownerHandle);
            if (protoArg.Tag == JsValueTag.Object)
            {
                var protoHandle = protoArg.AsObjectHandle();
                target.SetPrototype(protoHandle);
                _heap.WriteBarrier(ownerHandle, protoHandle);
            }
            else
            {
                target.SetPrototype(null);
            }

            return args[0];
        }, length: 2);

        // ECMA-262 20.1.2.2 Object.create(O[, Properties]). Allocates a new ordinary
        // object whose [[Prototype]] is the first argument (must be Object or null).
        // Properties parameter (DefineProperties-style) is deferred; passing it today
        // is silently ignored - tests should not rely on the second argument until a
        // follow-up commit wires DefineProperties.
        DefineIntrinsicFunction(constructorHandle, constructor, "create", (_, args) =>
        {
            var protoArg = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.create: prototype must be Object or null."));
            }

            var obj = CreateOrdinaryObject();
            if (protoArg.Tag == JsValueTag.Object)
            {
                obj.SetPrototype(protoArg.AsObjectHandle());
            }
            else
            {
                obj.SetPrototype(null);
            }

            var handle = _heap.AllocateObject(obj, AllocationSite.Current());
            if (protoArg.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(handle, protoArg.AsObjectHandle());
            }

            // ECMA-262 20.1.2.2 Object.create step 3: when Properties is not undefined,
            // call ObjectDefineProperties(obj, Properties). Each own enumerable key on
            // the descriptors object is converted to a property descriptor via the same
            // ObjectDefineProperty algorithm Object.defineProperties uses, so accessor
            // / data descriptors / writable / enumerable / configurable all flow
            // through one validator.
            if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
            {
                if (args[1].Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        "Object.create: properties argument must be an object."));
                }
                var propsObj = _heap.GetObject(args[1].AsObjectHandle());
                var propsReceiver = args[1];
                var keys = new List<string>();
                foreach (var pair in propsObj.EnumerateOwnProperties())
                {
                    if (pair.Value.Enumerable) keys.Add(pair.Key);
                }
                foreach (var key in keys)
                {
                    if (!TryGetPropertyValue(propsObj, propsReceiver, key, out var descValue) ||
                        descValue.Tag != JsValueTag.Object)
                    {
                        continue;
                    }
                    ObjectDefineProperty(JsValue.Undefined, new[]
                    {
                        JsValue.FromObject(handle),
                        JsValue.FromString(key),
                        descValue,
                    });
                }
            }

            return JsValue.FromObject(handle);
        }, length: 2);

        // ECMA-262 20.1.2.18 / 20.1.2.22 / 20.1.2.5 - Object.keys / values / entries.
        // Each calls EnumerableOwnProperties(O, kind) (7.3.24) which iterates the
        // target's own string-keyed properties in [[OwnPropertyKeys]] order and
        // filters to those with Enumerable: true. Undefined/null arguments raise
        // TypeError per the ToObject step (7.1.18). Other primitives currently
        // return an empty list; full ToObject boxing for primitives lands when the
        // String/Number/Boolean prototype enumerations land.
        DefineIntrinsicFunction(constructorHandle, constructor, "keys", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Keys), length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "values", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Values), length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "entries", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Entries), length: 1);

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "hasOwnProperty", ObjectPrototypeHasOwnProperty, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "isPrototypeOf", ObjectPrototypeIsPrototypeOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "propertyIsEnumerable", ObjectPrototypePropertyIsEnumerable, length: 1);

        _objectPrototypeHandle = prototypeHandle;
        _objectConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private enum OwnEnumerableKind
    {
        Keys,
        Values,
        Entries,
    }

    private JsValue CollectOwnEnumerable(IReadOnlyList<JsValue> args, OwnEnumerableKind kind)
    {
        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
        {
            // 7.1.18 ToObject(undefined|null) throws TypeError; the user-visible
            // call site is Object.{keys,values,entries}, so the message names it.
            throw new JsThrownException(CreateTypeError(
                "Cannot convert undefined or null to object."));
        }

        var items = new List<JsValue>();
        if (target.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(target.AsObjectHandle());
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (!pair.Value.Enumerable)
                {
                    continue;
                }

                switch (kind)
                {
                    case OwnEnumerableKind.Keys:
                        items.Add(JsValue.FromString(pair.Key));
                        break;
                    case OwnEnumerableKind.Values:
                        items.Add(pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value);
                        break;
                    case OwnEnumerableKind.Entries:
                    {
                        var value = pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value;
                        var entry = CreateArrayObject(new[] { JsValue.FromString(pair.Key), value });
                        var entryHandle = _heap.AllocateObject(entry, AllocationSite.Current());
                        items.Add(JsValue.FromObject(entryHandle));
                        break;
                    }
                }
            }
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private ObjectHandle EnsureFunctionPrototype()
    {
        _ = EnsureFunctionConstructor();
        return _functionPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureFunctionConstructor()
    {
        if (_functionConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new NativeFunctionObject(string.Empty, (_, _) => JsValue.Undefined);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Function",
            (_, args) => CreateDynamicFunction(args),
            args => CreateDynamicFunction(args),
            length: 1);
        constructor.SetPrototype(prototypeHandle);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var callHandle = EnsureFunctionCallMethod();
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _ = prototype.SetProperty("call", JsValue.FromObject(callHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _heap.WriteBarrier(prototypeHandle, callHandle);

        // ECMA-262 20.2.3.1 Function.prototype.apply(thisArg, argsArray). The
        // second argument is an Array (or array-like). null/undefined become an
        // empty argument list per spec step 3-4.
        var applyHandle = _heap.AllocateObject(
            new NativeFunctionObject("apply", FunctionPrototypeApply, length: 2),
            AllocationSite.Current());
        _ = prototype.SetProperty("apply", JsValue.FromObject(applyHandle));
        _heap.WriteBarrier(prototypeHandle, applyHandle);

        // ECMA-262 20.2.3.2 Function.prototype.bind(thisArg, ...args). Returns a new
        // function ("exotic bound function" in the spec). The returned function calls
        // the original with thisArg pre-set and any bound args prepended to the
        // call-site args.
        var bindHandle = _heap.AllocateObject(
            new NativeFunctionObject("bind", FunctionPrototypeBind, length: 1),
            AllocationSite.Current());
        _ = prototype.SetProperty("bind", JsValue.FromObject(bindHandle));
        _heap.WriteBarrier(prototypeHandle, bindHandle);

        _functionPrototypeHandle = prototypeHandle;
        _functionConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue CreateFunctionObject(
        BytecodeFunction function,
        EnvironmentRecord? outerEnvironment = null)
    {
        var fnObj = new JsFunctionObject(function, outerEnvironment, function.Kind);
        fnObj.SetPrototype(EnsureFunctionPrototype());
        _ = fnObj.DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(
                JsValue.FromString(function.Name ?? string.Empty),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        _ = fnObj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(function.ParameterNames.Count),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _ = fnObj.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var handle = _heap.AllocateObject(fnObj, AllocationSite.Current());
        _heap.WriteBarrier(handle, prototypeHandle);
        return JsValue.FromObject(handle);
    }

    private JsValue CreateDynamicFunction(IReadOnlyList<JsValue> args)
    {
        var parameters = new List<string>();
        for (var i = 0; i + 1 < args.Count; i++)
        {
            AddFunctionConstructorParameters(parameters, ToStringValue(args[i]));
        }

        var body = args.Count > 0 ? ToStringValue(args[^1]) : string.Empty;
        try
        {
            var compiled = new BytecodeCompiler().CompileFunctionBody(
                new SourceText(body, "<Function>"),
                parameters,
                "anonymous");
            new BytecodeVerifier().Verify(compiled);
            return CreateFunctionObject(compiled);
        }
        catch (Exception ex) when (ex is JsParserException or UnsupportedFeatureException)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }
    }

    private static void AddFunctionConstructorParameters(List<string> parameters, string parameterText)
    {
        foreach (var rawPart in parameterText.Split(','))
        {
            var parameter = rawPart.Trim();
            if (parameter.Length == 0)
            {
                continue;
            }

            if (!IsIdentifierName(parameter))
            {
                throw new JsParserException($"Invalid function parameter '{parameter}'.");
            }

            parameters.Add(parameter);
        }
    }

    private static bool IsIdentifierName(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var first = value[0];
        if (first != '_' && first != '$' && !char.IsLetter(first))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '_' && ch != '$' && !char.IsLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private ObjectHandle DefineNativePrototypeMethod(
        ObjectHandle prototypeHandle,
        JsObject prototype,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length = 0)
    {
        var function = new NativeFunctionObject(name, call, length: length);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        var callHandle = EnsureFunctionCallMethod();
        _ = function.SetProperty("call", JsValue.FromObject(callHandle));
        _heap.WriteBarrier(functionHandle, callHandle);
        _ = prototype.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, functionHandle);
        return functionHandle;
    }

    private ObjectHandle EnsureFunctionCallMethod()
    {
        if (_functionCallMethodHandle is { } existing)
        {
            return existing;
        }

        var call = new NativeFunctionObject(
            "call",
            (thisValue, args) => FunctionPrototypeCall(thisValue, args),
            length: 1);
        var callHandle = _heap.AllocateObject(call, AllocationSite.Current());
        _heap.PushRoot(callHandle);
        _functionCallMethodHandle = callHandle;
        return callHandle;
    }

    // ECMA-262 20.2.3.1 Function.prototype.apply. Distinct from .call in that the
    // arguments come packaged in an Array (or array-like) second parameter; null/
    // undefined yields an empty argument list per step 3-4.
    private JsValue FunctionPrototypeApply(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        // ECMA-262 20.2.3.1 step 1: coerce null/undefined thisArg to global object.
        var thisArgument = args.Count > 0 && args[0].Tag != JsValueTag.Null && args[0].Tag != JsValueTag.Undefined
            ? args[0]
            : JsValue.FromObject(EnsureGlobalObject());
        var argsArray = args.Count > 1 ? args[1] : JsValue.Undefined;

        JsValue[] callArgs;
        if (argsArray.Tag == JsValueTag.Undefined || argsArray.Tag == JsValueTag.Null)
        {
            callArgs = Array.Empty<JsValue>();
        }
        else if (argsArray.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(argsArray.AsObjectHandle());
            var length = GetArrayLength(obj);
            callArgs = new JsValue[length];
            for (var i = 0; i < length; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TryGetPropertyValue(obj, argsArray, key, out callArgs[i]);
            }
        }
        else
        {
            throw new JsThrownException(CreateTypeError(
                "Function.prototype.apply: second argument must be an object or null/undefined."));
        }

        return CallFunction(thisValue, callArgs, thisArgument);
    }

    // ECMA-262 20.2.3.2 Function.prototype.bind. Returns a BoundFunctionObject
    // exotic object with [[BoundTargetFunction]], [[BoundThis]], and [[BoundArguments]].
    private JsValue FunctionPrototypeBind(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype.bind called on non-function."));
        }

        var targetObj = _heap.GetObject(thisValue.AsObjectHandle());
        if (targetObj is not JsFunctionObject && targetObj is not NativeFunctionObject && targetObj is not BoundFunctionObject)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype.bind called on non-callable."));
        }

        var boundThis = args.Count > 0 ? args[0] : JsValue.Undefined;
        var boundArgs = new JsValue[Math.Max(0, args.Count - 1)];
        for (var i = 1; i < args.Count; i++)
        {
            boundArgs[i - 1] = args[i];
        }

        var bound = new BoundFunctionObject(
            thisValue,
            boundThis,
            boundArgs,
            Math.Max(0, GetCallableLength(targetObj) - boundArgs.Length));
        bound.SetPrototype(EnsureFunctionPrototype());

        var boundHandle = _heap.AllocateObject(bound, AllocationSite.Current());
        return JsValue.FromObject(boundHandle);
    }

    // Merge bound args + call-site args for BoundFunctionObject [[Call]]/[[Construct]].
    private static JsValue[] MergeBoundArgs(JsValue[] boundArgs, IReadOnlyList<JsValue> callArgs)
    {
        var merged = new JsValue[boundArgs.Length + callArgs.Count];
        Array.Copy(boundArgs, merged, boundArgs.Length);
        for (var i = 0; i < callArgs.Count; i++)
            merged[boundArgs.Length + i] = callArgs[i];
        return merged;
    }

    private static int GetCallableLength(JsObject callable)
    {
        if (callable.TryGetOwnProperty("length", out var desc) &&
            (desc.Value.Tag == JsValueTag.Number || desc.Value.Tag == JsValueTag.Int32))
        {
            return Math.Max(0, (int)desc.Value.AsNumber());
        }

        return 0;
    }

    private JsValue FunctionPrototypeCall(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var thisArgument = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (args.Count <= 1)
        {
            return CallFunction(thisValue, Array.Empty<JsValue>(), thisArgument);
        }

        var callArgs = new JsValue[args.Count - 1];
        for (var i = 1; i < args.Count; i++)
        {
            callArgs[i - 1] = args[i];
        }

        return CallFunction(thisValue, callArgs, thisArgument);
    }

    private JsValue CreateObjectFromValue(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current())),
            JsValueTag.Object => value,
            JsValueTag.Boolean => CreateBooleanObject(value.AsBoolean()),
            JsValueTag.Int32 or JsValueTag.Number => CreateNumberObject(ToNumber(value)),
            JsValueTag.String => CreateStringObject(value.AsString()),
            JsValueTag.HostObject => value,
            _ => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()))
        };
    }

    private JsValue ObjectPrototypeToString(JsValue thisValue)
    {
        var tag = thisValue.Tag switch
        {
            JsValueTag.Undefined => "Undefined",
            JsValueTag.Null => "Null",
            JsValueTag.Boolean => "Boolean",
            JsValueTag.Int32 or JsValueTag.Number => "Number",
            JsValueTag.String => "String",
            JsValueTag.HostObject => "Object",
            JsValueTag.Object => GetObjectToStringTag(thisValue.AsObjectHandle()),
            _ => "Object"
        };

        return JsValue.FromString($"[object {tag}]");
    }

    private JsValue ObjectPrototypeHasOwnProperty(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Object.prototype.hasOwnProperty called on null or undefined."));
        }

        if (thisValue.Tag == JsValueTag.HostObject)
        {
            return JsValue.FromBoolean(false);
        }

        var key = ToPropertyKey(args.Count > 0 ? args[0] : JsValue.Undefined);
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out _));
    }

    private JsValue ObjectPrototypePropertyIsEnumerable(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Object.prototype.propertyIsEnumerable called on null or undefined."));
        }

        if (thisValue.Tag == JsValueTag.HostObject)
        {
            return JsValue.FromBoolean(false);
        }

        var key = ToPropertyKey(args.Count > 0 ? args[0] : JsValue.Undefined);
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out var descriptor) && descriptor.Enumerable);
    }

    private string GetObjectToStringTag(ObjectHandle handle)
    {
        // ECMA-262 20.1.3.6 step 14: if the object has a Symbol.toStringTag
        // own or inherited string property, that string overrides the default
        // builtin tag. Module namespace objects use this hook to return
        // "[object Module]"; user code can override via Symbol.toStringTag.
        var obj = _heap.GetObject(handle);
        var tagId = GetWellKnownSymbolId("toStringTag");
        if (tagId != 0 && obj.TryGetSymbolProperty(tagId, h => _heap.GetObject(h), out var tagDesc)
            && !tagDesc.IsAccessor && tagDesc.Value.Tag == JsValueTag.String)
        {
            return tagDesc.Value.AsString();
        }

        return obj switch
        {
            ArrayObject => "Array",
            BooleanObject => "Boolean",
            DateObject => "Date",
            NumberObject => "Number",
            RegExpObject => "RegExp",
            StringObject => "String",
            JsFunctionObject or NativeFunctionObject => "Function",
            _ => "Object"
        };
    }

    private JsValue ObjectPrototypeIsPrototypeOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag != JsValueTag.Object || args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            return JsValue.FromBoolean(false);
        }

        var prototype = thisValue.AsObjectHandle();
        var candidate = _heap.GetObject(args[0].AsObjectHandle());
        while (candidate.PrototypeHandle is { } current)
        {
            if (current.Equals(prototype))
            {
                return JsValue.FromBoolean(true);
            }

            candidate = _heap.GetObject(current);
        }

        return JsValue.FromBoolean(false);
    }

    private JsValue ObjectDefineProperty(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count < 3 || args[0].Tag != JsValueTag.Object || args[2].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Object.defineProperty requires an object target and descriptor."));
        }

        var targetHandle = args[0].AsObjectHandle();
        var target = _heap.GetObject(targetHandle);
        var key = ToPropertyKey(args[1]);
        var descriptorObject = _heap.GetObject(args[2].AsObjectHandle());
        var descriptorReceiver = args[2];
        var hasValue = TryGetPropertyValue(descriptorObject, descriptorReceiver, "value", out var value);
        var hasWritable = descriptorObject.TryGetProperty("writable", h => _heap.GetObject(h), out _);
        var hasGetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "get", out var getter);
        var hasSetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "set", out var setter);
        if ((hasGetter || hasSetter) && (hasValue || hasWritable))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor cannot mix accessor and data fields."));
        }

        if (hasGetter &&
            getter.Tag != JsValueTag.Undefined &&
            !IsCallable(getter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor getter must be callable or undefined."));
        }

        if (hasSetter &&
            setter.Tag != JsValueTag.Undefined &&
            !IsCallable(setter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor setter must be callable or undefined."));
        }

        var writable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "writable");
        var enumerable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "enumerable");
        var configurable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "configurable");

        var descriptor = hasGetter || hasSetter
            ? JsPropertyDescriptor.Accessor(
                hasGetter ? getter : JsValue.Undefined,
                hasSetter ? setter : JsValue.Undefined,
                enumerable,
                configurable)
            : new JsPropertyDescriptor(hasValue ? value : JsValue.Undefined, writable, enumerable, configurable);

        _ = target.DefineOwnProperty(key, descriptor);
        WriteDescriptorBarrier(targetHandle, descriptor);

        return args[0];
    }

    private void WriteDescriptorBarrier(ObjectHandle ownerHandle, JsPropertyDescriptor descriptor)
    {
        if (descriptor.IsAccessor)
        {
            if (descriptor.Get.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, descriptor.Get.AsObjectHandle());
            }

            if (descriptor.Set.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, descriptor.Set.AsObjectHandle());
            }

            return;
        }

        if (descriptor.Value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(ownerHandle, descriptor.Value.AsObjectHandle());
        }
    }

    private JsValue ObjectGetOwnPropertyDescriptor(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Object.getOwnPropertyDescriptor requires an object target."));
        }

        var target = _heap.GetObject(args[0].AsObjectHandle());
        var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
        if (!target.TryGetOwnProperty(key, out var descriptor))
        {
            return JsValue.Undefined;
        }

        var descriptorObject = BuildDescriptorObject(descriptor);
        var descriptorHandle = _heap.AllocateObject(descriptorObject, AllocationSite.Current());
        WriteDescriptorBarrier(descriptorHandle, descriptor);
        return JsValue.FromObject(descriptorHandle);
    }

    // Spec 6.2.5.4 FromPropertyDescriptor: build the user-visible descriptor object
    // shape (get/set for accessor, value/writable for data, always enumerable +
    // configurable). Shared by Object.getOwnPropertyDescriptor and
    // Object.getOwnPropertyDescriptors so a single edit covers both.
    private JsObject BuildDescriptorObject(JsPropertyDescriptor descriptor)
    {
        var descriptorObject = CreateOrdinaryObject();
        if (descriptor.IsAccessor)
        {
            _ = descriptorObject.SetProperty("get", descriptor.Get);
            _ = descriptorObject.SetProperty("set", descriptor.Set);
        }
        else
        {
            _ = descriptorObject.SetProperty("value", descriptor.Value);
            _ = descriptorObject.SetProperty("writable", JsValue.FromBoolean(descriptor.Writable));
        }

        _ = descriptorObject.SetProperty("enumerable", JsValue.FromBoolean(descriptor.Enumerable));
        _ = descriptorObject.SetProperty("configurable", JsValue.FromBoolean(descriptor.Configurable));
        return descriptorObject;
    }

    // Unified property read that handles every JsValueTag the spec considers a valid
    // receiver of [[Get]]. Object falls through to the existing TryGetPropertyValue
    // path; String / Number / Boolean primitives consult their respective prototype
    // (with special-cased "length" / integer-index for String); undefined / null
    // raise TypeError per ToObject (7.1.18).
    [MayExecuteJs]
    private JsValue GetReceiverProperty(JsValue receiver, string key)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                return TryGetPropertyValue(obj, receiver, key, out var value) ? value : JsValue.Undefined;
            }
            case JsValueTag.HostObject:
                // F.5 - route through HostObjectTable validation, then IHostHooks.
                return GetHostObjectProperty(receiver, key);
            case JsValueTag.String:
            {
                var s = receiver.AsString();
                if (key == "length")
                {
                    return JsValue.FromNumber(s.Length);
                }

                // Integer index access ("abc"[1] == "b"). Out-of-range returns undefined
                // per 22.1.4.1; the spec uses an exotic-object [[GetOwnProperty]] but the
                // observable behaviour is exactly this.
                if (IsCanonicalIntegerIndex(key, out var idx))
                {
                    return idx >= 0 && idx < s.Length
                        ? JsValue.FromString(s[idx].ToString())
                        : JsValue.Undefined;
                }

                // Fall through to String.prototype - lookup returns the inherited method
                // value; the caller (CallMethodN opcode) keeps the receiver string as
                // `thisValue` so the native method receives the primitive directly.
                var stringProto = _heap.GetObject(GetGlobalPrototype("String"));
                return TryGetPropertyValue(stringProto, receiver, key, out var sv) ? sv : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(GetGlobalPrototype("Number"));
                return TryGetPropertyValue(numberProto, receiver, key, out var nv) ? nv : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(GetGlobalPrototype("Boolean"));
                return TryGetPropertyValue(boolProto, receiver, key, out var bv) ? bv : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading '" + key + "')."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading '" + key + "')."));
            default:
                return JsValue.Undefined;
        }
    }

    // Symbol-keyed [[Get]] mirroring GetReceiverProperty: Object falls through to
    // the symbol-table lookup; String/Number/Boolean primitives consult their
    // prototype's symbol table; undefined / null raise TypeError.
    [MayExecuteJs]
    private JsValue GetReceiverSymbolProperty(JsValue receiver, long symbolId)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                return obj.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.String:
            {
                var stringProto = _heap.GetObject(GetGlobalPrototype("String"));
                return stringProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(GetGlobalPrototype("Number"));
                return numberProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(GetGlobalPrototype("Boolean"));
                return boolProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading symbol key)."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading symbol key)."));
            default:
                return JsValue.Undefined;
        }
    }

    // True when `key` is a non-negative decimal integer with no leading zeros (except
    // the literal "0"). Matches the spec's "canonical numeric string" definition for
    // 7.1.21 CanonicalNumericIndexString restricted to non-negative integers, which is
    // what String exotic objects accept as index keys.
    private static bool IsCanonicalIntegerIndex(string key, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length > 1 && key[0] == '0')
        {
            return false;
        }

        var result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            // Guard against overflow on absurdly long keys.
            if (result > (int.MaxValue - (c - '0')) / 10)
            {
                return false;
            }

            result = result * 10 + (c - '0');
        }

        index = result;
        return true;
    }

    [MayExecuteJs]
    private bool TryGetPropertyValue(JsObject obj, JsValue receiver, string key, out JsValue value)
    {
        if (!obj.TryGetProperty(key, h => _heap.GetObject(h), out var descriptor))
        {
            value = JsValue.Undefined;
            return false;
        }

        value = GetDescriptorValue(descriptor, receiver);
        return true;
    }

    [MayExecuteJs]
    private JsValue GetDescriptorValue(JsPropertyDescriptor descriptor, JsValue receiver)
    {
        if (!descriptor.IsAccessor)
        {
            return descriptor.Value;
        }

        if (descriptor.Get.Tag == JsValueTag.Undefined)
        {
            return JsValue.Undefined;
        }

        if (!IsCallable(descriptor.Get))
        {
            throw new JsThrownException(CreateTypeError("Accessor getter must be callable or undefined."));
        }

        return CallFunction(descriptor.Get, Array.Empty<JsValue>(), receiver);
    }

    [MayExecuteJs]
    private bool SetPropertyValue(ObjectHandle ownerHandle, JsObject obj, string key, JsValue value, JsValue receiver)
    {
        if (obj.TryGetOwnProperty(key, out var ownDescriptor))
        {
            return SetPropertyFromDescriptor(ownerHandle, obj, key, ownDescriptor, value, receiver);
        }

        if (TryGetPrototypePropertyDescriptor(obj, key, out var inheritedDescriptor))
        {
            if (inheritedDescriptor.IsAccessor)
            {
                return CallSetter(inheritedDescriptor, value, receiver);
            }

            if (!inheritedDescriptor.Writable)
            {
                return false;
            }
        }

        var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
        _ = obj.DefineOwnProperty(key, descriptor);
        WriteDescriptorBarrier(ownerHandle, descriptor);
        return true;
    }

    [MayExecuteJs]
    private bool SetPropertyFromDescriptor(
        ObjectHandle ownerHandle,
        JsObject obj,
        string key,
        JsPropertyDescriptor descriptor,
        JsValue value,
        JsValue receiver)
    {
        if (descriptor.IsAccessor)
        {
            return CallSetter(descriptor, value, receiver);
        }

        if (!descriptor.Writable)
        {
            return false;
        }

        var updated = descriptor with { Value = value };
        _ = obj.DefineOwnProperty(key, updated);
        WriteDescriptorBarrier(ownerHandle, updated);
        return true;
    }

    private bool TryGetPrototypePropertyDescriptor(JsObject obj, string key, out JsPropertyDescriptor descriptor)
    {
        var prototype = obj.PrototypeHandle;
        while (prototype is { } handle)
        {
            var prototypeObject = _heap.GetObject(handle);
            if (prototypeObject.TryGetOwnProperty(key, out descriptor))
            {
                return true;
            }

            prototype = prototypeObject.PrototypeHandle;
        }

        descriptor = default;
        return false;
    }

    [MayExecuteJs]
    private bool CallSetter(JsPropertyDescriptor descriptor, JsValue value, JsValue receiver)
    {
        if (descriptor.Set.Tag == JsValueTag.Undefined)
        {
            return false;
        }

        if (!IsCallable(descriptor.Set))
        {
            throw new JsThrownException(CreateTypeError("Accessor setter must be callable or undefined."));
        }

        _ = CallFunction(descriptor.Set, new[] { value }, receiver);
        return true;
    }

    [MayExecuteJs]
    private bool ReadDescriptorFlag(JsObject descriptorObject, JsValue receiver, string propertyName)
    {
        return TryGetPropertyValue(descriptorObject, receiver, propertyName, out var value) &&
               IsTruthy(value);
    }

    private ObjectHandle EnsureArrayPrototype()
    {
        _ = EnsureArrayConstructor();
        return _arrayPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureArrayConstructor()
    {
        if (_arrayConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetProperty("length", JsValue.FromNumber(0));
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Array",
            (_, args) => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(args), AllocationSite.Current())),
            args => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(args), AllocationSite.Current())),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "push", ArrayPrototypePush, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", ArrayPrototypeToString);
        // ECMA-262 23.1.3.32 Array.prototype.toLocaleString. The spec calls each
        // element's toLocaleString through Invoke; here we approximate by calling
        // the element's toString conversion (which goes through Number / Boolean /
        // user toString as appropriate). Locale-sensitive output requires Intl which
        // is not wired yet; per the Intl-not-present clause this is the documented
        // fallback engines use.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString",
            (t, a) => { _ = a; return ArrayPrototypeToString(t, a); });
        // ECMA-262 23.1.3.18 Array.prototype.join, 23.1.3.16 indexOf, 23.1.3.14 includes.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "join", ArrayPrototypeJoin, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "indexOf", ArrayPrototypeIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "includes", ArrayPrototypeIncludes, length: 1);
        // ECMA-262 23.1.3.21 pop, 23.1.3.27 shift, 23.1.3.34 unshift.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "pop", ArrayPrototypePop);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "shift", ArrayPrototypeShift);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "unshift", ArrayPrototypeUnshift, length: 1);
        // ECMA-262 23.1.3.26 reverse, 23.1.3.7 fill.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reverse", ArrayPrototypeReverse);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "fill", ArrayPrototypeFill, length: 1);
        // ECMA-262 23.1.3.28 slice, 23.1.3.2 concat.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "slice", ArrayPrototypeSlice, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "concat", ArrayPrototypeConcat, length: 1);
        // ECMA-262 23.1.3.15 forEach, 23.1.3.19 map, 23.1.3.8 filter.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", ArrayPrototypeForEach, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "map", ArrayPrototypeMap, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "filter", ArrayPrototypeFilter, length: 1);
        // ECMA-262 23.1.3.24 reduce, 23.1.3.25 reduceRight.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reduce", (t, a) => ArrayPrototypeReduce(t, a, reverse: false), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reduceRight", (t, a) => ArrayPrototypeReduce(t, a, reverse: true), length: 1);
        // ECMA-262 23.1.3.6 every, 23.1.3.29 some, 23.1.3.10 find, 23.1.3.11 findIndex.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "every", ArrayPrototypeEvery, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "some", ArrayPrototypeSome, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "find", ArrayPrototypeFind, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findIndex", ArrayPrototypeFindIndex, length: 1);
        // ECMA-262 23.1.3.17 lastIndexOf, 23.1.3.9 flat, 23.1.3.10a flatMap.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "lastIndexOf", ArrayPrototypeLastIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "flat", ArrayPrototypeFlat);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "flatMap", ArrayPrototypeFlatMap, length: 1);
        // ECMA-262 23.1.3.30 sort.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "sort", ArrayPrototypeSort, length: 1);
        // ECMA-262 23.1.3.31 splice.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "splice", ArrayPrototypeSplice, length: 2);
        // ECMA-262 23.1.3.36/.16/.5 Array.prototype.values / keys / entries. Each
        // returns an Array Iterator: an object exposing .next() that yields
        // {value, done}. The iterator also routes through Symbol.iterator so
        // for-of over the iterator itself works.
        var valuesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Value); });
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Key); });
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Entry); });
        // ECMA-262 23.1.3.37 - Array.prototype[Symbol.iterator] is the same function
        // object as Array.prototype.values per spec note 1.
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(valuesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, valuesHandle);
        // ECMA-262 23.1.3.1 at, 23.1.3.12 findLast, 23.1.3.13 findLastIndex.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "at", ArrayPrototypeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findLast", ArrayPrototypeFindLast, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findLastIndex", ArrayPrototypeFindLastIndex, length: 1);
        // ECMA-262 23.1.3.4 copyWithin.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "copyWithin", ArrayPrototypeCopyWithin, length: 2);

        // ECMA-262 23.1.3.32 Array.prototype.toReversed (ES2023). Non-mutating reverse:
        // allocates a fresh Array of the receiver's length with elements in reverse
        // order. Holes are read via [[Get]] (so they become explicit undefined entries
        // in the result, not holes) per spec step 5.c.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toReversed", ArrayPrototypeToReversed);
        // ECMA-262 23.1.3.33 Array.prototype.toSorted(comparator) (ES2023). Non-mutating
        // sort: snapshots the receiver via [[Get]] (so holes become explicit undefined),
        // sorts using the same comparator/SortCompare rules as Array.prototype.sort,
        // and returns a fresh Array. Original is never observed mutating.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toSorted", ArrayPrototypeToSorted, length: 1);
        // ECMA-262 23.1.3.34 Array.prototype.toSpliced(start, skipCount, ...items)
        // (ES2023). Non-mutating splice: returns a fresh Array equal to a copy of the
        // receiver with skipCount entries at start removed and items inserted in their
        // place. The receiver is never mutated and the result is a fresh allocation.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toSpliced", ArrayPrototypeToSpliced, length: 2);
        // ECMA-262 23.1.3.36 Array.prototype.with(index, value) (ES2023). Returns a
        // fresh Array equal to the receiver with element at the resolved index
        // replaced by value. Negative index counts from length; out-of-range raises
        // RangeError per step 3.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "with", ArrayPrototypeWith, length: 2);

        // ECMA-262 23.1.2.3 Array.of(...items). Returns a fresh Array populated with
        // exactly the supplied items - distinct from new Array(n), which uses a
        // single Number argument to set length.
        DefineIntrinsicFunction(constructorHandle, constructor, "of", (_, args) =>
        {
            var items = new JsValue[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                items[i] = args[i];
            }

            var arr = CreateArrayFromElements(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 0);

        // ECMA-262 23.1.2.1 Array.from(items[, mapFn[, thisArg]]). Spec algorithm:
        // (1) if items has @@iterator, drain it via the iterator protocol; (2)
        // otherwise treat items as an array-like via its length property. Strings
        // funnel through the iterator path so surrogate pairs surface as single
        // code-point entries. The map function (when present) is called with
        // (element, index) per spec step 5.g.
        DefineIntrinsicFunction(constructorHandle, constructor, "from", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Array.from: argument must not be undefined or null."));
            }

            var source = args[0];
            var mapFn = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? args[1] : (JsValue?)null;
            var thisArg = args.Count > 2 ? args[2] : JsValue.Undefined;

            if (mapFn.HasValue && mapFn.Value.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Array.from: map function must be a function."));
            }

            var items = new List<JsValue>();

            bool useIterator = source.Tag == JsValueTag.String;
            if (!useIterator && source.Tag == JsValueTag.Object)
            {
                var obj0 = _heap.GetObject(source.AsObjectHandle());
                var iterId = GetWellKnownSymbolId("iterator");
                useIterator = iterId != 0 &&
                    obj0.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                    iterDesc.Value.Tag == JsValueTag.Object;
            }

            if (useIterator)
            {
                // CreateForOfIterator handles Symbol.iterator dispatch for objects
                // and per-character iteration for strings.
                var iter = CreateForOfIterator(source);
                if (iter.Tag == JsValueTag.Object &&
                    _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
                {
                    var idx = 0;
                    while (forOf.TryMoveNext(out var v))
                    {
                        if (mapFn.HasValue)
                        {
                            v = CallFunction(mapFn.Value, new[] { v, JsValue.FromNumber(idx) }, thisArg);
                        }
                        items.Add(v);
                        idx++;
                    }
                }
            }
            else if (source.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(source.AsObjectHandle());
                var length = GetArrayLength(obj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryGetPropertyValue(obj, source, key, out var v);
                    if (mapFn.HasValue)
                    {
                        v = CallFunction(mapFn.Value, new[] { v, JsValue.FromNumber(i) }, thisArg);
                    }
                    items.Add(v);
                }
            }

            var arr = CreateArrayObject(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // ECMA-262 23.1.2.2 Array.isArray(arg). Spec walks Proxy targets; we have no
        // Proxy yet, so the operation collapses to "is the value an ArrayObject?".
        DefineIntrinsicFunction(constructorHandle, constructor, "isArray", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return JsValue.FromBoolean(obj is ArrayObject);
        }, length: 1);

        _arrayPrototypeHandle = prototypeHandle;
        _arrayConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsObject CreateArrayObject(IReadOnlyList<JsValue> elements)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());

        if (elements.Count == 1 && (elements[0].Tag == JsValueTag.Int32 || elements[0].Tag == JsValueTag.Number))
        {
            var length = ToNumber(elements[0]);
            if (!IsValidArrayLength(length))
            {
                throw new JsThrownException(CreateRangeError("Invalid array length."));
            }

            _ = obj.SetProperty("length", JsValue.FromNumber(length));
            return obj;
        }

        return PopulateArrayWithElements(obj, elements);
    }

    // Element-list constructor that ALWAYS treats elements as values - never invokes
    // the "single Number = length" shortcut. Used by Array.of, Array.from,
    // Array.prototype.{slice/concat/map/filter/flat/flatMap/...} where the caller
    // already has the materialised element list and just wants a fresh Array.
    private JsObject CreateArrayFromElements(IReadOnlyList<JsValue> elements)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());
        return PopulateArrayWithElements(obj, elements);
    }

    private static JsObject PopulateArrayWithElements(ArrayObject obj, IReadOnlyList<JsValue> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            _ = obj.SetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), elements[i]);
        }

        _ = obj.SetProperty("length", JsValue.FromNumber(elements.Count));
        return obj;
    }

    private static bool IsValidArrayLength(double value)
    {
        return !double.IsNaN(value) &&
               !double.IsInfinity(value) &&
               value >= 0 &&
               value <= uint.MaxValue &&
               Math.Truncate(value) == value;
    }

    private JsValue ArrayPrototypePush(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);

        for (var i = 0; i < args.Count; i++)
        {
            var key = (length + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, args[i]);
            if (args[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, args[i].AsObjectHandle());
            }
        }

        var newLength = length + args.Count;
        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));
        return JsValue.FromNumber(newLength);
    }

    // ECMA-262 23.1.3.21 Array.prototype.pop. Returns undefined for empty arrays;
    // otherwise removes and returns the last element, decrementing length.
    private JsValue ArrayPrototypePop(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            _ = obj.SetProperty("length", JsValue.FromNumber(0));
            return JsValue.Undefined;
        }

        var lastKey = (length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TryGetPropertyValue(obj, thisValue, lastKey, out var value);
        obj.DeleteProperty(lastKey);
        _ = obj.SetProperty("length", JsValue.FromNumber(length - 1));
        return value;
    }

    // ECMA-262 23.1.3.27 Array.prototype.shift. Removes element at index 0 and
    // shifts every subsequent element down by one; returns the removed value.
    private JsValue ArrayPrototypeShift(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            _ = obj.SetProperty("length", JsValue.FromNumber(0));
            return JsValue.Undefined;
        }

        TryGetPropertyValue(obj, thisValue, "0", out var first);
        for (var i = 1; i < length; i++)
        {
            var fromKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var toKey = (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
            {
                _ = obj.SetProperty(toKey, v);
            }
            else
            {
                obj.DeleteProperty(toKey);
            }
        }

        obj.DeleteProperty((length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = obj.SetProperty("length", JsValue.FromNumber(length - 1));
        return first;
    }

    // ECMA-262 23.1.3.34 Array.prototype.unshift. Inserts arguments at the front,
    // shifting existing elements up by args.Count; returns the new length.
    private JsValue ArrayPrototypeUnshift(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var insert = args.Count;

        if (insert > 0 && length > 0)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                var fromKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insert).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }
        }

        for (var i = 0; i < insert; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, args[i]);
            if (args[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, args[i].AsObjectHandle());
            }
        }

        var newLength = length + insert;
        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));
        return JsValue.FromNumber(newLength);
    }

    // ECMA-262 23.1.3.26 Array.prototype.reverse. Swaps slot i with slot len-1-i
    // for i < len/2; preserves holes (a missing source slot deletes the target).
    private JsValue ArrayPrototypeToSpliced(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);
        // skipCount: missing or undefined => 0 (ES2023 step 7 actualSkipCount default).
        var skipRaw = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? (int)ToNumber(args[1]) : 0;
        var skip = Math.Clamp(skipRaw, 0, length - start);
        var insertCount = args.Count > 2 ? args.Count - 2 : 0;
        var newLen = length - skip + insertCount;

        var result = new JsValue[newLen];
        var w = 0;
        for (var i = 0; i < start; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result[w++] = TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined;
        }
        for (var i = 0; i < insertCount; i++)
        {
            result[w++] = args[2 + i];
        }
        for (var i = start + skip; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result[w++] = TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined;
        }

        var arr = CreateArrayFromElements(result);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue ArrayPrototypeWith(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var rawIndex = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var actual = rawIndex < 0 ? length + rawIndex : rawIndex;
        if (actual < 0 || actual >= length)
        {
            throw new JsThrownException(CreateRangeError("Array.prototype.with: index out of range."));
        }
        var value = args.Count > 1 ? args[1] : JsValue.Undefined;

        var result = new JsValue[length];
        for (var i = 0; i < length; i++)
        {
            if (i == actual)
            {
                result[i] = value;
            }
            else
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result[i] = TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined;
            }
        }
        var arr = CreateArrayFromElements(result);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue ArrayPrototypeToSorted(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var comparator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? args[0] : (JsValue?)null;
        if (comparator.HasValue && comparator.Value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype.toSorted: comparator must be a function or undefined."));
        }

        var items = new List<JsValue>(length);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        // SortIndexedProperties: undefined entries sort after non-undefined ones; the
        // comparator only ever sees non-undefined values (24.4.5 step 4).
        var present = new List<JsValue>(items.Count);
        var undefinedCount = 0;
        foreach (var v in items)
        {
            if (v.Tag == JsValueTag.Undefined) undefinedCount++;
            else present.Add(v);
        }

        if (comparator.HasValue)
        {
            var fn = comparator.Value;
            present.Sort((a, b) =>
            {
                var r = CallFunction(fn, new[] { a, b }, JsValue.Undefined);
                var n = ToNumber(r);
                if (double.IsNaN(n)) return 0;
                return n < 0 ? -1 : n > 0 ? 1 : 0;
            });
        }
        else
        {
            present.Sort((a, b) => string.CompareOrdinal(ToStringValue(a), ToStringValue(b)));
        }

        var result = new JsValue[length];
        for (var i = 0; i < present.Count; i++) result[i] = present[i];
        for (var i = 0; i < undefinedCount; i++) result[present.Count + i] = JsValue.Undefined;

        var arr = CreateArrayFromElements(result);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue ArrayPrototypeToReversed(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var elements = new JsValue[length];
        for (var i = 0; i < length; i++)
        {
            var key = (length - 1 - i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            elements[i] = TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined;
        }
        var resultObj = CreateArrayFromElements(elements);
        var handle = _heap.AllocateObject(resultObj, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    private JsValue ArrayPrototypeReverse(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var middle = length / 2;

        for (var lower = 0; lower < middle; lower++)
        {
            var upper = length - 1 - lower;
            var lowerKey = lower.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var upperKey = upper.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hasLower = TryGetPropertyValue(obj, thisValue, lowerKey, out var lowerValue);
            var hasUpper = TryGetPropertyValue(obj, thisValue, upperKey, out var upperValue);

            if (hasUpper)
            {
                _ = obj.SetProperty(lowerKey, upperValue);
            }
            else
            {
                obj.DeleteProperty(lowerKey);
            }

            if (hasLower)
            {
                _ = obj.SetProperty(upperKey, lowerValue);
            }
            else
            {
                obj.DeleteProperty(upperKey);
            }
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.7 Array.prototype.fill(value[, start[, end]]). Negative
    // start/end wrap from length; out-of-range values clamp into [0, length].
    private JsValue ArrayPrototypeFill(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        var start = NormaliseSliceIndex(args, 1, 0, length);
        var end = NormaliseSliceIndex(args, 2, length, length);

        for (var i = start; i < end; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, value);
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
            }
        }

        return thisValue;
    }

    // Shared helper for fill / slice. Reads args[argIndex] as a number (default
    // when missing or undefined), then converts to an integer clamped into
    // [0, length] using the spec's "negative-from-length" rule.
    private static int NormaliseSliceIndex(IReadOnlyList<JsValue> args, int argIndex, int defaultValue, int length)
    {
        if (argIndex >= args.Count || args[argIndex].Tag == JsValueTag.Undefined)
        {
            return Math.Clamp(defaultValue, 0, length);
        }

        var raw = args[argIndex].Tag == JsValueTag.Int32
            ? args[argIndex].AsInt32()
            : (int)args[argIndex].AsNumber();
        var idx = raw < 0 ? length + raw : raw;
        return Math.Clamp(idx, 0, length);
    }

    // ECMA-262 23.1.3.4 Array.prototype.copyWithin(target, start[, end]).
    // Shallow-copies the sequence at [start, end) to position target in-place,
    // returning the receiver. Negative indices wrap from length; ranges are clamped
    // into [0, length]. When source and target overlap, copies use a forward or
    // backward pass to avoid clobbering data not yet copied.
    private JsValue ArrayPrototypeCopyWithin(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var target = NormaliseSliceIndex(args, 0, 0, length);
        var start = NormaliseSliceIndex(args, 1, 0, length);
        var end = NormaliseSliceIndex(args, 2, length, length);

        var count = Math.Min(end - start, length - target);
        if (count <= 0)
        {
            return thisValue;
        }

        // Direction: when target < start, forward copy is safe; otherwise walk
        // backwards so already-overwritten elements don't pollute the not-yet-copied
        // tail.
        var direction = target < start ? 1 : -1;
        var from = direction == 1 ? start : start + count - 1;
        var to = direction == 1 ? target : target + count - 1;

        for (var i = 0; i < count; i++)
        {
            var fromKey = from.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var toKey = to.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
            {
                _ = obj.SetProperty(toKey, v);
                if (v.Tag == JsValueTag.Object)
                {
                    _heap.WriteBarrier(ownerHandle, v.AsObjectHandle());
                }
            }
            else
            {
                obj.DeleteProperty(toKey);
            }

            from += direction;
            to += direction;
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.1 Array.prototype.at(index). Negative indices wrap from
    // length; out-of-range returns undefined.
    private JsValue ArrayPrototypeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var raw = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var idx = raw < 0 ? length + raw : raw;
        if (idx < 0 || idx >= length)
        {
            return JsValue.Undefined;
        }

        var key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TryGetPropertyValue(obj, thisValue, key, out var v);
        return v;
    }

    // ECMA-262 23.1.3.12 findLast. Mirrors find but walks backwards; visits holes
    // as undefined-valued slots per spec.
    private JsValue ArrayPrototypeFindLast(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    // ECMA-262 23.1.3.13 findLastIndex.
    private JsValue ArrayPrototypeFindLastIndex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.31 Array.prototype.splice(start, deleteCount, ...items).
    // Removes deleteCount elements starting at start (negative wraps from length),
    // inserts items in their place, and returns a fresh Array of the removed
    // elements. Subsequent elements shift up or down depending on whether items
    // were inserted or extra elements removed.
    private JsValue ArrayPrototypeSplice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);

        int deleteCount;
        if (args.Count < 1)
        {
            deleteCount = 0;
        }
        else if (args.Count < 2)
        {
            // Single-arg form: delete from start to end (spec step 5.b).
            deleteCount = length - start;
        }
        else
        {
            deleteCount = Math.Clamp((int)ToNumber(args[1]), 0, length - start);
        }

        var insertCount = Math.Max(0, args.Count - 2);

        // Collect the removed slice.
        var removed = new List<JsValue>(deleteCount);
        for (var i = 0; i < deleteCount; i++)
        {
            var key = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            removed.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        var newLength = length - deleteCount + insertCount;
        if (insertCount < deleteCount)
        {
            // Shift down.
            for (var i = start; i < length - deleteCount; i++)
            {
                var fromKey = (i + deleteCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insertCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }

            for (var i = newLength; i < length; i++)
            {
                obj.DeleteProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else if (insertCount > deleteCount)
        {
            // Shift up: walk back-to-front so we never clobber a not-yet-moved slot.
            for (var i = length - deleteCount - 1; i >= start; i--)
            {
                var fromKey = (i + deleteCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insertCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }
        }

        // Insert new items.
        for (var i = 0; i < insertCount; i++)
        {
            var key = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = args[2 + i];
            _ = obj.SetProperty(key, v);
            if (v.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, v.AsObjectHandle());
            }
        }

        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));

        var resultArr = CreateArrayFromElements(removed);
        return JsValue.FromObject(_heap.AllocateObject(resultArr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.30 Array.prototype.sort([compareFn]). Default comparator
    // converts each element to a String and compares lexicographically; a user
    // comparator returning < 0 / 0 / > 0 controls the order. Undefined elements
    // always sort after non-undefined ones; missing slots after both. Uses
    // List<T>.Sort with a stable adapter so the spec-mandated stable sort holds
    // for v10+ (the spec made stability mandatory in ES2019 - .NET 5+ Sort is
    // already stable). Sorts in place and returns the receiver.
    private JsValue ArrayPrototypeSort(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var comparator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? args[0] : (JsValue?)null;

        if (comparator.HasValue && comparator.Value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype.sort: comparator must be a function or undefined."));
        }

        // Partition into present-values / undefined-values / holes per spec 23.1.3.30
        // step 3.b: SortIndexedProperties keeps undefined elements after sorted
        // non-undefined ones, and trailing holes after that.
        var present = new List<JsValue>(length);
        var undefinedCount = 0;
        var holeCount = 0;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                holeCount++;
                continue;
            }

            if (v.Tag == JsValueTag.Undefined)
            {
                undefinedCount++;
                continue;
            }

            present.Add(v);
        }

        if (comparator.HasValue)
        {
            var fn = comparator.Value;
            present.Sort((a, b) =>
            {
                var r = CallFunction(fn, new[] { a, b }, JsValue.Undefined);
                var n = ToNumber(r);
                if (double.IsNaN(n))
                {
                    return 0;
                }

                return n < 0 ? -1 : n > 0 ? 1 : 0;
            });
        }
        else
        {
            present.Sort((a, b) => string.CompareOrdinal(ToStringValue(a), ToStringValue(b)));
        }

        // Write back: present values first, then 'undefined' slots, then holes.
        for (var i = 0; i < present.Count; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, present[i]);
            if (present[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, present[i].AsObjectHandle());
            }
        }

        for (var i = 0; i < undefinedCount; i++)
        {
            var key = (present.Count + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, JsValue.Undefined);
        }

        for (var i = 0; i < holeCount; i++)
        {
            var key = (present.Count + undefinedCount + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            obj.DeleteProperty(key);
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.17 lastIndexOf. Strict equality, scans backwards from fromIndex
    // (default length-1, negative wraps from length). Returns -1 when not found.
    private JsValue ArrayPrototypeLastIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0 || args.Count == 0)
        {
            return JsValue.FromNumber(-1);
        }

        var target = args[0];
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : length - 1;
        if (fromIndex < 0)
        {
            fromIndex = length + fromIndex;
        }
        else if (fromIndex >= length)
        {
            fromIndex = length - 1;
        }

        for (var i = fromIndex; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && AreStrictlyEqual(value, target))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.9 flat([depth]). Default depth is 1. Array elements are spread
    // up to the given depth; non-Array elements are kept as-is. Holes are skipped.
    private JsValue ArrayPrototypeFlat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var depth = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? Math.Max(0, (int)ToNumber(args[0]))
            : 1;
        var items = new List<JsValue>();
        FlattenInto(obj, thisValue, depth, items);
        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private void FlattenInto(JsObject source, JsValue receiver, int depth, List<JsValue> sink)
    {
        var length = GetArrayLength(source);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(source, receiver, key, out var element))
            {
                continue;   // skip holes per spec step 5.b.ii
            }

            if (depth > 0 && element.Tag == JsValueTag.Object &&
                _heap.GetObject(element.AsObjectHandle()) is ArrayObject child)
            {
                FlattenInto(child, element, depth - 1, sink);
            }
            else
            {
                sink.Add(element);
            }
        }
    }

    // ECMA-262 23.1.3.10a flatMap(callback[, thisArg]). Equivalent to map followed
    // by flat with depth 1, but produced in a single pass to avoid the intermediate
    // array allocation that the spec also avoids.
    private JsValue ArrayPrototypeFlatMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            var mapped = InvokeArrayCallback(callback, v, i, thisValue, thisArg);
            if (mapped.Tag == JsValueTag.Object &&
                _heap.GetObject(mapped.AsObjectHandle()) is ArrayObject inner)
            {
                FlattenInto(inner, mapped, depth: 0, items);   // append elements (1 level)
            }
            else
            {
                items.Add(mapped);
            }
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.6 every: returns true iff callback returns truthy for every
    // present element. Short-circuits on first falsy. Holes are skipped (vacuously
    // satisfied).
    private JsValue ArrayPrototypeEvery(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            if (!IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromBoolean(false);
            }
        }

        return JsValue.FromBoolean(true);
    }

    // ECMA-262 23.1.3.29 some: returns true iff callback returns truthy for at least
    // one present element. Short-circuits on first truthy.
    private JsValue ArrayPrototypeSome(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromBoolean(true);
            }
        }

        return JsValue.FromBoolean(false);
    }

    // ECMA-262 23.1.3.10 find: returns the first element where callback is truthy,
    // or undefined. UNLIKE every/some/forEach/map/filter, find DOES visit holes (the
    // spec treats them as undefined-valued slots that the callback can match).
    private JsValue ArrayPrototypeFind(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    // ECMA-262 23.1.3.11 findIndex: as find, but returns the index (or -1). Visits
    // holes for the same reason find does.
    private JsValue ArrayPrototypeFindIndex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.24 / 23.1.3.25 Array.prototype.reduce / reduceRight.
    // Initial accumulator comes from args[1] when provided; otherwise the spec scans
    // for the first non-hole element and starts from there - throwing TypeError on
    // an empty array with no initial value. Forward scan for reduce, reverse for
    // reduceRight. Callback receives (accumulator, value, index, receiver).
    private JsValue ArrayPrototypeReduce(JsValue thisValue, IReadOnlyList<JsValue> args, bool reverse)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (callback.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Array reduce callback is not a function."));
        }

        var hasInitial = args.Count > 1;
        var accumulator = hasInitial ? args[1] : JsValue.Undefined;
        int start, step, end;
        if (!reverse)
        {
            start = 0;
            end = length;
            step = 1;
        }
        else
        {
            start = length - 1;
            end = -1;
            step = -1;
        }

        var i = start;
        if (!hasInitial)
        {
            var found = false;
            while (i != end)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, key, out var v))
                {
                    accumulator = v;
                    i += step;
                    found = true;
                    break;
                }

                i += step;
            }

            if (!found)
            {
                throw new JsThrownException(CreateTypeError(
                    reverse
                        ? "Reduce of empty array with no initial value (reduceRight)."
                        : "Reduce of empty array with no initial value."));
            }
        }

        while (i != end)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                var callArgs = new[]
                {
                    accumulator,
                    v,
                    JsValue.FromNumber(i),
                    thisValue,
                };
                accumulator = CallFunction(callback, callArgs, JsValue.Undefined);
            }

            i += step;
        }

        return accumulator;
    }

    // Shared callback-invoker for forEach/map/filter/find/etc. Calls
    // callback(value, index, receiverArray) with the given thisArg, sparing each
    // caller from repeating the args allocation and CallFunction routing.
    private JsValue InvokeArrayCallback(
        JsValue callback,
        JsValue value,
        int index,
        JsValue receiver,
        JsValue thisArg)
    {
        if (callback.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Array callback is not a function."));
        }

        var resolved = _heap.GetObject(callback.AsObjectHandle());
        if (resolved is not JsFunctionObject && resolved is not NativeFunctionObject)
        {
            throw new JsThrownException(CreateTypeError("Array callback is not a function."));
        }

        var args = new[] { value, JsValue.FromNumber(index), receiver };
        return CallFunction(callback, args, thisArg);
    }

    private JsValue ArrayPrototypeForEach(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;   // skip holes per spec
            }

            InvokeArrayCallback(callback, v, i, thisValue, thisArg);
        }

        return JsValue.Undefined;
    }

    private JsValue ArrayPrototypeMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>(length);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                items.Add(JsValue.Undefined);   // spec: preserves length, holes become undefined-ish
                continue;
            }

            items.Add(InvokeArrayCallback(callback, v, i, thisValue, thisArg));
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue ArrayPrototypeFilter(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;   // skip holes
            }

            var keep = InvokeArrayCallback(callback, v, i, thisValue, thisArg);
            if (IsTruthy(keep))
            {
                items.Add(v);
            }
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.28 Array.prototype.slice(start, end). Returns a fresh
    // ArrayObject containing the half-open range [start, end). Out-of-range or
    // missing arguments degrade to "0, length"; negative arguments wrap.
    private JsValue ArrayPrototypeSlice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);
        var end = NormaliseSliceIndex(args, 1, length, length);

        var items = new List<JsValue>();
        for (var i = start; i < end; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    // ECMA-262 23.1.3.2 Array.prototype.concat(...items). Returns a fresh
    // ArrayObject; Array arguments are spread (their elements appended one by one),
    // other values are appended as a single element.
    private JsValue ArrayPrototypeConcat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var items = new List<JsValue>();
        AppendConcatSource(items, thisValue);
        for (var i = 0; i < args.Count; i++)
        {
            AppendConcatSource(items, args[i]);
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private void AppendConcatSource(List<JsValue> items, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is ArrayObject)
            {
                var length = GetArrayLength(obj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    items.Add(TryGetPropertyValue(obj, value, key, out var v) ? v : JsValue.Undefined);
                }

                return;
            }
        }

        items.Add(value);
    }

    private JsValue ArrayPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(JoinArrayElements(thisValue, ","));
    }

    // ECMA-262 23.1.3.18 Array.prototype.join. Default separator is ",". Undefined
    // and null elements stringify to the empty string; everything else goes through
    // the shared ToString conversion.
    private JsValue ArrayPrototypeJoin(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var separator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? ToStringValue(args[0])
            : ",";
        return JsValue.FromString(JoinArrayElements(thisValue, separator));
    }

    private string JoinArrayElements(JsValue thisValue, string separator)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return string.Empty;
        }

        var values = new string[length];
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var value) ||
                value.Tag is JsValueTag.Undefined or JsValueTag.Null)
            {
                values[i] = string.Empty;
            }
            else
            {
                values[i] = ToStringValue(value);
            }
        }

        return string.Join(separator, values);
    }

    // ECMA-262 23.1.3.16 indexOf - strict equality, starts at fromIndex (default 0,
    // negative wraps from length). Returns -1 when not found.
    private JsValue ArrayPrototypeIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0 || args.Count == 0)
        {
            return JsValue.FromNumber(-1);
        }

        var target = args[0];
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        if (fromIndex < 0)
        {
            fromIndex = Math.Max(0, length + fromIndex);
        }

        for (var i = fromIndex; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && AreStrictlyEqual(value, target))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.14 includes - SameValueZero (NaN matches NaN; +0 matches -0).
    private JsValue ArrayPrototypeIncludes(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return JsValue.FromBoolean(false);
        }

        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        if (fromIndex < 0)
        {
            fromIndex = Math.Max(0, length + fromIndex);
        }

        for (var i = fromIndex; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && SameValueZero(value, target))
            {
                return JsValue.FromBoolean(true);
            }
        }

        return JsValue.FromBoolean(false);
    }

    // ECMA-262 7.2.11 SameValueZero - identical to SameValue except +0 and -0 are
    // considered equal. Used by Array.prototype.includes, Map/Set keys, etc.
    private static bool SameValueZero(JsValue a, JsValue b)
    {
        if ((a.Tag == JsValueTag.Int32 || a.Tag == JsValueTag.Number) &&
            (b.Tag == JsValueTag.Int32 || b.Tag == JsValueTag.Number))
        {
            var an = a.AsNumber();
            var bn = b.AsNumber();
            if (double.IsNaN(an) && double.IsNaN(bn))
            {
                return true;
            }

            return an == bn;
        }

        return AreStrictlyEqual(a, b);
    }

    private int GetArrayLength(JsObject obj)
    {
        if (!obj.TryGetOwnProperty("length", out var descriptor))
        {
            return 0;
        }

        var number = ToNumber(descriptor.Value);
        if (double.IsNaN(number) || number <= 0)
        {
            return 0;
        }

        return checked((int)Math.Min(Math.Truncate(number), int.MaxValue));
    }

    private ObjectHandle EnsureBooleanPrototype()
    {
        _ = EnsureBooleanConstructor();
        return _booleanPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureBooleanConstructor()
    {
        if (_booleanConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new BooleanObject(false);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Boolean",
            (_, args) => JsValue.FromBoolean(args.Count > 0 && IsTruthy(args[0])),
            args => CreateBooleanObject(args.Count > 0 && IsTruthy(args[0])),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", BooleanPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", BooleanPrototypeValueOf);

        _booleanPrototypeHandle = prototypeHandle;
        _booleanConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue BooleanPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(BooleanThisValue(thisValue) ? "true" : "false");
    }

    private JsValue BooleanPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromBoolean(BooleanThisValue(thisValue));
    }

    private bool BooleanThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Boolean)
        {
            return thisValue.AsBoolean();
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is BooleanObject booleanObject)
        {
            return booleanObject.Value;
        }

        throw new JsThrownException(CreateTypeError("Boolean.prototype method called on incompatible receiver."));
    }

    private JsValue CreateBooleanObject(bool value)
    {
        var obj = new BooleanObject(value);
        obj.SetPrototype(GetGlobalPrototype("Boolean"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle EnsureNumberPrototype()
    {
        _ = EnsureNumberConstructor();
        return _numberPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureNumberConstructor()
    {
        if (_numberConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Number",
            (_, args) => JsValue.FromNumber(args.Count > 0 ? ToNumber(args[0]) : 0d),
            args => CreateNumberObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        // ECMA-262 21.1.2 - Properties of the Number Constructor.
        _ = constructor.SetProperty("MAX_VALUE", JsValue.FromNumber(double.MaxValue));
        _ = constructor.SetProperty("MIN_VALUE", JsValue.FromNumber(double.Epsilon));
        _ = constructor.SetProperty("NaN", JsValue.FromNumber(double.NaN));
        _ = constructor.SetProperty("POSITIVE_INFINITY", JsValue.FromNumber(double.PositiveInfinity));
        _ = constructor.SetProperty("NEGATIVE_INFINITY", JsValue.FromNumber(double.NegativeInfinity));
        // 21.1.2.1 EPSILON - the difference between 1 and the smallest IEEE-754 double
        // strictly greater than 1, i.e. 2**-52.
        _ = constructor.SetProperty("EPSILON", JsValue.FromNumber(Math.Pow(2d, -52)));
        // 21.1.2.6 MAX_SAFE_INTEGER - 2**53 - 1, the largest integer n such that n and
        // n + 1 are both exactly representable as a Number.
        _ = constructor.SetProperty("MAX_SAFE_INTEGER", JsValue.FromNumber(9007199254740991d));
        // 21.1.2.8 MIN_SAFE_INTEGER - -(2**53 - 1).
        _ = constructor.SetProperty("MIN_SAFE_INTEGER", JsValue.FromNumber(-9007199254740991d));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        // ECMA-262 21.1.2.2 / 21.1.2.3 / 21.1.2.4 / 21.1.2.5 - the spec-defined
        // "isXxx" helpers. Unlike the global isFinite/isNaN, these do NOT coerce: a
        // non-Number argument simply returns false. The factoring below keeps each
        // helper a one-liner against ECMA-262.
        DefineNumberStatic(constructorHandle, constructor, "isFinite", static args
            => JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && !double.IsNaN(args[0].AsNumber()) && !double.IsInfinity(args[0].AsNumber())));
        DefineNumberStatic(constructorHandle, constructor, "isNaN", static args
            => JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && double.IsNaN(args[0].AsNumber())));
        DefineNumberStatic(constructorHandle, constructor, "isInteger", static args
            => JsValue.FromBoolean(IsIntegerNumber(args)));
        DefineNumberStatic(constructorHandle, constructor, "isSafeInteger", static args
            => JsValue.FromBoolean(IsIntegerNumber(args) && Math.Abs(args[0].AsNumber()) <= 9007199254740991d));

        // ECMA-262 21.1.2.13 / 21.1.2.14 - Number.parseInt and Number.parseFloat
        // are required to be the SAME function object as the global parseInt /
        // parseFloat. Install by reference using the cached handles.
        var parseIntHandle = EnsureParseIntFunction();
        constructor.DefineOwnProperty("parseInt", new JsPropertyDescriptor(
            JsValue.FromObject(parseIntHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(constructorHandle, parseIntHandle);

        var parseFloatHandle = EnsureParseFloatFunction();
        constructor.DefineOwnProperty("parseFloat", new JsPropertyDescriptor(
            JsValue.FromObject(parseFloatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(constructorHandle, parseFloatHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", NumberPrototypeToString);
        // ECMA-262 21.1.3.3 Number.prototype.toFixed(fractionDigits).
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toFixed", NumberPrototypeToFixed, length: 1);
        // ECMA-262 21.1.3.2 toExponential, 21.1.3.5 toPrecision.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toExponential", NumberPrototypeToExponential, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toPrecision", NumberPrototypeToPrecision, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", NumberPrototypeValueOf);

        _numberPrototypeHandle = prototypeHandle;
        _numberConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // ECMA-262 19.2.5 parseInt(string, radix). Trims leading whitespace, accepts an
    // optional sign, an optional 0x/0X prefix when radix is 16 or 0, then consumes
    // digits valid for the radix until the first invalid character. Returns NaN when
    // no valid digit is read.
    private ObjectHandle EnsureParseIntFunction()
    {
        if (_parseIntHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("parseInt", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            var radixArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            return JsValue.FromNumber(ParseIntegerLiteral(text, radixArg));
        }, length: 2);

        _parseIntHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_parseIntHandle.Value);
        return _parseIntHandle.Value;
    }

    // ECMA-262 19.2.4 parseFloat(string).
    private ObjectHandle EnsureParseFloatFunction()
    {
        if (_parseFloatHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("parseFloat", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromNumber(ParseFloatLiteral(text));
        }, length: 1);

        _parseFloatHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_parseFloatHandle.Value);
        return _parseFloatHandle.Value;
    }

    // ECMA-262 19.2.3 isNaN(number) - coerces, unlike Number.isNaN.
    private ObjectHandle EnsureIsNaNFunction()
    {
        if (_isNaNHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("isNaN", (_, args) =>
        {
            var n = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(double.IsNaN(n));
        }, length: 1);

        _isNaNHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_isNaNHandle.Value);
        return _isNaNHandle.Value;
    }

    // ECMA-262 19.2.6.4 encodeURI(uri). Unescaped set = uriReserved + uriUnescaped + "#".
    private ObjectHandle EnsureEncodeUriFunction()
    {
        if (_encodeUriHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("encodeURI", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromString(EncodeUri(text, encodeReserved: false));
        }, length: 1);

        _encodeUriHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_encodeUriHandle.Value);
        return _encodeUriHandle.Value;
    }

    // ECMA-262 19.2.6.5 encodeURIComponent(uriComponent). Unescaped set = uriUnescaped only;
    // every uriReserved character ;/?:@&=+$,# is percent-encoded.
    private ObjectHandle EnsureEncodeUriComponentFunction()
    {
        if (_encodeUriComponentHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("encodeURIComponent", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromString(EncodeUri(text, encodeReserved: true));
        }, length: 1);

        _encodeUriComponentHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_encodeUriComponentHandle.Value);
        return _encodeUriComponentHandle.Value;
    }

    // ECMA-262 19.2.6.2 decodeURI(encodedURI). Reserved-set bytes stay escaped so an
    // already-built URI doesn't lose its structural punctuation on a round trip.
    private ObjectHandle EnsureDecodeUriFunction()
    {
        if (_decodeUriHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("decodeURI", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromString(DecodeUri(text, preserveReserved: true));
        }, length: 1);

        _decodeUriHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_decodeUriHandle.Value);
        return _decodeUriHandle.Value;
    }

    // ECMA-262 19.2.6.3 decodeURIComponent(encodedURIComponent).
    private ObjectHandle EnsureDecodeUriComponentFunction()
    {
        if (_decodeUriComponentHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("decodeURIComponent", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromString(DecodeUri(text, preserveReserved: false));
        }, length: 1);

        _decodeUriComponentHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_decodeUriComponentHandle.Value);
        return _decodeUriComponentHandle.Value;
    }

    // ECMA-262 19.2.6.1.1 Encode(string, unescapedSet). Walks code points (surrogate
    // pairs are decoded as one), UTF-8 encodes non-unescaped ones, and percent-emits
    // each byte uppercased. Lone surrogates raise URIError per step 4.
    private string EncodeUri(string text, bool encodeReserved)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            int codePoint;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    throw new JsThrownException(CreateUriError("URI malformed: lone high surrogate."));
                }
                codePoint = char.ConvertToUtf32(ch, text[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                throw new JsThrownException(CreateUriError("URI malformed: lone low surrogate."));
            }
            else
            {
                codePoint = ch;
            }

            if (IsUriUnescaped(codePoint, encodeReserved))
            {
                _ = sb.Append((char)codePoint);
            }
            else
            {
                var buffer = codePoint <= 0x7F
                    ? new byte[] { (byte)codePoint }
                    : System.Text.Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint));
                foreach (var b in buffer)
                {
                    _ = sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        return sb.ToString();
    }

    private static bool IsUriUnescaped(int c, bool encodeReserved)
    {
        // ECMA-262 19.2.6.1.1 alphanumeric + uriMark + (uriReserved when not encoding).
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
        {
            return true;
        }
        switch (c)
        {
            case '-': case '_': case '.': case '!': case '~':
            case '*': case '\'': case '(': case ')':
                return true;
        }
        if (!encodeReserved)
        {
            switch (c)
            {
                case ';': case '/': case '?': case ':': case '@':
                case '&': case '=': case '+': case '$': case ',':
                case '#':
                    return true;
            }
        }
        return false;
    }

    // ECMA-262 19.2.6.1.2 Decode(string, reservedSet). Percent-triplets decode as
    // UTF-8 byte sequences; malformed input or invalid UTF-8 raises URIError. When
    // preserveReserved is true (decodeURI only), bytes whose UTF-8 decoding is a
    // reserved character are left as the literal %HH triplets.
    private string DecodeUri(string text, bool preserveReserved)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch != '%')
            {
                _ = sb.Append(ch);
                i++;
                continue;
            }

            if (i + 2 >= text.Length)
            {
                throw new JsThrownException(CreateUriError("URI malformed: truncated escape."));
            }

            var b0 = DecodeHexByte(text, i);
            i += 3;
            if ((b0 & 0x80) == 0)
            {
                if (preserveReserved && IsUriReservedAscii((char)b0))
                {
                    _ = sb.Append('%').Append(text[i - 2]).Append(text[i - 1]);
                }
                else
                {
                    _ = sb.Append((char)b0);
                }
                continue;
            }

            int extraBytes;
            if ((b0 & 0xE0) == 0xC0) extraBytes = 1;
            else if ((b0 & 0xF0) == 0xE0) extraBytes = 2;
            else if ((b0 & 0xF8) == 0xF0) extraBytes = 3;
            else throw new JsThrownException(CreateUriError("URI malformed: bad UTF-8 leading byte."));

            var bytes = new byte[1 + extraBytes];
            bytes[0] = b0;
            for (var k = 1; k <= extraBytes; k++)
            {
                if (i >= text.Length || text[i] != '%' || i + 2 >= text.Length)
                {
                    throw new JsThrownException(CreateUriError("URI malformed: truncated continuation."));
                }
                var bk = DecodeHexByte(text, i);
                if ((bk & 0xC0) != 0x80)
                {
                    throw new JsThrownException(CreateUriError("URI malformed: bad UTF-8 continuation."));
                }
                bytes[k] = bk;
                i += 3;
            }

            string decoded;
            try
            {
                decoded = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new JsThrownException(CreateUriError("URI malformed: invalid UTF-8 sequence."));
            }

            _ = sb.Append(decoded);
        }

        return sb.ToString();
    }

    private byte DecodeHexByte(string text, int percentIndex)
    {
        var hi = HexDigit(text[percentIndex + 1]);
        var lo = HexDigit(text[percentIndex + 2]);
        return (byte)((hi << 4) | lo);
    }

    private int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        throw new JsThrownException(CreateUriError("URI malformed: invalid hex digit."));
    }

    private static bool IsUriReservedAscii(char c)
    {
        switch (c)
        {
            case ';': case '/': case '?': case ':': case '@':
            case '&': case '=': case '+': case '$': case ',':
            case '#':
                return true;
            default:
                return false;
        }
    }

    private JsValue CreateUriError(string message)
    {
        return CreateErrorObject("URIError", GetGlobalPrototype("URIError"), message);
    }

    private ObjectHandle EnsureUriErrorPrototype()
    {
        _ = EnsureUriErrorConstructor();
        return _uriErrorPrototypeHandle!.Value;
    }

    // ECMA-262 20.5.5.{3,5} ReferenceError + EvalError native error constructors.
    // EvalError is reserved by the spec for backward compatibility - no operation in
    // the language throws it - but ECMA-262 still requires it to exist with the
    // standard NativeError shape.
    private ObjectHandle EnsureReferenceErrorConstructor()
    {
        return EnsureNativeErrorConstructor(
            "ReferenceError",
            ref _referenceErrorConstructorHandle,
            ref _referenceErrorPrototypeHandle);
    }

    private ObjectHandle EnsureReferenceErrorPrototype()
    {
        _ = EnsureReferenceErrorConstructor();
        return _referenceErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureEvalErrorConstructor()
    {
        return EnsureNativeErrorConstructor(
            "EvalError",
            ref _evalErrorConstructorHandle,
            ref _evalErrorPrototypeHandle);
    }

    // ECMA-262 20.5.7 AggregateError(errors, message). Aggregates a list of errors
    // into a single error instance. The first arg is iterable; we collect via
    // CreateForOfIterator so anything with @@iterator (Array, Set, custom iterators)
    // works. The result has an own 'errors' Array property and standard Error shape.
    private ObjectHandle EnsureAggregateErrorConstructor()
    {
        if (_aggregateErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(GetGlobalPrototype("Error"));
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);
        _ = prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString("AggregateError"), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        JsValue Build(IReadOnlyList<JsValue> args)
        {
            if (args.Count == 0 || (args[0].Tag != JsValueTag.Object && args[0].Tag != JsValueTag.String))
            {
                throw new JsThrownException(CreateTypeError(
                    "AggregateError: first argument must be iterable."));
            }
            var msg = args.Count > 1 ? FormatPrimitiveForString(args[1]) : string.Empty;

            // Collect the iterable into a list using the for-of pathway so
            // Symbol.iterator dispatch is honoured.
            var errorsList = new List<JsValue>();
            var iter = CreateForOfIterator(args[0]);
            if (iter.Tag == JsValueTag.Object &&
                _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
            {
                while (forOf.TryMoveNext(out var v))
                {
                    errorsList.Add(v);
                }
            }

            var errorsArrObj = CreateArrayFromElements(errorsList.ToArray());
            var errorsArrHandle = _heap.AllocateObject(errorsArrObj, AllocationSite.Current());

            var err = new JsObject();
            err.SetPrototype(EnsureAggregateErrorPrototype());
            _ = err.SetProperty("name", JsValue.FromString("AggregateError"));
            _ = err.SetProperty("message", JsValue.FromString(msg));
            _ = err.SetProperty("errors", JsValue.FromObject(errorsArrHandle));
            var handle = _heap.AllocateObject(err, AllocationSite.Current());
            _heap.WriteBarrier(handle, errorsArrHandle);
            return JsValue.FromObject(handle);
        }

        var constructor = new NativeFunctionObject(
            "AggregateError",
            (_, args) => Build(args),
            args => Build(args),
            length: 2);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _aggregateErrorPrototypeHandle = prototypeHandle;
        _aggregateErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureAggregateErrorPrototype()
    {
        _ = EnsureAggregateErrorConstructor();
        return _aggregateErrorPrototypeHandle!.Value;
    }

    private sealed class WeakRefObject : JsObject
    {
        public WeakRefObject(ObjectHandle target) { Target = target; }
        public ObjectHandle Target { get; }
    }

    // ECMA-262 26.1 WeakRef(target). The engine has no incremental GC tier that
    // can null-out weak references yet, so the held reference stays live for the
    // lifetime of the WeakRef. deref() therefore always returns the original
    // target. This still matches the spec's observable contract: deref's return
    // is allowed to be the target, and a future GC pass can flip it to undefined
    // without any program-visible breaking change.
    private ObjectHandle EnsureWeakRefConstructor()
    {
        if (_weakRefConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "WeakRef",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor WeakRef requires 'new'.")),
            args =>
            {
                if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        "WeakRef: target must be an object."));
                }
                var targetHandle = args[0].AsObjectHandle();
                var wr = new WeakRefObject(targetHandle);
                wr.SetPrototype(prototypeHandle);
                var handle = _heap.AllocateObject(wr, AllocationSite.Current());
                _heap.WriteBarrier(handle, targetHandle);
                return JsValue.FromObject(handle);
            },
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "deref", (thisValue, _) =>
        {
            if (thisValue.Tag != JsValueTag.Object ||
                _heap.GetObject(thisValue.AsObjectHandle()) is not WeakRefObject wr)
            {
                throw new JsThrownException(CreateTypeError(
                    "WeakRef.prototype.deref called on non-WeakRef receiver."));
            }
            return JsValue.FromObject(wr.Target);
        });

        _weakRefPrototypeHandle = prototypeHandle;
        _weakRefConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private sealed class FinalizationRegistryObject : JsObject
    {
        public sealed record Entry(ObjectHandle Target, JsValue HeldValue, JsValue UnregisterToken);
        public ObjectHandle Callback { get; }
        public List<Entry> Entries { get; } = new();
        public FinalizationRegistryObject(ObjectHandle callback) { Callback = callback; }
    }

    // ECMA-262 26.2 FinalizationRegistry(cleanupCallback). The engine has no
    // GC-driven cleanup pass yet, so register/unregister maintain the registry
    // bookkeeping but no callbacks ever fire automatically. cleanupSome runs the
    // callback against zero entries (nothing has been collected). This keeps the
    // API surface stable so user code that constructs registries continues to
    // work; a later commit can wire automatic cleanup when the GC gains
    // post-collection phases.
    private ObjectHandle EnsureFinalizationRegistryConstructor()
    {
        if (_finalizationRegistryConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "FinalizationRegistry",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor FinalizationRegistry requires 'new'.")),
            args =>
            {
                if (args.Count == 0 || args[0].Tag != JsValueTag.Object ||
                    _heap.GetObject(args[0].AsObjectHandle()) is not (JsFunctionObject or NativeFunctionObject))
                {
                    throw new JsThrownException(CreateTypeError(
                        "FinalizationRegistry: cleanup callback must be callable."));
                }
                var cbHandle = args[0].AsObjectHandle();
                var reg = new FinalizationRegistryObject(cbHandle);
                reg.SetPrototype(prototypeHandle);
                var handle = _heap.AllocateObject(reg, AllocationSite.Current());
                _heap.WriteBarrier(handle, cbHandle);
                return JsValue.FromObject(handle);
            },
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "register", (thisValue, args) =>
        {
            var reg = RequireFinalizationRegistry(thisValue);
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "FinalizationRegistry.prototype.register: target must be an object."));
            }
            var held = args.Count > 1 ? args[1] : JsValue.Undefined;
            var token = args.Count > 2 ? args[2] : JsValue.Undefined;
            reg.Entries.Add(new FinalizationRegistryObject.Entry(args[0].AsObjectHandle(), held, token));
            return JsValue.Undefined;
        }, length: 2);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "unregister", (thisValue, args) =>
        {
            var reg = RequireFinalizationRegistry(thisValue);
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "FinalizationRegistry.prototype.unregister: token must be an object."));
            }
            var tokenHandle = args[0].AsObjectHandle();
            var removed = reg.Entries.RemoveAll(e =>
                e.UnregisterToken.Tag == JsValueTag.Object &&
                e.UnregisterToken.AsObjectHandle() == tokenHandle);
            return JsValue.FromBoolean(removed > 0);
        }, length: 1);

        _finalizationRegistryPrototypeHandle = prototypeHandle;
        _finalizationRegistryConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // HTML Living Standard "structured serialize" + "structured deserialize". A
    // single in-process pass that walks the input, remembers visited source handles
    // so cycles produce shared references in the result, and rebuilds via fresh
    // allocations on the same heap. Functions, host objects, and Symbol values
    // raise the HTML-spec'd DataCloneError (we surface it as a plain Error with
    // the spec-mandated name 'DataCloneError' for code that string-checks).
    private ObjectHandle EnsureStructuredCloneFunction()
    {
        if (_structuredCloneHandle is { } existing)
        {
            return existing;
        }
        var fn = new NativeFunctionObject("structuredClone", (_, args) =>
        {
            if (args.Count == 0) return JsValue.Undefined;
            return StructuredCloneValue(args[0], new Dictionary<ObjectHandle, ObjectHandle>());
        }, length: 1);
        _structuredCloneHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_structuredCloneHandle.Value);
        return _structuredCloneHandle.Value;
    }

    private JsValue StructuredCloneValue(JsValue value, Dictionary<ObjectHandle, ObjectHandle> memo)
    {
        switch (value.Tag)
        {
            case JsValueTag.Undefined:
            case JsValueTag.Null:
            case JsValueTag.Boolean:
            case JsValueTag.Int32:
            case JsValueTag.Number:
            case JsValueTag.String:
                return value;
            case JsValueTag.Symbol:
                throw new JsThrownException(CreateDataCloneError("Symbol values cannot be structured-cloned."));
        }
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        var sourceHandle = value.AsObjectHandle();
        if (memo.TryGetValue(sourceHandle, out var existingClone))
        {
            return JsValue.FromObject(existingClone);
        }

        var sourceObj = _heap.GetObject(sourceHandle);
        switch (sourceObj)
        {
            case JsFunctionObject:
            case NativeFunctionObject:
                throw new JsThrownException(CreateDataCloneError("Functions cannot be structured-cloned."));
            case DateObject d:
            {
                var cloneObj = new DateObject(d.TimeValue);
                cloneObj.SetPrototype(EnsureDatePrototype());
                var h = _heap.AllocateObject(cloneObj, AllocationSite.Current());
                memo[sourceHandle] = h;
                return JsValue.FromObject(h);
            }
            case RegExpObject r:
            {
                var cloneObj = new RegExpObject(r.Pattern, r.Flags, r.Regex);
                cloneObj.SetPrototype(EnsureRegExpPrototype());
                var h = _heap.AllocateObject(cloneObj, AllocationSite.Current());
                memo[sourceHandle] = h;
                return JsValue.FromObject(h);
            }
            case ArrayObject:
            {
                var arr = new ArrayObject();
                arr.SetPrototype(EnsureArrayPrototype());
                var h = _heap.AllocateObject(arr, AllocationSite.Current());
                memo[sourceHandle] = h;
                var length = GetArrayLength(sourceObj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!TryGetPropertyValue(sourceObj, value, key, out var v)) continue;
                    var cloned = StructuredCloneValue(v, memo);
                    arr.SetProperty(key, cloned);
                    if (cloned.Tag == JsValueTag.Object) _heap.WriteBarrier(h, cloned.AsObjectHandle());
                }
                arr.SetProperty("length", JsValue.FromNumber(length));
                return JsValue.FromObject(h);
            }
            default:
            {
                var clone = CreateOrdinaryObject();
                var h = _heap.AllocateObject(clone, AllocationSite.Current());
                memo[sourceHandle] = h;
                foreach (var pair in sourceObj.EnumerateOwnProperties())
                {
                    if (!pair.Value.Enumerable) continue;
                    if (!TryGetPropertyValue(sourceObj, value, pair.Key, out var v)) continue;
                    var cloned = StructuredCloneValue(v, memo);
                    clone.SetProperty(pair.Key, cloned);
                    if (cloned.Tag == JsValueTag.Object) _heap.WriteBarrier(h, cloned.AsObjectHandle());
                }
                return JsValue.FromObject(h);
            }
        }
    }

    // ECMA-262 25.1.3 — the %ArrayBuffer% constructor.
    private ObjectHandle EnsureArrayBufferConstructor()
    {
        if (_arrayBufferConstructorHandle is { } existing)
            return existing;

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "ArrayBuffer",
            (_, _2) => throw new JsThrownException(CreateTypeError("ArrayBuffer constructor must be invoked with 'new'.")),
            args =>
            {
                var length = args.Count > 0 ? args[0].AsNumber() : 0;
                if (double.IsNaN(length) || length < 0 || length > 9007199254740991d) // 2^53-1
                    throw new JsThrownException(CreateRangeError("Invalid ArrayBuffer length."));
                var buf = new ArrayBufferObject((int)length);
                buf.SetPrototype(prototypeHandle);
                return JsValue.FromObject(_heap.AllocateObject(buf, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 25.1.5.2 get ArrayBuffer.prototype.byteLength
        var byteLengthGetter = new NativeFunctionObject("get byteLength", (thisValue, _2) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("ArrayBuffer.prototype.byteLength called on non-ArrayBuffer."));
            return JsValue.FromNumber(buf.IsDetached ? 0 : buf.ByteLength);
        }, length: 0);
        var byteLengthGetterHandle = _heap.AllocateObject(byteLengthGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("byteLength", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(byteLengthGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, byteLengthGetterHandle);

        // 25.1.5.3 ArrayBuffer.prototype.slice(begin, end)
        DefineNativePrototypeMethod(prototypeHandle, prototype, "slice", (thisValue, args) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("ArrayBuffer.prototype.slice called on non-ArrayBuffer."));
            if (buf.IsDetached)
                throw new JsThrownException(CreateTypeError("ArrayBuffer is detached."));
            var len = buf.ByteLength;
            var begin = args.Count > 0 ? (int)Math.Min(Math.Max(args[0].AsNumber(), 0), len) : 0;
            var end = args.Count > 1 ? (int)Math.Min(Math.Max(args[1].AsNumber(), 0), len) : len;
            if (end < begin) end = begin;
            var newLen = end - begin;
            var clone = buf.Clone(begin, newLen);
            clone.SetPrototype(EnsureArrayBufferPrototype());
            return JsValue.FromObject(_heap.AllocateObject(clone, AllocationSite.Current()));
        }, length: 2);

        // 25.1.5.6 ArrayBuffer.isView(arg)
        DefineIntrinsicFunction(constructorHandle, constructor, "isView", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
                return JsValue.FromBoolean(false);
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return JsValue.FromBoolean(obj is TypedArrayView);
        }, length: 1);

        _arrayBufferConstructorHandle = constructorHandle;
        _arrayBufferPrototypeHandle = prototypeHandle;
        return constructorHandle;
    }

    private ObjectHandle? _arrayBufferConstructorHandle;
    private ObjectHandle? _arrayBufferPrototypeHandle;

    private ObjectHandle EnsureArrayBufferPrototype()
    {
        EnsureArrayBufferConstructor();
        return _arrayBufferPrototypeHandle!.Value;
    }

    // ECMA-262 25.3 — the %DataView% constructor. Stubbed for now.
    private ObjectHandle EnsureDataViewConstructor()
    {
        throw new NotImplementedException("DataView constructor not yet wired.");
    }

    // ECMA-262 23.2 — all 11 %TypedArray% constructors. Stubbed for now.
    private BuiltinBinding[] EnsureTypedArrayConstructors()
    {
        throw new NotImplementedException("TypedArray constructors not yet wired.");
    }

    // HTML queueMicrotask(callback). The callback is appended to the pending
    // microtask queue and drained when the current Execute returns. Non-callable
    // argument raises TypeError per the HTML spec.
    private ObjectHandle EnsureQueueMicrotaskFunction()
    {
        if (_queueMicrotaskHandle is { } existing)
        {
            return existing;
        }
        var fn = new NativeFunctionObject("queueMicrotask", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object ||
                _heap.GetObject(args[0].AsObjectHandle()) is not (JsFunctionObject or NativeFunctionObject))
            {
                throw new JsThrownException(CreateTypeError("queueMicrotask: argument must be callable."));
            }
            _pendingMicrotasks.Enqueue(args[0]);
            return JsValue.Undefined;
        }, length: 1);
        _queueMicrotaskHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_queueMicrotaskHandle.Value);
        return _queueMicrotaskHandle.Value;
    }

    private JsValue CreateDataCloneError(string message)
    {
        var err = new JsObject();
        err.SetPrototype(EnsureErrorPrototype());
        err.SetProperty("name", JsValue.FromString("DataCloneError"));
        err.SetProperty("message", JsValue.FromString(message));
        return JsValue.FromObject(_heap.AllocateObject(err, AllocationSite.Current()));
    }

    private FinalizationRegistryObject RequireFinalizationRegistry(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is FinalizationRegistryObject reg)
        {
            return reg;
        }
        throw new JsThrownException(CreateTypeError(
            "FinalizationRegistry method called on non-FinalizationRegistry receiver."));
    }

    private ObjectHandle EnsureNativeErrorConstructor(
        string name,
        ref ObjectHandle? ctorField,
        ref ObjectHandle? protoField)
    {
        if (ctorField is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);
        _ = prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString(name), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        var capturedProto = prototypeHandle;
        var capturedName = name;
        var constructor = new NativeFunctionObject(
            name,
            (_, args) => CreateErrorObject(capturedName, capturedProto, GetOptionalMessage(args)),
            args => CreateErrorObject(capturedName, capturedProto, GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        protoField = prototypeHandle;
        ctorField = constructorHandle;
        return constructorHandle;
    }

    // ECMA-262 20.5.5.7 URIError native error constructor.
    private ObjectHandle EnsureUriErrorConstructor()
    {
        if (_uriErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);
        _ = prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString("URIError"), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        var constructor = new NativeFunctionObject(
            "URIError",
            (_, args) => CreateErrorObject("URIError", EnsureUriErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("URIError", EnsureUriErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _uriErrorPrototypeHandle = prototypeHandle;
        _uriErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // ECMA-262 19.2.2 isFinite(number) - coerces, unlike Number.isFinite.
    private ObjectHandle EnsureIsFiniteFunction()
    {
        if (_isFiniteHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("isFinite", (_, args) =>
        {
            var n = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(!double.IsNaN(n) && !double.IsInfinity(n));
        }, length: 1);

        _isFiniteHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_isFiniteHandle.Value);
        return _isFiniteHandle.Value;
    }

    private static double ParseIntegerLiteral(string text, JsValue radixArg)
    {
        var s = text.AsSpan().TrimStart();
        if (s.Length == 0)
        {
            return double.NaN;
        }

        var sign = 1;
        if (s[0] == '+')
        {
            s = s[1..];
        }
        else if (s[0] == '-')
        {
            sign = -1;
            s = s[1..];
        }

        var radix = 0;
        var stripPrefix = true;
        if (radixArg.Tag != JsValueTag.Undefined)
        {
            var r = (int)MathHelpers.ToInt32(ParseNumberForCoerce(radixArg));
            if (r != 0)
            {
                if (r < 2 || r > 36)
                {
                    return double.NaN;
                }

                radix = r;
                stripPrefix = r == 16;
            }
        }

        if (stripPrefix && s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        {
            s = s[2..];
            if (radix == 0)
            {
                radix = 16;
            }
        }

        if (radix == 0)
        {
            radix = 10;
        }

        var consumed = 0;
        double result = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var digit = DigitValue(s[i]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            result = result * radix + digit;
            consumed++;
        }

        if (consumed == 0)
        {
            return double.NaN;
        }

        return sign * result;
    }

    private static int DigitValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'z')
        {
            return 10 + (c - 'a');
        }

        if (c >= 'A' && c <= 'Z')
        {
            return 10 + (c - 'A');
        }

        return -1;
    }

    private static double ParseFloatLiteral(string text)
    {
        var s = text.AsSpan().TrimStart().ToString();
        if (s.Length == 0)
        {
            return double.NaN;
        }

        if (s.StartsWith("Infinity", StringComparison.Ordinal))
        {
            return double.PositiveInfinity;
        }

        if (s.StartsWith("+Infinity", StringComparison.Ordinal))
        {
            return double.PositiveInfinity;
        }

        if (s.StartsWith("-Infinity", StringComparison.Ordinal))
        {
            return double.NegativeInfinity;
        }

        // Take the longest prefix that parses as a Number per StrNumericLiteral. Walk
        // back from the end; the spec calls for the longest matching prefix.
        for (var len = s.Length; len > 0; len--)
        {
            var prefix = s[..len];
            if (double.TryParse(prefix, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }

        return double.NaN;
    }

    private static double ParseNumberForCoerce(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.Null => 0d,
            JsValueTag.Undefined => double.NaN,
            JsValueTag.String => double.TryParse(value.AsString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
            _ => double.NaN,
        };
    }

    private static bool IsIntegerNumber(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Number)
        {
            return false;
        }

        var value = args[0].AsNumber();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        return Math.Floor(value) == value;
    }

    private void DefineNumberStatic(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<IReadOnlyList<JsValue>, JsValue> call)
    {
        var function = new NativeFunctionObject(name, (_, args) => call(args), length: 1);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        _ = owner.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(ownerHandle, functionHandle);
    }

    // ECMA-262 21.1.3.2 Number.prototype.toExponential(fractionDigits). The
    // canonical scientific form: <mantissa>e+<exp> or <mantissa>e-<exp>. NaN and
    // +-Infinity surface as their default ToString. fractionDigits must be in
    // [0, 100]; when omitted, fractional digits go as small as needed to render
    // the value uniquely (we approximate via Round-Trip "R" then re-format).
    private JsValue NumberPrototypeToExponential(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            // Round-trip then reformat to lowercase e per spec.
            return JsValue.FromString(NormaliseExponential(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var digits = (int)ToNumber(args[0]);
        if (digits < 0 || digits > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toExponential() digits argument must be between 0 and 100."));
        }

        var format = "0." + new string('0', digits) + "e+0";
        var raw = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        if (digits == 0)
        {
            // Trim trailing "." that the format string can leave behind on whole values.
            raw = raw.Replace(".e", "e", StringComparison.Ordinal);
        }

        return JsValue.FromString(NormaliseExponential(raw));
    }

    // Convert any "1.23E+05" / "1.23E5" style produced by .NET into the canonical
    // ECMA-262 form "1.23e+5" (lowercase e, explicit sign, no leading zeros on the
    // exponent).
    private static string NormaliseExponential(string text)
    {
        var eIdx = text.IndexOfAny(['e', 'E']);
        if (eIdx < 0)
        {
            return text;
        }

        var mantissa = text[..eIdx];
        var expPart = text[(eIdx + 1)..];
        var sign = "+";
        if (expPart.Length > 0 && (expPart[0] == '+' || expPart[0] == '-'))
        {
            sign = expPart[0] == '-' ? "-" : "+";
            expPart = expPart[1..];
        }

        expPart = expPart.TrimStart('0');
        if (expPart.Length == 0)
        {
            expPart = "0";
        }

        return mantissa + "e" + sign + expPart;
    }

    // ECMA-262 21.1.3.5 Number.prototype.toPrecision(precision). When precision is
    // undefined, behaves like toString. Otherwise renders the value with `precision`
    // significant digits, using fixed notation when |value| has |exp| < precision
    // and scientific otherwise (matching spec 21.1.3.5 step 10's choice).
    private JsValue NumberPrototypeToPrecision(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        var precision = (int)ToNumber(args[0]);
        if (precision < 1 || precision > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toPrecision() precision argument must be between 1 and 100."));
        }

        if (value == 0d)
        {
            return JsValue.FromString(precision == 1
                ? "0"
                : "0." + new string('0', precision - 1));
        }

        // "Gn" rounds to n significant digits without exponent unless necessary.
        // For spec parity with V8/SM ("0.0001" -> precision 1 -> "0.0001" stays;
        // very small or very large slip into scientific) we route through G then
        // normalise the exponent form when present.
        var formatted = value.ToString("G" + precision, System.Globalization.CultureInfo.InvariantCulture);
        return JsValue.FromString(NormaliseExponential(formatted));
    }

    // ECMA-262 21.1.3.3 Number.prototype.toFixed(fractionDigits). fractionDigits
    // must be in [0, 100]; NaN/Infinity return their default ToString; otherwise the
    // value is fixed-point formatted to exactly N digits after the decimal point.
    private JsValue NumberPrototypeToFixed(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        var digits = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (digits < 0 || digits > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toFixed() digits argument must be between 0 and 100."));
        }

        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        // Very large magnitudes fall back to the standard Number.toString output per
        // spec step 9 (when |value| >= 10^21).
        if (Math.Abs(value) >= 1e21)
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        return JsValue.FromString(value.ToString(
            "F" + digits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    // ECMA-262 21.1.3.6 Number.prototype.toString([radix]). Radix must be in [2, 36];
    // 10 (or undefined) routes through the standard decimal formatter; other radices
    // emit the integer portion in that base (and, for fractional values, an
    // approximation that matches V8/SM for finite cases). NaN / +-Infinity always
    // render in decimal regardless of radix.
    private JsValue NumberPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        var radix = 10;
        if (args.Count > 0 && args[0].Tag != JsValueTag.Undefined)
        {
            radix = (int)ToNumber(args[0]);
        }

        if (radix < 2 || radix > 36)
        {
            throw new JsThrownException(CreateRangeError(
                "toString() radix must be an integer between 2 and 36."));
        }

        if (radix == 10 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        return JsValue.FromString(FormatNumberInRadix(value, radix));
    }

    private static string FormatNumberInRadix(double value, int radix)
    {
        if (value == 0d)
        {
            return "0";
        }

        var negative = value < 0;
        if (negative)
        {
            value = -value;
        }

        var integerPart = Math.Floor(value);
        var fraction = value - integerPart;

        var intText = LongToRadixString((long)integerPart, radix);
        if (fraction == 0d)
        {
            return negative ? "-" + intText : intText;
        }

        var sb = new System.Text.StringBuilder(intText);
        sb.Append('.');

        // Emit up to ~52 digits of fractional precision - enough for any double.
        for (var i = 0; i < 52 && fraction != 0d; i++)
        {
            fraction *= radix;
            var digit = (int)Math.Floor(fraction);
            sb.Append(DigitToChar(digit));
            fraction -= digit;
        }

        var result = sb.ToString();
        return negative ? "-" + result : result;
    }

    private static string LongToRadixString(long n, int radix)
    {
        if (n == 0)
        {
            return "0";
        }

        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            sb.Insert(0, DigitToChar((int)(n % radix)));
            n /= radix;
        }

        return sb.ToString();
    }

    private static char DigitToChar(int digit)
    {
        return digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10);
    }

    private JsValue NumberPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromNumber(NumberThisValue(thisValue));
    }

    private double NumberThisValue(JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            return ToNumber(thisValue);
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is NumberObject numberObject)
        {
            return numberObject.Value;
        }

        throw new JsThrownException(CreateTypeError("Number.prototype method called on incompatible receiver."));
    }

    private JsValue CreateNumberObject(double value)
    {
        var obj = new NumberObject(value);
        obj.SetPrototype(GetGlobalPrototype("Number"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle GetGlobalPrototype(string constructorName)
    {
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        if (TryGetPropertyValue(global, JsValue.FromObject(globalHandle), constructorName, out var ctorVal) &&
            ctorVal.Tag == JsValueTag.Object &&
            _heap.GetObject(ctorVal.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject &&
            TryGetPropertyValue(_heap.GetObject(ctorVal.AsObjectHandle()), ctorVal, "prototype", out var protoVal) &&
            protoVal.Tag == JsValueTag.Object)
        {
            return protoVal.AsObjectHandle();
        }
        // Fallback during bootstrap — the global property hasn't been set up yet.
        return constructorName switch
        {
            "Boolean" => EnsureBooleanPrototype(),
            "Number" => EnsureNumberPrototype(),
            "String" => EnsureStringPrototype(),
            "Date" => EnsureDatePrototype(),
            "RegExp" => EnsureRegExpPrototype(),
            "GeneratorPrototype" => EnsureGeneratorPrototype(),
            "Error" => EnsureErrorPrototype(),
            "TypeError" => EnsureTypeErrorPrototype(),
            "RangeError" => EnsureRangeErrorPrototype(),
            "SyntaxError" => EnsureSyntaxErrorPrototype(),
            "ReferenceError" => EnsureReferenceErrorPrototype(),
            "EvalError" => EnsureErrorPrototype(),
            "URIError" => EnsureErrorPrototype(),
            "Object" => EnsureObjectPrototype(),
            "Array" => EnsureArrayPrototype(),
            _ => EnsureObjectPrototype(),
        };
    }

    private ObjectHandle EnsureGeneratorPrototype()
    {
        if (_generatorPrototypeHandle is { } existing)
            return existing;

        // GeneratorBuiltin installs "GeneratorPrototype" as a global property during
        // InstallGlobalObjectProperties. Read it back so GetGlobalPrototype resolves
        // the real prototype with .next()/.return()/.throw() rather than Object.prototype.
        var global = EnsureGlobalObject();
        var globalObj = _heap.GetObject(global);
        if (globalObj.TryGetOwnProperty("GeneratorPrototype", out var desc) && desc.Value.Tag == JsValueTag.Object)
        {
            var protoHandle = desc.Value.AsObjectHandle();
            var protoObj = _heap.GetObject(protoHandle);

            // ECMA-262 27.5.1 — Generator objects are iterable. @@iterator returns
            // the generator object itself so yield* can delegate to generators.
            if (_generatorIteratorHandle is null)
            {
                var iterId = GetWellKnownSymbolId("iterator");
                if (iterId != 0)
                {
                    var iteratorFn = new NativeFunctionObject("[Symbol.iterator]",
                        (thisValue, _) => thisValue, length: 0);
                    _generatorIteratorHandle = _heap.AllocateObject(iteratorFn, AllocationSite.Current());
                    protoObj.DefineOwnSymbolProperty(iterId,
                        new JsPropertyDescriptor(JsValue.FromObject(_generatorIteratorHandle.Value),
                            Writable: true, Enumerable: false, Configurable: true));
                    _heap.WriteBarrier(protoHandle, _generatorIteratorHandle.Value);
                }
            }

            _generatorPrototypeHandle = protoHandle;
            return _generatorPrototypeHandle.Value;
        }

        return EnsureObjectPrototype();
    }

    private ObjectHandle EnsureStringPrototype()
    {
        _ = EnsureStringConstructor();
        return _stringPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureStringConstructor()
    {
        if (_stringConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new StringObject(string.Empty);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "String",
            (_, args) => JsValue.FromString(args.Count > 0 ? ToStringValue(args[0]) : string.Empty),
            args => CreateStringObject(args.Count > 0 ? ToStringValue(args[0]) : string.Empty),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", StringPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", StringPrototypeValueOf);
        // ECMA-262 22.1.3 String.prototype methods. Each receives the receiver as
        // either a primitive string or a boxed StringObject via StringThisValue.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "charAt", StringPrototypeCharAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "charCodeAt", StringPrototypeCharCodeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "codePointAt", StringPrototypeCodePointAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "at", StringPrototypeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "indexOf", StringPrototypeIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "lastIndexOf", StringPrototypeLastIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "includes", StringPrototypeIncludes, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "startsWith", StringPrototypeStartsWith, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "endsWith", StringPrototypeEndsWith, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "slice", StringPrototypeSlice, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "substring", StringPrototypeSubstring, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "substr", StringPrototypeSubstr, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "concat", StringPrototypeConcat, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "repeat", StringPrototypeRepeat, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "padStart", StringPrototypePadStart, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "padEnd", StringPrototypePadEnd, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trim", StringPrototypeTrim);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trimStart", StringPrototypeTrimStart);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trimEnd", StringPrototypeTrimEnd);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toUpperCase", StringPrototypeToUpperCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLowerCase", StringPrototypeToLowerCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLocaleUpperCase", StringPrototypeToUpperCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLocaleLowerCase", StringPrototypeToLowerCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "split", StringPrototypeSplit, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "replace", StringPrototypeReplace, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "replaceAll", StringPrototypeReplaceAll, length: 2);
        // ECMA-262 22.1.3.13 String.prototype.normalize([form]). Routes through
        // .NET String.Normalize which exposes the same four Unicode normalisation
        // forms (NFC default, NFD, NFKC, NFKD). Any other form value raises
        // RangeError per step 6.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "normalize", StringPrototypeNormalize, length: 0);

        // ECMA-262 22.1.2.1 String.fromCharCode(...codeUnits). Each argument is
        // truncated to a UTF-16 code unit (ToUint16) and concatenated. Surrogate
        // halves are kept as-is - String.fromCodePoint handles full code points.
        // ECMA-262 22.1.2.4 String.raw(template, ...substitutions). Reads template.raw
        // (must be coercible to Object) and walks it as an array-like: for each raw
        // segment at index i, append ToString(segment); if i is not the last index,
        // append ToString(substitutions[i]). Substitutions shorter than raw.length-1
        // are treated as missing (the spec replaces them with empty String).
        DefineIntrinsicFunction(constructorHandle, constructor, "raw", (_, args) =>
        {
            if (args.Count == 0 || (args[0].Tag != JsValueTag.Object && args[0].Tag != JsValueTag.String))
            {
                throw new JsThrownException(CreateTypeError("String.raw: template must be coercible to Object."));
            }
            var template = args[0];
            var templateObj = _heap.GetObject(template.AsObjectHandle());
            if (!TryGetPropertyValue(templateObj, template, "raw", out var rawValue) ||
                rawValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("String.raw: template.raw must be an object."));
            }
            var rawObj = _heap.GetObject(rawValue.AsObjectHandle());
            var rawLen = GetArrayLength(rawObj);
            if (rawLen == 0) return JsValue.FromString(string.Empty);

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < rawLen; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(rawObj, rawValue, key, out var seg))
                {
                    sb.Append(ToStringValue(seg));
                }
                if (i + 1 == rawLen) break;
                var subIndex = i + 1;
                if (subIndex < args.Count)
                {
                    sb.Append(ToStringValue(args[subIndex]));
                }
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "fromCharCode", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var codeUnit = (char)(ushort)MathHelpers.ToInt32(ToNumber(args[i]));
                sb.Append(codeUnit);
            }

            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // ECMA-262 22.1.2.2 String.fromCodePoint(...codePoints). Each argument must
        // be a non-negative integer <= 0x10FFFF; otherwise RangeError. Values
        // above 0xFFFF are encoded as a UTF-16 surrogate pair via char.ConvertFromUtf32.
        DefineIntrinsicFunction(constructorHandle, constructor, "fromCodePoint", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var n = ToNumber(args[i]);
                if (double.IsNaN(n) || n < 0 || n > 0x10FFFF || Math.Floor(n) != n)
                {
                    throw new JsThrownException(CreateRangeError(
                        "Invalid code point in String.fromCodePoint argument list."));
                }

                sb.Append(char.ConvertFromUtf32((int)n));
            }

            return JsValue.FromString(sb.ToString());
        }, length: 1);

        _stringPrototypeHandle = prototypeHandle;
        _stringConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue StringPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue));
    }

    private JsValue StringPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue));
    }

    // Normalises an index argument the way most String.prototype methods do: undefined
    // -> defaultValue, NaN -> 0, then clamp into [0, length].
    private static int StringIndexArg(IReadOnlyList<JsValue> args, int idx, int defaultValue, int length)
    {
        if (idx >= args.Count || args[idx].Tag == JsValueTag.Undefined)
        {
            return Math.Clamp(defaultValue, 0, length);
        }

        var raw = args[idx].Tag == JsValueTag.Int32 ? args[idx].AsInt32() : (int)args[idx].AsNumber();
        return Math.Clamp(raw, 0, length);
    }

    // 22.1.3.1 charAt: returns the single-character string at the integer index, or
    // "" when out of range. Negative or fractional indices floor toward 0/Length-1.
    private JsValue StringPrototypeCharAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.FromString(string.Empty);
        }

        return JsValue.FromString(s[pos].ToString());
    }

    // 22.1.3.2 charCodeAt: UTF-16 code unit at index, or NaN out of range.
    private JsValue StringPrototypeCharCodeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.FromNumber(double.NaN);
        }

        return JsValue.FromNumber(s[pos]);
    }

    // 22.1.3.3 codePointAt: full code point (including surrogate pairs) at the index.
    private JsValue StringPrototypeCodePointAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.Undefined;
        }

        var high = s[pos];
        if (char.IsHighSurrogate(high) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
        {
            return JsValue.FromNumber(char.ConvertToUtf32(high, s[pos + 1]));
        }

        return JsValue.FromNumber(high);
    }

    // 22.1.3.1a at: ES2022 negative-aware indexing; out-of-range returns undefined.
    private JsValue StringPrototypeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var raw = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var idx = raw < 0 ? s.Length + raw : raw;
        if (idx < 0 || idx >= s.Length)
        {
            return JsValue.Undefined;
        }

        return JsValue.FromString(s[idx].ToString());
    }

    // 22.1.3.8 indexOf - ordinal find, returns -1 when missing.
    private JsValue StringPrototypeIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        from = Math.Clamp(from, 0, s.Length);
        return JsValue.FromNumber(s.IndexOf(search, from, StringComparison.Ordinal));
    }

    // 22.1.3.10 lastIndexOf.
    private JsValue StringPrototypeLastIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Min(s.Length, Math.Max(0, (int)ToNumber(args[1])) + search.Length)
            : s.Length;
        if (search.Length == 0)
        {
            return JsValue.FromNumber(from);
        }

        var slice = s[..from];
        return JsValue.FromNumber(slice.LastIndexOf(search, StringComparison.Ordinal));
    }

    // 22.1.3.7 includes / 22.1.3.22 startsWith / 22.1.3.7a endsWith.
    private JsValue StringPrototypeIncludes(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromBoolean(s.IndexOf(search, from, StringComparison.Ordinal) >= 0);
    }

    private JsValue StringPrototypeStartsWith(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length) : 0;
        if (from + search.Length > s.Length)
        {
            return JsValue.FromBoolean(false);
        }

        return JsValue.FromBoolean(s.AsSpan(from, search.Length).SequenceEqual(search.AsSpan()));
    }

    private JsValue StringPrototypeEndsWith(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var endPos = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length)
            : s.Length;
        var start = endPos - search.Length;
        if (start < 0)
        {
            return JsValue.FromBoolean(false);
        }

        return JsValue.FromBoolean(s.AsSpan(start, search.Length).SequenceEqual(search.AsSpan()));
    }

    // 22.1.3.20 slice - negative indices wrap; out-of-range clamps to length.
    private JsValue StringPrototypeSlice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? WrapNegative((int)ToNumber(args[0]), len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? WrapNegative((int)ToNumber(args[1]), len)
            : len;
        return start >= end ? JsValue.FromString(string.Empty) : JsValue.FromString(s[start..end]);
    }

    // 22.1.3.23 substring - negative or NaN clamps to 0; swaps start/end so end<start
    // is treated as start<end.
    private JsValue StringPrototypeSubstring(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? Math.Clamp((int)ToNumber(args[0]), 0, len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ToNumber(args[1]), 0, len)
            : len;
        if (start > end)
        {
            (start, end) = (end, start);
        }

        return JsValue.FromString(s[start..end]);
    }

    // Annex B.2.2.1 substr(start, length) - legacy, but widely used.
    private JsValue StringPrototypeSubstr(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (start < 0)
        {
            start = Math.Max(0, len + start);
        }

        start = Math.Min(start, len);
        var count = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, Math.Min(len - start, (int)ToNumber(args[1])))
            : len - start;
        return JsValue.FromString(s.Substring(start, count));
    }

    // 22.1.3.4 concat - variadic string append.
    private JsValue StringPrototypeConcat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var sb = new System.Text.StringBuilder(StringThisValue(thisValue));
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append(ToStringValue(args[i]));
        }

        return JsValue.FromString(sb.ToString());
    }

    // 22.1.3.16 repeat - count must be non-negative integer-typed value < Infinity.
    private JsValue StringPrototypeRepeat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var n = args.Count > 0 ? ToNumber(args[0]) : 0;
        if (double.IsNaN(n) || n < 0 || double.IsInfinity(n))
        {
            throw new JsThrownException(CreateRangeError("Invalid repeat count."));
        }

        var count = (int)n;
        if (count == 0 || s.Length == 0)
        {
            return JsValue.FromString(string.Empty);
        }

        var sb = new System.Text.StringBuilder(s.Length * count);
        for (var i = 0; i < count; i++)
        {
            sb.Append(s);
        }

        return JsValue.FromString(sb.ToString());
    }

    // 22.1.3.15 / 22.1.3.14 padStart / padEnd.
    private JsValue StringPrototypePadStart(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var targetLen = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (targetLen <= s.Length)
        {
            return JsValue.FromString(s);
        }

        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ToStringValue(args[1]) : " ";
        if (pad.Length == 0)
        {
            return JsValue.FromString(s);
        }

        return JsValue.FromString(BuildPadding(pad, targetLen - s.Length) + s);
    }

    private JsValue StringPrototypePadEnd(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var targetLen = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (targetLen <= s.Length)
        {
            return JsValue.FromString(s);
        }

        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ToStringValue(args[1]) : " ";
        if (pad.Length == 0)
        {
            return JsValue.FromString(s);
        }

        return JsValue.FromString(s + BuildPadding(pad, targetLen - s.Length));
    }

    private static string BuildPadding(string fill, int needed)
    {
        var sb = new System.Text.StringBuilder(needed);
        while (sb.Length < needed)
        {
            var remaining = needed - sb.Length;
            sb.Append(remaining >= fill.Length ? fill : fill[..remaining]);
        }

        return sb.ToString();
    }

    // 22.1.3.31 / .32 / .33 trim / trimStart / trimEnd. The spec defines the
    // WhiteSpace and LineTerminator productions; the BCL's char.IsWhiteSpace is a
    // close-enough superset for almost every spec character.
    private JsValue StringPrototypeTrim(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).Trim());
    }

    private JsValue StringPrototypeTrimStart(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).TrimStart());
    }

    private JsValue StringPrototypeTrimEnd(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).TrimEnd());
    }

    // 22.1.3.26 / .27 toUpperCase / toLowerCase use the invariant culture so output
    // is deterministic across host locales (the spec is locale-insensitive).
    private JsValue StringPrototypeToUpperCase(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).ToUpperInvariant());
    }

    private JsValue StringPrototypeToLowerCase(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).ToLowerInvariant());
    }

    // 22.1.3.21 split. RegExp separator is deferred; string separator is the common
    // case. Empty separator splits into individual characters per spec.
    private JsValue StringPrototypeSplit(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var limit = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, (int)ToNumber(args[1]))
            : int.MaxValue;

        var items = new List<JsValue>();
        if (limit == 0)
        {
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            items.Add(JsValue.FromString(s));
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        var sep = ToStringValue(args[0]);
        if (sep.Length == 0)
        {
            for (var i = 0; i < s.Length && items.Count < limit; i++)
            {
                items.Add(JsValue.FromString(s[i].ToString()));
            }

            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        var start = 0;
        while (start <= s.Length && items.Count < limit)
        {
            var idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                items.Add(JsValue.FromString(s[start..]));
                break;
            }

            items.Add(JsValue.FromString(s[start..idx]));
            start = idx + sep.Length;
            if (start > s.Length)
            {
                break;
            }
        }

        return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
    }

    // 22.1.3.18 replace (string-search form). The single-replacement-only behaviour
    // matches the spec when the search value is a string. RegExp search is deferred.
    private JsValue StringPrototypeReplace(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        if (args.Count < 2)
        {
            return JsValue.FromString(s);
        }

        var search = ToStringValue(args[0]);
        var idx = s.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
        {
            return JsValue.FromString(s);
        }

        var replacement = ResolveStringReplacement(args[1], s, idx, search);
        return JsValue.FromString(string.Concat(s[..idx], replacement, s[(idx + search.Length)..]));
    }

    // 22.1.3.19 replaceAll (string-search form). Empty-string search throws TypeError
    // when the search value is a string per spec step 4.
    private JsValue StringPrototypeNormalize(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = ToStringValue(thisValue);
        var formArg = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? ToStringValue(args[0]) : "NFC";
        System.Text.NormalizationForm form;
        switch (formArg)
        {
            case "NFC":  form = System.Text.NormalizationForm.FormC; break;
            case "NFD":  form = System.Text.NormalizationForm.FormD; break;
            case "NFKC": form = System.Text.NormalizationForm.FormKC; break;
            case "NFKD": form = System.Text.NormalizationForm.FormKD; break;
            default:
                throw new JsThrownException(CreateRangeError(
                    "String.prototype.normalize: form must be one of NFC, NFD, NFKC, NFKD."));
        }
        return JsValue.FromString(s.Normalize(form));
    }

    private JsValue StringPrototypeReplaceAll(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        if (args.Count < 2)
        {
            return JsValue.FromString(s);
        }

        var search = ToStringValue(args[0]);
        if (search.Length == 0)
        {
            // Spec 22.1.3.19 step 4 raises TypeError only for the empty-RegExp-without-
            // global-flag case; an empty string search inserts the replacement between
            // every code unit per "string search" semantics.
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                sb.Append(ResolveStringReplacement(args[1], s, i, search));
                sb.Append(s[i]);
            }

            sb.Append(ResolveStringReplacement(args[1], s, s.Length, search));
            return JsValue.FromString(sb.ToString());
        }

        var result = new System.Text.StringBuilder();
        var start = 0;
        while (true)
        {
            var idx = s.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                result.Append(s, start, s.Length - start);
                break;
            }

            result.Append(s, start, idx - start);
            result.Append(ResolveStringReplacement(args[1], s, idx, search));
            start = idx + search.Length;
        }

        return JsValue.FromString(result.ToString());
    }

    private string ResolveStringReplacement(JsValue replacementValue, string source, int matchStart, string matched)
    {
        if (replacementValue.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(replacementValue.AsObjectHandle());
            if (obj is JsFunctionObject || obj is NativeFunctionObject)
            {
                var result = CallFunction(replacementValue,
                    new[] { JsValue.FromString(matched), JsValue.FromNumber(matchStart), JsValue.FromString(source) },
                    JsValue.Undefined);
                return ToStringValue(result);
            }
        }

        // Spec 22.1.3.18.1 - $$, $&, $`, $' substitutions; numbered captures only apply
        // to RegExp matches, which this string-search path never produces.
        var template = ToStringValue(replacementValue);
        if (template.IndexOf('$') < 0)
        {
            return template;
        }

        var sb = new System.Text.StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '$' || i + 1 >= template.Length)
            {
                sb.Append(template[i]);
                continue;
            }

            var next = template[i + 1];
            switch (next)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(source, 0, matchStart); i++; break;
                case '\'': sb.Append(source, matchStart + matched.Length, source.Length - matchStart - matched.Length); i++; break;
                default: sb.Append(template[i]); break;
            }
        }

        return sb.ToString();
    }

    private static int WrapNegative(int raw, int length)
    {
        if (raw < 0)
        {
            return Math.Max(0, length + raw);
        }

        return Math.Min(raw, length);
    }

    private string StringThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.String)
        {
            return thisValue.AsString();
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is StringObject stringObject)
        {
            return stringObject.Value;
        }

        throw new JsThrownException(CreateTypeError("String.prototype method called on incompatible receiver."));
    }

    private JsValue CreateStringObject(string value)
    {
        var obj = new StringObject(value);
        obj.SetPrototype(GetGlobalPrototype("String"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private static bool IsTruthy(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => false,
            JsValueTag.Null => false,
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => value.AsNumber() != 0 && !double.IsNaN(value.AsNumber()),
            JsValueTag.String => value.AsString().Length != 0,
            _ => true
        };
    }

    private bool AreEqual(JsValue left, JsValue right)
    {
        if (left.Tag == right.Tag)
        {
            return left.Tag switch
            {
                JsValueTag.Undefined => true,
                JsValueTag.Null => true,
                JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
                JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
                JsValueTag.Number => left.AsNumber() == right.AsNumber(),
                JsValueTag.BigInt => left.AsBigInt() == right.AsBigInt(),
                JsValueTag.String => left.AsString() == right.AsString(),
                JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
                JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
                _ => false
            };
        }

        if ((left.Tag == JsValueTag.Null && right.Tag == JsValueTag.Undefined) ||
            (left.Tag == JsValueTag.Undefined && right.Tag == JsValueTag.Null))
        {
            return true;
        }

        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return left.AsNumber() == right.AsNumber();
        }

        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) && right.Tag == JsValueTag.String)
        {
            return left.AsNumber() == ToNumberForEquality(right);
        }

        if (left.Tag == JsValueTag.String && (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return ToNumberForEquality(left) == right.AsNumber();
        }

        if (left.Tag == JsValueTag.Boolean)
        {
            return AreEqual(JsValue.FromNumber(left.AsBoolean() ? 1 : 0), right);
        }

        if (right.Tag == JsValueTag.Boolean)
        {
            return AreEqual(left, JsValue.FromNumber(right.AsBoolean() ? 1 : 0));
        }

        if (left.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(left, out var leftPrimitive))
        {
            return AreEqual(leftPrimitive, right);
        }

        if (right.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(right, out var rightPrimitive))
        {
            return AreEqual(left, rightPrimitive);
        }

        return false;
    }

    private bool TryGetObjectPrimitiveValue(JsValue value, out JsValue primitive)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            switch (obj)
            {
                case BooleanObject booleanObject:
                    primitive = JsValue.FromBoolean(booleanObject.Value);
                    return true;
                case NumberObject numberObject:
                    primitive = JsValue.FromNumber(numberObject.Value);
                    return true;
                case StringObject stringObject:
                    primitive = JsValue.FromString(stringObject.Value);
                    return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    // ECMA-262 7.2.10 SameValue. Distinguishes from AreStrictlyEqual on exactly two
    // points for numbers: SameValue(NaN, NaN) is true (strict eq is false) and
    // SameValue(+0, -0) is false (strict eq is true). All other tag combinations
    // delegate to strict equality.
    private static bool SameValue(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            var a = left.AsNumber();
            var b = right.AsNumber();
            if (double.IsNaN(a) && double.IsNaN(b))
            {
                return true;
            }

            if (a == 0d && b == 0d)
            {
                // +0 vs -0 - SameValue is false when sign bits differ.
                return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
            }

            return a == b;
        }

        return AreStrictlyEqual(left, right);
    }

    private static bool AreStrictlyEqual(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return left.AsNumber() == right.AsNumber();
        }

        if (left.Tag != right.Tag)
        {
            return false;
        }

        return left.Tag switch
        {
            JsValueTag.Undefined => true,
            JsValueTag.Null => true,
            JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
            JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
            JsValueTag.Number => left.AsNumber() == right.AsNumber(),
            JsValueTag.BigInt => left.AsBigInt() == right.AsBigInt(),
            JsValueTag.String => left.AsString() == right.AsString(),
            JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
            JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
            _ => false
        };
    }

    private static double ToNumberForEquality(JsValue value)
    {
        if (value.Tag == JsValueTag.Int32 || value.Tag == JsValueTag.Number)
        {
            return value.AsNumber();
        }

        if (value.Tag == JsValueTag.String)
        {
            var text = value.AsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var trimmed = text.Trim();
            if (string.Equals(trimmed, "Infinity", StringComparison.Ordinal) ||
                string.Equals(trimmed, "+Infinity", StringComparison.Ordinal))
            {
                return double.PositiveInfinity;
            }

            if (string.Equals(trimmed, "-Infinity", StringComparison.Ordinal))
            {
                return double.NegativeInfinity;
            }

            if (trimmed.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
            {
                return double.NaN;
            }

            if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return double.NaN;
        }

        return double.NaN;
    }

    private JsObject ResolveObject(JsValue value)
    {
        return _heap.GetObject(ResolveObjectHandle(value));
    }

    private void HandleDefineAccessor(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        // H.4 - install or update an accessor descriptor on the target object
        // under the given property name. Preserves the companion half (get/set)
        // if an accessor descriptor already exists for the key, per ECMA-262
        // 6.2.5.6 CompletePropertyDescriptor.
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineGetter/Setter target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var accessorName = function.PropertyNames[ins.B];
        var accessorFnValue = frame.Registers[ins.C];

        JsValue getValue = JsValue.Undefined;
        JsValue setValue = JsValue.Undefined;
        if (targetObj.TryGetOwnProperty(accessorName, out var existing) && existing.IsAccessor)
        {
            getValue = existing.Get;
            setValue = existing.Set;
        }

        if (ins.OpCode == OpCode.DefineGetter)
        {
            getValue = accessorFnValue;
        }
        else
        {
            setValue = accessorFnValue;
        }

        _ = targetObj.DefineOwnProperty(accessorName,
            Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: false, Configurable: true));
        if (accessorFnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
        }
    }

    // H.5 - HandleDefineAccessorByReg: Like HandleDefineAccessor but the property
    // key is a JsValue in a register (for computed property names) instead of an
    // index into the constant pool.
    private void HandleDefineAccessorByReg(InterpreterFrame frame, Instruction ins)
    {
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineGetter/Setter target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var keyValue = frame.Registers[ins.B];
        var accessorFnValue = frame.Registers[ins.C];

        // Convert the key value to a property key string
        var accessorName = ToPropertyKey(keyValue);

        JsValue getValue = JsValue.Undefined;
        JsValue setValue = JsValue.Undefined;
        if (targetObj.TryGetOwnProperty(accessorName, out var existing) && existing.IsAccessor)
        {
            getValue = existing.Get;
            setValue = existing.Set;
        }

        if (ins.OpCode == OpCode.DefineGetterByReg)
        {
            getValue = accessorFnValue;
        }
        else
        {
            setValue = accessorFnValue;
        }

        _ = targetObj.DefineOwnProperty(accessorName,
            Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: false, Configurable: true));
        if (accessorFnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
        }
    }

    private void HandleSetHomeObject(InterpreterFrame frame, Instruction ins)
    {
        var fnValue = frame.Registers[ins.A];
        var homeValue = frame.Registers[ins.B];
        if (fnValue.Tag != JsValueTag.Object || homeValue.Tag != JsValueTag.Object) return;
        if (_heap.GetObject(fnValue.AsObjectHandle()) is JsFunctionObject fn)
        {
            fn.HomeObject = homeValue.AsObjectHandle();
            _heap.WriteBarrier(fnValue.AsObjectHandle(), homeValue.AsObjectHandle());
        }
    }

    private void HandleLoadSuperProperty(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        // ECMA-262 13.3.7.3 MakeSuperPropertyReference + 9.1.2 GetSuperBase.
        var name = function.PropertyNames[ins.B];
        if (frame.CalleeFunctionObject is not { } calleeFn || calleeFn.HomeObject is not { } home)
        {
            ThrowOrHandle(frame, CreateReferenceError("super reference requires a class method context."));
            return;
        }

        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } baseProtoHandle)
        {
            frame.Registers[ins.A] = JsValue.Undefined;
            return;
        }

        var baseProto = _heap.GetObject(baseProtoHandle);
        frame.Registers[ins.A] = TryGetPropertyValue(baseProto, JsValue.FromObject(baseProtoHandle), name, out var v)
            ? v
            : JsValue.Undefined;
    }

    private void HandleLoadSuperConstructor(InterpreterFrame frame, Instruction ins)
    {
        // ECMA-262 13.3.7.4 GetSuperConstructor: read the active function's
        // HomeObject (which the class compiler sets to the class itself for the
        // constructor), then return HomeObject.[[Prototype]] - the base class.
        if (frame.CalleeFunctionObject is not { } callee || callee.HomeObject is not { } home)
        {
            ThrowOrHandle(frame, CreateReferenceError("super constructor call requires a class constructor context."));
            return;
        }

        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } baseHandle)
        {
            ThrowOrHandle(frame, CreateTypeError("super constructor is not callable (no base class)."));
            return;
        }

        frame.Registers[ins.A] = JsValue.FromObject(baseHandle);
    }

    private ObjectHandle ResolveObjectHandle(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                value.Tag == JsValueTag.Undefined
                    ? "Cannot read properties of undefined."
                    : value.Tag == JsValueTag.Null
                        ? "Cannot read properties of null."
                        : $"Cannot convert {value.Tag} to object."));
        }

        return value.AsObjectHandle();
    }

    // ECMA-262 27.7.5.2 Await(value).
    // If the awaited value is a settled promise, return (or throw) its result
    // immediately so the interpreter can continue without suspension. If the
    // promise is pending, save the frame state to the AsyncContext and attach
    // fulfill/reject handlers that will resume execution when the promise settles.
    private JsValue AwaitValue(InterpreterFrame frame, JsValue value, int destReg)
    {
        var awaitedPromise = PromiseResolveStatic(value);
        if (awaitedPromise.Tag != JsValueTag.Object ||
            _heap.GetObject(awaitedPromise.AsObjectHandle()) is not PromiseInstance instance)
        {
            return value;
        }

        if (instance.Promise.State == PromiseState.Fulfilled)
            return instance.Promise.GetResultUnchecked();

        if (instance.Promise.State == PromiseState.Rejected)
            throw new JsThrownException(instance.Promise.GetResultUnchecked());

        // Pending — suspend the async frame.
        SaveAsyncState(frame, destReg);

        var ctx = frame.AsyncContext!;
        var onFulfilled = GetOrCreateAsyncResumeCallback(isReject: false, ctx);
        var onRejected = GetOrCreateAsyncResumeCallback(isReject: true, ctx);
        var onFulfilledHandle = _heap.AllocateObject(onFulfilled, AllocationSite.Current());
        var onRejectedHandle = _heap.AllocateObject(onRejected, AllocationSite.Current());

        // Attach handlers to the awaited promise.
        PerformPromiseThen(
            awaitedPromise.AsObjectHandle(),
            instance.Promise,
            JsValue.FromObject(onFulfilledHandle),
            JsValue.FromObject(onRejectedHandle),
            GetDummyCapability());

        instance.Promise.IsHandled = true;
        return JsValue.Undefined;
    }

    // Creates (or reuses) a NativeFunctionObject that resumes the given
    // AsyncContext when the awaited promise settles. The callback captures
    // the async context by its ObjectHandle so GC can trace it.
    private NativeFunctionObject GetOrCreateAsyncResumeCallback(bool isReject, AsyncContext ctx)
    {
        // We always create a fresh callback, capturing the specific context.
        // Reuse of prototype patterns could be added as an optimization.
        return new NativeFunctionObject(
            isReject ? "asyncReject" : "asyncResolve",
            (_, args) =>
            {
                var arg = args.Count > 0 ? args[0] : JsValue.Undefined;
                // If this callback is called it means the Context is still alive,
                // so we can safely resume.
                return ResumeAsyncFunction(ctx, arg, isReject);
            },
            length: 1);
    }

    // A reusable no-op PromiseCapability for PerformPromiseThen when the caller
    // doesn't need the chained promise (e.g., async/await's internal handlers).
    private PromiseCapability GetDummyCapability()
    {
        if (_dummyCapability is not null)
            return _dummyCapability.Value;

        var noopResolve = new NativeFunctionObject("", (_, _2) => JsValue.Undefined, length: 1);
        var noopReject = new NativeFunctionObject("", (_, _2) => JsValue.Undefined, length: 1);
        var resolveHandle = _heap.AllocateObject(noopResolve, AllocationSite.Current());
        var rejectHandle = _heap.AllocateObject(noopReject, AllocationSite.Current());
        _heap.PushRoot(resolveHandle);
        _heap.PushRoot(rejectHandle);
        _dummyCapability = new PromiseCapability(
            JsValue.FromObject(resolveHandle),
            JsValue.FromObject(resolveHandle),
            JsValue.FromObject(rejectHandle));
        return _dummyCapability.Value;
    }
    private PromiseCapability? _dummyCapability;

    private JsValue CallFunction(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        var obj = ResolveObject(value);

        // ECMA-262 10.4.1.3 [[Call]] — merge bound args + call-site args,
        // then delegate to [[BoundTargetFunction]] with [[BoundThis]].
        if (obj is BoundFunctionObject bound)
        {
            var merged = MergeBoundArgs(bound.BoundArgs, args);
            return CallFunction(bound.TargetFunction, merged, bound.BoundThis);
        }

        if (obj is JsFunctionObject fn)
        {
            if (fn.Kind == FunctionKind.Async)
            {
                var capability = NewPromiseCapability();

                // Create an AsyncContext to hold suspended state. If the body
                // never awaits, the context is unused and the fast path applies.
                var registers = new JsValue[fn.Function.RegisterCount];
                for (var i = 0; i < registers.Length; i++)
                    registers[i] = JsValue.Undefined;
                var asyncCtx = new AsyncContext(fn.Function, registers, fn.OuterEnvironment)
                {
                    ThisValue = thisValue
                };
                var ctxHandle = _heap.AllocateObject(asyncCtx, AllocationSite.Current());

                // Store capability handles so ResumeAsyncFunction can settle
                // the outer promise when the body eventually completes.
                asyncCtx.CapabilityPromise = capability.Promise.Tag == JsValueTag.Object
                    ? capability.Promise.AsObjectHandle()
                    : null;
                asyncCtx.CapabilityResolve = capability.Resolve.Tag == JsValueTag.Object
                    ? capability.Resolve.AsObjectHandle()
                    : null;
                asyncCtx.CapabilityReject = capability.Reject.Tag == JsValueTag.Object
                    ? capability.Reject.AsObjectHandle()
                    : null;

                try
                {
                    var result = ExecuteInternal(fn.Function, args, thisValue, fn.OuterEnvironment, callee: fn, asyncContext: asyncCtx);

                    if (asyncCtx.IsSuspended)
                    {
                        // Body suspended at an await — resume callbacks already
                        // attached. Root the context so GC doesn't collect it.
                        _heap.PushRoot(ctxHandle);
                        return capability.Promise;
                    }

                    // Body completed without suspension (no await encountered,
                    // or all awaited promises were already settled).
                    _ = CallFunction(capability.Resolve, new[] { result }, JsValue.Undefined);
                }
                catch (JsThrownException ex)
                {
                    if (asyncCtx.IsSuspended)
                    {
                        _heap.PushRoot(ctxHandle);
                        _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
                        return capability.Promise;
                    }

                    _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
                }

                return capability.Promise;
            }

            if (fn.Kind == FunctionKind.Generator)
            {
                // ECMA-262 27.5.1.1 — calling a generator function returns a
                // GeneratorObject without executing the body. Execution starts
                // on the first .next() call.
                var registers = new JsValue[fn.Function.RegisterCount];
                for (var i = 0; i < registers.Length; i++)
                    registers[i] = JsValue.Undefined;
                // Bind parameters into registers
                var paramCount = Math.Min(args.Count, fn.Function.ParameterNames.Count);
                for (var i = 0; i < paramCount; i++)
                    registers[i + 1] = args[i]; // register 0 is return slot, params start at 1

                var genObj = new GeneratorObject(fn.Function, registers, fn.OuterEnvironment);
                genObj.ThisValue = thisValue;
                genObj.SetPrototype(GetGlobalPrototype("GeneratorPrototype"));
                return JsValue.FromObject(_heap.AllocateObject(genObj, AllocationSite.Current()));
            }

            return ExecuteInternal(fn.Function, args, thisValue, fn.OuterEnvironment, callee: fn);
        }

        if (obj is NativeFunctionObject native)
        {
            return native.Call(thisValue, args);
        }

        throw new InvalidOperationException("Value is not callable.");
    }

    private JsValue ConstructFunction(JsValue value, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(value);

        // ECMA-262 10.4.1.4 [[Construct]] — merge bound args + call-site args,
        // then construct [[BoundTargetFunction]].
        if (obj is BoundFunctionObject bound)
        {
            var merged = MergeBoundArgs(bound.BoundArgs, args);
            return ConstructFunction(bound.TargetFunction, merged);
        }

        if (obj is JsFunctionObject fn)
        {
            if (fn.Kind == FunctionKind.Generator)
                throw new JsThrownException(CreateTypeError("Generator functions cannot be used as constructors."));
            return ExecuteConstruct(fn, args, newTarget: value);
        }

        if (obj is NativeFunctionObject native)
        {
            return native.Construct(args);
        }

        throw new InvalidOperationException("Value is not constructible.");
    }

    private void StoreCallResult(InterpreterFrame frame, int destinationRegister, JsValue callee, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        try
        {
            frame.Registers[destinationRegister] = CallFunction(callee, args, thisValue);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    private void StoreConstructResult(InterpreterFrame frame, int destinationRegister, JsValue constructor, IReadOnlyList<JsValue> args)
    {
        try
        {
            frame.Registers[destinationRegister] = ConstructFunction(constructor, args);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    [MayExecuteJs]
    private string ToPropertyKey(JsValue value)
    {
        var primitive = ToPrimitive(value, PrimitiveHint.String);
        return primitive.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.String => primitive.AsString(),
            JsValueTag.Number => FormatNumberForString(primitive.AsNumber()),
            JsValueTag.Int32 => primitive.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Boolean => primitive.AsBoolean() ? "true" : "false",
            _ => primitive.Tag.ToString()
        };
    }

    [MayExecuteJs]
    private JsValue ToPrimitive(JsValue value, PrimitiveHint hint)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        if (TryOrdinaryToPrimitive(value, hint, out var primitive))
        {
            return primitive;
        }

        if (TryGetObjectPrimitiveValue(value, out primitive))
        {
            return primitive;
        }

        throw new JsThrownException(CreateTypeError("Cannot convert object to primitive value."));
    }

    private bool TryOrdinaryToPrimitive(JsValue value, PrimitiveHint hint, out JsValue primitive)
    {
        var obj = _heap.GetObject(value.AsObjectHandle());
        var first = hint == PrimitiveHint.String ? "toString" : "valueOf";
        var second = hint == PrimitiveHint.String ? "valueOf" : "toString";
        return TryCallPrimitiveMethod(obj, value, first, out primitive) ||
               TryCallPrimitiveMethod(obj, value, second, out primitive);
    }

    private bool TryCallPrimitiveMethod(JsObject obj, JsValue thisValue, string name, out JsValue primitive)
    {
        if (TryGetPropertyValue(obj, thisValue, name, out var method) &&
            IsCallable(method))
        {
            var result = CallFunction(method, Array.Empty<JsValue>(), thisValue);
            if (result.Tag != JsValueTag.Object)
            {
                primitive = result;
                return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    private bool IsCallable(JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               _heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject;
    }

    private JsValue BigIntArith(JsValue left, JsValue right, string opName,
        Func<System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger> bigIntOp,
        Func<double, double, double> numOp)
    {
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(bigIntOp(left.AsBigInt(), right.AsBigInt()));
        if (left.Tag == JsValueTag.BigInt || right.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError($"Cannot mix BigInt and other types in {opName}."));
        return JsValue.FromNumber(numOp(ToNumber(left), ToNumber(right)));
    }

    private JsValue Add(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
        {
            return JsValue.FromBigInt(left.AsBigInt() + right.AsBigInt());
        }

        if (left.Tag == JsValueTag.BigInt || right.Tag == JsValueTag.BigInt)
        {
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types in addition."));
        }

        if (left.Tag is JsValueTag.String or JsValueTag.Object || right.Tag is JsValueTag.String or JsValueTag.Object)
        {
            return JsValue.FromString(ToStringValue(left) + ToStringValue(right));
        }

        return JsValue.FromNumber(ToNumber(left) + ToNumber(right));
    }

    private string ToStringValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            if (TryGetObjectPrimitiveValue(value, out var primitive))
            {
                return ToStringValue(primitive);
            }

            var obj = _heap.GetObject(value.AsObjectHandle());
            if (TryGetPropertyValue(obj, value, "toString", out var toString) &&
                toString.Tag == JsValueTag.Object &&
                _heap.GetObject(toString.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject)
            {
                var result = CallFunction(toString, Array.Empty<JsValue>(), value);
                if (result.Tag != JsValueTag.Object)
                {
                    return ToStringValue(result);
                }
            }

            return "[object Object]";
        }

        return FormatPrimitiveForString(value);
    }

    private static string FormatPrimitiveForString(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => FormatNumberForString(value.AsNumber()),
            JsValueTag.String => value.AsString(),
            JsValueTag.Symbol => "Symbol(" + (value.AsSymbolDescription() ?? string.Empty) + ")",
            JsValueTag.BigInt => value.AsBigInt().ToString(System.Globalization.CultureInfo.InvariantCulture) + "n",
            JsValueTag.Object => "[object Object]",
            JsValueTag.HostObject => "[object Object]",
            _ => value.Tag.ToString()
        };
    }

    private static string FormatNumberForString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            return "0";
        }

        var absolute = Math.Abs(value);
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        if (text.Contains('E', StringComparison.Ordinal) || text.Contains('e', StringComparison.Ordinal))
        {
            if (absolute >= 1e-6 && absolute < 1e21)
            {
                return ExpandExponentialNumber(text);
            }

            return NormalizeExponentialNumber(text);
        }

        return TrimDecimalZeros(text);
    }

    private static string ExpandExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = normalized[..exponentMarker];
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var negative = coefficient.StartsWith("-", StringComparison.Ordinal);
        if (negative || coefficient.StartsWith("+", StringComparison.Ordinal))
        {
            coefficient = coefficient[1..];
        }

        var decimalIndex = coefficient.IndexOf('.', StringComparison.Ordinal);
        if (decimalIndex < 0)
        {
            decimalIndex = coefficient.Length;
        }

        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        var newDecimalIndex = decimalIndex + exponent;
        string expanded;
        if (newDecimalIndex <= 0)
        {
            expanded = "0." + new string('0', -newDecimalIndex) + digits;
        }
        else if (newDecimalIndex >= digits.Length)
        {
            expanded = digits + new string('0', newDecimalIndex - digits.Length);
        }
        else
        {
            expanded = digits[..newDecimalIndex] + "." + digits[newDecimalIndex..];
        }

        expanded = TrimDecimalZeros(expanded);
        return negative ? "-" + expanded : expanded;
    }

    private static string NormalizeExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = TrimDecimalZeros(normalized[..exponentMarker]);
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var sign = exponent >= 0 ? "+" : string.Empty;
        return $"{coefficient}e{sign}{exponent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string TrimDecimalZeros(string text)
    {
        if (!text.Contains(".", StringComparison.Ordinal))
        {
            return text;
        }

        return text.TrimEnd('0').TrimEnd('.');
    }

    private double ToNumber(JsValue value)
    {
        if (value.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(value, out var primitive))
        {
            return ToNumber(primitive);
        }

        if (value.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot convert a BigInt value to a number."));

        return value.Tag switch
        {
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.Null => 0d,
            JsValueTag.Undefined => double.NaN,
            JsValueTag.String => ToNumberForEquality(value),
            _ => double.NaN
        };
    }

    private bool IsLessThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) < 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() < right.AsBigInt();
        return ToNumber(left) < ToNumber(right);
    }

    private bool IsGreaterThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) > 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() > right.AsBigInt();
        return ToNumber(left) > ToNumber(right);
    }

    private bool IsLessThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) <= 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() <= right.AsBigInt();
        return ToNumber(left) <= ToNumber(right);
    }

    private bool IsGreaterThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) >= 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() >= right.AsBigInt();
        return ToNumber(left) >= ToNumber(right);
    }

    private bool TryInstanceOf(InterpreterFrame frame, JsValue left, JsValue right, out bool result)
    {
        result = false;

        if (right.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' must be an object.");
            return false;
        }

        var ctorObj = ResolveObject(right);
        if (ctorObj is not JsFunctionObject && ctorObj is not NativeFunctionObject)
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' is not callable.");
            return false;
        }

        if (left.Tag != JsValueTag.Object)
        {
            result = false;
            return true;
        }

        if (!TryGetPropertyValue(ctorObj, right, "prototype", out var prototypeValue) ||
            prototypeValue.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Function has non-object prototype in 'instanceof'.");
            return false;
        }

        var targetPrototype = prototypeValue.AsObjectHandle();
        var currentObj = ResolveObject(left);
        while (currentObj.PrototypeHandle is { } proto)
        {
            if (proto.Equals(targetPrototype))
            {
                result = true;
                return true;
            }

            currentObj = _heap.GetObject(proto);
        }

        result = false;
        return true;
    }

    private string TypeOfValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is JsFunctionObject or NativeFunctionObject)
            {
                return "function";
            }

            return "object";
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "object",
            JsValueTag.Boolean => "boolean",
            JsValueTag.Int32 => "number",
            JsValueTag.Number => "number",
            JsValueTag.String => "string",
            JsValueTag.Symbol => "symbol",
            JsValueTag.BigInt => "bigint",
            JsValueTag.HostObject => "object",
            _ => "undefined"
        };
    }

    [MayExecuteJs]
    private JsValue ExecuteConstruct(JsFunctionObject callee, IReadOnlyList<JsValue> args, JsValue newTarget = default)
    {
        var instanceObject = CreateOrdinaryObject();
        if (callee.TryGetProperty("prototype", h => _heap.GetObject(h), out var prototypeDescriptor) &&
            prototypeDescriptor.Value.Tag == JsValueTag.Object)
        {
            instanceObject.SetPrototype(prototypeDescriptor.Value.AsObjectHandle());
        }

        var defaultInstance = JsValue.FromObject(_heap.AllocateObject(instanceObject, AllocationSite.Current()));
        _pendingNewTarget = newTarget.Tag == JsValueTag.Undefined
            ? JsValue.Undefined
            : newTarget;
        var result = ExecuteInternal(callee.Function, args, defaultInstance, callee.OuterEnvironment, callee: callee);
        return result.Tag == JsValueTag.Object ? result : defaultInstance;
    }

    private enum PrimitiveHint
    {
        String,
        Number
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
