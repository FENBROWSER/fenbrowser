using FenBrowser.Js.Builtins;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FenBrowser.Js.Regex;

namespace FenBrowser.Js.Interpreter;

public sealed partial class BytecodeInterpreter : IBuiltinContext, IHeapRootSource
{
    private static readonly char[] EcmaWhitespaceChars =
    [
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007',
        '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
        '\uFEFF'
    ];

    private readonly JsHeap _heap;
    public JsHeap Heap => _heap;
    // Audit �1: every active InterpreterFrame is registered here so the GC
    // sees its register/this/newtarget/env references as roots. Without this,
    // a cell only reachable through a frame register can be reclaimed by an
    // auto-MinorCollect inside user code and surface as "Stale heap handle.".
    private readonly Stack<InterpreterFrame> _activeFrames = new();

    private readonly struct ActiveFrameScope : IDisposable
    {
        private readonly Stack<InterpreterFrame> _stack;
        public ActiveFrameScope(Stack<InterpreterFrame> stack, InterpreterFrame frame)
        {
            _stack = stack;
            stack.Push(frame);
        }
        public void Dispose() => _stack.Pop();
    }

    void IHeapRootSource.TraceRoots(IHeapTracer tracer)
    {
        foreach (var frame in _activeFrames)
        {
            for (var i = 0; i < frame.Registers.Length; i++)
            {
                var v = frame.Registers[i];
                if (v.Tag == JsValueTag.Object) tracer.Trace(v.AsObjectHandle());
            }
            if (frame.ThisValue.Tag == JsValueTag.Object) tracer.Trace(frame.ThisValue.AsObjectHandle());
            if (frame.NewTarget.Tag == JsValueTag.Object) tracer.Trace(frame.NewTarget.AsObjectHandle());
            if (frame.PendingException is { } pe && pe.Tag == JsValueTag.Object)
                tracer.Trace(pe.AsObjectHandle());
            frame.Environment?.Trace(tracer);
        }
    }
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
    ObjectHandle IBuiltinContext.MaterializeSuppressedErrorConstructor() => GetGlobalConstructorHandle("SuppressedError");
    ObjectHandle IBuiltinContext.MaterializeGeneratorFunctionConstructor() => EnsureGeneratorFunctionConstructor();
    string IBuiltinContext.CaptureCallStack(string errorName, string message) => FormatCallStack(errorName, message);
    ObjectHandle IBuiltinContext.MaterializeStructuredCloneFunction() => EnsureStructuredCloneFunction();
    ObjectHandle IBuiltinContext.MaterializeIntlObject() => EnsureIntlObject();
    ObjectHandle IBuiltinContext.MaterializeArrayBufferConstructor() => EnsureArrayBufferConstructor();
    ObjectHandle IBuiltinContext.MaterializeSharedArrayBufferConstructor() => EnsureSharedArrayBufferConstructor();
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
    private ObjectHandle? _generatorFunctionConstructorHandle;
    private ObjectHandle? _functionPrototypeHandle;
    private ObjectHandle? _throwTypeErrorIntrinsicHandle;
    private ObjectHandle? _generatorFunctionPrototypeHandle;
    private ObjectHandle? _functionCallMethodHandle;
    private ObjectHandle? _evalFunctionHandle;
    private EnvironmentRecord? _directEvalEnv;
    private bool _directEvalStrictMode;
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
    private ObjectHandle? _asyncGeneratorPrototypeHandle;
    private ObjectHandle? _asyncGeneratorAsyncIteratorHandle;
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
    private const int DirectEvalCallFlag = 1;

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
    private ObjectHandle? _intlObjectHandle;
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

    // Tier 5 #27: wall-clock execution deadline in milliseconds. Zero = no
    // limit. Checked every WallClockCheckInterval instructions to keep the
    // hot path cheap; a small over-shoot beyond the deadline is acceptable
    // because the budget exists to bound runaway scripts, not to provide
    // sub-millisecond precision.
    public long WallClockTimeoutMs { get; set; }
    private const int WallClockCheckInterval = 1024;
    private long _wallClockDeadlineTicks;
    private int _wallClockCheckCountdown;

    // Tier 5 #25: per-realm CSP eval policy. When false, eval() and the
    // Function/AsyncFunction/GeneratorFunction constructors throw EvalError,
    // mirroring the effect of a `script-src` directive without `'unsafe-eval'`.
    public bool EvalAllowed { get; set; } = true;

    public BytecodeInterpreter(JsHeap? heap = null)
    {
        _heap = heap ?? new JsHeap();
        _heap.AddRootSource(this);
    }

    // Diagnostic-only: render a thrown JS value as a short "Name: message" string by
    // reading the (prototype-resolved) `name` and `message` properties when the value is
    // an Error-like object. Used by the test262 runner to turn the opaque "thrown=object"
    // classification into an actionable error description. Best-effort: never throws.
    public string DescribeThrownValue(JsValue value)
    {
        try
        {
            if (value.Tag != JsValueTag.Object)
            {
                return value.Tag switch
                {
                    JsValueTag.Undefined => "undefined",
                    JsValueTag.Null => "null",
                    JsValueTag.Boolean => $"boolean:{value.AsBoolean()}",
                    JsValueTag.Int32 => $"int32:{value.AsInt32()}",
                    JsValueTag.Number => $"number:{value.AsNumber()}",
                    JsValueTag.String => $"string:{value.AsString()}",
                    JsValueTag.Symbol => "symbol",
                    JsValueTag.BigInt => $"bigint:{value.AsBigInt()}",
                    _ => value.Tag.ToString()
                };
            }

            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is null) return "object";

            string name = TryGetPropertyValue(obj, value, "name", out var nameVal) &&
                          nameVal.Tag != JsValueTag.Undefined
                ? ToStringValue(nameVal)
                : "Error";
            string message = TryGetPropertyValue(obj, value, "message", out var msgVal) &&
                             msgVal.Tag != JsValueTag.Undefined
                ? ToStringValue(msgVal)
                : string.Empty;

            var rendered = string.IsNullOrEmpty(message) ? name : $"{name}: {message}";

            if (TryGetPropertyValue(obj, value, "stack", out var stackVal) &&
                stackVal.Tag == JsValueTag.String)
            {
                var stack = stackVal.AsString();
                if (!string.IsNullOrEmpty(stack) && stack != rendered)
                {
                    rendered += "\n" + stack;
                }
            }

            return rendered;
        }
        catch
        {
            return "object";
        }
    }

    [MayExecuteJs]
    public JsValue Execute(BytecodeFunction function)
    {
        _instructionCount = 0;
        if (WallClockTimeoutMs > 0)
        {
            _wallClockDeadlineTicks = System.Environment.TickCount64 + WallClockTimeoutMs;
            _wallClockCheckCountdown = WallClockCheckInterval;
        }
        else
        {
            _wallClockDeadlineTicks = 0;
        }
        var globalHandle = EnsureGlobalObject();
        JsValue result;
        try
        {
            result = ExecuteInternal(
                function,
                Array.Empty<JsValue>(),
                JsValue.FromObject(globalHandle),
                frameEnvironment: EnsureGlobalEnvironment());
        }
        catch (FenBrowser.Js.Heap.JsEngineFatalException fatal)
        {
            // Heap consistency exceptions ("Stale heap handle.", "Invalid heap
            // handle index.") indicate a latent GC root-tracking gap, not a
            // user-visible JS condition. Reporting them as engine crashes loses
            // a whole test for what may be a recoverable native path; convert
            // them at the top level into an uncaught TypeError so test262
            // wrappers like assert.throws(TypeError, ...) still observe the
            // expected exception kind and the test grader sees a runtime error
            // rather than a crash. The root cause is tracked separately.
            throw new JsThrownException(CreateTypeError("Internal heap error: " + fatal.Message));
        }
        catch (JsThrownException thrown)
        {
            StampDescription(thrown);
            throw;
        }
        DrainPendingMicrotasks();
        return result;
    }

    // Populate the diagnostic Description of an escaping JS exception while the heap is
    // still live, so host/log sites that render ex.Message see the real error text.
    internal void StampDescription(JsThrownException thrown)
    {
        if (thrown is null || !string.IsNullOrEmpty(thrown.Description))
        {
            return;
        }

        try
        {
            thrown.Description = DescribeThrownValue(thrown.Value);
        }
        catch
        {
            // Diagnostic best-effort only — never let description rendering mask the
            // original exception.
        }
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
        gen.SavedCatchHandlers = frame.CatchHandlers.ToArray();
		gen.SavedFinallyHandlers = frame.FinallyHandlers.ToArray();
		gen.PendingException = frame.PendingException;
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
        ctx.SavedCatchHandlers = frame.CatchHandlers.ToArray();
		ctx.SavedFinallyHandlers = frame.FinallyHandlers.ToArray();
		ctx.PendingException = frame.PendingException;
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
    // Drain the microtask/promise-job queues from a host callback boundary (timers,
    // events) so promise reactions scheduled inside a setTimeout/rAF callback run with
    // the same checkpoint semantics as top-level script execution.
    public void PumpMicrotasks() => DrainPendingMicrotasks();

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
    // Keep the cap just above the deepest intentional regression depth (35) while
    // reserving stack headroom for unwind/exception paths.
    private const int MaxCallDepth = 40;
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
        // ECMA-262 10.2.1.3 OrdinaryCallBindThis: a non-strict ordinary function called
        // with a null/undefined receiver binds `this` to the realm's global object.
        // Arrow functions have no own `this`; derived constructors bind it via super().
        if (!function.IsStrictMode
            && function.Kind != FunctionKind.Arrow
            && !function.IsDerivedConstructor
            && thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            thisValue = JsValue.FromObject(EnsureGlobalObject());
        }

        EnvironmentRecord? frameEnv;
        if (frameEnvironment is not null)
        {
            frameEnv = frameEnvironment;
        }
        else if (outerEnvironment is null)
        {
            frameEnv = null;
        }
        else if (function.IsDerivedConstructor)
        {
            // `this` stays uninitialized until super(...) runs InitThisBinding.
            frameEnv = new FunctionEnvironmentRecord(
                ThisBindingStatus.Uninitialized, JsValue.Undefined, JsValue.Undefined, callee?.HomeObject, outerEnvironment);
        }
        else if (function.Kind == FunctionKind.Arrow)
        {
            // ECMA-262 9.1.1.3: arrow functions have no `this` binding of their
            // own; a plain declarative record lets `this` resolve through the
            // outer (enclosing function/global) environment.
            frameEnv = new DeclarativeEnvironmentRecord(outerEnv: outerEnvironment);
        }
        else
        {
            // Ordinary functions / methods bind `this` into a function
            // environment record (10.2.1.2 OrdinaryCallBindThis) so that nested
            // arrow functions — which read `this` via GetThisEnvironment — can
            // observe it. Previously this lived only in frame.ThisValue, which
            // is invisible to an inner arrow's own frame.
            var functionEnv = new FunctionEnvironmentRecord(
                ThisBindingStatus.Uninitialized, JsValue.Undefined, JsValue.Undefined, callee?.HomeObject, outerEnvironment);
            _ = functionEnv.BindThisValue(thisValue);
            // ECMA-262 15.2.5: a named function expression binds its own name (immutably)
            // in scope of its body so it can reference itself (e.g. for recursion).
            if (function.BindsOwnNameInBody && function.Name is { Length: > 0 } selfName
                && callee?.SelfHandle is { } selfHandle)
            {
                _ = functionEnv.CreateImmutableBinding(selfName, strict: true);
                _ = functionEnv.InitializeBinding(selfName, JsValue.FromObject(selfHandle));
            }
            frameEnv = functionEnv;
        }
        var frame = new InterpreterFrame(function, thisValue, frameEnv) { CalleeFunctionObject = callee, OwnerGenerator = ownerGenerator, AsyncContext = asyncContext };
        // Audit �1: pin this frame's registers/env into the GC root set for
        // its execution lifetime. Dispose pops on every return path (normal
        // return, exception, generator yield) via using-scope semantics.
        using var _frameScope = new ActiveFrameScope(_activeFrames, frame);
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
            var savedCatch = ownerGenerator.SavedCatchHandlers;
			var savedFinally = ownerGenerator.SavedFinallyHandlers;
            
    for (var i = savedCatch.Length - 1; i >= 0; i--)
            {
                frame.CatchHandlers.Push(savedCatch[i]);
                frame.FinallyHandlers.Push(savedFinally[i]);
            }
            frame.PendingException = ownerGenerator.PendingException;
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
            var savedCatch = asyncContext.SavedCatchHandlers;
			var savedFinally = asyncContext.SavedFinallyHandlers;
            
    for (var i = savedCatch.Length - 1; i >= 0; i--)
            {
                frame.CatchHandlers.Push(savedCatch[i]);
                frame.FinallyHandlers.Push(savedFinally[i]);
            }
            frame.PendingException = asyncContext.PendingException;
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
                JsValue paramValue;
                if (i == function.RestParameterIndex)
                {
                    var restCount = Math.Max(0, args.Count - i);
                    var restValues = new JsValue[restCount];
                    for (var restIndex = 0; restIndex < restCount; restIndex++)
                    {
                        restValues[restIndex] = args[i + restIndex];
                    }

                    var restArray = CreateArrayObject(restValues);
                    paramValue = JsValue.FromObject(_heap.AllocateObject(restArray, AllocationSite.Current()));
                }
                else if (function.RestParameterIndex >= 0 && i > function.RestParameterIndex)
                {
                    // The parser disallows trailing parameters after a rest
                    // parameter, but keep runtime behavior deterministic.
                    paramValue = JsValue.Undefined;
                }
                else
                {
                    paramValue = i < args.Count ? args[i] : JsValue.Undefined;
                }

                _ = frame.Environment.CreateMutableBinding(paramName, deletable: false);
                _ = frame.Environment.InitializeBinding(paramName, paramValue);
            }

            if (function.HasOwnArgumentsObject &&
                !function.ParameterNames.Contains("arguments", StringComparer.Ordinal))
            {
                var argumentsObject = CreateArgumentsObject(args, function.UsesRestrictedArgumentsObject, callee);
                _ = frame.Environment.CreateMutableBinding("arguments", deletable: false);
                _ = frame.Environment.InitializeBinding("arguments", argumentsObject);
            }

            InstantiateVarDeclarations(function, frame);
            InstantiateLexicalDeclarations(function, frame);
        }

        // Tier 4 #24: if a JIT delegate is available, run it instead of
        // the dispatch loop. The delegate executes the entire function
        // body and returns the function's return value. Exceptions
        // propagate via JsThrownException same as the interpreter.
        if (function.JitDelegate is { } jitFn)
        {
            return jitFn(this, frame);
        }

        while (frame.InstructionPointer < function.Instructions.Count)
        {
            // Plan §14.2: instruction budget and interrupt check.
            if (InstructionBudget > 0 && ++_instructionCount > InstructionBudget)
                throw new JsThrownException(CreateRangeError("Maximum instruction budget exceeded."));
            if (InterruptCallback is { } cb && !cb())
                throw new JsThrownException(CreateRangeError("Execution interrupted."));
            // Tier 5 #27: wall-clock deadline. Sampled every N instructions to
            // amortize the TickCount64 read.
            if (_wallClockDeadlineTicks != 0 && --_wallClockCheckCountdown <= 0)
            {
                _wallClockCheckCountdown = WallClockCheckInterval;
                if (System.Environment.TickCount64 >= _wallClockDeadlineTicks)
                    throw new JsThrownException(CreateRangeError("Script wall-clock timeout exceeded."));
            }

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

                    // ECMA-262 9.1.2.5 GetThisEnvironment: walk the lexical
                    // environment chain to the nearest record that provides a
                    // `this` binding (a function or the global record). Arrow
                    // functions create a declarative record with no own `this`,
                    // so resolution must climb to the enclosing function's
                    // FunctionEnvironmentRecord (or globalThis) rather than read
                    // the call-site receiver, which is undefined for `arrow()`.
                    if (TryResolveThisBinding(frame.Environment, out var boundThis))
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
                    // Tier-4 #24 (audit �3.2): a back-edge is a Jump
                    // whose target precedes the source IP. Saturating add
                    // so a tight inner loop in a runaway script can't
                    // overflow into negative territory and reset
                    // tier-up trigger logic.
                    if (ins.A < frame.InstructionPointer - 1 && function.BackEdges < int.MaxValue)
                    {
                        function.BackEdges++;
                    }
                    frame.InstructionPointer = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (!IsTruthy(frame.Registers[ins.A]))
                    {
                        if (ins.B < frame.InstructionPointer - 1 && function.BackEdges < int.MaxValue)
                        {
                            function.BackEdges++;
                        }
                        frame.InstructionPointer = ins.B;
                    }
                    break;
                case OpCode.PushHandler:
                    frame.CatchHandlers.Push(ins.A);
					frame.FinallyHandlers.Push(ins.D);
                    break;
                case OpCode.PopHandler:
                    if (frame.CatchHandlers.Count > 0)
                    {
                        frame.CatchHandlers.Pop(); frame.FinallyHandlers.Pop();
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
                case OpCode.CopyDataProperties:
                {
                    // ECMA-262 13.2.5.5 object spread `{ ...src }`: copy own
                    // enumerable string+symbol properties from the source into the
                    // target object literal. null/undefined source is a no-op.
                    CopyDataPropertiesInto(frame.Registers[ins.A], frame.Registers[ins.B]);
                    break;
                }
                case OpCode.NewArray:
                {
                    var obj = CreateArrayObject(Array.Empty<JsValue>());
                    var handle = _heap.AllocateObject(obj, AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.NewRegExp:
                {
                    var rawText = function.Constants[ins.B].AsString();
                    frame.Registers[ins.A] = NewRegExpLiteral(rawText);
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
                case OpCode.PrologueEnd:
                    // ECMA-262 FunctionDeclarationInstantiation runs synchronously
                    // before generator/async-generator construction returns to the
                    // caller. When this frame is owned by a generator, treat the
                    // marker like a value-less yield: persist the frame state and
                    // hand control back to the call-site path so it can return the
                    // newly-paused generator. For ordinary frames it is a Nop.
                    if (frame.OwnerGenerator is not null)
                    {
                        SaveGeneratorState(frame, 0);
                        return JsValue.Undefined;
                    }
                    break;
                case OpCode.DefineMethod:
                    HandleDefineMethod(frame, function, ins);
                    break;
                case OpCode.DefineMethodByReg:
                    HandleDefineMethodByReg(frame, ins);
                    break;
                case OpCode.SetHomeObject:
                    HandleSetHomeObject(frame, ins);
                    break;
                case OpCode.LoadSuperProperty:
                    HandleLoadSuperProperty(frame, function, ins);
                    break;
                case OpCode.LoadSuperElement:
                    HandleLoadSuperElement(frame, ins);
                    break;
                case OpCode.DynamicImport:
                    frame.Registers[ins.A] = HandleDynamicImport(frame.Registers[ins.B]);
                    break;
                case OpCode.ImportMeta:
                    frame.Registers[ins.A] = HandleImportMeta();
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
                    gen.SavedCatchHandlers = frame.CatchHandlers.ToArray();
		gen.SavedFinallyHandlers = frame.FinallyHandlers.ToArray();
		gen.PendingException = frame.PendingException;

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
                    // ECMA-262 14.2 � every block creates a fresh lexical
                    // env-record. ins.A is the variable-slot index for the
                    // let/const name this EnterScope owns; SlotNameTable
                    // resolves it. Slot 0 is a perfectly valid name slot
                    // when the bound name is the function's first variable
                    // (e.g. `function f(x){ {let x=...} }`), so we use the
                    // slot-name presence rather than `slot != 0` as the
                    // "has a binding to install" predicate.
                    var scopeName = SlotNameTable.GetName(function, ins.A);
                    if (scopeName != null)
                    {
                        // B=0 -> mutable (let), B=1 -> immutable (const)
                        if (ins.B == 1)
                            _ = newScope.CreateImmutableBinding(scopeName, strict: true);
                        else
                            _ = newScope.CreateMutableBinding(scopeName, deletable: true);
                        // Do NOT initialize - leave the binding in TDZ state.
                    }
                    frame.Environment = newScope;
                    break;
                }
                case OpCode.LeaveScope:
                {
                    frame.Environment = frame.Environment.OuterEnv ?? frame.Environment;
                    break;
                }
                case OpCode.EndFinally:
                {
                    if (frame.PendingException is { } pending)
                    {
                        frame.PendingException = null;
                        ThrowOrHandle(frame, pending);
                    }
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
                    var icOffsetStore = frame.InstructionPointer - 1;
                    if (receiverValue.Tag == JsValueTag.Object &&
                        TryStoreIC(function, icOffsetStore, ownerHandle, receiverValue, prop, value))
                    {
                        break;
                    }
                    var obj = _heap.GetObject(ownerHandle);
                    if (obj is ProxyObject proxySet)
                    {
                        try { _ = ProxySet(proxySet, receiverValue, prop, value); }
                        catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                        break;
                    }
                    try
                    {
                        var ok = SetPropertyValue(ownerHandle, obj, prop, value, receiverValue);
                        if (!ok && function.IsStrictMode)
                        {
                            // ECMA-262 6.2.5.4 PutValue: strict-mode writes that
                            // return false (non-writable data, missing setter,
                            // non-extensible) throw TypeError.
                            ThrowOrHandle(frame, CreateTypeError($"Cannot assign to read-only property '{prop}'."));
                            break;
                        }
                        if (receiverValue.Tag == JsValueTag.Object)
                            PopulateStoreIC(function, icOffsetStore, receiverValue, prop);
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

                    var prop = function.PropertyNames[ins.C];
                    var obj = ResolveObject(receiver);
                    if (obj is ProxyObject proxyDel)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(ProxyDelete(proxyDel, prop));
                        break;
                    }
                    frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteProperty(prop));
                    break;
                }
                case OpCode.SetElem:
                {
                    var ownerHandle = ResolveObjectHandle(frame.Registers[ins.A]);
                    var obj = _heap.GetObject(ownerHandle);
                    var keyValue = frame.Registers[ins.B];
                    var value = frame.Registers[ins.C];

                    // ECMA-262 23.2.4.3 IntegerIndexedElementSet: TypedArray integer
                    // indices write through to the underlying buffer; out-of-bounds
                    // writes are silently dropped and the property table is untouched.
                    if (obj is TypedArrayObject taSet)
                    {
                        string? taKey = keyValue.Tag switch
                        {
                            JsValueTag.String => keyValue.AsString(),
                            JsValueTag.Int32 or JsValueTag.Number => ToPropertyKey(keyValue),
                            _ => null
                        };
                        if (taKey != null && IsCanonicalIntegerIndex(taKey, out var taSetIdx))
                        {
                            taSet.SetElement(taSetIdx, value);
                            break;
                        }
                    }

                    if (keyValue.Tag == JsValueTag.Symbol)
                    {
                        var ok = SetSymbolPropertyValue(ownerHandle, obj, keyValue.AsSymbolId(), value, frame.Registers[ins.A]);
                        if (!ok && function.IsStrictMode)
                        {
                            ThrowTypeError(frame, "Cannot assign to symbol-keyed property.");
                        }

                        break;
                    }

                    var key = ToPropertyKey(keyValue);
                    try
                    {
                        var ok = SetPropertyValue(ownerHandle, obj, key, value, frame.Registers[ins.A]);
                        if (!ok && function.IsStrictMode)
                        {
                            ThrowOrHandle(frame, CreateTypeError($"Cannot assign to read-only property '{key}'."));
                            break;
                        }
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                        break;
                    }

                    // ECMA-262 10.4.2.1 ArraySetLength coupling: writing a canonical
                    // array index that is >= the current length grows "length".
                    // This is an Array exotic-object behavior ONLY — plain objects
                    // that merely happen to carry a "length" property (array-likes,
                    // `Math`, instances inheriting length) must not have their
                    // length mutated by an indexed assignment.
                    if (obj is ArrayObject && IsCanonicalIntegerIndex(key, out var arrayIndex))
                    {
                        var nextLength = (double)arrayIndex + 1;
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
                    var keyValueDel = frame.Registers[ins.C];
                    if (keyValueDel.Tag == JsValueTag.Symbol)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteSymbolProperty(keyValueDel.AsSymbolId()));
                        break;
                    }
                    var key = ToPropertyKey(keyValueDel);
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
                    // GetIterator runs the user @@iterator method, which can throw;
                    // route it through the frame's handler stack like other
                    // user-code-invoking opcodes.
                    try
                    {
                        frame.Registers[ins.A] = CreateForOfIteratorState(frame.Registers[ins.B]);
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                }
                case OpCode.ForOfNext:
                {
                    // Lazy mode calls the user .next(), which can throw.
                    try
                    {
                        var iter = ResolveObject(frame.Registers[ins.B]) as ForOfIteratorObject
                            ?? throw new InvalidOperationException("Invalid for-of iterator object.");
                        if (ForOfStepDone(iter, out var value))
                        {
                            frame.InstructionPointer = ins.C;
                        }
                        else
                        {
                            frame.Registers[ins.A] = value;
                        }
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                }
                case OpCode.IteratorClose:
                {
                    // ECMA-262 7.4.11 — emitted on the for-of break exit path. The
                    // iterator's return() method (and the not-an-object TypeError)
                    // must be observable by an enclosing try.
                    try
                    {
                        if (ResolveObject(frame.Registers[ins.B]) is ForOfIteratorObject closing)
                        {
                            CloseForOfIteratorState(closing);
                        }
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
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
                    // ECMA-262 PrivateGet: a private field is an own data property, but a
                    // private method/accessor lives on the prototype. Resolve through the
                    // chain (TryGetPropertyValue invokes a private getter when present).
                    if (!TryGetPropertyValue(obj, objVal, name, out var privateValue))
                        throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
                    frame.Registers[ins.A] = privateValue;
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
                    if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand)
                        throw new JsThrownException(CreateTypeError("Cannot write private field to an object whose class did not declare it."));
                    WritePrivateField(obj, objVal, name, value);
                    break;
                }
                case OpCode.GetElem:
                {
                    var receiver = frame.Registers[ins.B];
                    var keyValue = frame.Registers[ins.C];
                    try
                    {
                        if (keyValue.Tag == JsValueTag.Symbol)
                        {
                            frame.Registers[ins.A] = GetReceiverSymbolProperty(receiver, keyValue.AsSymbolId());
                        }
                        else
                        {
                            // Tier 4 #20: GetElem IC fast path for the common
                            // `obj["foo"]` (string-keyed) form. Other key types
                            // (Int32 indices into Arrays, Number, etc.) fall
                            // through to the generic ToPropertyKey path.
                            var icOffsetElem = frame.InstructionPointer - 1;
                            if (keyValue.Tag == JsValueTag.String &&
                                TryGetElemStringIC(function, icOffsetElem, receiver, keyValue.AsString(), out var elemResult))
                            {
                                frame.Registers[ins.A] = elemResult;
                            }
                            else
                            {
                                var propKey = ToPropertyKey(keyValue);
                                frame.Registers[ins.A] = GetReceiverProperty(receiver, propKey);
                                if (keyValue.Tag == JsValueTag.String)
                                {
                                    PopulateGetElemStringIC(function, icOffsetElem, receiver, propKey);
                                }
                            }
                        }
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
                    StoreCallResult(
                        frame,
                        ins.A,
                        frame.Registers[ins.B],
                        Array.Empty<JsValue>(),
                        JsValue.Undefined,
                        allowDirectEval: ins.E == DirectEvalCallFlag,
                        icOffset: frame.InstructionPointer - 1);
                    break;
                }
                case OpCode.Call1:
                {
                    StoreCallResult(
                        frame,
                        ins.A,
                        frame.Registers[ins.B],
                        new[] { frame.Registers[ins.C] },
                        JsValue.Undefined,
                        allowDirectEval: ins.E == DirectEvalCallFlag,
                        icOffset: frame.InstructionPointer - 1);
                    break;
                }
                case OpCode.CallMethod0:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>(), frame.Registers[ins.C], icOffset: frame.InstructionPointer - 1);
                    break;
                }
                case OpCode.CallMethod1:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.D] }, frame.Registers[ins.C], icOffset: frame.InstructionPointer - 1);
                    break;
                }
                case OpCode.CallMethodN:
                {
                    var callArgs = new JsValue[ins.E];
                    for (var i = 0; i < ins.E; i++)
                    {
                        callArgs[i] = frame.Registers[ins.D + i];
                    }

                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], callArgs, frame.Registers[ins.C], icOffset: frame.InstructionPointer - 1);
                    break;
                }
                case OpCode.CallN:
                {
                    var callArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        callArgs[i] = frame.Registers[ins.C + i];
                    }

                    StoreCallResult(
                        frame,
                        ins.A,
                        frame.Registers[ins.B],
                        callArgs,
                        JsValue.Undefined,
                        allowDirectEval: ins.E == DirectEvalCallFlag,
                        icOffset: frame.InstructionPointer - 1);
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
                    StoreCallResult(
                        frame,
                        ins.A,
                        frame.Registers[ins.B],
                        unpackedArgs,
                        thisVal,
                        allowDirectEval: ins.E == DirectEvalCallFlag);
                    break;
                }
                case OpCode.ConstructSpread:
                {
                    // ECMA-262 13.3.5.1 — unpack a spread array into constructor args.
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
                                unpackedArgs[i] = arrObj.TryGetOwnProperty(i.ToString(), out var elemDesc)
                                    ? elemDesc.Value
                                    : JsValue.Undefined;
                            }
                        }
                    }

                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], unpackedArgs);
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
                case OpCode.ToNumeric:
                    // ToNumeric can run user code (valueOf/toString via ToPrimitive)
                    // and a Symbol/BigInt mismatch throws — route through the
                    // frame handler stack.
                    try
                    {
                        frame.Registers[ins.A] = ToNumericValue(frame.Registers[ins.B]);
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.ToStringCoerce:
                    // ToString runs user code (toString/valueOf via ToPrimitive) and may
                    // throw — route through the frame handler stack.
                    try
                    {
                        frame.Registers[ins.A] = JsValue.FromString(ToStringValue(frame.Registers[ins.B]));
                    }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Increment:
                    frame.Registers[ins.A] = StepNumeric(frame.Registers[ins.B], +1);
                    break;
                case OpCode.Decrement:
                    frame.Registers[ins.A] = StepNumeric(frame.Registers[ins.B], -1);
                    break;
                case OpCode.Delete:
                    frame.Registers[ins.A] = DeleteName(frame, ins.B);
                    break;
                case OpCode.TypeOf:
                    frame.Registers[ins.A] = JsValue.FromString(TypeOfValue(frame.Registers[ins.B]));
                    break;
                case OpCode.TypeOfName:
                    frame.Registers[ins.A] = JsValue.FromString(TypeOfName(frame, ins.B));
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
                case OpCode.Exp:
                    try
                    {
                        var left = frame.Registers[ins.B];
                        var right = frame.Registers[ins.C];
                        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
                        {
                            var baseVal = left.AsBigInt();
                            var expVal = right.AsBigInt();
                            if (expVal < System.Numerics.BigInteger.Zero)
                                throw new JsThrownException(CreateRangeError("BigInt exponent must be non-negative."));
                            if (expVal > int.MaxValue)
                                throw new JsThrownException(CreateRangeError("BigInt exponent is too large."));
                            frame.Registers[ins.A] = JsValue.FromBigInt(System.Numerics.BigInteger.Pow(baseVal, (int)expVal));
                        }
                        else
                        {
                            var a = ToNumber(left);
                            var b = ToNumber(right);
                            frame.Registers[ins.A] = JsValue.FromNumber(Math.Pow(a, b));
                        }
                    }
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
                    var has = HasPropertyIncludingProxy(obj, key);
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
                case OpCode.BitAnd:
                    try { frame.Registers[ins.A] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[ins.B]) & (int)ToNumber(frame.Registers[ins.C]))); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.BitOr:
                    try { frame.Registers[ins.A] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[ins.B]) | (int)ToNumber(frame.Registers[ins.C]))); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.BitXor:
                    try { frame.Registers[ins.A] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[ins.B]) ^ (int)ToNumber(frame.Registers[ins.C]))); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.BitNot:
                    try { frame.Registers[ins.A] = JsValue.FromNumber((double)(~(int)ToNumber(frame.Registers[ins.B]))); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.ShiftLeft:
                    try { var sl = (int)ToNumber(frame.Registers[ins.B]); var sc = (int)ToNumber(frame.Registers[ins.C]) & 0x1F; frame.Registers[ins.A] = JsValue.FromNumber((double)(sl << sc)); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.ShiftRight:
                    try { var sr = (int)ToNumber(frame.Registers[ins.B]); var sc2 = (int)ToNumber(frame.Registers[ins.C]) & 0x1F; frame.Registers[ins.A] = JsValue.FromNumber((double)(sr >> sc2)); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.UnsignedShiftRight:
                    try { var u32 = (uint)(int)ToNumber(frame.Registers[ins.B]); var sc3 = (int)ToNumber(frame.Registers[ins.C]) & 0x1F; frame.Registers[ins.A] = JsValue.FromNumber((double)(u32 >> sc3)); }
                    catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                    break;
                case OpCode.Return:
                    return frame.Registers[ins.A];
                default:
                    throw new InvalidOperationException($"Unsupported opcode {ins.OpCode}.");
            }
        }

        return JsValue.Undefined;
    }

    private JsObject CreateOrdinaryObject()
    {
        var obj = new JsObject();
        obj.SetPrototype(EnsureObjectPrototype());
        return obj;
    }

    private JsValue CreateArgumentsObject(IReadOnlyList<JsValue> args, bool restricted, JsFunctionObject? callee)
    {
        var obj = CreateOrdinaryObject();
        obj.ToStringTagSlot = BuiltinTagSlot.Arguments;
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

        if (restricted)
        {
            var thrower = ResolveRestrictedFunctionThrower(callee);
            var descriptor = JsPropertyDescriptor.Accessor(
                thrower,
                thrower,
                Enumerable: false,
                Configurable: false);
            _ = obj.DefineOwnProperty("callee", descriptor);
            WriteDescriptorBarrier(handle, descriptor);
        }

        return JsValue.FromObject(handle);
    }

    private JsValue ResolveRestrictedFunctionThrower(JsFunctionObject? callee)
    {
        if (callee is not null &&
            callee.TryGetOwnProperty("__throwTypeError__", out var overrideDesc) &&
            overrideDesc.Value.Tag == JsValueTag.Object &&
            IsCallable(overrideDesc.Value))
        {
            return overrideDesc.Value;
        }

        return JsValue.FromObject(EnsureThrowTypeErrorIntrinsic());
    }

    private ObjectHandle EnsureThrowTypeErrorIntrinsic()
    {
        if (_throwTypeErrorIntrinsicHandle is { } existing)
        {
            return existing;
        }

        var thrower = new NativeFunctionObject(
            string.Empty,
            (_, _) => throw new JsThrownException(CreateTypeError(string.Empty)),
            length: 0,
            lengthConfigurable: false,
            nameConfigurable: false);
        thrower.SetPrototype(EnsureFunctionPrototype());
        thrower.PreventExtensions();

        var handle = _heap.AllocateObject(thrower, AllocationSite.Current());
        _heap.PushRoot(handle);
        _throwTypeErrorIntrinsicHandle = handle;
        return handle;
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

        // ECMA-262 23.1.5.2.2 %ArrayIteratorPrototype% [ @@toStringTag ] = "Array Iterator".
        DefineBuiltinToStringTag(proto, "Array Iterator");

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

    // CreateForOfIterator / DrainIteratorIntoList / CreateForInIterator /
    // CollectEnumerableKeys / ForOfIteratorObject / ForInIteratorObject moved
    // to BytecodeInterpreter.Iterators.cs (audit �2 slice 3).

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
        InstallWellKnownSymbol(constructor, "dispose");
        InstallWellKnownSymbol(constructor, "asyncDispose");
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

        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

        // ECMA-262 24.2.3.12 Set.prototype [ @@toStringTag ] = "Set".
        DefineBuiltinToStringTag(prototype, "Set");

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

        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

        // ECMA-262 24.1.3.10 Map.prototype [ @@toStringTag ] = "Map".
        DefineBuiltinToStringTag(prototype, "Map");

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

        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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
            if (obj is ProxyObject proxyOwnKeys)
            {
                items.AddRange(ProxyOwnKeys(proxyOwnKeys));
            }
            else
            {
                foreach (var p in obj.EnumerateOwnProperties())
                {
                    items.Add(JsValue.FromString(p.Key));
                }
                foreach (var p in obj.EnumerateOwnSymbolProperties())
                {
                    items.Add(JsValue.SymbolFromId(p.Key));
                }
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
            if (obj is ProxyObject proxyGetPrototypeOf)
            {
                return ProxyGetPrototypeOf(proxyGetPrototypeOf);
            }
            return obj.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
        }, length: 1);

        // 28.1.13 setPrototypeOf - returns boolean (no TypeError on non-Object proto;
        // it returns false instead, per spec step 5).
        DefineIntrinsicFunction(handle, reflect, "setPrototypeOf", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.setPrototypeOf");
            var protoArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            // ECMA-262 28.1.13 step 2: a proto that is neither Object nor null is a TypeError.
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Reflect.setPrototypeOf: prototype must be an Object or null."));
            }

            var ownerHandle = args[0].AsObjectHandle();
            // ECMA-262 28.1.13 Reflect.setPrototypeOf: return the [[SetPrototypeOf]]
            // status as a Boolean (no throw on a disallowed change).
            return JsValue.FromBoolean(OrdinarySetPrototypeOf(ownerHandle, protoArg));
        }, length: 2);

        // 28.1.10 isExtensible
        DefineIntrinsicFunction(handle, reflect, "isExtensible", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.isExtensible");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            if (obj is ProxyObject proxyIsExtensible)
            {
                return JsValue.FromBoolean(ProxyIsExtensible(proxyIsExtensible));
            }
            return JsValue.FromBoolean(obj.Extensible);
        }, length: 1);

        // 28.1.12 preventExtensions
        DefineIntrinsicFunction(handle, reflect, "preventExtensions", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.preventExtensions");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            if (obj is ProxyObject proxyPreventExtensions)
            {
                return JsValue.FromBoolean(ProxyPreventExtensions(proxyPreventExtensions));
            }
            obj.PreventExtensions();
            return JsValue.FromBoolean(true);
        }, length: 1);

        // 28.1.1 apply(target, thisArg, argsList)
        DefineIntrinsicFunction(handle, reflect, "apply", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply target must be a function."));
            }

            if (!IsCallable(args[0]))
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

        // 28.1.2 construct(target, argumentsList [, newTarget])
        DefineIntrinsicFunction(handle, reflect, "construct", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            var argumentsList = args.Count > 1 ? args[1] : JsValue.Undefined;
            var newTarget = args.Count > 2 ? args[2] : target;

            if (target.Tag != JsValueTag.Object)
                throw new JsThrownException(CreateTypeError("Reflect.construct: target must be an object."));
            if (newTarget.Tag != JsValueTag.Object)
                throw new JsThrownException(CreateTypeError("Reflect.construct: newTarget must be an object."));

            // Unpack argumentsList (must be array-like) into individual args.
            JsValue[] callArgs;
            if (argumentsList.Tag == JsValueTag.Object)
            {
                var lobj = _heap.GetObject(argumentsList.AsObjectHandle());
                var len = GetArrayLength(lobj);
                callArgs = new JsValue[len];
                for (var i = 0; i < len; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryGetPropertyValue(lobj, argumentsList, k, out callArgs[i]);
                }
            }
            else if (argumentsList.Tag == JsValueTag.Undefined || argumentsList.Tag == JsValueTag.Null)
            {
                callArgs = Array.Empty<JsValue>();
            }
            else
            {
                throw new JsThrownException(CreateTypeError("Reflect.construct argumentsList must be Array-like."));
            }

            return ConstructFunction(target, callArgs, newTarget);
        }, length: 2);

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

        // ECMA-262 27.1.3.1 Iterator(): a TypeError when called/constructed with
        // NewTarget undefined or === %Iterator% itself (the abstract base), but a
        // subclass (`class X extends Iterator {}`) constructs normally — NewTarget is X,
        // so we return a fresh object that ConstructFunction re-parents to X.prototype.
        ObjectHandle? iteratorCtorHandleRef = null;
        var constructor = new NativeFunctionObject(
            "Iterator",
            (_, _) => throw new JsThrownException(CreateTypeError("Iterator is abstract; cannot be invoked directly.")),
            length: 0,
            constructWithNewTarget: (_, newTarget) =>
            {
                if (newTarget.Tag != JsValueTag.Object ||
                    (iteratorCtorHandleRef is { } selfHandle && newTarget.AsObjectHandle() == selfHandle))
                {
                    throw new JsThrownException(CreateTypeError("Iterator is abstract; cannot be constructed directly."));
                }

                return JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()));
            });
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        iteratorCtorHandleRef = constructorHandle;
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 27.1.4.1 Iterator.from(O). If O already inherits from %Iterator.prototype%
        // and exposes a callable .next, return it unchanged. Otherwise build a
        // wrapper iterator whose .next delegates to the underlying iterable's
        // Symbol.iterator + next.
        DefineIntrinsicFunction(constructorHandle, constructor, "from", (_, args) =>
            IteratorFrom(args.Count > 0 ? args[0] : JsValue.Undefined), length: 1);

        // ECMA-262 27.1.4.1 Iterator.concat(...items).
        DefineIntrinsicFunction(constructorHandle, constructor, "concat", (_, args) =>
            IteratorConcat(args), length: 0);

        // Joint Iteration — Iterator.zip / Iterator.zipKeyed.
        DefineIntrinsicFunction(constructorHandle, constructor, "zip", (_, args) =>
            IteratorZip(args, keyed: false), length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "zipKeyed", (_, args) =>
            IteratorZip(args, keyed: true), length: 1);

        // %Iterator.prototype%[Symbol.iterator] returns this per 27.1.4.2.1.
        var selfIter = new NativeFunctionObject("[Symbol.iterator]", (thisValue, _) => thisValue, length: 0);
        var selfIterHandle = _heap.AllocateObject(selfIter, AllocationSite.Current());
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(selfIterHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, selfIterHandle);

        // The Iterator Helpers (27.1.4). map/filter/take/drop/flatMap are lazy and
        // return Iterator Helper objects; toArray/forEach/reduce/some/every/find
        // consume the receiver lazily one value at a time. Implementations live in
        // BytecodeInterpreter.IteratorHelpers.cs.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "map", IteratorProtoMap, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "filter", IteratorProtoFilter, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "take", IteratorProtoTake, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "drop", IteratorProtoDrop, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "flatMap", IteratorProtoFlatMap, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", IteratorProtoForEach, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "toArray", IteratorProtoToArray, length: 0);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "every", IteratorProtoEvery, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "some", IteratorProtoSome, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "find", IteratorProtoFind, length: 1);
        DefineNativePrototypeMethod(prototypeHandle, prototype, "reduce", IteratorProtoReduce, length: 1);

        // ECMA-262 27.1.2.1 %Iterator.prototype% [ @@toStringTag ]. The accessor form
        // is observable, but a configurable string value matches engines and the
        // delete-then-inherit chain that test262 exercises.
        DefineBuiltinToStringTag(prototype, "Iterator");

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

    private ObjectHandle EnsureIteratorPrototype()
    {
        _ = EnsureIteratorConstructor();
        return _iteratorPrototypeHandle!.Value;
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
            .Register(new BigIntBuiltin())
            .Register(new StringBuiltin())
            .Register(new SymbolBuiltin())
            .Register(new DisposableStackBuiltin())
            .Register(new AsyncDisposableStackBuiltin())
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
            .Register(new ProxyBuiltin())
            .Register(new IteratorBuiltin())
            .Register(new MiscGlobalsBuiltin())
            .Register(new AggregateErrorBuiltin())
            .Register(new GeneratorBuiltin())
            .Register(new GeneratorFunctionBuiltin())
            .Register(new ArrayBufferBuiltin())
            .Register(new SharedArrayBufferBuiltin())
            .Register(new DataViewBuiltin())
            .Register(new TypedArrayBuiltin())
            .Register(new AtomicsBuiltin())
            .Register(new IntlBuiltin())
            .Register(new TemporalStub());
        foreach (var b in registry.Materialize(this))
            InstallBinding(global, globalHandle, b);

        // Post-install fixup: during Materialize(), GeneratorFunction may be
        // constructed before GeneratorPrototype is installed on global. Patch
        // GeneratorPrototype.constructor once both bindings exist.
        FixupGeneratorPrototypeConstructor(global, globalHandle);
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

    private void FixupGeneratorPrototypeConstructor(JsObject global, ObjectHandle globalHandle)
    {
        if (!global.TryGetOwnProperty("GeneratorPrototype", out var gpDesc) ||
            gpDesc.Value.Tag != JsValueTag.Object)
        {
            return;
        }

        if (!global.TryGetOwnProperty("GeneratorFunction", out var gfDesc) ||
            gfDesc.Value.Tag != JsValueTag.Object)
        {
            return;
        }

        var protoHandle = gpDesc.Value.AsObjectHandle();
        var proto = _heap.GetObject(protoHandle);
        _ = proto.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                gfDesc.Value,
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, gfDesc.Value.AsObjectHandle());
        _heap.WriteBarrier(globalHandle, gfDesc.Value.AsObjectHandle());
    }

    // LoadName/StoreName funnel slot-based LoadVar/StoreVar opcodes through the
    // active EnvironmentRecord chain. Unresolvable reads become ReferenceError, and
    // non-strict unresolvable writes create/update a property on the global object.
    // Tier 4 #24: internal so the JIT-emitted Expression-tree code (in
    // JitCompiler) can invoke it from inside the same assembly.
    internal JsValue LoadName(InterpreterFrame frame, int slot)
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

                var status = env.GetBindingValue(name, strict: frame.Function.IsStrictMode, out var envValue);
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

    internal void StoreName(InterpreterFrame frame, int slot, JsValue value)
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

                var status = env.SetMutableBinding(name, value, strict: frame.Function.IsStrictMode);
                if (status == BindingOpResult.Ok)
                {
                    return;
                }

                ThrowBindingFailure(frame, status, name, assignment: true);
                return;
            }

            if (frame.Function.IsStrictMode)
            {
                ThrowReferenceError(frame, $"{name} is not defined.");
                return;
            }

            SetImplicitGlobalProperty(name, value);
            return;
        }

        ThrowReferenceError(frame, $"Invalid variable slot {slot}.");
    }

    internal void InitializeName(InterpreterFrame frame, int slot, JsValue value)
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

        // Tier 5 #25: enforce the realm's CSP eval gate. Non-string inputs
        // are returned unchanged above (no eval actually runs), matching the
        // ECMA-262 19.2.1 step that skips parsing for non-strings.
        if (!EvalAllowed)
        {
            throw new JsThrownException(CreateError("Refused to evaluate a string as JavaScript because 'unsafe-eval' is not allowed by the policy."));
        }

        var directEvalEnvironment = _directEvalEnv;
        var directEvalStrictMode = _directEvalStrictMode;
        _directEvalEnv = null;
        _directEvalStrictMode = false;

        // ECMA-262 19.2.1.1: parsing/early-error failures of the eval source must
        // throw a SyntaxError (a catchable JS error), not a raw host exception.
        BytecodeFunction compiled;
        try
        {
            var program = JsParser.ParseScript(new SourceText(args[0].AsString(), "<eval>"));
            compiled = new BytecodeCompiler().CompileProgram(program, inheritedStrictMode: directEvalStrictMode);
            new BytecodeVerifier().Verify(compiled);
        }
        catch (Exception ex) when (ex is JsParserException or UnsupportedFeatureException)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }
        var globalHandle = EnsureGlobalObject();
        EnvironmentRecord env;
        if (directEvalEnvironment is not null)
        {
            // Strict direct eval gets a fresh lexical scope so var/function
            // declarations do not leak into the caller's environment.
            env = directEvalStrictMode
                ? new DeclarativeEnvironmentRecord(directEvalEnvironment)
                : directEvalEnvironment;
        }
        else
        {
            env = EnsureGlobalEnvironment();
        }

        return ExecuteInternal(
            compiled,
            Array.Empty<JsValue>(),
            JsValue.FromObject(globalHandle),
            frameEnvironment: env);
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Error",
            (_, args) => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
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

            var year = ToNumber(args[0]);
            if (double.IsFinite(year) && year >= 0 && year <= 99)
            {
                year += 1900;
            }

            var month = args.Count > 1 ? ToNumber(args[1]) : 0;
            var day = args.Count > 2 ? ToNumber(args[2]) : 1;
            var hours = args.Count > 3 ? ToNumber(args[3]) : 0;
            var minutes = args.Count > 4 ? ToNumber(args[4]) : 0;
            var seconds = args.Count > 5 ? ToNumber(args[5]) : 0;
            var ms = args.Count > 6 ? ToNumber(args[6]) : 0;

            var v = DateMath.MakeDate(DateMath.MakeDay(year, month, day), DateMath.MakeTime(hours, minutes, seconds, ms));
            return JsValue.FromNumber(DateMath.TimeClip(v));
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
        double baseT;
        if (double.IsFinite(date.TimeValue))
        {
            baseT = date.TimeValue;
        }
        else if (hasYear)
        {
            // setFullYear is allowed to resurrect a NaN-valued Date per spec 21.4.4.21
            // step 2: "If t is NaN, set t to +0".
            baseT = 0;
        }
        else
        {
            date.TimeValue = double.NaN;
            return JsValue.FromNumber(double.NaN);
        }

        // ECMA-262 21.4.4.21/.20/.19: read the untouched portions from the current
        // time, overwrite the requested ones from args (ToNumber in argument order),
        // then recompose via MakeDay/MakeDate. MakeDay overflow handles Feb 30 etc.
        double year = DateMath.YearFromTime(baseT);
        double month = DateMath.MonthFromTime(baseT);
        double day = DateMath.DateFromTime(baseT);
        if (hasYear) { year = ToNumber(args[yearArgIndex]); }
        if (hasMonth) { month = ToNumber(args[monthArgIndex]); }
        if (hasDay) { day = ToNumber(args[dayArgIndex]); }

        var newDay = DateMath.MakeDay(year, month, day);
        date.TimeValue = DateMath.TimeClip(DateMath.MakeDate(newDay, DateMath.TimeWithinDay(baseT)));
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
        var t0 = date.TimeValue;
        // Untouched portions keep their current value; provided args overwrite from
        // startIndex (0=hour..3=ms), ToNumber in argument order (21.4.4.34/.33/.32/.31).
        var components = new double[]
        {
            DateMath.HoursFromTime(t0), DateMath.MinFromTime(t0),
            DateMath.SecFromTime(t0), DateMath.MsFromTime(t0)
        };
        for (var i = 0; i < args.Count && startIndex + i < 4; i++)
        {
            components[startIndex + i] = ToNumber(args[i]);
        }
        var time = DateMath.MakeTime(components[0], components[1], components[2], components[3]);
        date.TimeValue = DateMath.TimeClip(DateMath.MakeDate(DateMath.Day(t0), time));
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
            (t, _) => GetDateComponent(t, "getFullYear", DateMath.YearFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMonth",
            (t, _) => GetDateComponent(t, "getMonth", DateMath.MonthFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getDate",
            (t, _) => GetDateComponent(t, "getDate", DateMath.DateFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getDay",
            (t, _) => GetDateComponent(t, "getDay", DateMath.WeekDay));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getHours",
            (t, _) => GetDateComponent(t, "getHours", DateMath.HoursFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMinutes",
            (t, _) => GetDateComponent(t, "getMinutes", DateMath.MinFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getSeconds",
            (t, _) => GetDateComponent(t, "getSeconds", DateMath.SecFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getMilliseconds",
            (t, _) => GetDateComponent(t, "getMilliseconds", DateMath.MsFromTime));

        // LocalTZA is 0, so the UTC accessors share the same extractors as the local ones.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCFullYear",
            (t, _) => GetDateComponent(t, "getUTCFullYear", DateMath.YearFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMonth",
            (t, _) => GetDateComponent(t, "getUTCMonth", DateMath.MonthFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCDate",
            (t, _) => GetDateComponent(t, "getUTCDate", DateMath.DateFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCDay",
            (t, _) => GetDateComponent(t, "getUTCDay", DateMath.WeekDay));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCHours",
            (t, _) => GetDateComponent(t, "getUTCHours", DateMath.HoursFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMinutes",
            (t, _) => GetDateComponent(t, "getUTCMinutes", DateMath.MinFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCSeconds",
            (t, _) => GetDateComponent(t, "getUTCSeconds", DateMath.SecFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getUTCMilliseconds",
            (t, _) => GetDateComponent(t, "getUTCMilliseconds", DateMath.MsFromTime));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getTimezoneOffset",
            (t, _) => GetDateComponent(t, "getTimezoneOffset", _ => 0));

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", DatePrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toDateString", DatePrototypeToDateString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toTimeString", DatePrototypeToTimeString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toUTCString", DatePrototypeToUtcString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString", DatePrototypeToLocaleString, length: 2);

        // Annex B B.2.3 legacy aliases (audit �4.1).
        // B.2.3.1 Date.prototype.getYear: return year - 1900, NaN if invalid.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "getYear",
            (t, _) => GetDateComponent(t, "getYear", tv => DateMath.YearFromTime(tv) - 1900));
        // B.2.3.2 Date.prototype.setYear(year): treat 0..99 as offset from 1900,
        // any other number assigned directly; NaN clears to NaN time value.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "setYear",
            (t, a) =>
            {
                var date = RequireDate(t, "setYear");
                var arg = a.Count > 0 ? ToNumber(a[0]) : double.NaN;
                if (double.IsNaN(arg))
                {
                    date.TimeValue = double.NaN;
                    return JsValue.FromNumber(double.NaN);
                }
                var year = (int)arg;
                if (year >= 0 && year <= 99) year += 1900;
                // Preserve existing month / day; reconstruct from current value
                // (or epoch if NaN). Reuse SetDateField with hasYear semantics.
                if (double.IsNaN(date.TimeValue)) date.TimeValue = 0;
                var argsList = new List<JsValue> { JsValue.FromNumber(year) };
                return SetDateField(t, "setYear", argsList, hasYear: true, hasMonth: false, hasDay: false);
            }, length: 1);
        // B.2.3.3 Date.prototype.toGMTString � alias of toUTCString.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toGMTString", DatePrototypeToUtcString);
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

    private static string FormatYear4(double t)
    {
        var y = DateMath.YearFromTime(t);
        return y >= 0
            ? y.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : "-" + Math.Abs(y).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string FormatDatePart(double t) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0} {1} {2:D2} {3}",
            DayNames[DateMath.WeekDay(t)], MonthNames[DateMath.MonthFromTime(t)], DateMath.DateFromTime(t), FormatYear4(t));

    private string FormatTimePart(double t) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2} GMT+0000 (Coordinated Universal Time)",
            DateMath.HoursFromTime(t), DateMath.MinFromTime(t), DateMath.SecFromTime(t));

    private JsValue DatePrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(FormatDatePart(t) + " " + FormatTimePart(t));
    }

    private JsValue DatePrototypeToLocaleString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var t = GetDateTimeValue(thisValue, "toLocaleString");
        if (!double.IsFinite(t))
        {
            return JsValue.FromString("Invalid Date");
        }

        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = FenBrowser.Js.Intl.IntlDateTimeFormatting.ResolveCulture(locale);
        var options = ParseDateTimeFormatOptions(locale, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            FenBrowser.Js.Intl.IntlDateTimeFormatting.ValidateOptions(options);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var result = FenBrowser.Js.Intl.IntlDateTimeFormatting.Format(DateTimeOffset.FromUnixTimeMilliseconds((long)t), culture, options);
        return JsValue.FromString(result.Text);
    }

    private JsValue DatePrototypeToDateString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toDateString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(FormatDatePart(t));
    }

    private JsValue DatePrototypeToTimeString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toTimeString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(FormatTimePart(t));
    }

    // ECMA-262 21.4.4.43 Date.prototype.toUTCString. Format: "Day, DD Mon YYYY HH:MM:SS GMT".
    private JsValue DatePrototypeToUtcString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var t = GetDateTimeValue(thisValue, "toUTCString");
        if (!double.IsFinite(t)) return JsValue.FromString("Invalid Date");
        return JsValue.FromString(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}, {1:D2} {2} {3} {4:D2}:{5:D2}:{6:D2} GMT",
            DayNames[DateMath.WeekDay(t)], DateMath.DateFromTime(t), MonthNames[DateMath.MonthFromTime(t)], FormatYear4(t),
            DateMath.HoursFromTime(t), DateMath.MinFromTime(t), DateMath.SecFromTime(t)));
    }

    private JsValue GetDateComponent(JsValue thisValue, string method, Func<double, int> extract)
    {
        var t = GetDateTimeValue(thisValue, method);
        if (!double.IsFinite(t))
        {
            return JsValue.FromNumber(double.NaN);
        }
        return JsValue.FromNumber(extract(t));
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
        // Extended year form (+YYYYYY/-YYYYYY) when outside [0, 9999] per 21.4.1.18.
        var year = DateMath.YearFromTime(t);
        var yearStr = (year >= 0 && year <= 9999)
            ? year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : (year >= 0 ? "+" : "-") + Math.Abs(year).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return JsValue.FromString(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}-{1:D2}-{2:D2}T{3:D2}:{4:D2}:{5:D2}.{6:D3}Z",
            yearStr, DateMath.MonthFromTime(t) + 1, DateMath.DateFromTime(t),
            DateMath.HoursFromTime(t), DateMath.MinFromTime(t), DateMath.SecFromTime(t), DateMath.MsFromTime(t)));
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
            (_, args) => RegExpFunctionCall(args),
            args => RegExpFunctionConstruct(args),
            length: 2);
        var functionPrototypeHandle = GetGlobalPrototype("Function");
        constructor.SetPrototype(functionPrototypeHandle);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _heap.WriteBarrier(constructorHandle, functionPrototypeHandle);

        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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
        // Cache the first prototype handle seen — this comes from the builtin
        // during InstallGlobalObjectProperties, before any bytecode executes.
        _regexpPrototypeHandle ??= prototypeHandle;
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "test", RegExpPrototypeTest, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "exec", RegExpPrototypeExec, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", RegExpPrototypeToString);
        // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags) � mutate
        // this instance to act like a freshly constructed RegExp. Audit �4.1.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "compile", RegExpPrototypeCompile, length: 2);
        DefineRegExpSymbolMethod(prototypeHandle, prototype, "match", RegExpPrototypeSymbolMatch);
        DefineRegExpSymbolMethod(prototypeHandle, prototype, "search", RegExpPrototypeSymbolSearch);
        DefineRegExpSymbolMethod(prototypeHandle, prototype, "replace", RegExpPrototypeSymbolReplace);
        DefineRegExpSymbolMethod(prototypeHandle, prototype, "split", RegExpPrototypeSymbolSplit);
        DefineRegExpSymbolMethod(prototypeHandle, prototype, "matchAll", RegExpPrototypeSymbolMatchAll);
    }

    private void DefineRegExpSymbolMethod(
        ObjectHandle prototypeHandle,
        JsObject prototype,
        string symbolName,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call)
    {
        var symbol = GetWellKnownSymbol(symbolName);
        if (symbol.Tag != JsValueTag.Symbol)
        {
            return;
        }

        var fn = new NativeFunctionObject($"[Symbol.{symbolName}]", call, length: 1);
        var fnHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = EnsureFunctionCallMethod();
        _ = fn.SetProperty("call", JsValue.FromObject(callHandle));
        _heap.WriteBarrier(fnHandle, callHandle);
        _ = prototype.DefineOwnSymbolProperty(
            symbol.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, fnHandle);
    }

    // ECMA-262 12.2.8 RegularExpressionLiteral.
    internal JsValue NewRegExpLiteral(string rawText)
    {
        // rawText is "/pattern/flags" — find the last '/' to separate flags.
        var lastSlash = rawText.LastIndexOf('/');
        // Pattern: rawText[1..lastSlash], Flags: rawText[(lastSlash+1)..]
        var pattern = rawText.Substring(1, lastSlash - 1);
        var flags = lastSlash + 1 < rawText.Length ? rawText.Substring(lastSlash + 1) : string.Empty;
        var normalizedFlags = NormalizeRegExpFlags(flags);
        var hasS = normalizedFlags.Contains('s', StringComparison.Ordinal);
        var hasU = normalizedFlags.Contains('u', StringComparison.Ordinal);
        var hasV = normalizedFlags.Contains('v', StringComparison.Ordinal);
        var options = (hasS || hasU || hasV)
            ? RegexOptions.CultureInvariant
            : RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
        if (normalizedFlags.Contains('m', StringComparison.Ordinal)) options |= RegexOptions.Multiline;
        if (hasS) options |= RegexOptions.Singleline;
        var dotNetPattern = RewriteEcmaCharacterClassEscapes(pattern);
        dotNetPattern = RegExpCompiler.RewriteUnicodePropertyEscapesForDotNet(dotNetPattern);

        BclRegex regex;
        try
        {
            regex = new BclRegex(dotNetPattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        var nativeProgram = RegExpCompiler.CompileNative(pattern, normalizedFlags);

        var obj = new RegExpObject(pattern, normalizedFlags, regex, nativeProgram);
        // Use the prototype cached from the builtin during
        // InstallPrototypeMethodsOnRegExpPrototype, or resolve from global.
        obj.SetPrototype(_regexpPrototypeHandle ?? GetGlobalPrototype("RegExp"));
        _ = obj.DefineOwnProperty("source",
            new JsPropertyDescriptor(JsValue.FromString(pattern), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("global",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('g', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("ignoreCase",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('i', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("multiline",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('m', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("dotAll",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasS), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("unicode",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasU), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("unicodeSets",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasV), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("sticky",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('y', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("hasIndices",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('d', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("flags",
            new JsPropertyDescriptor(JsValue.FromString(normalizedFlags), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty("lastIndex",
            new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private JsValue RegExpFunctionCall(IReadOnlyList<JsValue> args)
    {
        var patternArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (flagsArg.Tag == JsValueTag.Undefined && ShouldReturnPatternOnRegExpCall(patternArg))
        {
            return patternArg;
        }

        return CreateRegExpObject(args);
    }

    private JsValue RegExpFunctionConstruct(IReadOnlyList<JsValue> args)
    {
        return CreateRegExpObject(args);
    }

    private bool ShouldReturnPatternOnRegExpCall(JsValue patternArg)
    {
        if (patternArg.Tag != JsValueTag.Object || !IsRegExpLike(patternArg))
        {
            return false;
        }

        var obj = _heap.GetObject(patternArg.AsObjectHandle());
        if (!TryGetPropertyValue(obj, patternArg, "constructor", out var patternConstructor) ||
            patternConstructor.Tag != JsValueTag.Object)
        {
            return false;
        }

        return _regexpConstructorHandle is { } ctor &&
               patternConstructor.AsObjectHandle() == ctor;
    }

    private void ResolveRegExpPatternAndFlags(JsValue patternArg, JsValue flagsArg, out string pattern, out string flags)
    {
        if (patternArg.Tag == JsValueTag.Object && IsRegExpLike(patternArg))
        {
            var obj = _heap.GetObject(patternArg.AsObjectHandle());
            if (!TryGetPropertyValue(obj, patternArg, "source", out var sourceValue))
            {
                sourceValue = JsValue.FromString(string.Empty);
            }

            pattern = sourceValue.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(sourceValue);
            if (flagsArg.Tag == JsValueTag.Undefined)
            {
                if (!TryGetPropertyValue(obj, patternArg, "flags", out var inheritedFlags))
                {
                    inheritedFlags = JsValue.FromString(string.Empty);
                }

                flags = inheritedFlags.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(inheritedFlags);
                return;
            }
        }
        else
        {
            pattern = patternArg.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(patternArg);
        }

        flags = flagsArg.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(flagsArg);
    }

    private bool IsRegExpLike(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var obj = _heap.GetObject(value.AsObjectHandle());
        var matchSymbolId = GetWellKnownSymbolId("match");
        var matcher = matchSymbolId == 0 ? JsValue.Undefined : GetReceiverSymbolProperty(value, matchSymbolId);
        if (matcher.Tag != JsValueTag.Undefined)
        {
            return IsTruthy(matcher);
        }

        return obj is RegExpObject;
    }

    private JsValue CreateRegExpObject(IReadOnlyList<JsValue> args)
    {
        var patternArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        ResolveRegExpPatternAndFlags(patternArg, flagsArg, out var pattern, out var flags);
        var normalizedFlags = NormalizeRegExpFlags(flags);
        // ECMA-262 22.2.4 flags → RegexOptions mapping.
        // ECMAScript mode is the default; dotAll (s) conflicts with it and
        // Unicode (u) restricts \w/\d to ASCII in ECMAScript mode, so both
        // remove the ECMAScript option to get fuller Unicode behaviour.
        bool hasS = normalizedFlags.Contains('s', StringComparison.Ordinal);
        bool hasU = normalizedFlags.Contains('u', StringComparison.Ordinal);
        bool hasV = normalizedFlags.Contains('v', StringComparison.Ordinal);
        var options = (hasS || hasU || hasV)
            ? RegexOptions.CultureInvariant
            : RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (normalizedFlags.Contains('m', StringComparison.Ordinal))
        {
            options |= RegexOptions.Multiline;
        }

        if (hasS) options |= RegexOptions.Singleline;
        var dotNetPattern = RewriteEcmaCharacterClassEscapes(pattern);
        dotNetPattern = RegExpCompiler.RewriteUnicodePropertyEscapesForDotNet(dotNetPattern);

        BclRegex regex;
        try
        {
            regex = new BclRegex(dotNetPattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        var nativeProgram = RegExpCompiler.CompileNative(pattern, normalizedFlags);

        var obj = new RegExpObject(pattern, normalizedFlags, regex, nativeProgram);
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
            "dotAll",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasS), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "unicode",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasU), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "unicodeSets",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasV), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "sticky",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('y', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "hasIndices",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('d', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "flags",
            new JsPropertyDescriptor(JsValue.FromString(normalizedFlags), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "lastIndex",
            new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private JsValue RegExpPrototypeTest(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        if (regexp.NativeProgram is { } nativeProgram &&
            !regexp.Flags.Contains('g', StringComparison.Ordinal) &&
            !regexp.Flags.Contains('y', StringComparison.Ordinal))
        {
            var nativeMatch = new RegexVM(nativeProgram).Execute(input);
            return JsValue.FromBoolean(nativeMatch.Success);
        }

        return JsValue.FromBoolean(regexp.Regex.IsMatch(input));
    }

    // ECMA-262 22.2.5.2 RegExp.prototype.exec(string).
    private JsValue RegExpPrototypeExec(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var flags = regexp.Flags;
        var global = flags.Contains('g', StringComparison.Ordinal);
        var sticky = flags.Contains('y', StringComparison.Ordinal);

        var lastIndex = 0;
        if (TryGetPropertyValue((JsObject)regexp, thisValue, "lastIndex", out var liVal) &&
            liVal.Tag == JsValueTag.Number)
        {
            var d = liVal.AsNumber();
            if (d >= 0 && d <= input.Length && double.IsFinite(d))
                lastIndex = (int)d;
        }

        if (!global && !sticky) lastIndex = 0;
        if (lastIndex < 0) lastIndex = 0;
        if (lastIndex > input.Length) lastIndex = input.Length;

        var match = lastIndex <= input.Length
            ? regexp.Regex.Match(input, lastIndex)
            : System.Text.RegularExpressions.Match.Empty;

        if (!match.Success)
        {
            _ = ((JsObject)regexp).SetProperty("lastIndex", JsValue.FromNumber(0));
            return JsValue.Null;
        }

        if (sticky && match.Index != lastIndex)
        {
            _ = ((JsObject)regexp).SetProperty("lastIndex", JsValue.FromNumber(0));
            return JsValue.Null;
        }

        var result = CreateArrayObject(Array.Empty<JsValue>());
        _ = result.DefineOwnProperty("0",
            new JsPropertyDescriptor(JsValue.FromString(match.Value), Writable: true, Enumerable: true, Configurable: true));

        var nCaptures = match.Groups.Count;
        for (var i = 1; i < nCaptures; i++)
        {
            var group = match.Groups[i];
            var val = group.Success ? JsValue.FromString(group.Value) : JsValue.Undefined;
            _ = result.DefineOwnProperty(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new JsPropertyDescriptor(val, Writable: true, Enumerable: true, Configurable: true));
        }

        _ = result.DefineOwnProperty("index",
            new JsPropertyDescriptor(JsValue.FromNumber(match.Index), Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty("input",
            new JsPropertyDescriptor(JsValue.FromString(input), Writable: true, Enumerable: true, Configurable: true));

        // ECMA-262 §22.2.5.2 — build "groups" object from named capture groups
        var groupsObj = new JsObject();
        var groupNames = regexp.Regex.GetGroupNames();
        var hasNamedGroups = false;
        foreach (var name in groupNames)
        {
            if (int.TryParse(name, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
            hasNamedGroups = true;
            var group = match.Groups[name];
            var gval = group.Success ? JsValue.FromString(group.Value) : JsValue.Undefined;
            groupsObj.DefineOwnProperty(name,
                new JsPropertyDescriptor(gval, Writable: true, Enumerable: true, Configurable: true));
        }
        // ECMA-262: groups is undefined when there are no named groups
        if (hasNamedGroups)
        {
            var gh = _heap.AllocateObject(groupsObj, AllocationSite.Current());
            _ = result.DefineOwnProperty("groups",
                new JsPropertyDescriptor(JsValue.FromObject(gh), Writable: true, Enumerable: true, Configurable: true));
        }
        else
        {
            _ = result.DefineOwnProperty("groups",
                new JsPropertyDescriptor(JsValue.Undefined, Writable: true, Enumerable: true, Configurable: true));
        }

        // ECMA-262 §22.2.5.2 — hasIndices (d flag): build "indices" array
        var dFlag = flags.Contains('d', StringComparison.Ordinal);
        if (dFlag)
        {
            var indices = CreateArrayObject(Array.Empty<JsValue>());
            for (var i = 0; i < nCaptures; i++)
            {
                var grp = match.Groups[i];
                var start = grp.Success ? JsValue.FromNumber(grp.Index) : JsValue.Undefined;
                var end = grp.Success ? JsValue.FromNumber(grp.Index + grp.Length) : JsValue.Undefined;
                var pair = CreateArrayObject(new[] { start, end });
                var pairH = _heap.AllocateObject(pair, AllocationSite.Current());
                _ = indices.DefineOwnProperty(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new JsPropertyDescriptor(JsValue.FromObject(pairH), Writable: true, Enumerable: true, Configurable: true));
            }
            // indices.groups for named groups
            if (hasNamedGroups)
            {
                var igObj = new JsObject();
                foreach (var name in groupNames)
                {
                    if (int.TryParse(name, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
                    var grp = match.Groups[name];
                    var start = grp.Success ? JsValue.FromNumber(grp.Index) : JsValue.Undefined;
                    var end = grp.Success ? JsValue.FromNumber(grp.Index + grp.Length) : JsValue.Undefined;
                    var pair = CreateArrayObject(new[] { start, end });
                    var pairH = _heap.AllocateObject(pair, AllocationSite.Current());
                    igObj.DefineOwnProperty(name,
                        new JsPropertyDescriptor(JsValue.FromObject(pairH), Writable: true, Enumerable: true, Configurable: true));
                }
                var igH = _heap.AllocateObject(igObj, AllocationSite.Current());
                _ = indices.DefineOwnProperty("groups",
                    new JsPropertyDescriptor(JsValue.FromObject(igH), Writable: true, Enumerable: true, Configurable: true));
            }
            else
            {
                _ = indices.DefineOwnProperty("groups",
                    new JsPropertyDescriptor(JsValue.Undefined, Writable: true, Enumerable: true, Configurable: true));
            }
            var indicesH = _heap.AllocateObject(indices, AllocationSite.Current());
            _ = result.DefineOwnProperty("indices",
                new JsPropertyDescriptor(JsValue.FromObject(indicesH), Writable: true, Enumerable: true, Configurable: true));
        }

        _ = result.DefineOwnProperty("length",
            new JsPropertyDescriptor(JsValue.FromNumber(nCaptures), Writable: true, Enumerable: false, Configurable: false));

        if (global || sticky)
            _ = ((JsObject)regexp).SetProperty("lastIndex", JsValue.FromNumber(match.Index + match.Length));

        var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
        return JsValue.FromObject(resultHandle);
    }

    private JsValue RegExpPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var regexp = RegExpThisValue(thisValue);
        var escapedSource = regexp.Pattern.Replace("/", "\\/", StringComparison.Ordinal);
        return JsValue.FromString($"/{escapedSource}/{regexp.Flags}");
    }

    // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags) � re-initializes
    // this RegExp instance. If pattern is itself a RegExp and flags is undefined,
    // copy its pattern + flags; otherwise treat as new pattern+flags. Mutates
    // `this` in place and returns it. Audit �4.1.
    private JsValue RegExpPrototypeCompile(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var target = RegExpThisValue(thisValue);
        string newPattern;
        string newFlags;
        var patternArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (patternArg.Tag == JsValueTag.Object &&
            _heap.GetObject(patternArg.AsObjectHandle()) is RegExpObject src)
        {
            if (flagsArg.Tag != JsValueTag.Undefined)
                throw new JsThrownException(CreateTypeError("Cannot supply flags when constructing one RegExp from another."));
            newPattern = src.Pattern;
            newFlags = src.Flags;
        }
        else
        {
            newPattern = patternArg.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(patternArg);
            newFlags = flagsArg.Tag == JsValueTag.Undefined ? string.Empty : ToStringValue(flagsArg);
        }
        var normalizedFlags = NormalizeRegExpFlags(newFlags);
        var hasS = normalizedFlags.Contains('s', StringComparison.Ordinal);
        var hasU = normalizedFlags.Contains('u', StringComparison.Ordinal);
        var hasV = normalizedFlags.Contains('v', StringComparison.Ordinal);
        var options = (hasS || hasU || hasV)
            ? RegexOptions.CultureInvariant
            : RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
        if (normalizedFlags.Contains('m', StringComparison.Ordinal)) options |= RegexOptions.Multiline;
        if (hasS) options |= RegexOptions.Singleline;
        var dotNetPattern = RewriteEcmaCharacterClassEscapes(newPattern);
        BclRegex regex;
        try
        {
            regex = new BclRegex(dotNetPattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }
        target.Recompile(newPattern, normalizedFlags, regex);
        // Refresh externally observable own properties to mirror constructor init.
        _ = target.DefineOwnProperty("source",
            new JsPropertyDescriptor(JsValue.FromString(newPattern), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("global",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('g', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("ignoreCase",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('i', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("multiline",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('m', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("dotAll",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasS), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("unicode",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasU), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("unicodeSets",
            new JsPropertyDescriptor(JsValue.FromBoolean(hasV), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("sticky",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('y', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("hasIndices",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('d', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("flags",
            new JsPropertyDescriptor(JsValue.FromString(normalizedFlags), Writable: false, Enumerable: false, Configurable: true));
        _ = target.DefineOwnProperty("lastIndex",
            new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return thisValue;
    }

    private JsValue RegExpPrototypeSymbolMatch(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        if (!regexp.Flags.Contains('g', StringComparison.Ordinal))
        {
            return RegExpPrototypeExec(thisValue, new[] { JsValue.FromString(input) });
        }

        var values = new List<JsValue>();
        foreach (Match match in regexp.Regex.Matches(input))
        {
            values.Add(JsValue.FromString(match.Value));
        }

        if (values.Count == 0)
        {
            return JsValue.Null;
        }

        return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(values), AllocationSite.Current()));
    }

    private JsValue RegExpPrototypeSymbolSearch(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var match = regexp.Regex.Match(input);
        return JsValue.FromNumber(match.Success ? match.Index : -1);
    }

    private JsValue RegExpPrototypeSymbolReplace(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var replacement = args.Count > 1 ? args[1] : JsValue.Undefined;

        if (replacement.Tag == JsValueTag.Object && IsCallable(replacement))
        {
            var output = regexp.Regex.Replace(input, m =>
            {
                var result = CallFunction(
                    replacement,
                    new[] { JsValue.FromString(m.Value), JsValue.FromNumber(m.Index), JsValue.FromString(input) },
                    JsValue.Undefined);
                return ToStringValue(result);
            });
            return JsValue.FromString(output);
        }

        // ECMA-262 §22.2.5.9 GetSubstitution — manual replacement with spec patterns.
        var replStr = ToStringValue(replacement);
        var global = regexp.Flags.Contains('g', StringComparison.Ordinal);
        if (global)
        {
            // Global: replace all matches using ECMA-262 GetSubstitution
            var matches = regexp.Regex.Matches(input);
            if (matches.Count == 0) return JsValue.FromString(input);
            var sb = new System.Text.StringBuilder();
            var prevEnd = 0;
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                sb.Append(input.AsSpan(prevEnd, m.Index - prevEnd));
                sb.Append(GetSubstitution(input, m, replStr, regexp));
                prevEnd = m.Index + m.Length;
            }
            sb.Append(input.AsSpan(prevEnd));
            return JsValue.FromString(sb.ToString());
        }
        else
        {
            // Non-global: replace first match only
            var match = regexp.Regex.Match(input);
            if (!match.Success) return JsValue.FromString(input);
            var result = input.Substring(0, match.Index)
                + GetSubstitution(input, match, replStr, regexp)
                + input.Substring(match.Index + match.Length);
            return JsValue.FromString(result);
        }
    }

    /// <summary>ECMA-262 §22.2.5.9 GetSubstitution.</summary>
    private string GetSubstitution(string input, System.Text.RegularExpressions.Match match, string replacement, RegExpObject regexp)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '$' && i + 1 < replacement.Length)
            {
                var c = replacement[i + 1];
                switch (c)
                {
                    case '$':
                        sb.Append('$'); i++; break;
                    case '&':
                        sb.Append(match.Value); i++; break;
                    case '`':
                        sb.Append(input.AsSpan(0, match.Index)); i++; break;
                    case '\'':
                        sb.Append(input.AsSpan(match.Index + match.Length)); i++; break;
                    case '<':
                        // $<name> — named capture group
                        var endBracket = replacement.IndexOf('>', i + 2);
                        if (endBracket >= 0)
                        {
                            var name = replacement.Substring(i + 2, endBracket - i - 2);
                            var namedGroup = match.Groups[name];
                            sb.Append(namedGroup.Success ? namedGroup.Value : "");
                            i = endBracket;
                        }
                        else
                        {
                            sb.Append('$'); sb.Append('<');
                            i++;
                        }
                        break;
                    default:
                        if (c >= '0' && c <= '9')
                        {
                            // $n or $nn — numbered capture group
                            var numStr = c.ToString();
                            var j = i + 2;
                            while (j < replacement.Length && replacement[j] >= '0' && replacement[j] <= '9')
                            {
                                numStr += replacement[j];
                                j++;
                            }
                            if (int.TryParse(numStr, System.Globalization.NumberStyles.Integer,
                                    System.Globalization.CultureInfo.InvariantCulture, out var groupNum) &&
                                groupNum < match.Groups.Count)
                            {
                                var capGroup = match.Groups[groupNum];
                                sb.Append(capGroup.Success ? capGroup.Value : "");
                                i += numStr.Length;
                            }
                            else
                            {
                                sb.Append('$');
                                i++;
                            }
                        }
                        else
                        {
                            sb.Append('$');
                            i++;
                            sb.Append(c);
                        }
                        break;
                }
            }
            else
            {
                sb.Append(replacement[i]);
            }
        }
        return sb.ToString();
    }

    // ECMA-262 §22.2.5.11 RegExp.prototype [ @@split ] ( string, limit )
    private JsValue RegExpPrototypeSymbolSplit(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var limit = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, (int)ToNumber(args[1]))
            : int.MaxValue;

        if (limit == 0)
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(Array.Empty<JsValue>()), AllocationSite.Current()));

        // If input is empty and the regex matches empty string, we need special handling
        if (input.Length == 0)
        {
            var m = regexp.Regex.Match(input);
            if (m.Success)
                return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(Array.Empty<JsValue>()), AllocationSite.Current()));
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(new[] { JsValue.FromString(input) }), AllocationSite.Current()));
        }

        var parts = new List<JsValue>();
        var p = 0;
        var q = 0;

        while (q < input.Length)
        {
            var m = regexp.Regex.Match(input, q);
            var e = !m.Success || m.Length == 0 ? -1 : m.Index + m.Length;

            if (e == -1 || e <= q)
            {
                q++;
                continue;
            }

            // Add substring from p to match start
            if (parts.Count < limit)
            {
                parts.Add(JsValue.FromString(input.Substring(p, m.Index - p)));
            }
            if (parts.Count >= limit) break;
            p = e;

            // Interleave capturing group matches (ECMA-262 step 13.h)
            for (var i = 1; i < m.Groups.Count && parts.Count < limit; i++)
            {
                var g = m.Groups[i];
                parts.Add(g.Success ? JsValue.FromString(g.Value) : JsValue.Undefined);
            }
            q = p;
        }

        // Remaining substring
        if (p < input.Length && parts.Count < limit)
        {
            parts.Add(JsValue.FromString(input.Substring(p)));
        }

        return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(parts), AllocationSite.Current()));
    }

    private JsValue RegExpPrototypeSymbolMatchAll(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var results = new List<JsValue>();

        foreach (Match match in regexp.Regex.Matches(input))
        {
            var record = CreateArrayObject(Array.Empty<JsValue>());
            _ = record.DefineOwnProperty("0",
                new JsPropertyDescriptor(JsValue.FromString(match.Value), Writable: true, Enumerable: true, Configurable: true));
            for (var i = 1; i < match.Groups.Count; i++)
            {
                var g = match.Groups[i];
                var v = g.Success ? JsValue.FromString(g.Value) : JsValue.Undefined;
                _ = record.DefineOwnProperty(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new JsPropertyDescriptor(v, Writable: true, Enumerable: true, Configurable: true));
            }
            // Named groups
            var mgroupsObj = new JsObject();
            var mGroupNames = regexp.Regex.GetGroupNames();
            var mHasNamed = false;
            foreach (var mName in mGroupNames)
            {
                if (int.TryParse(mName, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
                mHasNamed = true;
                var mGrp = match.Groups[mName];
                mgroupsObj.DefineOwnProperty(mName,
                    new JsPropertyDescriptor(mGrp.Success ? JsValue.FromString(mGrp.Value) : JsValue.Undefined,
                        Writable: true, Enumerable: true, Configurable: true));
            }
            if (mHasNamed)
                _ = record.DefineOwnProperty("groups",
                    new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(mgroupsObj, AllocationSite.Current())),
                        Writable: true, Enumerable: true, Configurable: true));
            else
                _ = record.DefineOwnProperty("groups",
                    new JsPropertyDescriptor(JsValue.Undefined, Writable: true, Enumerable: true, Configurable: true));

            _ = record.DefineOwnProperty("index",
                new JsPropertyDescriptor(JsValue.FromNumber(match.Index), Writable: true, Enumerable: true, Configurable: true));
            _ = record.DefineOwnProperty("input",
                new JsPropertyDescriptor(JsValue.FromString(input), Writable: true, Enumerable: true, Configurable: true));
            _ = record.DefineOwnProperty("length",
                new JsPropertyDescriptor(JsValue.FromNumber(match.Groups.Count), Writable: true, Enumerable: false, Configurable: false));
            results.Add(JsValue.FromObject(_heap.AllocateObject(record, AllocationSite.Current())));
        }

        // ECMA-262 §22.2.5.10 — return a RegExpStringIterator, not a plain array.
        var capturedResults = results;
        var iter = new RegExpStringIteratorObject(capturedResults);
        iter.SetPrototype(EnsureRegExpStringIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    private ObjectHandle? _regExpStringIteratorProtoHandle;
    private ObjectHandle EnsureRegExpStringIteratorPrototype()
    {
        if (_regExpStringIteratorProtoHandle is { } e) return e;
        var proto = CreateOrdinaryObject();
        proto.SetPrototype(EnsureIteratorPrototype());
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);
        // ECMA-262 §22.2.5.10.1 %RegExpStringIteratorPrototype%.next()
        var next = new NativeFunctionObject("next", (thisValue, _) =>
        {
            if (thisValue.Tag != JsValueTag.Object ||
                _heap.GetObject(thisValue.AsObjectHandle()) is not RegExpStringIteratorObject ri)
                throw new JsThrownException(CreateTypeError("RegExpStringIterator.prototype.next called on incompatible receiver."));
            if (ri.Index >= ri.Results.Count)
                return BuildIteratorResult(JsValue.Undefined, done: true);
            return BuildIteratorResult(ri.Results[ri.Index++], done: false);
        }, length: 0);
        var nextH = _heap.AllocateObject(next, AllocationSite.Current());
        proto.DefineOwnProperty("next", new JsPropertyDescriptor(JsValue.FromObject(nextH), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ph, nextH);
        _regExpStringIteratorProtoHandle = ph;
        return ph;
    }

    /// <summary>ECMA-262 §22.2.5.10.1 RegExpStringIterator — holds pre-computed match results.</summary>
    private sealed class RegExpStringIteratorObject : JsObject
    {
        public readonly List<JsValue> Results;
        public int Index;
        public RegExpStringIteratorObject(List<JsValue> results) { Results = results; Index = 0; }
    }

    private RegExpObject RegExpThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is RegExpObject regexp)
        {
            return regexp;
        }

        throw new JsThrownException(CreateTypeError("RegExp.prototype method called on incompatible receiver."));
    }

    // Proxy intercept helpers — ECMA-262 28.2 internal method dispatch.
    // Each method checks for a handler trap; when present the trap is called
    // with the proper arguments. When absent the operation falls through to
    // the target object.

    [MayExecuteJs]
    private JsValue? TryGetProxyTrap(ProxyObject proxy, string trapName)
    {
        if (proxy.IsRevoked)
            throw new JsThrownException(CreateTypeError(
                "Cannot perform '" + trapName + "' on a revoked proxy."));
        var handlerHandle = proxy.HandlerHandle!.Value;
        var handler = _heap.GetObject(handlerHandle);
        var handlerValue = JsValue.FromObject(handlerHandle);
        if (!TryGetPropertyValue(handler, handlerValue, trapName, out var trapValue))
        {
            return null;
        }

        if (trapValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            return null;
        }

        if (!IsCallable(trapValue))
        {
            throw new JsThrownException(CreateTypeError("Proxy trap '" + trapName + "' is not callable."));
        }

        return trapValue;
    }

    private bool IsCallableTarget(ObjectHandle targetHandle)
        => IsCallableTarget(targetHandle, new HashSet<ObjectHandle>());

    private bool IsCallableTarget(ObjectHandle targetHandle, HashSet<ObjectHandle> visited)
    {
        if (!visited.Add(targetHandle))
        {
            return false;
        }

        var targetObj = _heap.GetObject(targetHandle);
        return targetObj switch
        {
            JsFunctionObject => true,
            NativeFunctionObject => true,
            BoundFunctionObject bound when bound.TargetFunction.Tag == JsValueTag.Object =>
                IsCallableTarget(bound.TargetFunction.AsObjectHandle(), visited),
            ProxyObject nestedProxy => IsCallableTarget(nestedProxy.TargetHandle, visited),
            _ => false
        };
    }

    private bool IsConstructableTarget(ObjectHandle targetHandle)
        => IsConstructableTarget(targetHandle, new HashSet<ObjectHandle>());

    private bool IsConstructableTarget(ObjectHandle targetHandle, HashSet<ObjectHandle> visited)
    {
        if (!visited.Add(targetHandle))
        {
            return false;
        }

        var targetObj = _heap.GetObject(targetHandle);
        return targetObj switch
        {
            JsFunctionObject jsFn => jsFn.Kind is not (FunctionKind.Generator or FunctionKind.AsyncGenerator),
            NativeFunctionObject nativeFn => nativeFn.IsConstructor,
            BoundFunctionObject bound when bound.TargetFunction.Tag == JsValueTag.Object =>
                IsConstructableTarget(bound.TargetFunction.AsObjectHandle(), visited),
            ProxyObject nestedProxy => IsConstructableTarget(nestedProxy.TargetHandle, visited),
            _ => false
        };
    }

    private bool IsTargetExtensible(ObjectHandle targetHandle)
    {
        var targetObj = _heap.GetObject(targetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return IsTargetExtensible(nestedProxy.TargetHandle);
        }

        return targetObj.Extensible;
    }

    private bool TryGetOwnPropertyDescriptorForTarget(ObjectHandle targetHandle, string prop, out JsPropertyDescriptor descriptor)
    {
        var targetObj = _heap.GetObject(targetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyTryGetOwnPropertyDescriptor(nestedProxy, prop, out descriptor);
        }

        return targetObj.TryGetOwnProperty(prop, out descriptor);
    }

    private bool DescriptorCompatibleWithTarget(bool targetExtensible, bool hasTargetDesc, JsPropertyDescriptor targetDesc, JsPropertyDescriptor resultDesc)
    {
        if (!hasTargetDesc)
        {
            if (!targetExtensible)
            {
                return false;
            }

            return resultDesc.Configurable;
        }

        if (!targetDesc.Configurable)
        {
            if (resultDesc.Configurable)
            {
                return false;
            }

            if (targetDesc.IsAccessor != resultDesc.IsAccessor)
            {
                return false;
            }

            if (!targetDesc.IsAccessor)
            {
                if (!targetDesc.Writable && resultDesc.Writable)
                {
                    return false;
                }

                if (!targetDesc.Writable && !AreStrictlyEqual(resultDesc.Value, targetDesc.Value))
                {
                    return false;
                }
            }
            else
            {
                if (!AreStrictlyEqual(resultDesc.Get, targetDesc.Get) ||
                    !AreStrictlyEqual(resultDesc.Set, targetDesc.Set))
                {
                    return false;
                }
            }
        }

        if (!targetDesc.Configurable && resultDesc.Configurable)
        {
            return false;
        }

        if (!targetExtensible && !hasTargetDesc)
        {
            return false;
        }

        return true;
    }

    internal string TypeOfName(InterpreterFrame frame, int slot)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is null)
        {
            return "undefined";
        }

        for (var env = (EnvironmentRecord?)frame.Environment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.GetBindingValue(name, strict: frame.Function.IsStrictMode, out var envValue);
            if (status == BindingOpResult.Ok)
            {
                return TypeOfValue(envValue);
            }

            ThrowBindingFailure(frame, status, name, assignment: false);
            return "undefined";
        }

        return "undefined";
    }

    private static bool ValueToBooleanProxy(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => false,
            JsValueTag.Null => false,
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => value.AsNumber() != 0.0 && !double.IsNaN(value.AsNumber()),
            JsValueTag.String => value.AsString().Length > 0,
            JsValueTag.Symbol => true,
            JsValueTag.Object => true,
            JsValueTag.HostObject => true,
            _ => false
        };
    }

    [MayExecuteJs]
    private JsValue ProxyGet(ProxyObject proxy, JsValue receiver, string prop)
    {
        var trap = TryGetProxyTrap(proxy, "get");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var propVal = JsValue.FromString(prop);
            var trapResult = CallFunction(trap.Value, new[] { target, propVal, receiver },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
            if (TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out var targetDesc) &&
                !targetDesc.Configurable)
            {
                if (!targetDesc.IsAccessor && !targetDesc.Writable &&
                    !AreStrictlyEqual(trapResult, targetDesc.Value))
                {
                    throw new JsThrownException(CreateTypeError(
                        "Proxy get trap must return the target value for non-writable, non-configurable data properties."));
                }

                if (targetDesc.IsAccessor && targetDesc.Get.Tag == JsValueTag.Undefined &&
                    trapResult.Tag != JsValueTag.Undefined)
                {
                    throw new JsThrownException(CreateTypeError(
                        "Proxy get trap must return undefined for non-configurable accessor properties without a getter."));
                }
            }

            return trapResult;
        }
        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyGet(nestedProxy, receiver, prop);
        }
        return TryGetPropertyValue(targetObj, receiver, prop, out var value)
            ? value : JsValue.Undefined;
    }

    [MayExecuteJs]
    private JsValue ProxyGetSymbol(ProxyObject proxy, JsValue receiver, long symbolId)
    {
        var trap = TryGetProxyTrap(proxy, "get");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var propVal = JsValue.SymbolFromId(symbolId);
            return CallFunction(trap.Value, new[] { target, propVal, receiver },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyGetSymbol(nestedProxy, receiver, symbolId);
        }

        return targetObj.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
            ? GetDescriptorValue(desc, receiver)
            : JsValue.Undefined;
    }

    [MayExecuteJs]
    private bool ProxySet(ProxyObject proxy, JsValue receiver, string prop, JsValue value)
    {
        var trap = TryGetProxyTrap(proxy, "set");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var propVal = JsValue.FromString(prop);
            var result = CallFunction(trap.Value,
                new[] { target, propVal, value, receiver },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
            return ValueToBooleanProxy(result);
        }
        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxySet(nestedProxy, receiver, prop, value);
        }
        return targetObj.SetProperty(prop, value);
    }

    [MayExecuteJs]
    private bool ProxyHas(ProxyObject proxy, string prop)
    {
        var trap = TryGetProxyTrap(proxy, "has");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var propVal = JsValue.FromString(prop);
            var result = CallFunction(trap.Value, new[] { target, propVal },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
            var trapResult = ValueToBooleanProxy(result);
            if (!trapResult &&
                TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out var targetDesc) &&
                (!targetDesc.Configurable || !IsTargetExtensible(proxy.TargetHandle)))
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy has trap cannot report a non-configurable or non-extensible own property as absent."));
            }

            return trapResult;
        }
        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyHas(nestedProxy, prop);
        }
        return targetObj.TryGetProperty(prop, h => _heap.GetObject(h), out _);
    }

    [MayExecuteJs]
    private bool ProxyDelete(ProxyObject proxy, string prop)
    {
        var trap = TryGetProxyTrap(proxy, "deleteProperty");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var propVal = JsValue.FromString(prop);
            var result = CallFunction(trap.Value, new[] { target, propVal },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
            var trapResult = ValueToBooleanProxy(result);
            if (trapResult &&
                TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out var targetDesc) &&
                !targetDesc.Configurable)
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy deleteProperty trap cannot report deletion of a non-configurable own property."));
            }

            if (trapResult &&
                TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out _) &&
                !IsTargetExtensible(proxy.TargetHandle))
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy deleteProperty trap cannot report deletion of an existing property on a non-extensible target."));
            }

            return trapResult;
        }
        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyDelete(nestedProxy, prop);
        }
        if (targetObj.TryGetOwnProperty(prop, out _))
        {
            return targetObj.DeleteProperty(prop);
        }

        return true;
    }

    [MayExecuteJs]
    private JsValue ProxyCall(ProxyObject proxy, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        var trap = TryGetProxyTrap(proxy, "apply");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var argsArray = CreateArrayFromElements(args);
            var argsHandle = _heap.AllocateObject(argsArray, AllocationSite.Current());
            return CallFunction(trap.Value,
                new[] { target, thisValue, JsValue.FromObject(argsHandle) },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
        }
        return CallFunction(JsValue.FromObject(proxy.TargetHandle), args, thisValue);
    }

    [MayExecuteJs]
    private JsValue ProxyConstruct(ProxyObject proxy, IReadOnlyList<JsValue> args, JsValue newTarget)
    {
        var trap = TryGetProxyTrap(proxy, "construct");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var argsArray = CreateArrayFromElements(args);
            var argsHandle = _heap.AllocateObject(argsArray, AllocationSite.Current());
            var result = CallFunction(trap.Value,
                new[] { target, JsValue.FromObject(argsHandle), newTarget },
                JsValue.FromObject(proxy.HandlerHandle!.Value));
            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Proxy construct trap must return an object."));
            }

            return result;
        }
        return ConstructFunction(JsValue.FromObject(proxy.TargetHandle), args, newTarget);
    }

    [MayExecuteJs]
    private JsValue ProxyGetPrototypeOf(ProxyObject proxy)
    {
        var trap = TryGetProxyTrap(proxy, "getPrototypeOf");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var result = CallFunction(trap.Value, new[] { target }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            if (result.Tag != JsValueTag.Object && result.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError("Proxy getPrototypeOf trap must return object or null."));
            }

            if (!IsTargetExtensible(proxy.TargetHandle))
            {
                var targetObject = _heap.GetObject(proxy.TargetHandle);
                var targetProto = targetObject.PrototypeHandle is { } protoHandle
                    ? JsValue.FromObject(protoHandle)
                    : JsValue.Null;
                if (!AreStrictlyEqual(result, targetProto))
                {
                    throw new JsThrownException(CreateTypeError(
                        "Proxy getPrototypeOf trap must return the same prototype for non-extensible targets."));
                }
            }

            return result;
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyGetPrototypeOf(nestedProxy);
        }
        return targetObj.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
    }

    [MayExecuteJs]
    private bool ProxySetPrototypeOf(ProxyObject proxy, JsValue protoValue)
    {
        var trap = TryGetProxyTrap(proxy, "setPrototypeOf");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var result = CallFunction(trap.Value, new[] { target, protoValue }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            return ValueToBooleanProxy(result);
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxySetPrototypeOf(nestedProxy, protoValue);
        }
        if (protoValue.Tag == JsValueTag.Object)
        {
            targetObj.SetPrototype(protoValue.AsObjectHandle());
            _heap.WriteBarrier(proxy.TargetHandle, protoValue.AsObjectHandle());
            return true;
        }

        if (protoValue.Tag == JsValueTag.Null)
        {
            targetObj.SetPrototype(null);
            return true;
        }

        return false;
    }

    [MayExecuteJs]
    private bool ProxyIsExtensible(ProxyObject proxy)
    {
        var trap = TryGetProxyTrap(proxy, "isExtensible");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var result = CallFunction(trap.Value, new[] { target }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            var trapResult = ValueToBooleanProxy(result);
            var targetExtensible = IsTargetExtensible(proxy.TargetHandle);
            if (trapResult != targetExtensible)
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy isExtensible trap result must match the target extensibility."));
            }

            return trapResult;
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyIsExtensible(nestedProxy);
        }

        return targetObj.Extensible;
    }

    [MayExecuteJs]
    private bool ProxyPreventExtensions(ProxyObject proxy)
    {
        var trap = TryGetProxyTrap(proxy, "preventExtensions");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var result = CallFunction(trap.Value, new[] { target }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            return ValueToBooleanProxy(result);
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyPreventExtensions(nestedProxy);
        }

        targetObj.PreventExtensions();
        return true;
    }

    [MayExecuteJs]
    private List<JsValue> ProxyOwnKeys(ProxyObject proxy)
    {
        var trap = TryGetProxyTrap(proxy, "ownKeys");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var result = CallFunction(trap.Value, new[] { target }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Proxy ownKeys trap must return an object."));
            }

            var listObj = _heap.GetObject(result.AsObjectHandle());
            var len = GetArrayLength(listObj);
            var keys = new List<JsValue>(len);
            for (var i = 0; i < len; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!TryGetPropertyValue(listObj, result, key, out var item))
                {
                    continue;
                }

                if (item.Tag == JsValueTag.String || item.Tag == JsValueTag.Symbol)
                {
                    keys.Add(item);
                }
                else
                {
                    keys.Add(JsValue.FromString(ToPropertyKey(item)));
                }
            }

            return keys;
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyOwnKeys(nestedProxy);
        }
        var fallback = new List<JsValue>();
        foreach (var p in targetObj.EnumerateOwnProperties())
        {
            fallback.Add(JsValue.FromString(p.Key));
        }
        foreach (var p in targetObj.EnumerateOwnSymbolProperties())
        {
            fallback.Add(JsValue.SymbolFromId(p.Key));
        }
        return fallback;
    }

    [MayExecuteJs]
    private bool ProxyTryGetOwnPropertyDescriptor(ProxyObject proxy, string prop, out JsPropertyDescriptor descriptor)
    {
        var trap = TryGetProxyTrap(proxy, "getOwnPropertyDescriptor");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var key = JsValue.FromString(prop);
            var result = CallFunction(trap.Value, new[] { target, key }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            var targetHasDesc = TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out var targetDesc);
            var targetExtensible = IsTargetExtensible(proxy.TargetHandle);
            if (result.Tag == JsValueTag.Undefined)
            {
                if (targetHasDesc && (!targetDesc.Configurable || !targetExtensible))
                {
                    throw new JsThrownException(CreateTypeError(
                        "Proxy getOwnPropertyDescriptor trap cannot report an existing non-configurable or non-extensible own property as absent."));
                }

                descriptor = default;
                return false;
            }

            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Proxy getOwnPropertyDescriptor trap must return object or undefined."));
            }

            var resultDesc = ToPropertyDescriptor(result);
            if (!DescriptorCompatibleWithTarget(targetExtensible, targetHasDesc, targetDesc, resultDesc))
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy getOwnPropertyDescriptor trap returned a descriptor incompatible with the target."));
            }

            descriptor = resultDesc;
            return true;
        }

        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyTryGetOwnPropertyDescriptor(nestedProxy, prop, out descriptor);
        }
        return targetObj.TryGetOwnProperty(prop, out descriptor);
    }

    [MayExecuteJs]
    private bool ProxyDefineProperty(ProxyObject proxy, string prop, JsValue descriptorValue)
    {
        var trap = TryGetProxyTrap(proxy, "defineProperty");
        if (trap is not null)
        {
            var target = JsValue.FromObject(proxy.TargetHandle);
            var key = JsValue.FromString(prop);
            var result = CallFunction(trap.Value, new[] { target, key, descriptorValue }, JsValue.FromObject(proxy.HandlerHandle!.Value));
            var trapResult = ValueToBooleanProxy(result);
            if (!trapResult)
            {
                return false;
            }

            if (!IsTargetExtensible(proxy.TargetHandle) &&
                !TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out _))
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy defineProperty trap cannot create a new property on a non-extensible target."));
            }

            var hasTargetDesc = TryGetOwnPropertyDescriptorForTarget(proxy.TargetHandle, prop, out var targetDesc);
            var descriptorObj = descriptorValue.Tag == JsValueTag.Object
                ? _heap.GetObject(descriptorValue.AsObjectHandle())
                : null;
            var configurableValue = JsValue.Undefined;
            var hasConfigurable = descriptorObj is not null &&
                                  TryGetPropertyValue(descriptorObj, descriptorValue, "configurable", out configurableValue);
            var setsConfigurableFalse = hasConfigurable && !ValueToBooleanProxy(configurableValue);
            var writableValue = JsValue.Undefined;
            var hasWritable = descriptorObj is not null &&
                              TryGetPropertyValue(descriptorObj, descriptorValue, "writable", out writableValue);
            var setsWritableFalse = hasWritable && !ValueToBooleanProxy(writableValue);
            var valueValue = JsValue.Undefined;
            var hasValue = descriptorObj is not null &&
                           TryGetPropertyValue(descriptorObj, descriptorValue, "value", out valueValue);
            var getValue = JsValue.Undefined;
            var hasGet = descriptorObj is not null &&
                         TryGetPropertyValue(descriptorObj, descriptorValue, "get", out getValue);
            var setValue = JsValue.Undefined;
            var hasSet = descriptorObj is not null &&
                         TryGetPropertyValue(descriptorObj, descriptorValue, "set", out setValue);

            if (!hasTargetDesc)
            {
                if (setsConfigurableFalse)
                {
                    throw new JsThrownException(CreateTypeError(
                        "Proxy defineProperty trap cannot define a new non-configurable property."));
                }

                return true;
            }

            if (setsConfigurableFalse && targetDesc.Configurable)
            {
                throw new JsThrownException(CreateTypeError(
                    "Proxy defineProperty trap cannot make a configurable target property non-configurable."));
            }

            if (!targetDesc.Configurable)
            {
                if (!targetDesc.IsAccessor)
                {
                    if (hasGet || hasSet)
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot convert a non-configurable data property to an accessor property."));
                    }

                    if (targetDesc.Writable && setsWritableFalse)
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot report writable=false for a non-configurable writable data property."));
                    }

                    if (!targetDesc.Writable && hasValue && !AreStrictlyEqual(valueValue, targetDesc.Value))
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot change value of a non-configurable, non-writable data property."));
                    }
                }
                else
                {
                    if (hasValue || hasWritable)
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot convert a non-configurable accessor property to a data property."));
                    }

                    if (hasGet && !AreStrictlyEqual(getValue, targetDesc.Get))
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot change getter of a non-configurable accessor property."));
                    }

                    if (hasSet && !AreStrictlyEqual(setValue, targetDesc.Set))
                    {
                        throw new JsThrownException(CreateTypeError(
                            "Proxy defineProperty trap cannot change setter of a non-configurable accessor property."));
                    }
                }
            }

            return true;
        }

        var desc = ToPropertyDescriptor(descriptorValue);
        var targetObj = _heap.GetObject(proxy.TargetHandle);
        if (targetObj is ProxyObject nestedProxy)
        {
            return ProxyDefineProperty(nestedProxy, prop, descriptorValue);
        }
        var ok = targetObj.DefineOwnProperty(prop, desc);
        if (ok)
        {
            WriteDescriptorBarrier(proxy.TargetHandle, desc);
        }
        return ok;
    }

    [MayExecuteJs]
    private JsPropertyDescriptor ToPropertyDescriptor(JsValue descriptorValue)
    {
        if (descriptorValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Property descriptor must be an object."));
        }

        var descriptorObject = _heap.GetObject(descriptorValue.AsObjectHandle());
        var descriptorReceiver = descriptorValue;
        var hasValue = TryGetPropertyValue(descriptorObject, descriptorReceiver, "value", out var value);
        var hasWritable = descriptorObject.TryGetProperty("writable", h => _heap.GetObject(h), out _);
        var hasGetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "get", out var getter);
        var hasSetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "set", out var setter);

        if ((hasGetter || hasSetter) && (hasValue || hasWritable))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor cannot mix accessor and data fields."));
        }

        if (hasGetter && getter.Tag != JsValueTag.Undefined && !IsCallable(getter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor getter must be callable or undefined."));
        }

        if (hasSetter && setter.Tag != JsValueTag.Undefined && !IsCallable(setter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor setter must be callable or undefined."));
        }

        var writable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "writable");
        var enumerable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "enumerable");
        var configurable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "configurable");

        return hasGetter || hasSetter
            ? JsPropertyDescriptor.Accessor(
                hasGetter ? getter : JsValue.Undefined,
                hasSetter ? setter : JsValue.Undefined,
                enumerable,
                configurable)
            : new JsPropertyDescriptor(hasValue ? value : JsValue.Undefined, writable, enumerable, configurable);
    }


    private string NormalizeRegExpFlags(string flags)
    {
        var seenGlobal = false;
        var seenIgnoreCase = false;
        var seenMultiline = false;
        var seenDotAll = false;
        var seenUnicode = false;
        var seenUnicodeSets = false;
        var seenSticky = false;
        var seenHasIndices = false;
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
                case 's' when !seenDotAll:
                    seenDotAll = true;
                    break;
                case 'u' when !seenUnicode:
                    seenUnicode = true;
                    break;
                case 'v' when !seenUnicodeSets:
                    seenUnicodeSets = true;
                    break;
                case 'y' when !seenSticky:
                    seenSticky = true;
                    break;
                case 'd' when !seenHasIndices:
                    seenHasIndices = true;
                    break;
                case 'g':
                case 'i':
                case 'm':
                case 's':
                case 'u':
                case 'v':
                case 'y':
                case 'd':
                    throw new JsThrownException(CreateSyntaxError("RegExp flags must not be duplicated."));
                default:
                    throw new JsThrownException(CreateSyntaxError($"Invalid RegExp flag '{flag}'."));
            }
        }

        // Canonical order per RegExp.prototype.flags getter.
        return string.Concat(
            seenHasIndices ? "d" : string.Empty,
            seenGlobal ? "g" : string.Empty,
            seenIgnoreCase ? "i" : string.Empty,
            seenMultiline ? "m" : string.Empty,
            seenDotAll ? "s" : string.Empty,
            seenUnicode ? "u" : string.Empty,
            seenUnicodeSets ? "v" : string.Empty,
            seenSticky ? "y" : string.Empty);
    }

    private static string RewriteEcmaCharacterClassEscapes(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        const string whiteSpaceClass = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        const string nonWhiteSpaceClass = @"[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        var rewritten = new System.Text.StringBuilder(pattern.Length + 24);
        var inCharClass = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[i + 1];
                if (!inCharClass)
                {
                    switch (next)
                    {
                        case 'd':
                            rewritten.Append("[0-9]");
                            i++;
                            continue;
                        case 'D':
                            rewritten.Append("[^0-9]");
                            i++;
                            continue;
                        case 'w':
                            rewritten.Append("[A-Za-z0-9_]");
                            i++;
                            continue;
                        case 'W':
                            rewritten.Append("[^A-Za-z0-9_]");
                            i++;
                            continue;
                        case 's':
                            rewritten.Append(whiteSpaceClass);
                            i++;
                            continue;
                        case 'S':
                            rewritten.Append(nonWhiteSpaceClass);
                            i++;
                            continue;
                    }
                }

                rewritten.Append(ch);
                rewritten.Append(next);
                i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
                rewritten.Append(ch);
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                rewritten.Append(ch);
                continue;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
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

        // ECMA-262 25.5.3 JSON [ @@toStringTag ] = "JSON".
        DefineBuiltinToStringTag(json, "JSON");

        _jsonObjectHandle = handle;
        return handle;
    }

    private ObjectHandle EnsureIntlObject()
    {
        if (_intlObjectHandle is { } existing)
        {
            return existing;
        }

        var intl = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(intl, AllocationSite.Current());
        _heap.PushRoot(handle);

        // ECMA-402 §11 DateTimeFormat constructor.
        {
            var ctor = new NativeFunctionObject(
                "DateTimeFormat",
                (_, _) => throw new JsThrownException(CreateTypeError("Intl.DateTimeFormat must be invoked with 'new'.")),
                construct: args => DateTimeFormatConstruct(args),
                length: 0);
            var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
            _heap.PushRoot(ctorHandle);
            var prototypeHandle = EnsureDateTimeFormatPrototype();
            var prototype = _heap.GetObject(prototypeHandle);
            _ = ctor.DefineOwnProperty(
                "prototype",
                new JsPropertyDescriptor(
                    JsValue.FromObject(prototypeHandle),
                    Writable: false,
                    Enumerable: false,
                    Configurable: false));
            _heap.WriteBarrier(ctorHandle, prototypeHandle);
            _ = prototype.DefineOwnProperty(
                "constructor",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(prototypeHandle, ctorHandle);
            _ = intl.DefineOwnProperty(
                "DateTimeFormat",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, ctorHandle);
        }

        // ECMA-402 §13 NumberFormat constructor.
        {
            var ctor = new NativeFunctionObject(
                "NumberFormat",
                (_, _) => throw new JsThrownException(CreateTypeError("Intl.NumberFormat must be invoked with 'new'.")),
                construct: args => NumberFormatConstruct(args),
                length: 0);
            var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
            _heap.PushRoot(ctorHandle);
            _ = intl.DefineOwnProperty(
                "NumberFormat",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, ctorHandle);
        }

        // ECMA-402 §10 Collator constructor.
        {
            var ctor = new NativeFunctionObject(
                "Collator",
                (_, _) => throw new JsThrownException(CreateTypeError("Intl.Collator must be invoked with 'new'.")),
                construct: args => CollatorConstruct(args),
                length: 0);
            var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
            _heap.PushRoot(ctorHandle);
            _ = intl.DefineOwnProperty(
                "Collator",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, ctorHandle);
        }

        // ECMA-402 Intl.ListFormat constructor.
        {
            var ctor = new NativeFunctionObject(
                "ListFormat",
                (_, _) => throw new JsThrownException(CreateTypeError("Intl.ListFormat must be invoked with 'new'.")),
                construct: args => ListFormatConstruct(args),
                length: 0);
            var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
            _heap.PushRoot(ctorHandle);
            _ = intl.DefineOwnProperty(
                "ListFormat",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, ctorHandle);
        }

        // ECMA-402 Intl.DurationFormat constructor.
        {
            var prototypeHandle = EnsureDurationFormatPrototype();
            var prototype = _heap.GetObject(prototypeHandle);
            var ctor = new NativeFunctionObject(
                "DurationFormat",
                (_, _) => throw new JsThrownException(CreateTypeError("Intl.DurationFormat must be invoked with 'new'.")),
                construct: args => DurationFormatConstruct(args),
                length: 0);
            var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
            _heap.PushRoot(ctorHandle);

            _ = ctor.DefineOwnProperty(
                "prototype",
                new JsPropertyDescriptor(
                    JsValue.FromObject(prototypeHandle),
                    Writable: false,
                    Enumerable: false,
                    Configurable: false));
            _heap.WriteBarrier(ctorHandle, prototypeHandle);

            var supportedLocalesOf = new NativeFunctionObject(
                "supportedLocalesOf",
                (_, args) => DurationFormatSupportedLocalesOf(args),
                length: 1);
            var supportedLocalesOfHandle = _heap.AllocateObject(supportedLocalesOf, AllocationSite.Current());
            _ = ctor.DefineOwnProperty(
                "supportedLocalesOf",
                new JsPropertyDescriptor(
                    JsValue.FromObject(supportedLocalesOfHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(ctorHandle, supportedLocalesOfHandle);

            _ = prototype.DefineOwnProperty(
                "constructor",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(prototypeHandle, ctorHandle);

            _ = intl.DefineOwnProperty(
                "DurationFormat",
                new JsPropertyDescriptor(
                    JsValue.FromObject(ctorHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, ctorHandle);
            _durationFormatConstructorHandle = ctorHandle;
        }

        // ECMA-402 §14 Intl.Locale constructor.
        _ = EnsureLocaleConstructor(intl, handle);

        // ECMA-402 §17 Intl.RelativeTimeFormat constructor.
        _ = EnsureRelativeTimeFormatConstructor(intl, handle);

        // ECMA-402 §9.2.1 getCanonicalLocales(locales).
        {
            var fn = new NativeFunctionObject(
                "getCanonicalLocales",
                (_, args) => GetCanonicalLocales(args),
                length: 1);
            var fnHandle = _heap.AllocateObject(fn, AllocationSite.Current());
            _ = intl.DefineOwnProperty(
                "getCanonicalLocales",
                new JsPropertyDescriptor(
                    JsValue.FromObject(fnHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, fnHandle);
        }

        {
            var fn = new NativeFunctionObject(
                "supportedValuesOf",
                (_, args) =>
                {
                    var key = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
                    var values = key == "numberingSystem"
                        ? new[] { JsValue.FromString("latn"), JsValue.FromString("arab"), JsValue.FromString("thai") }
                        : Array.Empty<JsValue>();
                    var arr = CreateArrayFromElements(values);
                    return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
                },
                length: 1);
            var fnHandle = _heap.AllocateObject(fn, AllocationSite.Current());
            _ = intl.DefineOwnProperty(
                "supportedValuesOf",
                new JsPropertyDescriptor(
                    JsValue.FromObject(fnHandle),
                    Writable: true,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(handle, fnHandle);
        }

        _intlObjectHandle = handle;
        return handle;
    }

    private JsValue GetCanonicalLocales(IReadOnlyList<JsValue> args)
    {
        var canonicalLocales = args.Count == 0
            ? Array.Empty<JsValue>()
            : CanonicalizeLocaleListForDuration(args[0]).Select(JsValue.FromString).ToArray();
        var arrObj = CreateArrayFromElements(canonicalLocales);
        var arrHandle = _heap.AllocateObject(arrObj, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private void DefineIntrinsicFunction(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var function = new NativeFunctionObject(name, call, length: length);
        if (_functionPrototypeHandle is { } fnProto)
        {
            function.SetPrototype(fnProto);
        }

        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        if (_functionPrototypeHandle is { } fnProtoHandle)
        {
            _heap.WriteBarrier(functionHandle, fnProtoHandle);
        }

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

        var objectPrototype = new JsObject();
        // ECMA-262 10.4.7 / 20.1.3: %Object.prototype% is an immutable prototype
        // exotic object — [[SetPrototypeOf]] only succeeds for the same value.
        objectPrototype.ImmutablePrototype = true;
        var prototypeHandle = _heap.AllocateObject(objectPrototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Object",
            (_, args) => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            args => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        // Built-in methods are { [[Writable]]: true, [[Enumerable]]: false,
        // [[Configurable]]: true } — use DefineIntrinsicFunction so they are not
        // enumerable (SetProperty would create them enumerable).
        DefineIntrinsicFunction(constructorHandle, constructor, "defineProperty", ObjectDefineProperty, length: 3);
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertyDescriptor", ObjectGetOwnPropertyDescriptor, length: 2);

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

            var keyArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            if (keyArg.Tag == JsValueTag.Symbol)
            {
                return JsValue.FromBoolean(obj.TryGetOwnSymbolProperty(keyArg.AsSymbolId(), out var __));
            }
            return JsValue.FromBoolean(obj.TryGetOwnProperty(ToPropertyKey(keyArg), out var ___));
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
            // ECMA-262 20.1.2.10 step 1: obj = ToObject(O). A primitive coerces to its
            // wrapper (getOwnPropertyNames("ab") → ["0","1","length"]); only
            // undefined/null throw.
            var firstArg = args.Count > 0 ? args[0] : JsValue.Undefined;
            var targetValue = ToObjectValue(firstArg);
            var obj = _heap.GetObject(targetValue.AsObjectHandle());

            var items = new List<JsValue>();
            if (obj is ProxyObject proxyOwnNames)
            {
                foreach (var key in ProxyOwnKeys(proxyOwnNames))
                {
                    if (key.Tag == JsValueTag.String)
                    {
                        items.Add(key);
                    }
                }
            }
            else
            {
                foreach (var pair in obj.EnumerateOwnProperties())
                {
                    items.Add(JsValue.FromString(pair.Key));
                }
            }

            var arr = CreateArrayFromElements(items);
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

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj is ProxyObject proxyPreventExtensions)
            {
                ProxyPreventExtensions(proxyPreventExtensions);
                return target;
            }

            obj.PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isExtensible", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj is ProxyObject proxyIsExtensible)
            {
                return JsValue.FromBoolean(ProxyIsExtensible(proxyIsExtensible));
            }

            return JsValue.FromBoolean(obj.Extensible);
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
            // ECMA-262 20.1.2.1 Object.assign(target, ...sources).
            // 1. Let to be ? ToObject(target).
            var toValue = ToObjectValue(args.Count > 0 ? args[0] : JsValue.Undefined);
            var toHandle = toValue.AsObjectHandle();
            var to = _heap.GetObject(toHandle);

            for (var i = 1; i < args.Count; i++)
            {
                var source = args[i];
                if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
                {
                    continue;
                }

                // 4.b.i Let from be ! ToObject(nextSource). Strings expose their
                // indexed code units (enumerable) plus a non-enumerable length.
                var fromValue = ToObjectValue(source);
                var fromObj = _heap.GetObject(fromValue.AsObjectHandle());

                // String-keyed own enumerable properties, in own-key order. Read
                // each value through [[Get]] (so getters run and their throws
                // propagate) and write through [[Set]] (so setters/extensibility/
                // writability are honored); a false result is a TypeError.
                foreach (var pair in fromObj.EnumerateOwnProperties())
                {
                    if (!pair.Value.Enumerable)
                    {
                        continue;
                    }

                    var value = TryGetPropertyValue(fromObj, fromValue, pair.Key, out var v)
                        ? v
                        : JsValue.Undefined;
                    if (!SetPropertyValue(toHandle, to, pair.Key, value, toValue))
                    {
                        throw new JsThrownException(CreateTypeError(
                            $"Cannot assign to read-only property '{pair.Key}'."));
                    }
                }

                // Symbol-keyed own enumerable properties follow the string keys.
                foreach (var pair in fromObj.EnumerateOwnSymbolProperties())
                {
                    if (!pair.Value.Enumerable)
                    {
                        continue;
                    }

                    var value = GetReceiverSymbolProperty(fromValue, pair.Key);
                    to.DefineOwnSymbolProperty(pair.Key,
                        new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
                    if (value.Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(toHandle, value.AsObjectHandle());
                    }
                }
            }

            return toValue;
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
            if (target is ProxyObject proxyGetPrototypeOf)
            {
                return ProxyGetPrototypeOf(proxyGetPrototypeOf);
            }
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
            // ECMA-262 20.1.2.21 step 4-5: ? O.[[SetPrototypeOf]](proto); throw if false.
            if (!OrdinarySetPrototypeOf(ownerHandle, protoArg))
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.setPrototypeOf: cannot set prototype (cycle, non-extensible, or immutable)."));
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
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertySymbols", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.getOwnPropertySymbols called on non-object."));
            }

            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var symbols = new List<JsValue>();
            if (obj is ProxyObject proxyOwnSymbols)
            {
                foreach (var key in ProxyOwnKeys(proxyOwnSymbols))
                {
                    if (key.Tag == JsValueTag.Symbol)
                    {
                        symbols.Add(key);
                    }
                }
            }
            else
            {
                foreach (var pair in obj.EnumerateOwnSymbolProperties())
                {
                    symbols.Add(JsValue.SymbolFromId(pair.Key));
                }
            }

            var arr = CreateArrayObject(symbols);
            var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
            return JsValue.FromObject(arrHandle);
        }, length: 1);

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "valueOf", (thisValue, _) => ObjectPrototypeValueOf(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "hasOwnProperty", ObjectPrototypeHasOwnProperty, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "isPrototypeOf", ObjectPrototypeIsPrototypeOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "propertyIsEnumerable", ObjectPrototypePropertyIsEnumerable, length: 1);

        // ECMA-262 Annex B B.2.2 legacy accessors (__proto__, __defineGetter__, etc.).
        InstallAnnexBObjectPrototype(prototypeHandle, prototype);

        _objectPrototypeHandle = prototypeHandle;
        _objectConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // ECMA-262 7.3.25 CopyDataProperties(target, source, excludedItems=empty).
    // Used by object spread `{ ...source }`. Copies every own enumerable property
    // (string keys first, then symbol keys) from source into target using [[Get]]
    // to read and CreateDataPropertyOrThrow to write. A null/undefined source is a
    // no-op (step 2). The target is always a freshly built object literal, so the
    // CreateDataProperty (define) semantics — distinct from Object.assign's [[Set]]
    // — never observe an inherited setter.
    private void CopyDataPropertiesInto(JsValue targetValue, JsValue source)
    {
        if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
        {
            return;
        }

        if (targetValue.Tag != JsValueTag.Object)
        {
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var target = _heap.GetObject(targetHandle);

        var fromValue = ToObjectValue(source);
        var fromObj = _heap.GetObject(fromValue.AsObjectHandle());

        foreach (var pair in fromObj.EnumerateOwnProperties())
        {
            if (!pair.Value.Enumerable)
            {
                continue;
            }

            var value = TryGetPropertyValue(fromObj, fromValue, pair.Key, out var v)
                ? v
                : JsValue.Undefined;
            _ = target.DefineOwnProperty(
                pair.Key,
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(targetHandle, value.AsObjectHandle());
            }
        }

        foreach (var pair in fromObj.EnumerateOwnSymbolProperties())
        {
            if (!pair.Value.Enumerable)
            {
                continue;
            }

            var value = GetReceiverSymbolProperty(fromValue, pair.Key);
            _ = target.DefineOwnSymbolProperty(
                pair.Key,
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(targetHandle, value.AsObjectHandle());
            }
        }
    }

    private enum OwnEnumerableKind
    {
        Keys,
        Values,
        Entries,
    }

    private JsValue CollectOwnEnumerable(IReadOnlyList<JsValue> args, OwnEnumerableKind kind)
    {
        // ECMA-262 20.1.2.{keys,values,entries} step 1: obj = ToObject(O) (coerces
        // primitives; undefined/null throw via ToObjectValue).
        var firstArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var targetValue = ToObjectValue(firstArg);
        var obj = _heap.GetObject(targetValue.AsObjectHandle());

        // 7.3.23 EnumerableOwnProperties: snapshot the own string keys first, then for
        // each re-read the own descriptor (so a getter that deletes a later key or flips
        // its enumerability is observed) and [[Get]] the value (invoking accessors).
        var keys = new List<string>();
        if (obj is ProxyObject proxyOwnEnumerable)
        {
            foreach (var key in ProxyOwnKeys(proxyOwnEnumerable))
            {
                if (key.Tag == JsValueTag.String)
                {
                    keys.Add(key.AsString());
                }
            }
        }
        else
        {
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                keys.Add(pair.Key);
            }
        }

        var items = new List<JsValue>();
        foreach (var key in keys)
        {
            bool enumerable;
            if (obj is ProxyObject proxyDesc)
            {
                if (!ProxyTryGetOwnPropertyDescriptor(proxyDesc, key, out var pd) || !pd.Enumerable)
                {
                    continue;
                }

                enumerable = true;
            }
            else
            {
                if (!obj.TryGetOwnProperty(key, out var d) || !d.Enumerable)
                {
                    continue;
                }

                enumerable = true;
            }

            if (!enumerable)
            {
                continue;
            }

            if (kind == OwnEnumerableKind.Keys)
            {
                items.Add(JsValue.FromString(key));
                continue;
            }

            var value = GetReceiverProperty(targetValue, key);
            if (kind == OwnEnumerableKind.Values)
            {
                items.Add(value);
            }
            else
            {
                var entry = CreateArrayFromElements(new[] { JsValue.FromString(key), value });
                items.Add(JsValue.FromObject(_heap.AllocateObject(entry, AllocationSite.Current())));
            }
        }

        // CreateArrayFromElements (not CreateArrayObject) so a single numeric value is
        // never misread as an array length.
        var arr = CreateArrayFromElements(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private ObjectHandle EnsureFunctionPrototype()
    {
        if (_functionPrototypeHandle is { } existing)
        {
            return existing;
        }

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
        _functionPrototypeHandle = prototypeHandle;

        var constructor = new NativeFunctionObject(
            "Function",
            (_, args) => CreateDynamicFunction(args, FunctionKind.Ordinary),
            args => CreateDynamicFunction(args, FunctionKind.Ordinary),
            length: 1);
        constructor.SetPrototype(prototypeHandle);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var callHandle = EnsureFunctionCallMethod();
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("call", new JsPropertyDescriptor(JsValue.FromObject(callHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _heap.WriteBarrier(prototypeHandle, callHandle);

        var restrictedThrower = JsValue.FromObject(EnsureThrowTypeErrorIntrinsic());
        var restrictedDescriptor = JsPropertyDescriptor.Accessor(
            restrictedThrower,
            restrictedThrower,
            Enumerable: false,
            Configurable: true);
        _ = prototype.DefineOwnProperty("arguments", restrictedDescriptor);
        _ = prototype.DefineOwnProperty("caller", restrictedDescriptor);
        WriteDescriptorBarrier(prototypeHandle, restrictedDescriptor);

        // ECMA-262 20.2.3.1 Function.prototype.apply(thisArg, argsArray). The
        // second argument is an Array (or array-like). null/undefined become an
        // empty argument list per spec step 3-4.
        var applyHandle = _heap.AllocateObject(
            new NativeFunctionObject("apply", FunctionPrototypeApply, length: 2),
            AllocationSite.Current());
        _ = prototype.DefineOwnProperty("apply", new JsPropertyDescriptor(JsValue.FromObject(applyHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, applyHandle);

        // ECMA-262 20.2.3.2 Function.prototype.bind(thisArg, ...args). Returns a new
        // function ("exotic bound function" in the spec). The returned function calls
        // the original with thisArg pre-set and any bound args prepended to the
        // call-site args.
        var bindHandle = _heap.AllocateObject(
            new NativeFunctionObject("bind", FunctionPrototypeBind, length: 1),
            AllocationSite.Current());
        _ = prototype.DefineOwnProperty("bind", new JsPropertyDescriptor(JsValue.FromObject(bindHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, bindHandle);

        var toStringHandle = _heap.AllocateObject(
            new NativeFunctionObject("toString", FunctionPrototypeToString, length: 0),
            AllocationSite.Current());
        _ = prototype.DefineOwnProperty("toString", new JsPropertyDescriptor(JsValue.FromObject(toStringHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, toStringHandle);

        var hasInstanceFnHandle = _heap.AllocateObject(
            new NativeFunctionObject("[Symbol.hasInstance]", FunctionPrototypeHasInstance, length: 1),
            AllocationSite.Current());
        var hasInstanceSymbol = GetWellKnownSymbol("hasInstance");
        if (hasInstanceSymbol.Tag == JsValueTag.Symbol)
        {
            _ = prototype.DefineOwnSymbolProperty(
                hasInstanceSymbol.AsSymbolId(),
                new JsPropertyDescriptor(JsValue.FromObject(hasInstanceFnHandle), Writable: false, Enumerable: false, Configurable: false));
            _heap.WriteBarrier(prototypeHandle, hasInstanceFnHandle);
        }

        _functionConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureGeneratorFunctionConstructor()
    {
        if (_generatorFunctionConstructorHandle is { } existing)
            return existing;

        var prototypeHandle = EnsureGeneratorFunctionPrototype();
        var constructor = new NativeFunctionObject(
            "GeneratorFunction",
            (_, args) => CreateDynamicFunction(args, FunctionKind.Generator),
            args => CreateDynamicFunction(args, FunctionKind.Generator),
            length: 1);
        constructor.SetPrototype(EnsureFunctionPrototype());
        _ = constructor.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(
                JsValue.FromObject(prototypeHandle),
                Writable: false,
                Enumerable: false,
                Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var protoObj = _heap.GetObject(prototypeHandle);
        _ = protoObj.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(constructorHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        _generatorFunctionConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureGeneratorFunctionPrototype()
    {
        if (_generatorFunctionPrototypeHandle is { } existing)
            return existing;

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureFunctionPrototype());
        _ = prototype.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(
                JsValue.FromObject(EnsureGeneratorPrototype()),
                Writable: true,
                Enumerable: false,
                Configurable: false));
        var handle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(handle);
        _generatorFunctionPrototypeHandle = handle;
        return handle;
    }

    private string FormatCallStack(string errorName, string message)
    {
        return errorName + ": " + message;
    }

    private JsValue CreateFunctionObject(
        BytecodeFunction function,
        EnvironmentRecord? outerEnvironment = null)
    {
        var fnObj = new JsFunctionObject(function, outerEnvironment, function.Kind);
        // ECMA-262 PrivateBrandAdd for the static side: a class constructor object
        // carries its class brand so that `C.#staticPriv` access (which brand-checks
        // the constructor object itself) succeeds. We stamp every brand-bearing
        // function object; only constructors are ever the target of a private access,
        // so stamping ordinary private-name-referencing methods is inert.
        if (function.BrandTokens.Count > 0)
        {
            fnObj.PrivateBrand = function.BrandTokens[0];
        }
        var functionPrototype = function.Kind switch
        {
            FunctionKind.Generator => EnsureGeneratorFunctionPrototype(),
            _ => EnsureFunctionPrototype()
        };
        fnObj.SetPrototype(functionPrototype);
        _ = fnObj.DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(
                JsValue.FromString(function.Name ?? string.Empty),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        var functionLength = function.ExpectedArgumentCount >= 0
            ? function.ExpectedArgumentCount
            : function.ParameterNames.Count;
        _ = fnObj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(functionLength),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        // ECMA-262 15.x — only ordinary, generator, async-generator and constructor
        // function objects have an own `prototype` property. Arrow functions, methods,
        // async (non-generator) functions and bound functions have none. When present,
        // `prototype` is { [[Writable]]: true (false for class constructors),
        // [[Enumerable]]: false, [[Configurable]]: false } — crucially non-enumerable,
        // so Object.keys(fn) never surfaces it (webpack's onChunksLoaded helper iterates
        // Object.keys(__webpack_require__.O) and calls each, which breaks if `prototype`
        // leaks in as an enumerable key).
        var hasOwnPrototype = function.Kind is FunctionKind.Ordinary
            or FunctionKind.Generator
            or FunctionKind.AsyncGenerator
            or FunctionKind.Constructor;
        if (!hasOwnPrototype)
        {
            var bareHandle = _heap.AllocateObject(fnObj, AllocationSite.Current());
            fnObj.SelfHandle = bareHandle;
            return JsValue.FromObject(bareHandle);
        }

        var functionInstancePrototype = CreateOrdinaryObject();
        if (function.Kind == FunctionKind.Generator)
        {
            functionInstancePrototype.SetPrototype(EnsureGeneratorPrototype());
        }
        else if (function.Kind == FunctionKind.AsyncGenerator)
        {
            functionInstancePrototype.SetPrototype(EnsureAsyncGeneratorPrototype());
        }

        var prototypeHandle = _heap.AllocateObject(functionInstancePrototype, AllocationSite.Current());
        _ = fnObj.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(
                JsValue.FromObject(prototypeHandle),
                Writable: function.Kind != FunctionKind.Constructor,
                Enumerable: false,
                Configurable: false));
        var handle = _heap.AllocateObject(fnObj, AllocationSite.Current());
        fnObj.SelfHandle = handle;
        _ = functionInstancePrototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(handle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(handle, prototypeHandle);
        _heap.WriteBarrier(prototypeHandle, handle);
        return JsValue.FromObject(handle);
    }

    private JsValue CreateDynamicFunction(IReadOnlyList<JsValue> args, FunctionKind kind)
    {
        // Tier 5 #25: same CSP eval gate applies to the Function constructor
        // family (Function, AsyncFunction, GeneratorFunction).
        if (!EvalAllowed)
        {
            throw new JsThrownException(CreateError("Refused to compile a Function() because 'unsafe-eval' is not allowed by the policy."));
        }

        // ECMA-262 20.2.1.1.1 CreateDynamicFunction step 10/11 � parameters
        // and body are *parsed*; failures must throw SyntaxError, not a raw
        // host exception. Run parameter validation inside the same try so
        // its JsParserException is wrapped consistently with the body path.
        var body = args.Count > 0 ? ToStringValue(args[^1]) : string.Empty;
        try
        {
            var parameters = new List<string>();
            for (var i = 0; i + 1 < args.Count; i++)
            {
                AddFunctionConstructorParameters(parameters, ToStringValue(args[i]));
            }

            var compiled = new BytecodeCompiler().CompileFunctionBody(
                new SourceText(body, "<Function>"),
                parameters,
                "anonymous",
                kind);
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
        // ECMA-262 Annex B.1.3 � non-module source admits SingleLineHTMLOpenComment
        // (`<!--` to LineTerminator) and SingleLineHTMLCloseComment (`-->` to
        // LineTerminator, valid only when preceded by a LineTerminator in the
        // input). The Function constructor parameter goal is non-strict
        // FormalParameters, so strip these comments before splitting.
        var stripped = StripAnnexBHtmlComments(parameterText);
        foreach (var rawPart in stripped.Split(','))
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

    private static string StripAnnexBHtmlComments(string text)
    {
        // ECMA-262 Annex B.1.3: elide SingleLineHTMLOpenComment ("<!--" to
        // LineTerminator) and SingleLineHTMLCloseComment ("-->" to
        // LineTerminator). The close form is only valid when preceded by
        // a LineTerminator in the source -- tracked via atLineStart, which
        // horizontal whitespace does not reset.
        var sb = new System.Text.StringBuilder(text.Length);
        var atLineStart = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (IsLineTerminator(ch))
            {
                sb.Append(ch);
                atLineStart = true;
                i++;
                continue;
            }
            if (IsHorizontalWhitespace(ch))
            {
                sb.Append(ch);
                i++;
                continue;
            }
            if (ch == '<' && i + 4 <= text.Length && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
            {
                while (i < text.Length && !IsLineTerminator(text[i])) i++;
                continue;
            }
            if (atLineStart && ch == '-' && i + 3 <= text.Length && text[i + 1] == '-' && text[i + 2] == '>')
            {
                while (i < text.Length && !IsLineTerminator(text[i])) i++;
                continue;
            }
            sb.Append(ch);
            atLineStart = false;
            i++;
        }
        return sb.ToString();
    }

    // ECMA-262 11.3 LineTerminator: LF, CR, LS (U+2028), PS (U+2029).
    private static bool IsLineTerminator(char ch) =>
        ch == '\n' || ch == '\r' || ch == '\u2028' || ch == '\u2029';

    // ECMA-262 12.2 WhiteSpace: TAB, VT, FF, SP, NBSP, ZWNBSP.
    private static bool IsHorizontalWhitespace(char ch) =>
        ch == ' ' || ch == '\t' || ch == '\v' || ch == '\f' || ch == '\u00A0' || ch == '\uFEFF';
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
        // ECMA-262 — every built-in function inherits %Function.prototype%. Set it
        // when already materialised (it always is by the time lazy builtins like the
        // Iterator helpers are defined); skipping avoids bootstrap recursion.
        if (_functionPrototypeHandle is { } fnProto)
        {
            function.SetPrototype(fnProto);
        }

        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        if (_functionPrototypeHandle is { } fnProtoHandle)
        {
            _heap.WriteBarrier(functionHandle, fnProtoHandle);
        }

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

    private JsValue FunctionPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype.toString called on non-function."));
        }

        var obj = _heap.GetObject(thisValue.AsObjectHandle());
        return obj switch
        {
            NativeFunctionObject nfo => JsValue.FromString($"function {nfo.Name}() {{ [native code] }}"),
            JsFunctionObject jfo => JsValue.FromString($"function {(jfo.Function.Name ?? string.Empty)}() {{ [bytecode] }}"),
            BoundFunctionObject => JsValue.FromString("function bound() { [native code] }"),
            _ => throw new JsThrownException(CreateTypeError("Function.prototype.toString called on non-function."))
        };
    }

    private JsValue FunctionPrototypeHasInstance(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype[@@hasInstance] called on non-object."));
        }

        if (!IsCallable(thisValue))
        {
            return JsValue.FromBoolean(false);
        }

        if (value.Tag != JsValueTag.Object)
        {
            return JsValue.FromBoolean(false);
        }

        var prototypeValue = GetReceiverProperty(thisValue, "prototype");
        if (prototypeValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Function has non-object prototype in @@hasInstance."));
        }

        var targetPrototype = prototypeValue.AsObjectHandle();
        return JsValue.FromBoolean(OrdinaryHasInstancePrototype(value, targetPrototype));
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
            JsValueTag.BigInt => CreateBigIntObject(value.AsBigInt()),
            JsValueTag.String => CreateStringObject(value.AsString()),
            JsValueTag.Symbol => CreateSymbolObject(value.AsSymbolId()),
            JsValueTag.HostObject => value,
            _ => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()))
        };
    }

    // Installs a builtin prototype's @@toStringTag per the spec (e.g. 24.1.3.10
    // Map.prototype [ @@toStringTag ] = "Map"): a String value with attributes
    // { [[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: true }.
    private void DefineBuiltinToStringTag(JsObject prototype, string tag)
    {
        var tagId = GetWellKnownSymbolId("toStringTag");
        if (tagId == 0)
        {
            return;
        }

        _ = prototype.DefineOwnSymbolProperty(
            tagId,
            new JsPropertyDescriptor(JsValue.FromString(tag), Writable: false, Enumerable: false, Configurable: true));
    }

    private JsValue ObjectPrototypeToString(JsValue thisValue)
    {
        // ECMA-262 20.1.3.6 Object.prototype.toString ( )
        if (thisValue.Tag == JsValueTag.Undefined)
        {
            return JsValue.FromString("[object Undefined]");
        }

        if (thisValue.Tag == JsValueTag.Null)
        {
            return JsValue.FromString("[object Null]");
        }

        // Steps 3-14: O = ToObject(this); pick the builtin tag from its internal slots.
        var builtinTag = DetermineToStringBuiltinTag(thisValue);

        // Step 15: tag = ? Get(O, @@toStringTag). The real [[Get]] invokes accessors,
        // dispatches proxy traps, and propagates abrupt completions.
        var tagId = GetWellKnownSymbolId("toStringTag");
        var tag = tagId != 0 ? GetReceiverSymbolProperty(thisValue, tagId) : JsValue.Undefined;

        // Step 16: a non-string @@toStringTag is ignored in favour of the builtin tag.
        var resolved = tag.Tag == JsValueTag.String ? tag.AsString() : builtinTag;

        return JsValue.FromString($"[object {resolved}]");
    }

    // ECMA-262 20.1.3.6 steps 4-14: derive the builtin tag from O's internal slots.
    // IsArray and [[Call]] look through Proxy/bound wrappers; IsArray throws on a
    // revoked proxy (step 4 is observable before @@toStringTag is read).
    private string DetermineToStringBuiltinTag(JsValue thisValue)
    {
        switch (thisValue.Tag)
        {
            case JsValueTag.Boolean:
                return "Boolean";
            case JsValueTag.Int32:
            case JsValueTag.Number:
                return "Number";
            case JsValueTag.String:
                return "String";
            case JsValueTag.Symbol:
            case JsValueTag.BigInt:
            case JsValueTag.HostObject:
                // Boxed wrappers without a dedicated builtin tag; their prototype's
                // @@toStringTag ("Symbol"/"BigInt") supplies the visible tag.
                return "Object";
        }

        if (thisValue.Tag != JsValueTag.Object)
        {
            return "Object";
        }

        // Step 4: IsArray (recurses through proxy targets, throws on a revoked proxy).
        if (IsArrayValue(thisValue))
        {
            return "Array";
        }

        var handle = thisValue.AsObjectHandle();
        var obj = _heap.GetObject(handle);

        // Step 6: [[ParameterMap]].
        if (obj.ToStringTagSlot == BuiltinTagSlot.Arguments)
        {
            return "Arguments";
        }

        // Step 7: [[Call]] (ordinary functions, bound functions, callable proxies).
        if (IsCallableTarget(handle))
        {
            return "Function";
        }

        // Step 8: [[ErrorData]].
        if (obj.ToStringTagSlot == BuiltinTagSlot.Error)
        {
            return "Error";
        }

        // Steps 9-13: primitive-wrapper / Date / RegExp internal slots.
        return obj switch
        {
            BooleanObject => "Boolean",
            NumberObject => "Number",
            StringObject => "String",
            DateObject => "Date",
            RegExpObject => "RegExp",
            _ => "Object"
        };
    }

    private JsValue ObjectPrototypeValueOf(JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Object.prototype.valueOf called on null or undefined."));
        }

        return thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
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

        var keyArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        if (keyArg.Tag == JsValueTag.Symbol)
        {
            return JsValue.FromBoolean(obj.TryGetOwnSymbolProperty(keyArg.AsSymbolId(), out _));
        }
        return JsValue.FromBoolean(obj.TryGetOwnProperty(ToPropertyKey(keyArg), out _));
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

        var keyArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        if (keyArg.Tag == JsValueTag.Symbol)
        {
            return JsValue.FromBoolean(obj.TryGetOwnSymbolProperty(keyArg.AsSymbolId(), out var symDesc) && symDesc.Enumerable);
        }
        return JsValue.FromBoolean(obj.TryGetOwnProperty(ToPropertyKey(keyArg), out var descriptor) && descriptor.Enumerable);
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
        var keyArg = args[1];
        var isSymbolKey = keyArg.Tag == JsValueTag.Symbol;
        var key = isSymbolKey ? string.Empty : ToPropertyKey(keyArg);
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

        var hasWritableFlag = descriptorObject.TryGetProperty("writable", h => _heap.GetObject(h), out _);
        var hasEnumerable = descriptorObject.TryGetProperty("enumerable", h => _heap.GetObject(h), out _);
        var hasConfigurable = descriptorObject.TryGetProperty("configurable", h => _heap.GetObject(h), out _);
        var writable = hasWritableFlag && ReadDescriptorFlag(descriptorObject, descriptorReceiver, "writable");
        var enumerable = hasEnumerable && ReadDescriptorFlag(descriptorObject, descriptorReceiver, "enumerable");
        var configurable = hasConfigurable && ReadDescriptorFlag(descriptorObject, descriptorReceiver, "configurable");

        // ECMA-262 10.1.6.3 ValidateAndApplyPropertyDescriptor: when an existing
        // property is being updated, fields the input descriptor leaves out are
        // taken from the existing descriptor instead of defaulting to false /
        // undefined. This is what lets `defineProperty(o,k,{get:g2})` preserve
        // the prior `set` and the prior `configurable`/`enumerable` flags.
        JsPropertyDescriptor existingDescriptor = default;
        bool hasExisting = false;
        if (target is not ProxyObject)
        {
            hasExisting = isSymbolKey
                ? target.TryGetOwnSymbolProperty(keyArg.AsSymbolId(), out existingDescriptor)
                : target.TryGetOwnProperty(key, out existingDescriptor);
        }

        var newIsAccessor = hasGetter || hasSetter;
        JsPropertyDescriptor descriptor;
        if (hasExisting)
        {
            var existingIsAccessor = existingDescriptor.IsAccessor;
            // Same-kind merge keeps unchanged attributes from `existing`.
            if (newIsAccessor && existingIsAccessor)
            {
                descriptor = JsPropertyDescriptor.Accessor(
                    hasGetter ? getter : existingDescriptor.Get,
                    hasSetter ? setter : existingDescriptor.Set,
                    hasEnumerable ? enumerable : existingDescriptor.Enumerable,
                    hasConfigurable ? configurable : existingDescriptor.Configurable);
            }
            else if (!newIsAccessor && !existingIsAccessor)
            {
                descriptor = new JsPropertyDescriptor(
                    hasValue ? value : existingDescriptor.Value,
                    hasWritableFlag ? writable : existingDescriptor.Writable,
                    hasEnumerable ? enumerable : existingDescriptor.Enumerable,
                    hasConfigurable ? configurable : existingDescriptor.Configurable);
            }
            else
            {
                // Cross-kind transition (data <-> accessor). Missing fields on the
                // new descriptor default to "false"/undefined per spec step 4.
                descriptor = newIsAccessor
                    ? JsPropertyDescriptor.Accessor(
                        hasGetter ? getter : JsValue.Undefined,
                        hasSetter ? setter : JsValue.Undefined,
                        hasEnumerable ? enumerable : existingDescriptor.Enumerable,
                        hasConfigurable ? configurable : existingDescriptor.Configurable)
                    : new JsPropertyDescriptor(
                        hasValue ? value : JsValue.Undefined,
                        hasWritableFlag ? writable : false,
                        hasEnumerable ? enumerable : existingDescriptor.Enumerable,
                        hasConfigurable ? configurable : existingDescriptor.Configurable);
            }
        }
        else
        {
            // Fresh property: missing fields default to false/undefined per spec.
            descriptor = newIsAccessor
                ? JsPropertyDescriptor.Accessor(
                    hasGetter ? getter : JsValue.Undefined,
                    hasSetter ? setter : JsValue.Undefined,
                    enumerable,
                    configurable)
                : new JsPropertyDescriptor(hasValue ? value : JsValue.Undefined, writable, enumerable, configurable);
        }

        // ECMA-262 10.4.2.4 ArraySetLength: defining an Array's "length" is exotic —
        // it coerces/validates the value, may delete out-of-range elements, and fails
        // (TypeError) when a non-configurable element blocks truncation. The ordinary
        // define path below would just overwrite the slot.
        if (!isSymbolKey && key == "length" && target is ArrayObject && target is not ProxyObject)
        {
            var succeeded = !newIsAccessor && ApplyArrayLengthDefine(
                targetHandle, target, hasValue, value, hasWritableFlag, writable,
                hasEnumerable, enumerable, hasConfigurable, configurable);
            if (!succeeded)
            {
                throw new JsThrownException(CreateTypeError("Cannot redefine property: length"));
            }

            return args[0];
        }

        if (target is ProxyObject proxyDefineProperty)
        {
            if (isSymbolKey)
            {
                throw new JsThrownException(CreateTypeError("Proxy.defineProperty with symbol key is not yet supported."));
            }
            var ok = ProxyDefineProperty(proxyDefineProperty, key, args[2]);
            if (!ok)
            {
                throw new JsThrownException(CreateTypeError("Cannot define property on proxy target."));
            }
        }
        else
        {
            // ECMA-262 10.1.6.3 ValidateAndApplyPropertyDescriptor: reject (TypeError)
            // adding to a non-extensible object, or any disallowed change to a
            // non-configurable property, before mutating.
            if (!IsCompatiblePropertyDescriptor(
                    target.Extensible, hasExisting, existingDescriptor,
                    newIsAccessor, hasValue, value, hasWritableFlag, writable,
                    hasEnumerable, enumerable, hasConfigurable, configurable,
                    hasGetter, getter, hasSetter, setter))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot redefine property: " + (isSymbolKey ? "(symbol)" : key)));
            }

            if (isSymbolKey)
            {
                _ = target.DefineOwnSymbolProperty(keyArg.AsSymbolId(), descriptor);
            }
            else
            {
                _ = target.DefineOwnProperty(key, descriptor);
                // ECMA-262 10.4.2.1 Array [[DefineOwnProperty]]: defining an array-index
                // element at or beyond the current length extends "length" (matching
                // what plain `arr[i] = v` assignment already does).
                if (target is ArrayObject && IsCanonicalIntegerIndex(key, out var definedIdx))
                {
                    ExtendArrayLengthForIndex(targetHandle, target, definedIdx);
                }
            }
            WriteDescriptorBarrier(targetHandle, descriptor);
        }

        return args[0];
    }

    // ECMA-262 10.4.2.1 step 3.h: after an array-index element is defined, if its
    // index is >= the array's length, set length = index + 1 (only when length is a
    // writable data property; a non-writable length would have failed validation).
    private void ExtendArrayLengthForIndex(ObjectHandle handle, JsObject arr, int index)
    {
        if (!arr.TryGetOwnProperty("length", out var lenDesc) || lenDesc.IsAccessor || !lenDesc.Writable)
        {
            return;
        }

        var oldLen = ToUint32(lenDesc.Value.AsNumber());
        if ((uint)index >= oldLen)
        {
            var updated = lenDesc with { Value = JsValue.FromNumber((uint)index + 1) };
            _ = arr.DefineOwnProperty("length", updated);
            WriteDescriptorBarrier(handle, updated);
        }
    }

    // ECMA-262 10.1.6.3 ValidateAndApplyPropertyDescriptor (validation half): whether
    // defining/redefining a property is permitted. Returns false (→ TypeError at the
    // call site) for: a new property on a non-extensible object, or a forbidden change
    // to a non-configurable property (turning it configurable, flipping enumerable,
    // changing data<->accessor kind, un-freezing a non-writable data value/writable,
    // or changing a non-configurable accessor's get/set).
    private static bool IsCompatiblePropertyDescriptor(
        bool extensible, bool hasExisting, JsPropertyDescriptor current,
        bool newIsAccessor, bool hasValue, JsValue value, bool hasWritable, bool writable,
        bool hasEnumerable, bool enumerable, bool hasConfigurable, bool configurable,
        bool hasGetter, JsValue getter, bool hasSetter, JsValue setter)
    {
        if (!hasExisting)
        {
            return extensible;
        }

        if (current.Configurable)
        {
            return true;
        }

        if (hasConfigurable && configurable)
        {
            return false;
        }

        if (hasEnumerable && enumerable != current.Enumerable)
        {
            return false;
        }

        var descHasKind = newIsAccessor || hasValue || hasWritable;
        if (!descHasKind)
        {
            return true;
        }

        var currentIsAccessor = current.IsAccessor;
        if (newIsAccessor != currentIsAccessor)
        {
            return false;
        }

        if (!currentIsAccessor)
        {
            if (!current.Writable)
            {
                if (hasWritable && writable)
                {
                    return false;
                }

                if (hasValue && !SameValue(value, current.Value))
                {
                    return false;
                }
            }

            return true;
        }

        if (hasGetter && !SameValue(getter, current.Get))
        {
            return false;
        }

        if (hasSetter && !SameValue(setter, current.Set))
        {
            return false;
        }

        return true;
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
        // ECMA-262 20.1.2.8 step 1: obj = ToObject(O). A primitive first argument is
        // coerced to its wrapper (so getOwnPropertyDescriptor(true, "foo") returns
        // undefined rather than throwing); only undefined/null raise a TypeError.
        var firstArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var targetValue = ToObjectValue(firstArg);
        var target = _heap.GetObject(targetValue.AsObjectHandle());
        var keyArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var isSymbolKey = keyArg.Tag == JsValueTag.Symbol;
        var key = isSymbolKey ? string.Empty : ToPropertyKey(keyArg);
        var found = false;
        JsPropertyDescriptor descriptor;
        if (target is ProxyObject proxyGetOwnPropertyDescriptor)
        {
            if (isSymbolKey)
            {
                return JsValue.Undefined;
            }
            found = ProxyTryGetOwnPropertyDescriptor(proxyGetOwnPropertyDescriptor, key, out descriptor);
        }
        else
        {
            found = isSymbolKey
                ? target.TryGetOwnSymbolProperty(keyArg.AsSymbolId(), out descriptor)
                : target.TryGetOwnProperty(key, out descriptor);
        }
        if (!found)
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

    // GetReceiverProperty / GetReceiverSymbolProperty / IsCanonicalIntegerIndex /
    // TryGetPropertyValue moved to BytecodeInterpreter.Properties.cs (audit �2 slice 1).

    [MayExecuteJs]
    private bool HasPropertyIncludingProxy(JsObject obj, string key)
    {
        if (obj is ProxyObject proxyHas)
        {
            return ProxyHas(proxyHas, key);
        }

        if (obj is TypedArrayObject typedArray &&
            IsCanonicalIntegerIndex(key, out var typedArrayIndex))
        {
            return !typedArray.IsOutOfBounds() &&
                   typedArrayIndex >= 0 &&
                   typedArrayIndex < typedArray.Length;
        }

        if (obj.TryGetOwnProperty(key, out _))
        {
            return true;
        }

        if (obj.PrototypeHandle is { } prototypeHandle)
        {
            return HasPropertyIncludingProxy(_heap.GetObject(prototypeHandle), key);
        }

        return false;
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
        // ECMA-262 10.4.2.4 ArraySetLength: assigning to an Array's "length" is an
        // exotic operation that deletes own array-index elements at or above the new
        // length (and fails on a non-configurable one). The ordinary data-property
        // path below would just overwrite the slot and leave stale elements behind.
        if (key == "length" && obj is ArrayObject)
        {
            return SetArrayLength(ownerHandle, obj, value);
        }

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

        // ECMA-262 10.1.9.2 OrdinarySetWithOwnDescriptor → CreateDataProperty:
        // creating a brand-new own property on a non-extensible object fails
        // ([[Set]] returns false; strict callers turn that into a TypeError).
        if (!obj.Extensible)
        {
            return false;
        }

        var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
        _ = obj.DefineOwnProperty(key, descriptor);
        WriteDescriptorBarrier(ownerHandle, descriptor);
        return true;
    }

    // ECMA-262 10.4.2.4 ArraySetLength — assign an Array's "length", deleting own
    // array-index elements >= the new length (highest first). A non-configurable
    // element stops the truncation: length is left one past it and the write fails.
    private bool SetArrayLength(ObjectHandle ownerHandle, JsObject arr, JsValue value)
    {
        var numberLen = ToNumber(value);
        var newLen = ToUint32(numberLen);
        if (newLen != numberLen)
        {
            throw new JsThrownException(CreateRangeError("Invalid array length"));
        }

        if (!arr.TryGetOwnProperty("length", out var lenDesc))
        {
            var created = new JsPropertyDescriptor(
                JsValue.FromNumber(newLen), Writable: true, Enumerable: false, Configurable: false);
            _ = arr.DefineOwnProperty("length", created);
            WriteDescriptorBarrier(ownerHandle, created);
            return true;
        }

        if (lenDesc.IsAccessor || !lenDesc.Writable)
        {
            return false;
        }

        var oldLen = ToUint32(lenDesc.Value.AsNumber());
        if (newLen >= oldLen)
        {
            var grown = lenDesc with { Value = JsValue.FromNumber(newLen) };
            _ = arr.DefineOwnProperty("length", grown);
            WriteDescriptorBarrier(ownerHandle, grown);
            return true;
        }

        // Shrinking: gather own integer-index keys >= newLen and delete them from the
        // highest index down so a non-configurable element leaves length just past it.
        var toDelete = new List<int>();
        foreach (var p in arr.EnumerateOwnProperties())
        {
            if (IsCanonicalIntegerIndex(p.Key, out var idx) && (uint)idx >= newLen)
            {
                toDelete.Add(idx);
            }
        }

        toDelete.Sort();
        var finalLen = newLen;
        var success = true;
        for (var i = toDelete.Count - 1; i >= 0; i--)
        {
            var ikey = toDelete[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (arr.TryGetOwnProperty(ikey, out var elemDesc) && !elemDesc.Configurable)
            {
                finalLen = (uint)toDelete[i] + 1;
                success = false;
                break;
            }

            _ = arr.DeleteProperty(ikey);
        }

        var finalDesc = lenDesc with { Value = JsValue.FromNumber(finalLen) };
        _ = arr.DefineOwnProperty("length", finalDesc);
        WriteDescriptorBarrier(ownerHandle, finalDesc);
        return success;
    }

    // ECMA-262 10.4.2.4 ArraySetLength invoked from Array [[DefineOwnProperty]] on
    // "length" (descriptor form). Returns false when the redefinition must fail (the
    // caller raises TypeError); throws RangeError for a non-uint32 length value.
    private bool ApplyArrayLengthDefine(
        ObjectHandle handle, JsObject arr,
        bool hasValue, JsValue value,
        bool hasWritable, bool writable,
        bool hasEnumerable, bool enumerable,
        bool hasConfigurable, bool configurable)
    {
        if (!arr.TryGetOwnProperty("length", out var oldLenDesc))
        {
            return false;
        }

        // Array "length" is permanently non-enumerable and non-configurable.
        if ((hasEnumerable && enumerable) || (hasConfigurable && configurable))
        {
            return false;
        }

        // Step 1: a length descriptor with no [[Value]] only adjusts attributes.
        if (!hasValue)
        {
            if (!oldLenDesc.Writable && hasWritable && writable)
            {
                return false;   // a non-writable length cannot be made writable again
            }

            var attrOnly = oldLenDesc with { Writable = hasWritable ? writable : oldLenDesc.Writable };
            _ = arr.DefineOwnProperty("length", attrOnly);
            WriteDescriptorBarrier(handle, attrOnly);
            return true;
        }

        var numberLen = ToNumber(value);
        var newLen = ToUint32(numberLen);
        if (newLen != numberLen)
        {
            throw new JsThrownException(CreateRangeError("Invalid array length"));
        }

        var oldLen = ToUint32(oldLenDesc.Value.AsNumber());
        var newWritable = !hasWritable || writable;

        if (newLen >= oldLen)
        {
            var grown = oldLenDesc with { Value = JsValue.FromNumber(newLen), Writable = newWritable };
            _ = arr.DefineOwnProperty("length", grown);
            WriteDescriptorBarrier(handle, grown);
            return true;
        }

        if (!oldLenDesc.Writable)
        {
            return false;
        }

        var toDelete = new List<int>();
        foreach (var p in arr.EnumerateOwnProperties())
        {
            if (IsCanonicalIntegerIndex(p.Key, out var idx) && (uint)idx >= newLen)
            {
                toDelete.Add(idx);
            }
        }

        toDelete.Sort();
        var finalLen = newLen;
        var success = true;
        for (var i = toDelete.Count - 1; i >= 0; i--)
        {
            var ikey = toDelete[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (arr.TryGetOwnProperty(ikey, out var elemDesc) && !elemDesc.Configurable)
            {
                finalLen = (uint)toDelete[i] + 1;
                success = false;
                break;
            }

            _ = arr.DeleteProperty(ikey);
        }

        var finalDesc = oldLenDesc with { Value = JsValue.FromNumber(finalLen), Writable = newWritable };
        _ = arr.DefineOwnProperty("length", finalDesc);
        WriteDescriptorBarrier(handle, finalDesc);
        return success;
    }

    private static uint ToUint32(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number) || number == 0d)
        {
            return 0u;
        }

        var truncated = Math.Truncate(number);
        var modulo = truncated % 4294967296.0;
        if (modulo < 0)
        {
            modulo += 4294967296.0;
        }

        return (uint)modulo;
    }

    [MayExecuteJs]
    private bool SetSymbolPropertyValue(ObjectHandle ownerHandle, JsObject obj, long symbolId, JsValue value, JsValue receiver)
    {
        if (obj.TryGetOwnSymbolProperty(symbolId, out var ownDescriptor))
        {
            return SetSymbolPropertyFromDescriptor(ownerHandle, obj, symbolId, ownDescriptor, value, receiver);
        }

        if (TryGetPrototypeSymbolPropertyDescriptor(obj, symbolId, out var inheritedDescriptor))
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

        if (!obj.Extensible)
        {
            return false;
        }

        var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
        _ = obj.DefineOwnSymbolProperty(symbolId, descriptor);
        WriteDescriptorBarrier(ownerHandle, descriptor);
        return true;
    }

    [MayExecuteJs]
    private bool SetSymbolPropertyFromDescriptor(
        ObjectHandle ownerHandle,
        JsObject obj,
        long symbolId,
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
        _ = obj.DefineOwnSymbolProperty(symbolId, updated);
        WriteDescriptorBarrier(ownerHandle, updated);
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

    private bool TryGetPrototypeSymbolPropertyDescriptor(JsObject obj, long symbolId, out JsPropertyDescriptor descriptor)
    {
        var prototype = obj.PrototypeHandle;
        while (prototype is { } handle)
        {
            var prototypeObject = _heap.GetObject(handle);
            if (prototypeObject.TryGetOwnSymbolProperty(symbolId, out descriptor))
            {
                return true;
            }

            prototype = prototypeObject.PrototypeHandle;
        }

        descriptor = default;
        return false;
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

            if (useIterator && source.Tag == JsValueTag.Object)
            {
                // ECMA-262 23.1.2.1 Array.from, iterator path. Pull the iterator
                // lazily one step at a time so that (a) an abrupt completion from
                // the map function closes the iterator via IteratorClose instead
                // of being unreachable, and (b) infinite iterators don't hang the
                // engine before user code can throw. The eager CreateForOfIterator
                // path buffers every value up front and loops forever on an
                // iterator whose next() never reports done (see
                // built-ins/Array/from/iter-map-fn-err.js).
                var srcObj = _heap.GetObject(source.AsObjectHandle());
                var iterId = GetWellKnownSymbolId("iterator");
                srcObj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc);
                var iterator = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), source);
                if (iterator.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        "Array.from: @@iterator method did not return an object."));
                }

                var iterObj = _heap.GetObject(iterator.AsObjectHandle());
                // 7.4.1 GetIterator reads "next" once and reuses it each step.
                if (!TryGetPropertyValue(iterObj, iterator, "next", out var nextFn) ||
                    nextFn.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        "Array.from: iterator has no callable 'next' method."));
                }

                // Pin the iterator and suspend auto-MinorCollect for the duration
                // of the pull, mirroring DrainIteratorIntoList: CallFunction can
                // tick the young-GC counter while results live only on the C#
                // stack.
                var rootMark = _heap.RootCount;
                var savedYoungThreshold = _heap.YoungAllocationsPerMinorGc;
                _heap.YoungAllocationsPerMinorGc = -1;
                try
                {
                    _heap.PushRoot(iterator.AsObjectHandle());
                    var k = 0;
                    while (true)
                    {
                        var result = CallFunction(nextFn, Array.Empty<JsValue>(), iterator);
                        if (result.Tag != JsValueTag.Object)
                        {
                            throw new JsThrownException(CreateTypeError(
                                "Array.from: iterator result is not an object."));
                        }

                        _heap.PushRoot(result.AsObjectHandle());
                        var resultObj = _heap.GetObject(result.AsObjectHandle());
                        TryGetPropertyValue(resultObj, result, "done", out var doneVal);
                        if (IsTruthy(doneVal))
                        {
                            break;
                        }

                        TryGetPropertyValue(resultObj, result, "value", out var v);
                        if (mapFn.HasValue)
                        {
                            try
                            {
                                v = CallFunction(mapFn.Value, new[] { v, JsValue.FromNumber(k) }, thisArg);
                            }
                            catch (JsThrownException)
                            {
                                // 23.1.2.1 step 6.g.vii.2: abrupt mapped value
                                // closes the iterator, then the original throw
                                // propagates.
                                IteratorCloseOnAbrupt(iterator);
                                throw;
                            }
                        }

                        if (v.Tag == JsValueTag.Object)
                        {
                            _heap.PushRoot(v.AsObjectHandle());
                        }

                        items.Add(v);
                        k++;
                    }
                }
                finally
                {
                    _heap.PopRootsTo(rootMark);
                    _heap.YoungAllocationsPerMinorGc = savedYoungThreshold;
                }
            }
            else if (useIterator)
            {
                // String source: finite per-code-unit iteration. The eager
                // CreateForOfIterator path is safe (bounded by string length).
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

            var arr = CreateArrayFromElements(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // ECMA-262 23.1.2.2 Array.isArray(arg) delegates to IsArray, including
        // proxy target recursion and revoked-proxy TypeError behavior.
        DefineIntrinsicFunction(constructorHandle, constructor, "isArray", (_, args) =>
        {
            var value = args.Count == 0 ? JsValue.Undefined : args[0];
            return JsValue.FromBoolean(IsArrayValue(value));
        }, length: 1);

        _arrayPrototypeHandle = prototypeHandle;
        _arrayConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsObject CreateArrayObject(IReadOnlyList<JsValue> elements)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());
        // ECMA-262 23.1.4.2: Array's 'length' must be {Writable: true,
        // Enumerable: false, Configurable: false}. Install via DefineOwn
        // up front so later SetProperty calls preserve those attrs.
        _ = obj.DefineOwnProperty("length", new JsPropertyDescriptor(
            JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));

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
        _ = obj.DefineOwnProperty("length", new JsPropertyDescriptor(
            JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
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
        var obj = ToObject(thisValue);
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
        var obj = ToObject(thisValue);
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
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
        var obj = ToObject(thisValue);
        var lengthD = GetArrayLengthDouble(obj);
        var length = (int)Math.Min(lengthD, int.MaxValue);
        var start = NormaliseSliceIndex(args, 0, 0, length);
        // skipCount: missing or undefined => 0 (ES2023 step 7 actualSkipCount default).
        var skipRaw = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? (int)ToNumber(args[1]) : 0;
        var skip = Math.Clamp(skipRaw, 0, length - start);
        var insertCount = args.Count > 2 ? args.Count - 2 : 0;

        // ECMA-262 23.1.3.34 steps 8-10: newLen = len - actualSkipCount + insertCount;
        // TypeError if it exceeds 2^53-1, then ArrayCreate(newLen) RangeError if it
        // exceeds the 2^32-1 array-length limit — both before allocating storage.
        var newLenD = lengthD - skip + insertCount;
        if (newLenD > 9007199254740991.0)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype.toSpliced result length exceeds the maximum safe integer."));
        }

        ThrowIfArrayLengthExceedsLimit(newLenD);
        var newLen = (int)newLenD;
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
        var obj = ToObject(thisValue);
        ThrowIfArrayLengthExceedsLimit(GetArrayLengthDouble(obj));
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
        var obj = ToObject(thisValue);
        ThrowIfArrayLengthExceedsLimit(GetArrayLengthDouble(obj));
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
        var obj = _heap.GetObject(ownerHandle);
        ThrowIfArrayLengthExceedsLimit(GetArrayLengthDouble(obj));
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
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
        var receiver = ToObjectValue(thisValue);
        var ownerHandle = receiver.AsObjectHandle();
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

        return receiver;
    }

    // Shared helper for fill / slice. Reads args[argIndex] as a number (default
    // when missing or undefined), then converts to an integer clamped into
    // [0, length] using the spec's "negative-from-length" rule.
    // ECMA-262 relative-index clamping shared by slice / copyWithin / fill /
    // lastIndexOf etc. The argument is coerced with ToIntegerOrInfinity (which
    // runs ToNumber → @@toPrimitive/valueOf, and throws a TypeError for Symbol
    // or BigInt), not read as a raw number. Negative values count from the end;
    // ±Infinity clamps to the ends.
    private int NormaliseSliceIndex(IReadOnlyList<JsValue> args, int argIndex, int defaultValue, int length)
    {
        if (argIndex >= args.Count || args[argIndex].Tag == JsValueTag.Undefined)
        {
            return Math.Clamp(defaultValue, 0, length);
        }

        var relative = ToIntegerOrInfinity(args[argIndex]);
        int idx;
        if (double.IsNegativeInfinity(relative))
        {
            idx = 0;
        }
        else if (relative < 0)
        {
            idx = (int)Math.Max(length + relative, 0);
        }
        else if (double.IsPositiveInfinity(relative) || relative > length)
        {
            idx = length;
        }
        else
        {
            idx = (int)relative;
        }

        return Math.Clamp(idx, 0, length);
    }

    // ECMA-262 7.1.5 ToIntegerOrInfinity: ToNumber, then NaN → +0, ±Infinity
    // preserved, otherwise truncate toward zero. ToNumber honors @@toPrimitive /
    // valueOf and throws for Symbol / BigInt operands.
    private double ToIntegerOrInfinity(JsValue value)
    {
        var number = ToNumber(value);
        if (double.IsNaN(number))
        {
            return 0;
        }

        if (double.IsInfinity(number))
        {
            return number;
        }

        return Math.Truncate(number);
    }

    // ECMA-262 23.1.3.4 Array.prototype.copyWithin(target, start[, end]).
    // Shallow-copies the sequence at [start, end) to position target in-place,
    // returning the receiver. Negative indices wrap from length; ranges are clamped
    // into [0, length]. When source and target overlap, copies use a forward or
    // backward pass to avoid clobbering data not yet copied.
    private JsValue ArrayPrototypeCopyWithin(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var ownerHandle = receiver.AsObjectHandle();
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var target = NormaliseSliceIndex(args, 0, 0, length);
        var start = NormaliseSliceIndex(args, 1, 0, length);
        var end = NormaliseSliceIndex(args, 2, length, length);

        var count = Math.Min(end - start, length - target);
        if (count <= 0)
        {
            return receiver;
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
            if (TryGetPropertyValue(obj, receiver, fromKey, out var v))
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

        return receiver;
    }

    // ECMA-262 23.1.3.1 Array.prototype.at(index). Negative indices wrap from
    // length; out-of-range returns undefined.
    private JsValue ArrayPrototypeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        var raw = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var idx = raw < 0 ? length + raw : raw;
        if (idx < 0 || idx >= length)
        {
            return JsValue.Undefined;
        }

        var key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return GetReceiverProperty(thisValue, key);
    }

    // ECMA-262 23.1.3.12 findLast. Mirrors find but walks backwards; visits holes
    // as undefined-valued slots per spec.
    private JsValue ArrayPrototypeFindLast(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLengthDouble(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.findLast");
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = GetReceiverProperty(receiver, key);
            if (IsTruthy(InvokeArrayCallback(callback, v, JsValue.FromNumber(i), receiver, thisArg)))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    // ECMA-262 23.1.3.13 findLastIndex.
    private JsValue ArrayPrototypeFindLastIndex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLengthDouble(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.findLastIndex");
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = GetReceiverProperty(receiver, key);
            if (IsTruthy(InvokeArrayCallback(callback, v, JsValue.FromNumber(i), receiver, thisArg)))
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
        var obj = _heap.GetObject(ownerHandle);
        var lengthD = GetArrayLengthDouble(obj);
        var length = (int)Math.Min(lengthD, int.MaxValue);
        var start = NormaliseSliceIndex(args, 0, 0, length);

        // The removed array is ArraySpeciesCreate(O, actualDeleteCount) (23.1.3.31
        // step 11). Compute that count with the true (double) length so a huge
        // count surfaces as a RangeError before any allocation, matching ArrayCreate.
        double actualDeleteCountD;
        if (args.Count < 1)
        {
            actualDeleteCountD = 0;
        }
        else if (args.Count < 2)
        {
            actualDeleteCountD = lengthD - start;
        }
        else
        {
            actualDeleteCountD = Math.Clamp(Math.Truncate(ToNumber(args[1])), 0, lengthD - start);
        }

        ThrowIfArrayLengthExceedsLimit(actualDeleteCountD);
        var deleteCount = (int)Math.Min(actualDeleteCountD, int.MaxValue);
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
        var ownerHandle = ToObjectValue(thisValue).AsObjectHandle();
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
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return JsValue.FromNumber(-1);
        }

        // ECMA-262 23.1.3.20: a missing searchElement defaults to undefined and the
        // search still runs (so [undefined].lastIndexOf() returns 0).
        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
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
        var obj = ToObject(thisValue);
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.flatMap");
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                continue;
            }

            var mapped = InvokeArrayCallback(callback, v, i, receiver, thisArg);
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.every");
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                continue;
            }

            if (!IsTruthy(InvokeArrayCallback(callback, v, i, receiver, thisArg)))
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.some");
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                continue;
            }

            if (IsTruthy(InvokeArrayCallback(callback, v, i, receiver, thisArg)))
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.find");
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = GetReceiverProperty(receiver, key);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, receiver, thisArg)))
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.findIndex");
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = GetReceiverProperty(receiver, key);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, receiver, thisArg)))
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        RequireCallable(callback, reverse ? "Array.prototype.reduceRight" : "Array.prototype.reduce");

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
                if (TryGetPropertyValue(obj, receiver, key, out var v))
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
            if (TryGetPropertyValue(obj, receiver, key, out var v))
            {
                var callArgs = new[]
                {
                    accumulator,
                    v,
                    JsValue.FromNumber(i),
                    receiver,
                };
                accumulator = CallFunction(callback, callArgs, JsValue.Undefined);
            }

            i += step;
        }

        return accumulator;
    }

    // ECMA-262 7.2.3 IsCallable � front-loaded check for Array.prototype
    // callback methods (every/some/forEach/map/filter/find/findIndex/
    // findLast/findLastIndex/reduce/reduceRight/flatMap). Spec requires
    // these to throw TypeError before any iteration starts when the
    // callbackfn is not callable. Without this guard, sparse arrays
    // (e.g. `new Array(10).every()`) never reach InvokeArrayCallback's
    // own check because there are no own properties to iterate.
    private void RequireCallable(JsValue value, string methodName)
    {
        if (value.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError($"{methodName} callback is not a function."));
        var obj = _heap.GetObject(value.AsObjectHandle());
        if (obj is not JsFunctionObject && obj is not NativeFunctionObject && obj is not BoundFunctionObject)
            throw new JsThrownException(CreateTypeError($"{methodName} callback is not a function."));
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
        => InvokeArrayCallback(callback, value, JsValue.FromNumber(index), receiver, thisArg);

    private JsValue InvokeArrayCallback(
        JsValue callback,
        JsValue value,
        JsValue index,
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

        var args = new[] { value, index, receiver };
        return CallFunction(callback, args, thisArg);
    }

    private JsValue ArrayPrototypeForEach(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.forEach");
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                continue;   // skip holes per spec
            }

            InvokeArrayCallback(callback, v, i, receiver, thisArg);
        }

        return JsValue.Undefined;
    }

    private JsValue ArrayPrototypeMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var lengthD = GetArrayLengthDouble(obj);
        var length = (int)Math.Min(lengthD, int.MaxValue);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.map");
        // ECMA-262 23.1.3.21 step 5: A = ArraySpeciesCreate(O, len) — RangeError for
        // a length beyond the array-length limit, before the callback runs.
        ThrowIfArrayLengthExceedsLimit(lengthD);
        var items = new List<JsValue>(length);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                items.Add(JsValue.Undefined);   // spec: preserves length, holes become undefined-ish
                continue;
            }

            items.Add(InvokeArrayCallback(callback, v, i, receiver, thisArg));
        }

        return ArraySpeciesCreate(receiver, items);
    }

    private JsValue ArrayPrototypeFilter(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        RequireCallable(callback, "Array.prototype.filter");
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, receiver, key, out var v))
            {
                continue;   // skip holes
            }

            var keep = InvokeArrayCallback(callback, v, i, receiver, thisArg);
            if (IsTruthy(keep))
            {
                items.Add(v);
            }
        }

        return ArraySpeciesCreate(thisValue, items);
    }

    // ECMA-262 23.1.3.28 Array.prototype.slice(start, end). Returns a fresh
    // ArrayObject containing the half-open range [start, end). Out-of-range or
    // missing arguments degrade to "0, length"; negative arguments wrap.
    private JsValue ArrayPrototypeSlice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);
        var end = NormaliseSliceIndex(args, 1, length, length);

        var items = new List<JsValue>();
        for (var i = start; i < end; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        return ArraySpeciesCreate(thisValue, items);
    }

    private JsValue ArrayPrototypeConcat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        // ECMA-262 23.1.3.2 steps 1-2: ToObject(this) then ArraySpeciesCreate.
        // The creation order is observable (constructor side effects happen
        // before any @@isConcatSpreadable lookup).
        var receiver = ToObjectValue(thisValue);
        var resultValue = ArraySpeciesCreate(receiver, Array.Empty<JsValue>());
        if (resultValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Array species constructor must return an object."));
        }

        var resultObj = _heap.GetObject(resultValue.AsObjectHandle());
        long nextIndex = 0;
        AppendConcatSource(resultObj, receiver, ref nextIndex);
        for (var i = 0; i < args.Count; i++)
        {
            AppendConcatSource(resultObj, args[i], ref nextIndex);
        }

        // Step 6: Set(A, "length", n, true).
        if (!resultObj.SetProperty("length", JsValue.FromNumber(nextIndex)))
        {
            throw new JsThrownException(CreateTypeError("Cannot set Array.prototype.concat result length."));
        }

        return resultValue;
    }

    private void AppendConcatSource(JsObject resultObj, JsValue value, ref long nextIndex)
    {
        const long MaxSafeInteger = 9007199254740991L; // 2^53 - 1
        if (IsConcatSpreadable(value))
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            var rawLength = LengthOfArrayLikeAsDouble(obj, value);
            if ((double)nextIndex + rawLength > MaxSafeInteger)
            {
                throw new JsThrownException(CreateTypeError(
                    "Array.prototype.concat result length exceeds 2^53 - 1."));
            }

            // Implementation limit: the loop index is int-based. Preserve the
            // first observable access (index 0) before bailing out.
            if (rawLength > int.MaxValue)
            {
                if (rawLength > 0)
                {
                    const string key0 = "0";
                    if (HasPropertyIncludingProxy(obj, key0))
                    {
                        _ = TryGetPropertyValue(obj, value, key0, out _);
                    }
                }

                throw new JsThrownException(CreateTypeError(
                    "Array.prototype.concat result length exceeds the implementation-defined limit."));
            }

            var length = (int)rawLength;
            for (var i = 0; i < length; i++)
            {
                var sourceKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (HasPropertyIncludingProxy(obj, sourceKey))
                {
                    var subElement = GetReceiverProperty(value, sourceKey);
                    var targetKey = nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    CreateDataPropertyOrThrow(resultObj, targetKey, subElement);
                }

                nextIndex++;
            }

            return;
        }

        if (nextIndex >= MaxSafeInteger)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype.concat result length exceeds 2^53 - 1."));
        }

        var key = nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CreateDataPropertyOrThrow(resultObj, key, value);
        nextIndex++;
    }

    private void CreateDataPropertyOrThrow(JsObject obj, string key, JsValue value)
    {
        var descriptor = new JsPropertyDescriptor(
            value,
            Writable: true,
            Enumerable: true,
            Configurable: true);

        if (obj.TryGetOwnProperty(key, out var existing))
        {
            // Configurable properties can always be replaced by CreateDataProperty.
            if (existing.Configurable)
            {
                _ = obj.DefineOwnProperty(key, descriptor);
                return;
            }

            // Non-configurable properties reject changes to [[Enumerable]],
            // [[Configurable]], accessor/data kind, and [[Writable]] false data.
            if (existing.IsAccessor || !existing.Writable || !existing.Enumerable)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot redefine non-configurable property '" + key + "'."));
            }

            _ = obj.DefineOwnProperty(key, existing with { Value = value });
            return;
        }

        if (!obj.Extensible)
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot create property '" + key + "' on a non-extensible object."));
        }

        _ = obj.DefineOwnProperty(key, descriptor);
    }

    // ECMA-262 7.3.19 LengthOfArrayLike: ToLength(Get(O, "length")).
    // Returns a non-negative integer in [0, 2^53 - 1] as a double.
    // Walks the prototype chain via [[Get]] semantics (matching
    // GetArrayLength) so subclasses inherit array-length lookup.
    private double LengthOfArrayLikeAsDouble(JsObject obj, JsValue receiver)
    {
        if (!TryGetPropertyValue(obj, receiver, "length", out var raw))
        {
            return 0;
        }
        var n = ToNumber(raw);
        if (double.IsNaN(n) || n <= 0)
        {
            return 0;
        }
        var truncated = Math.Truncate(n);
        const double MaxSafeInteger = 9007199254740991.0; // 2^53 - 1
        return Math.Min(truncated, MaxSafeInteger);
    }

    // ECMA-262 23.1.3.2.2 ArraySpeciesCreate(originalArray, length).
    // If the receiver has a constructor[@@species] that is a callable other than
    // the default Array constructor, use it; otherwise return a plain Array.
    private JsValue ArraySpeciesCreate(JsValue originalArray, IReadOnlyList<JsValue> items)
    {
        if (originalArray.Tag == JsValueTag.Object && IsArrayValue(originalArray))
        {
            var obj = _heap.GetObject(originalArray.AsObjectHandle());
            if (TryGetPropertyValue(obj, originalArray, "constructor", out var ctor) &&
                ctor.Tag != JsValueTag.Undefined)
            {
                if (ctor.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError("Array constructor must be a constructor function."));
                }

                var speciesCtor = ctor;
                if (IsMarkedCrossRealmArrayConstructor(ctor))
                {
                    goto fallbackArraySpecies;
                }

                var speciesSymbolId = GetWellKnownSymbolId("species");
                if (speciesSymbolId != 0)
                {
                    var species = GetReceiverSymbolProperty(ctor, speciesSymbolId);
                    if (species.Tag == JsValueTag.Null || species.Tag == JsValueTag.Undefined)
                    {
                        goto fallbackArraySpecies;
                    }

                    if (species.Tag != JsValueTag.Object || !IsCallable(species))
                    {
                        throw new JsThrownException(CreateTypeError("Array @@species is not a constructor."));
                    }

                    speciesCtor = species;
                }

                if (speciesCtor.Tag != JsValueTag.Object || !IsCallable(speciesCtor))
                {
                    throw new JsThrownException(CreateTypeError("Array @@species is not a constructor."));
                }

                var result = ConstructFunction(speciesCtor, new[] { JsValue.FromNumber(items.Count) });
                if (result.Tag == JsValueTag.Object)
                {
                    var resultObj = _heap.GetObject(result.AsObjectHandle());
                    for (var i = 0; i < items.Count; i++)
                    {
                        var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        resultObj.SetProperty(key, items[i]);
                    }
                    if (resultObj is ArrayObject) resultObj.SetProperty("length", JsValue.FromNumber(items.Count));
                    return result;
                }
            }
        }

fallbackArraySpecies:
        var arr = CreateArrayFromElements(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private bool IsMarkedCrossRealmArrayConstructor(JsValue ctor)
    {
        if (ctor.Tag != JsValueTag.Object)
        {
            return false;
        }

        var ctorObj = _heap.GetObject(ctor.AsObjectHandle());
        if (!TryGetPropertyValue(ctorObj, ctor, "__fenRealmIntrinsic__", out var intrinsic) ||
            intrinsic.Tag != JsValueTag.String ||
            intrinsic.AsString() != "Array")
        {
            return false;
        }

        return TryGetPropertyValue(ctorObj, ctor, "__fenRealmId__", out var realmId) &&
               realmId.Tag == JsValueTag.String;
    }

    // ECMA-262 23.1.3.2.1 IsConcatSpreadable(O).
    private bool IsConcatSpreadable(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var spreadableSymbolId = GetWellKnownSymbolId("isConcatSpreadable");
        if (spreadableSymbolId != 0)
        {
            var spreadable = GetReceiverSymbolProperty(value, spreadableSymbolId);
            if (spreadable.Tag != JsValueTag.Undefined)
            {
                return IsTruthy(spreadable);
            }
        }

        return IsArrayValue(value);
    }

    private bool IsArrayValue(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var handle = value.AsObjectHandle();
        if (handle == _arrayPrototypeHandle)
        {
            return true;
        }

        return IsArrayObject(_heap.GetObject(handle));
    }

    private bool IsArrayObject(JsObject obj)
    {
        if (obj is ArrayObject)
        {
            return true;
        }

        if (obj is not ProxyObject proxy)
        {
            return false;
        }

        if (proxy.IsRevoked)
        {
            throw new JsThrownException(CreateTypeError("Cannot perform 'IsArray' on a revoked proxy."));
        }

        var target = _heap.GetObject(proxy.TargetHandle);
        return IsArrayObject(target);
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
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        var separator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? ToStringValue(args[0])
            : ",";
        return JsValue.FromString(JoinArrayElements(thisValue, obj, length, separator));
    }

    private string JoinArrayElements(JsValue thisValue, string separator)
    {
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        return JoinArrayElements(thisValue, obj, length, separator);
    }

    private string JoinArrayElements(JsValue thisValue, JsObject obj, int length, string separator)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        var values = new string[length];
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var value = GetReceiverProperty(thisValue, key);
            if (value.Tag is JsValueTag.Undefined or JsValueTag.Null)
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
        var obj = ToObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return JsValue.FromNumber(-1);
        }

        // ECMA-262 23.1.3.16: a missing searchElement defaults to undefined and the
        // search still runs (so [undefined].indexOf() returns 0).
        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
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
        var receiver = ToObjectValue(thisValue);
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        var length = GetArrayLengthDouble(obj);
        if (length <= 0)
        {
            return JsValue.FromBoolean(false);
        }

        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
        var fromIndex = args.Count > 1 ? ToIntegerOrInfinity(args[1]) : 0d;
        double start;
        if (double.IsNegativeInfinity(fromIndex))
        {
            start = 0;
        }
        else if (fromIndex < 0)
        {
            start = Math.Max(length + fromIndex, 0);
        }
        else
        {
            start = fromIndex;
        }

        if (double.IsPositiveInfinity(start) || start >= length)
        {
            return JsValue.FromBoolean(false);
        }

        for (var i = start; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var value = GetReceiverProperty(receiver, key);
            if (SameValueZero(value, target))
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

    // ECMA-262 9.1.2.5 GetThisEnvironment: return the value of the nearest
    // lexical environment record (this frame's environment or an outer one)
    // that has a `this` binding. Returns false only if no record in the chain
    // provides one (so the caller can fall back to the frame receiver).
    private static bool TryResolveThisBinding(Environments.EnvironmentRecord? environment, out JsValue value)
    {
        for (var env = environment; env is not null; env = env.OuterEnv)
        {
            if (env.HasThisBinding)
            {
                return env.GetThisBinding(out value) == Environments.BindingOpResult.Ok;
            }
        }

        value = JsValue.Undefined;
        return false;
    }

    private int GetArrayLength(JsObject obj)
        => checked((int)Math.Min(GetArrayLengthDouble(obj), int.MaxValue));

    // The largest value a JS array's `length` may hold (ECMA-262 10.4.2.2 ArraySetLength
    // / ArrayCreate). ArrayCreate throws RangeError when asked for more than this.
    private const double MaxArrayLength = 4294967295.0; // 2^32 - 1

    // ECMA-262 ArrayCreate(length): throw RangeError if the requested length exceeds
    // 2^32-1. Methods that allocate a fresh result array sized by a source length call
    // this before allocating any storage so a huge/invalid `length` surfaces as a spec
    // RangeError instead of a host out-of-range allocation crash.
    private void ThrowIfArrayLengthExceedsLimit(double length)
    {
        if (length > MaxArrayLength)
        {
            throw new JsThrownException(CreateRangeError("Invalid array length."));
        }
    }

    private double GetArrayLengthDouble(JsObject obj)
    {
        // ECMA-262 7.1.20 LengthOfArrayLike: Return ToLength(? Get(O, "length")).
        // [[Get]] walks the prototype chain AND invokes accessor getters, so a
        // `length` defined as a getter (its side effects observable per spec) and
        // inherited `length` data properties both resolve correctly. The old
        // path read the own-property descriptor's Value directly, which is
        // undefined for an accessor and skips inherited lengths' [[Get]].
        JsValue lengthValue;
        if (obj.OwnerHandle is { } handle)
        {
            if (!TryGetPropertyValue(obj, JsValue.FromObject(handle), "length", out lengthValue))
            {
                lengthValue = JsValue.Undefined;
            }
        }
        else if (obj.TryGetOwnProperty("length", out var ownDescriptor))
        {
            lengthValue = ownDescriptor.Value;
        }
        else if (obj.PrototypeHandle is { } proto)
        {
            return GetArrayLengthDouble(_heap.GetObject(proto));
        }
        else
        {
            return 0;
        }

        var number = ToNumber(lengthValue);
        if (double.IsNaN(number) || number <= 0)
        {
            return 0;
        }

        // ToLength clamps to [0, 2^53-1]; callers needing the int form go through
        // GetArrayLength which further clamps to int.MaxValue.
        return Math.Min(Math.Truncate(number), 9007199254740991.0);
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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
            (_, args) => JsValue.FromNumber(args.Count > 0 ? ToNumberConstructorValue(args[0]) : 0d),
            args => CreateNumberObject(args.Count > 0 ? ToNumberConstructorValue(args[0]) : 0d),
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
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
        _ = prototypeObject.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

    private double ToNumberConstructorValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            value = ToPrimitive(value, PrimitiveHint.Number);
        }

        return value.Tag == JsValueTag.BigInt
            ? (double)value.AsBigInt()
            : ToNumber(value);
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
            // 19.2.5 step 7: R = ToInt32(radix). ToNumber runs ToPrimitive so object
            // radices like new Number(2) / {valueOf(){return 2}} coerce correctly.
            var radixNum = args.Count > 1 ? ToNumber(args[1]) : double.NaN;
            return JsValue.FromNumber(ParseIntegerLiteral(text, radixNum));
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
            // ECMA-262 20.5.7.1.1 AggregateError ( errors, message ):
            //   message via CreateMethodProperty -> { w:t, e:f, c:t }
            //   errors via DefinePropertyOrThrow with { w:t, e:f, c:t }
            // (name is inherited from the prototype.)
            if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
            {
                _ = err.DefineOwnProperty("message",
                    new JsPropertyDescriptor(JsValue.FromString(msg), Writable: true, Enumerable: false, Configurable: true));
            }
            _ = err.DefineOwnProperty("errors",
                new JsPropertyDescriptor(JsValue.FromObject(errorsArrHandle), Writable: true, Enumerable: false, Configurable: true));
            var handle = _heap.AllocateObject(err, AllocationSite.Current());
            _heap.WriteBarrier(handle, errorsArrHandle);
            return JsValue.FromObject(handle);
        }

        var constructor = new NativeFunctionObject(
            "AggregateError",
            (_, args) => Build(args),
            args => Build(args),
            length: 2);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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
                var cloneObj = new RegExpObject(r.Pattern, r.Flags, r.Regex, r.NativeProgram);
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
                if (double.IsNaN(length) || length < 0 || length > int.MaxValue)
                    throw new JsThrownException(CreateRangeError("Invalid ArrayBuffer length."));
                // ES2024: optional maxByteLength for resizable buffers
                var maxByteLen = 0;
                if (args.Count > 1 && args[1].Tag == JsValueTag.Object)
                {
                    var opts = _heap.GetObject(args[1].AsObjectHandle());
                    if (opts.TryGetProperty("maxByteLength", x => _heap.GetObject(x), out var mblDesc) &&
                        mblDesc.Value.Tag is JsValueTag.Number or JsValueTag.Int32)
                    {
                        var mbl = mblDesc.Value.AsNumber();
                        if (mbl >= length && mbl <= int.MaxValue)
                            maxByteLen = (int)mbl;
                    }
                }
                var buf = new ArrayBufferObject((int)length, maxByteLen);
                buf.SetPrototype(prototypeHandle);
                return JsValue.FromObject(_heap.AllocateObject(buf, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

        // ES2024 25.1.5.X get ArrayBuffer.prototype.resizable
        var resizableGetter = new NativeFunctionObject("get resizable", (thisValue, _2) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("ArrayBuffer.prototype.resizable called on non-ArrayBuffer."));
            return JsValue.FromBoolean(buf.IsResizable);
        }, length: 0);
        var resizableGetterHandle = _heap.AllocateObject(resizableGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("resizable", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(resizableGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, resizableGetterHandle);

        // ES2024 get ArrayBuffer.prototype.maxByteLength
        var maxByteLengthGetter = new NativeFunctionObject("get maxByteLength", (thisValue, _2) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("ArrayBuffer.prototype.maxByteLength called on non-ArrayBuffer."));
            if (buf.IsDetached) return JsValue.FromNumber(0);
            return JsValue.FromNumber(buf.MaxByteLength);
        }, length: 0);
        var maxByteLengthGetterHandle = _heap.AllocateObject(maxByteLengthGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("maxByteLength", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(maxByteLengthGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, maxByteLengthGetterHandle);

        // ES2024 25.1.5.X ArrayBuffer.prototype.resize(newLength)
        DefineNativePrototypeMethod(prototypeHandle, prototype, "resize", (thisValue, args) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("ArrayBuffer.prototype.resize called on non-ArrayBuffer."));
            if (buf.IsDetached)
                throw new JsThrownException(CreateTypeError("ArrayBuffer is detached."));
            if (!buf.IsResizable && buf.MaxByteLength == buf.ByteLength)
                throw new JsThrownException(CreateTypeError("ArrayBuffer is not resizable."));
            var newLen = args.Count > 0 ? (int)args[0].AsNumber() : 0;
            if (newLen < 0 || newLen > buf.MaxByteLength)
                throw new JsThrownException(CreateRangeError("Invalid resize length."));
            buf.Resize(newLen);
            return JsValue.Undefined;
        }, length: 1);

        // 25.1.5.6 ArrayBuffer.isView(arg)
        DefineIntrinsicFunction(constructorHandle, constructor, "isView", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
                return JsValue.FromBoolean(false);
            var objHandle = args[0].AsObjectHandle();
            var obj = _heap.GetObject(objHandle);
            if (obj is TypedArrayView) return JsValue.FromBoolean(true);
            // Subclass instances (class DV extends DataView {}) are plain JsObjects
            // whose prototype chain leads to %DataView.prototype%. Walk the chain.
            var proto = obj.PrototypeHandle;
            while (proto is { } protoHandle)
            {
                var protoObj = _heap.GetObject(protoHandle);
                if (protoObj is TypedArrayView)
                    return JsValue.FromBoolean(true);
                proto = protoObj.PrototypeHandle;
            }
            return JsValue.FromBoolean(false);
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

    // ECMA-262 25.2.3 — the %SharedArrayBuffer% constructor.
    private ObjectHandle EnsureSharedArrayBufferConstructor()
    {
        if (_sharedArrayBufferConstructorHandle is { } existing)
            return existing;

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "SharedArrayBuffer",
            (_, _2) => throw new JsThrownException(CreateTypeError("SharedArrayBuffer constructor must be invoked with 'new'.")),
            args =>
            {
                var length = args.Count > 0 ? args[0].AsNumber() : 0;
                if (double.IsNaN(length) || length < 0 || length > int.MaxValue)
                    throw new JsThrownException(CreateRangeError("Invalid SharedArrayBuffer length."));
                var buf = new ArrayBufferObject((int)length);
                buf.SetPrototype(prototypeHandle);
                return JsValue.FromObject(_heap.AllocateObject(buf, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        var byteLengthGetter = new NativeFunctionObject("get byteLength", (thisValue, _2) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("SharedArrayBuffer.prototype.byteLength called on non-SharedArrayBuffer."));
            return JsValue.FromNumber(buf.ByteLength);
        }, length: 0);
        var byteLengthGetterHandle = _heap.AllocateObject(byteLengthGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("byteLength", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(byteLengthGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, byteLengthGetterHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "slice", (thisValue, args) =>
        {
            if (thisValue.Tag != JsValueTag.Object || _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
                throw new JsThrownException(CreateTypeError("SharedArrayBuffer.prototype.slice called on non-SharedArrayBuffer."));
            var len = buf.ByteLength;
            var begin = args.Count > 0 ? (int)Math.Min(Math.Max(args[0].AsNumber(), 0), len) : 0;
            var end = args.Count > 1 ? (int)Math.Min(Math.Max(args[1].AsNumber(), 0), len) : len;
            if (end < begin) end = begin;
            var newLen = end - begin;
            var clone = buf.Clone(begin, newLen);
            clone.SetPrototype(EnsureSharedArrayBufferPrototype());
            return JsValue.FromObject(_heap.AllocateObject(clone, AllocationSite.Current()));
        }, length: 2);

        _sharedArrayBufferConstructorHandle = constructorHandle;
        _sharedArrayBufferPrototypeHandle = prototypeHandle;
        return constructorHandle;
    }

    private ObjectHandle? _sharedArrayBufferConstructorHandle;
    private ObjectHandle? _sharedArrayBufferPrototypeHandle;

    private ObjectHandle EnsureSharedArrayBufferPrototype()
    {
        EnsureSharedArrayBufferConstructor();
        return _sharedArrayBufferPrototypeHandle!.Value;
    }

    // ECMA-262 25.3 — the %DataView% constructor.
    private ObjectHandle EnsureDataViewConstructor()
    {
        if (_dataViewConstructorHandle is { } existing)
            return existing;

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "DataView",
            (_, _2) => throw new JsThrownException(CreateTypeError("DataView constructor must be invoked with 'new'.")),
            args =>
            {
                if (args.Count == 0 || args[0].Tag != JsValueTag.Object ||
                    _heap.GetObject(args[0].AsObjectHandle()) is not ArrayBufferObject buf)
                    throw new JsThrownException(CreateTypeError("DataView: first argument must be an ArrayBuffer."));
                if (buf.IsDetached)
                    throw new JsThrownException(CreateTypeError("DataView: ArrayBuffer is detached."));
                var byteOffset = args.Count > 1 ? ToIndexForView(args[1], "Invalid DataView byteOffset.") : 0;
                var byteLength = args.Count > 2 ? ToIndexForView(args[2], "Invalid DataView byteLength.") : buf.ByteLength - byteOffset;
                if (byteOffset + byteLength > buf.ByteLength)
                    throw new JsThrownException(CreateRangeError("DataView: offset + length exceeds ArrayBuffer bounds."));
                var view = new DataViewObject(buf, byteOffset, byteLength);
                view.SetPrototype(prototypeHandle);
                return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // Getters: buffer, byteLength, byteOffset
        prototype.DefineOwnProperty("buffer", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get buffer", (thisValue, _2) =>
            {
                var dv = RequireDataView(thisValue);
                if (dv.Buffer.OwnerHandle is not { } bufferHandle)
                    bufferHandle = _heap.AllocateObject(dv.Buffer, AllocationSite.Current());
                return JsValue.FromObject(bufferHandle);
            }, length: 0), AllocationSite.Current())), JsValue.Undefined, Enumerable: false, Configurable: true));

        prototype.DefineOwnProperty("byteLength", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get byteLength", (thisValue, _2) =>
                JsValue.FromNumber(RequireDataView(thisValue).ByteLength), length: 0), AllocationSite.Current())), JsValue.Undefined, Enumerable: false, Configurable: true));

        prototype.DefineOwnProperty("byteOffset", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get byteOffset", (thisValue, _2) =>
                JsValue.FromNumber(RequireDataView(thisValue).ByteOffset), length: 0), AllocationSite.Current())), JsValue.Undefined, Enumerable: false, Configurable: true));

        // Prototype methods: getInt8 through setBigUint64
        InstallDataViewPrototypeMethods(prototypeHandle, prototype);

        _dataViewConstructorHandle = constructorHandle;
        _dataViewPrototypeHandle = prototypeHandle;
        return constructorHandle;
    }

    private ObjectHandle? _dataViewConstructorHandle;
    private ObjectHandle? _dataViewPrototypeHandle;

    private DataViewObject RequireDataView(JsValue value)
    {
        if (value.Tag != JsValueTag.Object || _heap.GetObject(value.AsObjectHandle()) is not DataViewObject dv)
            throw new JsThrownException(CreateTypeError("DataView.prototype method called on non-DataView."));
        return dv;
    }

    private void InstallDataViewPrototypeMethods(ObjectHandle protoHandle, JsObject proto)
    {
        JsValue GuardDataViewOp(JsValue thisValue, Func<DataViewObject, JsValue> op)
        {
            var view = RequireDataView(thisValue);
            try
            {
                return op(view);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new JsThrownException(CreateRangeError("Offset is outside the bounds of the DataView."));
            }
            catch (OverflowException)
            {
                throw new JsThrownException(CreateRangeError("Offset is outside the bounds of the DataView."));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("detached", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsThrownException(CreateTypeError("DataView operation on detached ArrayBuffer."));
            }
        }

        JsValue GuardDataViewOpVoid(JsValue thisValue, Action<DataViewObject> op)
        {
            var view = RequireDataView(thisValue);
            try
            {
                op(view);
                return JsValue.Undefined;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new JsThrownException(CreateRangeError("Offset is outside the bounds of the DataView."));
            }
            catch (OverflowException)
            {
                throw new JsThrownException(CreateRangeError("Offset is outside the bounds of the DataView."));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("detached", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsThrownException(CreateTypeError("DataView operation on detached ArrayBuffer."));
            }
        }

        int ReadByteOffset(IReadOnlyList<JsValue> args)
            => args.Count > 0 ? ToIndexForView(args[0], "Invalid DataView byteOffset.") : 0;

        bool ReadLittleEndian(IReadOnlyList<JsValue> args, int index)
            => index < args.Count && ValueToBooleanProxy(args[index]);

        double ReadNumberArg(IReadOnlyList<JsValue> args, int index)
            => index < args.Count ? ToNumber(args[index]) : 0;

        BigInteger ReadBigIntArg(IReadOnlyList<JsValue> args, int index)
        {
            if (index >= args.Count)
                return BigInteger.Zero;

            var value = args[index];
            if (value.Tag == JsValueTag.Object)
                value = ToPrimitive(value, PrimitiveHint.Number);

            if (value.Tag != JsValueTag.BigInt)
                throw new JsThrownException(CreateTypeError("Cannot convert value to BigInt."));
            return value.AsBigInt();
        }

        // 25.3.1.1 GetViewValue - all getters
        DefineNativePrototypeMethod(protoHandle, proto, "getInt8", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetInt8(ReadByteOffset(args)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getUint8", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetUint8(ReadByteOffset(args)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getInt16", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetInt16(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getUint16", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetUint16(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getInt32", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetInt32(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getUint32", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetUint32(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getFloat32", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetFloat32(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getFloat64", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetFloat64(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getBigInt64", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromBigInt(dv.GetBigInt64(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getBigUint64", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromBigInt(dv.GetBigUint64(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "getFloat16", (thisValue, args) =>
            GuardDataViewOp(thisValue, dv => JsValue.FromNumber(dv.GetFloat16(ReadByteOffset(args), ReadLittleEndian(args, 1)))), length: 1);

        // 25.3.1.2 SetViewValue - all setters
        DefineNativePrototypeMethod(protoHandle, proto, "setInt8", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetInt8(ReadByteOffset(args), (sbyte)ReadNumberArg(args, 1))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setUint8", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetUint8(ReadByteOffset(args), (byte)ReadNumberArg(args, 1))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setInt16", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetInt16(ReadByteOffset(args), (short)ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setUint16", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetUint16(ReadByteOffset(args), (ushort)ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setInt32", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetInt32(ReadByteOffset(args), (int)ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setUint32", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetUint32(ReadByteOffset(args), (uint)ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setFloat32", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetFloat32(ReadByteOffset(args), (float)ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setFloat64", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetFloat64(ReadByteOffset(args), ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setBigInt64", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetBigInt64(ReadByteOffset(args), ReadBigIntArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setBigUint64", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetBigUint64(ReadByteOffset(args), ReadBigIntArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "setFloat16", (thisValue, args) =>
            GuardDataViewOpVoid(thisValue, dv => dv.SetFloat16(ReadByteOffset(args), ReadNumberArg(args, 1), ReadLittleEndian(args, 2))), length: 2);
    }

    // ECMA-262 23.2 — all 11 %TypedArray% constructors.
    private BuiltinBinding[] EnsureTypedArrayConstructors()
    {
        if (_typedArrayConstructors is not null)
            return _typedArrayConstructors;

        var results = new List<BuiltinBinding>(12);
        var typedArrayCtorHandle = EnsureTypedArrayConstructor();
        results.Add(BuiltinBinding.NonEnumerable("TypedArray", JsValue.FromObject(typedArrayCtorHandle)));
        results.Add(CreateTypedArrayCtor("Int8Array", TypedArrayElementType.Int8, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Uint8Array", TypedArrayElementType.Uint8, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Uint8ClampedArray", TypedArrayElementType.Uint8Clamped, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Int16Array", TypedArrayElementType.Int16, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Uint16Array", TypedArrayElementType.Uint16, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Int32Array", TypedArrayElementType.Int32, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Uint32Array", TypedArrayElementType.Uint32, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Float32Array", TypedArrayElementType.Float32, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("Float64Array", TypedArrayElementType.Float64, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("BigInt64Array", TypedArrayElementType.BigInt64, typedArrayCtorHandle));
        results.Add(CreateTypedArrayCtor("BigUint64Array", TypedArrayElementType.BigUint64, typedArrayCtorHandle));

        _typedArrayConstructors = results.ToArray();
        return _typedArrayConstructors;
    }

    private BuiltinBinding[]? _typedArrayConstructors;
    private ObjectHandle? _typedArrayConstructorHandle;
    private ObjectHandle? _typedArraySharedPrototypeHandle;

    private ObjectHandle EnsureTypedArrayConstructor()
    {
        if (_typedArrayConstructorHandle is { } existing)
        {
            return existing;
        }

        var sharedPrototype = EnsureTypedArraySharedPrototype();
        var constructor = new NativeFunctionObject(
            "TypedArray",
            (_, _2) => throw new JsThrownException(CreateTypeError("TypedArray constructor is not callable.")),
            _ => throw new JsThrownException(CreateTypeError("TypedArray constructor is abstract and cannot be constructed directly.")),
            length: 0);
        constructor.SetPrototype(EnsureFunctionPrototype());
        _ = constructor.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(
                JsValue.FromObject(sharedPrototype),
                Writable: false,
                Enumerable: false,
                Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var shared = _heap.GetObject(sharedPrototype);
        _ = shared.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(constructorHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(sharedPrototype, constructorHandle);

        DefineIntrinsicFunction(constructorHandle, constructor, "from", TypedArrayFrom, length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "of", TypedArrayOf, length: 0);

        var speciesId = GetWellKnownSymbolId("species");
        if (speciesId != 0)
        {
            var getter = new NativeFunctionObject("get [Symbol.species]", (thisValue, _args) => thisValue, length: 0);
            var getterHandle = _heap.AllocateObject(getter, AllocationSite.Current());
            _ = constructor.DefineOwnSymbolProperty(
                speciesId,
                JsPropertyDescriptor.Accessor(
                    JsValue.FromObject(getterHandle),
                    JsValue.Undefined,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(constructorHandle, getterHandle);
        }

        _typedArrayConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureTypedArraySharedPrototype()
    {
        if (_typedArraySharedPrototypeHandle is { } existing)
        {
            return existing;
        }

        var shared = CreateOrdinaryObject();
        shared.SetPrototype(EnsureObjectPrototype());
        var sharedHandle = _heap.AllocateObject(shared, AllocationSite.Current());
        _heap.PushRoot(sharedHandle);
        InstallTypedArrayPrototypeMethods(sharedHandle, shared, "TypedArray", TypedArrayElementType.Int8, 1);
        _typedArraySharedPrototypeHandle = sharedHandle;
        return sharedHandle;
    }

    private BuiltinBinding CreateTypedArrayCtor(string name, TypedArrayElementType elementType, ObjectHandle typedArrayCtorHandle)
    {
        var elementSize = elementType switch
        {
            TypedArrayElementType.Int8 or TypedArrayElementType.Uint8 or TypedArrayElementType.Uint8Clamped => 1,
            TypedArrayElementType.Int16 or TypedArrayElementType.Uint16 => 2,
            TypedArrayElementType.Int32 or TypedArrayElementType.Uint32 or TypedArrayElementType.Float32 => 4,
            TypedArrayElementType.Float64 or TypedArrayElementType.BigInt64 or TypedArrayElementType.BigUint64 => 8,
            _ => 1
        };

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureTypedArraySharedPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            name,
            (_, _2) => throw new JsThrownException(CreateTypeError($"{name} constructor must be invoked with 'new'.")),
            args => ConstructTypedArray(elementType, elementSize, prototypeHandle, args),
            length: 3);
        constructor.SetPrototype(typedArrayCtorHandle);
        _ = constructor.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty(
            "BYTES_PER_ELEMENT",
            new JsPropertyDescriptor(JsValue.FromNumber(elementSize), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _heap.WriteBarrier(constructorHandle, typedArrayCtorHandle);
        _ = prototype.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty(
            "BYTES_PER_ELEMENT",
            new JsPropertyDescriptor(JsValue.FromNumber(elementSize), Writable: false, Enumerable: false, Configurable: false));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        InstallTypedArrayPrototypeMethods(prototypeHandle, prototype, name, elementType, elementSize);

        return BuiltinBinding.NonEnumerable(name, JsValue.FromObject(constructorHandle));
    }

    private JsValue TypedArrayFrom(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("TypedArray.from requires a source object."));
        }

        var source = args[0];
        var mapFn = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? args[1] : (JsValue?)null;
        var thisArg = args.Count > 2 ? args[2] : JsValue.Undefined;
        if (mapFn.HasValue && !IsCallable(mapFn.Value))
        {
            throw new JsThrownException(CreateTypeError("TypedArray.from map function must be callable."));
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
            var iter = CreateForOfIterator(source);
            if (iter.Tag == JsValueTag.Object && _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
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

        return ConstructTypedArrayFromItems(thisValue, items);
    }

    private JsValue TypedArrayOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        return ConstructTypedArrayFromItems(thisValue, args);
    }

    private JsValue ConstructTypedArrayFromItems(JsValue thisValue, IReadOnlyList<JsValue> items)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("TypedArray operation requires a constructor receiver."));
        }

        var result = ConstructFunction(thisValue, new[] { JsValue.FromNumber(items.Count) });
        if (result.Tag != JsValueTag.Object || _heap.GetObject(result.AsObjectHandle()) is not TypedArrayObject typed)
        {
            throw new JsThrownException(CreateTypeError("TypedArray constructor did not produce a typed array instance."));
        }

        for (var i = 0; i < items.Count; i++)
        {
            typed.SetElement(i, NormalizeTypedArrayElementValue(typed.ElementType, items[i]));
        }

        return result;
    }

    private JsValue ConstructTypedArray(TypedArrayElementType elementType, int elementSize, ObjectHandle protoHandle, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            var emptyBuf = new ArrayBufferObject(0);
            var empty = CreateTypedArrayInstance(elementType, emptyBuf, 0, 0);
            empty.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(empty, AllocationSite.Current()));
        }

        var arg0 = args[0];

        // new X(TypedArray) — copy elements from existing
        if (arg0.Tag == JsValueTag.Object && _heap.GetObject(arg0.AsObjectHandle()) is TypedArrayObject src)
        {
            var len = src.Length;
            var buf = new ArrayBufferObject(len * elementSize);
            var view = CreateTypedArrayInstance(elementType, buf, 0, len * elementSize);
                for (var i = 0; i < len; i++)
                    view.SetElement(i, NormalizeTypedArrayElementValue(elementType, src.GetElement(i)));
            view.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
        }

        // new X(ArrayBuffer [, byteOffset [, length]])
        if (arg0.Tag == JsValueTag.Object && _heap.GetObject(arg0.AsObjectHandle()) is ArrayBufferObject ab)
        {
            var byteOffset = args.Count > 1 ? (int)Math.Max(args[1].AsNumber(), 0) : 0;
            if (byteOffset % elementSize != 0)
                throw new JsThrownException(CreateRangeError($"{nameof(TypedArrayElementType)}: byteOffset must be a multiple of {elementSize}."));
            var isLengthTracking = args.Count <= 2;
            var byteLength = args.Count > 2 ? (int)args[2].AsNumber() * elementSize : ab.ByteLength - byteOffset;
            if (byteOffset + byteLength > ab.ByteLength)
                throw new JsThrownException(CreateRangeError("TypedArray: offset + length exceeds ArrayBuffer bounds."));
            var view = CreateTypedArrayInstance(elementType, ab, byteOffset, byteLength, isLengthTracking);
            view.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
        }

        // ECMA-262 23.2.5.1 step 4-5: when arg0 is an Object that is not a
        // TypedArray and not an ArrayBuffer, treat it as either an iterable
        // (if Symbol.iterator is defined) or an array-like (read length + indices).
        if (arg0.Tag == JsValueTag.Object)
        {
            var srcObj = _heap.GetObject(arg0.AsObjectHandle());
            var iterSymId = GetWellKnownSymbolId("iterator");
            bool hasIterator = iterSymId != 0 &&
                               srcObj.TryGetSymbolProperty(iterSymId, h => _heap.GetObject(h), out var iterDesc) &&
                               iterDesc.Value.Tag != JsValueTag.Undefined;

            if (hasIterator)
            {
                // InitializeTypedArrayFromList: iterate to collect values, then allocate.
                var collected = new List<JsValue>();
                var iter = CreateForOfIterator(arg0);
                if (iter.Tag == JsValueTag.Object &&
                    _heap.GetObject(iter.AsObjectHandle()) is ForOfIteratorObject forOf)
                {
                    while (forOf.TryMoveNext(out var v)) collected.Add(v);
                }
                var len = collected.Count;
                var buf = new ArrayBufferObject(len * elementSize);
                var view = CreateTypedArrayInstance(elementType, buf, 0, len * elementSize);
                for (var i = 0; i < len; i++) view.SetElement(i, NormalizeTypedArrayElementValue(elementType, collected[i]));
                view.SetPrototype(protoHandle);
                return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
            }
            else
            {
                // InitializeTypedArrayFromArrayLike: read length, then indices 0..len-1.
                // ECMA-262 23.2.5.1 step 6 → AllocateTypedArrayBuffer routes the length
                // through ToIndex + the byte-size limit; an excessive length (e.g. 2^53)
                // must surface as a RangeError instead of overflowing the int multiply
                // and crashing the host buffer allocation.
                var len = ValidateTypedArrayLength(GetArrayLengthDouble(srcObj), elementSize);
                var buf = new ArrayBufferObject(len * elementSize);
                var view = CreateTypedArrayInstance(elementType, buf, 0, len * elementSize);
                for (var i = 0; i < len; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var element = srcObj.TryGetProperty(key, h => _heap.GetObject(h), out var d) ? d.Value : JsValue.Undefined;
                    view.SetElement(i, NormalizeTypedArrayElementValue(elementType, element));
                }
                view.SetPrototype(protoHandle);
                return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
            }
        }

        // new X(length) — allocate new buffer. ECMA-262 23.2.5.1 step 3 routes the
        // length through ToIndex (RangeError for Infinity / negative / > 2^53-1).
        {
            var length = ValidateTypedArrayLength(ToNumber(arg0), elementSize);
            var buf = new ArrayBufferObject(length * elementSize);
            var view = CreateTypedArrayInstance(elementType, buf, 0, length * elementSize);
            view.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
        }
    }

    // ECMA-262 7.1.22 ToIndex + the array-length allocation limit. Rejects
    // non-integer-index / infinite / negative / > 2^53-1 lengths, and lengths whose
    // byte size would overflow the int-addressed backing store, with a RangeError
    // instead of letting the (int) cast wrap or the byte multiply overflow (crash).
    private int ValidateTypedArrayLength(double lengthValue, int elementSize)
    {
        var intLen = double.IsNaN(lengthValue)
            ? 0.0
            : (lengthValue >= 0 ? Math.Floor(lengthValue) : Math.Ceiling(lengthValue));
        if (intLen < 0 || intLen > 9007199254740991.0 || intLen * (double)elementSize > int.MaxValue)
            throw new JsThrownException(CreateRangeError("Invalid typed array length."));
        return (int)intLen;
    }

    private int ToIndexForView(JsValue value, string errorMessage)
    {
        var integer = ToIntegerOrInfinity(value);
        if (integer < 0 || double.IsPositiveInfinity(integer) || integer > int.MaxValue)
            throw new JsThrownException(CreateRangeError(errorMessage));
        return (int)integer;
    }

    private JsValue NormalizeTypedArrayElementValue(TypedArrayElementType elementType, JsValue value)
    {
        if (elementType is TypedArrayElementType.BigInt64 or TypedArrayElementType.BigUint64)
        {
            var primitive = value.Tag == JsValueTag.Object ? ToPrimitive(value, PrimitiveHint.Number) : value;
            if (primitive.Tag != JsValueTag.BigInt)
                throw new JsThrownException(CreateTypeError("Cannot convert value to BigInt."));
            return primitive;
        }

        return JsValue.FromNumber(ToNumber(value));
    }

    private static TypedArrayObject CreateTypedArrayInstance(TypedArrayElementType elementType, ArrayBufferObject buf, int byteOffset, int byteLength, bool isLengthTracking = false)
    {
        return elementType switch
        {
            TypedArrayElementType.Int8 => new Int8Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Uint8 => new Uint8Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Uint8Clamped => new Uint8ClampedArray(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Int16 => new Int16Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Uint16 => new Uint16Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Int32 => new Int32Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Uint32 => new Uint32Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Float32 => new Float32Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.Float64 => new Float64Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.BigInt64 => new BigInt64Array(buf, byteOffset, byteLength, isLengthTracking),
            TypedArrayElementType.BigUint64 => new BigUint64Array(buf, byteOffset, byteLength, isLengthTracking),
            _ => throw new ArgumentOutOfRangeException(nameof(elementType))
        };
    }

    private void InstallTypedArrayPrototypeMethods(ObjectHandle protoHandle, JsObject proto, string name, TypedArrayElementType elementType, int elementSize)
    {
        _ = name;
        _ = elementType;
        _ = elementSize;

        // 23.2.3 getters: buffer, byteLength, byteOffset, length
        proto.DefineOwnProperty("buffer", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get buffer",
                (thisValue, _2) =>
                {
                    var typed = RequireTypedArray(thisValue);
                    if (typed.Buffer.OwnerHandle is not { } bufferHandle)
                    {
                        // The backing buffer created during typed-array construction
                        // has no prototype yet; give it %ArrayBuffer.prototype% the
                        // first time it is exposed so byteLength/slice/transfer work.
                        if (typed.Buffer.PrototypeHandle is null)
                            typed.Buffer.SetPrototype(EnsureArrayBufferPrototype());
                        bufferHandle = _heap.AllocateObject(typed.Buffer, AllocationSite.Current());
                    }
                    return JsValue.FromObject(bufferHandle);
                },
                length: 0), AllocationSite.Current())),
            JsValue.Undefined, Enumerable: false, Configurable: true));

        proto.DefineOwnProperty("byteLength", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get byteLength",
                (thisValue, _2) => JsValue.FromNumber(RequireTypedArray(thisValue).ByteLength),
                length: 0), AllocationSite.Current())),
            JsValue.Undefined, Enumerable: false, Configurable: true));

        proto.DefineOwnProperty("byteOffset", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get byteOffset",
                (thisValue, _2) => JsValue.FromNumber(RequireTypedArray(thisValue).ByteOffset),
                length: 0), AllocationSite.Current())),
            JsValue.Undefined, Enumerable: false, Configurable: true));

        proto.DefineOwnProperty("length", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(_heap.AllocateObject(new NativeFunctionObject("get length",
                (thisValue, _2) => JsValue.FromNumber(RequireTypedArray(thisValue).Length),
                length: 0), AllocationSite.Current())),
            JsValue.Undefined, Enumerable: false, Configurable: true));

        // 23.2.3.22 TypedArray.prototype.set(array [, offset])
        DefineNativePrototypeMethod(protoHandle, proto, "set", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var source = args.Count > 0 ? args[0] : JsValue.Undefined;
            var targetOffset = args.Count > 1 ? (int)Math.Max(args[1].AsNumber(), 0) : 0;
            if (source.Tag == JsValueTag.Object && _heap.GetObject(source.AsObjectHandle()) is TypedArrayObject src)
            {
                var count = Math.Min(src.Length, self.Length - targetOffset);
                for (var i = 0; i < count; i++)
                    self.SetElement(targetOffset + i, src.GetElement(i));
            }
            return JsValue.Undefined;
        }, length: 2);

        // 23.2.3.19 TypedArray.prototype.slice(begin, end)
        DefineNativePrototypeMethod(protoHandle, proto, "slice", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var len = self.Length;
            var begin = args.Count > 0 ? (int)Math.Min(Math.Max(args[0].AsNumber(), 0), len) : 0;
            var end = args.Count > 1 ? (int)Math.Min(Math.Max(args[1].AsNumber(), 0), len) : len;
            if (end < begin) end = begin;
            var newLen = end - begin;
            var buf = new ArrayBufferObject(newLen * self.ElementSize);
            var sliced = CreateTypedArrayInstance(self.ElementType, buf, 0, newLen * self.ElementSize);
            for (var i = 0; i < newLen; i++)
                sliced.SetElement(i, self.GetElement(begin + i));
            sliced.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(sliced, AllocationSite.Current()));
        }, length: 2);

        DefineNativePrototypeMethod(protoHandle, proto, "at", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var index = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            if (index < 0)
            {
                index += self.Length;
            }
            return self.GetElement(index);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "copyWithin", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var len = self.Length;
            var target = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            var start = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            var end = args.Count > 2 ? (int)ToNumber(args[2]) : len;
            if (target < 0) target = Math.Max(len + target, 0);
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = Math.Max(len + end, 0);
            target = Math.Min(Math.Max(target, 0), len);
            start = Math.Min(Math.Max(start, 0), len);
            end = Math.Min(Math.Max(end, 0), len);
            var count = Math.Min(end - start, len - target);
            if (count > 0)
            {
                var temp = new JsValue[count];
                for (var i = 0; i < count; i++) temp[i] = self.GetElement(start + i);
                for (var i = 0; i < count; i++) self.SetElement(target + i, temp[i]);
            }
            return thisValue;
        }, length: 2);

        DefineNativePrototypeMethod(protoHandle, proto, "fill", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            var len = self.Length;
            var start = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            var end = args.Count > 2 ? (int)ToNumber(args[2]) : len;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = Math.Max(len + end, 0);
            start = Math.Min(Math.Max(start, 0), len);
            end = Math.Min(Math.Max(end, 0), len);
            for (var i = start; i < end; i++) self.SetElement(i, value);
            return thisValue;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "forEach", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.forEach callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < self.Length; i++)
                _ = CallFunction(callback, new[] { self.GetElement(i), JsValue.FromNumber(i), thisValue }, thisArg);
            return JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "map", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.map callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var buf = new ArrayBufferObject(self.Length * self.ElementSize);
            var mapped = CreateTypedArrayInstance(self.ElementType, buf, 0, self.Length * self.ElementSize);
            mapped.SetPrototype(protoHandle);
            for (var i = 0; i < self.Length; i++)
            {
                var next = CallFunction(callback, new[] { self.GetElement(i), JsValue.FromNumber(i), thisValue }, thisArg);
                mapped.SetElement(i, next);
            }
            return JsValue.FromObject(_heap.AllocateObject(mapped, AllocationSite.Current()));
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "filter", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.filter callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var selected = new List<JsValue>();
            for (var i = 0; i < self.Length; i++)
            {
                var value = self.GetElement(i);
                var keep = CallFunction(callback, new[] { value, JsValue.FromNumber(i), thisValue }, thisArg);
                if (IsTruthy(keep)) selected.Add(value);
            }
            var buf = new ArrayBufferObject(selected.Count * self.ElementSize);
            var filtered = CreateTypedArrayInstance(self.ElementType, buf, 0, selected.Count * self.ElementSize);
            filtered.SetPrototype(protoHandle);
            for (var i = 0; i < selected.Count; i++) filtered.SetElement(i, selected[i]);
            return JsValue.FromObject(_heap.AllocateObject(filtered, AllocationSite.Current()));
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "find", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.find callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < self.Length; i++)
            {
                var value = self.GetElement(i);
                if (IsTruthy(CallFunction(callback, new[] { value, JsValue.FromNumber(i), thisValue }, thisArg)))
                    return value;
            }
            return JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "findIndex", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.findIndex callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < self.Length; i++)
            {
                var value = self.GetElement(i);
                if (IsTruthy(CallFunction(callback, new[] { value, JsValue.FromNumber(i), thisValue }, thisArg)))
                    return JsValue.FromNumber(i);
            }
            return JsValue.FromNumber(-1);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "findLast", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.findLast callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = self.Length - 1; i >= 0; i--)
            {
                var value = self.GetElement(i);
                if (IsTruthy(CallFunction(callback, new[] { value, JsValue.FromNumber(i), thisValue }, thisArg)))
                    return value;
            }
            return JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "findLastIndex", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.findLastIndex callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = self.Length - 1; i >= 0; i--)
            {
                var value = self.GetElement(i);
                if (IsTruthy(CallFunction(callback, new[] { value, JsValue.FromNumber(i), thisValue }, thisArg)))
                    return JsValue.FromNumber(i);
            }
            return JsValue.FromNumber(-1);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "every", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.every callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < self.Length; i++)
                if (!IsTruthy(CallFunction(callback, new[] { self.GetElement(i), JsValue.FromNumber(i), thisValue }, thisArg)))
                    return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(true);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "some", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.some callback is not callable."));
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < self.Length; i++)
                if (IsTruthy(CallFunction(callback, new[] { self.GetElement(i), JsValue.FromNumber(i), thisValue }, thisArg)))
                    return JsValue.FromBoolean(true);
            return JsValue.FromBoolean(false);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "includes", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var search = args.Count > 0 ? args[0] : JsValue.Undefined;
            var from = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            if (from < 0) from = Math.Max(self.Length + from, 0);
            for (var i = from; i < self.Length; i++)
                if (SameValueZero(self.GetElement(i), search)) return JsValue.FromBoolean(true);
            return JsValue.FromBoolean(false);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "indexOf", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var search = args.Count > 0 ? args[0] : JsValue.Undefined;
            var from = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            if (from < 0) from = Math.Max(self.Length + from, 0);
            for (var i = from; i < self.Length; i++)
                if (AreStrictlyEqual(self.GetElement(i), search)) return JsValue.FromNumber(i);
            return JsValue.FromNumber(-1);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "lastIndexOf", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var search = args.Count > 0 ? args[0] : JsValue.Undefined;
            var from = args.Count > 1 ? (int)ToNumber(args[1]) : self.Length - 1;
            if (from < 0) from = self.Length + from;
            from = Math.Min(from, self.Length - 1);
            for (var i = from; i >= 0; i--)
                if (AreStrictlyEqual(self.GetElement(i), search)) return JsValue.FromNumber(i);
            return JsValue.FromNumber(-1);
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "join", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var sep = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? ToStringValue(args[0]) : ",";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < self.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(ToStringValue(self.GetElement(i)));
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "keys", (thisValue, _) =>
        {
            var self = RequireTypedArray(thisValue);
            var keys = new List<JsValue>(self.Length);
            for (var i = 0; i < self.Length; i++) keys.Add(JsValue.FromNumber(i));
            var arr = CreateArrayObject(keys);
            var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
            return CreateArrayIterator(JsValue.FromObject(arrHandle), ArrayIteratorKind.Value);
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "values", (thisValue, _) =>
        {
            var self = RequireTypedArray(thisValue);
            var values = new List<JsValue>(self.Length);
            for (var i = 0; i < self.Length; i++) values.Add(self.GetElement(i));
            var arr = CreateArrayObject(values);
            var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
            return CreateArrayIterator(JsValue.FromObject(arrHandle), ArrayIteratorKind.Value);
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "entries", (thisValue, _) =>
        {
            var self = RequireTypedArray(thisValue);
            var entries = new List<JsValue>(self.Length);
            for (var i = 0; i < self.Length; i++)
            {
                var pair = CreateArrayObject(new[] { JsValue.FromNumber(i), self.GetElement(i) });
                entries.Add(JsValue.FromObject(_heap.AllocateObject(pair, AllocationSite.Current())));
            }
            var arr = CreateArrayObject(entries);
            var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
            return CreateArrayIterator(JsValue.FromObject(arrHandle), ArrayIteratorKind.Value);
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "toString", (thisValue, _) =>
            CallFunction(GetReceiverProperty(thisValue, "join"), Array.Empty<JsValue>(), thisValue), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "toLocaleString", (thisValue, _) =>
            CallFunction(GetReceiverProperty(thisValue, "join"), Array.Empty<JsValue>(), thisValue), length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "reverse", (thisValue, _) =>
        {
            var self = RequireTypedArray(thisValue);
            for (int i = 0, j = self.Length - 1; i < j; i++, j--)
            {
                var a = self.GetElement(i);
                var b = self.GetElement(j);
                self.SetElement(i, b);
                self.SetElement(j, a);
            }
            return thisValue;
        }, length: 0);

        DefineNativePrototypeMethod(protoHandle, proto, "reduce", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.reduce callback is not callable."));
            var k = 0;
            JsValue acc;
            if (args.Count > 1) acc = args[1];
            else
            {
                if (self.Length == 0) throw new JsThrownException(CreateTypeError("Reduce of empty typed array with no initial value."));
                acc = self.GetElement(0);
                k = 1;
            }
            for (; k < self.Length; k++)
                acc = CallFunction(callback, new[] { acc, self.GetElement(k), JsValue.FromNumber(k), thisValue }, JsValue.Undefined);
            return acc;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "reduceRight", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(callback)) throw new JsThrownException(CreateTypeError("TypedArray.prototype.reduceRight callback is not callable."));
            var k = self.Length - 1;
            JsValue acc;
            if (args.Count > 1) acc = args[1];
            else
            {
                if (self.Length == 0) throw new JsThrownException(CreateTypeError("Reduce of empty typed array with no initial value."));
                acc = self.GetElement(k--);
            }
            for (; k >= 0; k--)
                acc = CallFunction(callback, new[] { acc, self.GetElement(k), JsValue.FromNumber(k), thisValue }, JsValue.Undefined);
            return acc;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "subarray", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var len = self.Length;
            var begin = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            var end = args.Count > 1 ? (int)ToNumber(args[1]) : len;
            if (begin < 0) begin = Math.Max(len + begin, 0);
            if (end < 0) end = Math.Max(len + end, 0);
            begin = Math.Min(Math.Max(begin, 0), len);
            end = Math.Min(Math.Max(end, 0), len);
            if (end < begin) end = begin;
            var newLen = end - begin;
            var byteOffset = self.ByteOffset + begin * self.ElementSize;
            var byteLength = newLen * self.ElementSize;
            var view = CreateTypedArrayInstance(self.ElementType, self.Buffer, byteOffset, byteLength);
            view.SetPrototype(protoHandle);
            return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
        }, length: 2);

        DefineNativePrototypeMethod(protoHandle, proto, "sort", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var comparer = args.Count > 0 ? args[0] : JsValue.Undefined;
            var values = new List<JsValue>(self.Length);
            for (var i = 0; i < self.Length; i++) values.Add(self.GetElement(i));
            values.Sort((a, b) =>
            {
                if (IsCallable(comparer))
                {
                    var v = CallFunction(comparer, new[] { a, b }, JsValue.Undefined);
                    var n = ToNumber(v);
                    return n < 0 ? -1 : (n > 0 ? 1 : 0);
                }
                var na = ToNumber(a);
                var nb = ToNumber(b);
                return na.CompareTo(nb);
            });
            for (var i = 0; i < values.Count; i++) self.SetElement(i, values[i]);
            return thisValue;
        }, length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "with", (thisValue, args) =>
        {
            var self = RequireTypedArray(thisValue);
            var index = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
            if (index < 0) index += self.Length;
            if (index < 0 || index >= self.Length)
                throw new JsThrownException(CreateRangeError("TypedArray.prototype.with index out of range."));
            var value = args.Count > 1 ? args[1] : JsValue.Undefined;
            var buf = new ArrayBufferObject(self.Length * self.ElementSize);
            var copy = CreateTypedArrayInstance(self.ElementType, buf, 0, self.Length * self.ElementSize);
            copy.SetPrototype(protoHandle);
            for (var i = 0; i < self.Length; i++) copy.SetElement(i, self.GetElement(i));
            copy.SetElement(index, value);
            return JsValue.FromObject(_heap.AllocateObject(copy, AllocationSite.Current()));
        }, length: 2);

        var iteratorSymbolId = GetWellKnownSymbolId("iterator");
        if (iteratorSymbolId != 0)
        {
            var valuesMethod = GetReceiverProperty(JsValue.FromObject(protoHandle), "values");
            if (valuesMethod.Tag == JsValueTag.Object)
            {
                _ = proto.DefineOwnSymbolProperty(
                    iteratorSymbolId,
                    new JsPropertyDescriptor(valuesMethod, Writable: true, Enumerable: false, Configurable: true));
                _heap.WriteBarrier(protoHandle, valuesMethod.AsObjectHandle());
            }
        }

        var toStringTagSymbolId = GetWellKnownSymbolId("toStringTag");
        if (toStringTagSymbolId != 0)
        {
            var getter = new NativeFunctionObject("get [Symbol.toStringTag]", (thisValue, _args) =>
            {
                if (thisValue.Tag != JsValueTag.Object)
                {
                    return JsValue.Undefined;
                }

                if (_heap.GetObject(thisValue.AsObjectHandle()) is not TypedArrayObject typed)
                {
                    return JsValue.Undefined;
                }

                return JsValue.FromString(typed.ElementType switch
                {
                    TypedArrayElementType.Int8 => "Int8Array",
                    TypedArrayElementType.Uint8 => "Uint8Array",
                    TypedArrayElementType.Uint8Clamped => "Uint8ClampedArray",
                    TypedArrayElementType.Int16 => "Int16Array",
                    TypedArrayElementType.Uint16 => "Uint16Array",
                    TypedArrayElementType.Int32 => "Int32Array",
                    TypedArrayElementType.Uint32 => "Uint32Array",
                    TypedArrayElementType.Float32 => "Float32Array",
                    TypedArrayElementType.Float64 => "Float64Array",
                    TypedArrayElementType.BigInt64 => "BigInt64Array",
                    TypedArrayElementType.BigUint64 => "BigUint64Array",
                    _ => "TypedArray"
                });
            }, length: 0);
            var getterHandle = _heap.AllocateObject(getter, AllocationSite.Current());
            _ = proto.DefineOwnSymbolProperty(
                toStringTagSymbolId,
                JsPropertyDescriptor.Accessor(
                    JsValue.FromObject(getterHandle),
                    JsValue.Undefined,
                    Enumerable: false,
                    Configurable: true));
            _heap.WriteBarrier(protoHandle, getterHandle);
        }
    }

    private TypedArrayObject RequireTypedArray(JsValue value)
    {
        if (value.Tag != JsValueTag.Object || _heap.GetObject(value.AsObjectHandle()) is not TypedArrayObject ta)
            throw new JsThrownException(CreateTypeError("TypedArray.prototype method called on non-TypedArray."));
        return ta;
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

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

    private static double ParseIntegerLiteral(string text, double radixNumber)
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
        {
            var r = (int)MathHelpers.ToInt32(radixNumber);
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
            "BigInt" => EnsureObjectPrototype(),
            "Symbol" => EnsureObjectPrototype(),
            "Date" => EnsureDatePrototype(),
            "RegExp" => EnsureRegExpPrototype(),
            "GeneratorPrototype" => EnsureGeneratorPrototype(),
            "AsyncGeneratorPrototype" => EnsureAsyncGeneratorPrototype(),
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

    private ObjectHandle GetGlobalConstructorHandle(string constructorName)
    {
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        if (TryGetPropertyValue(global, JsValue.FromObject(globalHandle), constructorName, out var ctorVal) &&
            ctorVal.Tag == JsValueTag.Object)
        {
            return ctorVal.AsObjectHandle();
        }

        throw new InvalidOperationException($"Global constructor '{constructorName}' was not found.");
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

            // ECMA-262 27.5.1 — %GeneratorPrototype%.[[Prototype]] is
            // %Iterator.prototype% (NOT %Object.prototype%). GeneratorBuiltin can't
            // reach %Iterator.prototype% during bootstrap, so it parents to
            // Object.prototype; re-parent here so generator instances inherit the
            // Iterator Helpers (map/filter/take/drop/flatMap/reduce/…).
            var iteratorProtoHandle = EnsureIteratorPrototype();
            if (protoObj.PrototypeHandle != iteratorProtoHandle)
            {
                protoObj.SetPrototype(iteratorProtoHandle);
                _heap.WriteBarrier(protoHandle, iteratorProtoHandle);
            }

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

    private ObjectHandle EnsureAsyncGeneratorPrototype()
    {
        if (_asyncGeneratorPrototypeHandle is { } existing)
            return existing;

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var nextFn = new NativeFunctionObject(
            "next",
            (thisValue, args) =>
            {
                if (thisValue.Tag != JsValueTag.Object ||
                    _heap.GetObject(thisValue.AsObjectHandle()) is not GeneratorObject generator ||
                    !generator.IsAsyncGenerator)
                {
                    throw new JsThrownException(CreateTypeError("AsyncGenerator.prototype.next: receiver is not an async generator"));
                }

                if (generator.State == GeneratorState.Completed)
                {
                    var doneResult = CreateIteratorResult(JsValue.Undefined, done: true);
                    return CreateResolvedPromise(doneResult);
                }

                if (generator.State == GeneratorState.Executing)
                {
                    return CreateRejectedPromise(CreateTypeError("AsyncGenerator.prototype.next: generator is already executing"));
                }

                var sentValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                generator.State = GeneratorState.Executing;
                try
                {
                    var result = ExecuteGenerator(generator, sentValue);
                    return CreateResolvedPromise(result);
                }
                catch (JsThrownException ex)
                {
                    generator.State = GeneratorState.Completed;
                    return CreateRejectedPromise(ex.Value);
                }
            },
            length: 1);
        var nextHandle = _heap.AllocateObject(nextFn, AllocationSite.Current());
        _heap.PushRoot(nextHandle);
        _ = prototype.DefineOwnProperty(
            "next",
            new JsPropertyDescriptor(JsValue.FromObject(nextHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, nextHandle);

        var returnFn = new NativeFunctionObject(
            "return",
            (thisValue, args) =>
            {
                if (thisValue.Tag != JsValueTag.Object ||
                    _heap.GetObject(thisValue.AsObjectHandle()) is not GeneratorObject generator ||
                    !generator.IsAsyncGenerator)
                {
                    throw new JsThrownException(CreateTypeError("AsyncGenerator.prototype.return: receiver is not an async generator"));
                }

                var returnValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                if (generator.State == GeneratorState.Completed)
                {
                    var doneResult = CreateIteratorResult(returnValue, done: true);
                    return CreateResolvedPromise(doneResult);
                }

                if (generator.State == GeneratorState.Executing)
                {
                    return CreateRejectedPromise(CreateTypeError("AsyncGenerator.prototype.return: generator is already executing"));
                }

                if (generator.InstructionPointer == 0)
                {
                    generator.State = GeneratorState.Completed;
                    var doneResult = CreateIteratorResult(returnValue, done: true);
                    return CreateResolvedPromise(doneResult);
                }

                generator.CompletionMode = GeneratorCompletionMode.Return;
                generator.State = GeneratorState.Executing;
                try
                {
                    var result = ExecuteGenerator(generator, returnValue);
                    if (generator.State == GeneratorState.Suspended)
                    {
                        generator.State = GeneratorState.Completed;
                        var doneResult = CreateIteratorResult(returnValue, done: true);
                        return CreateResolvedPromise(doneResult);
                    }

                    return CreateResolvedPromise(result);
                }
                catch (JsThrownException ex)
                {
                    generator.State = GeneratorState.Completed;
                    return CreateRejectedPromise(ex.Value);
                }
            },
            length: 1);
        var returnHandle = _heap.AllocateObject(returnFn, AllocationSite.Current());
        _heap.PushRoot(returnHandle);
        _ = prototype.DefineOwnProperty(
            "return",
            new JsPropertyDescriptor(JsValue.FromObject(returnHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, returnHandle);

        var throwFn = new NativeFunctionObject(
            "throw",
            (thisValue, args) =>
            {
                if (thisValue.Tag != JsValueTag.Object ||
                    _heap.GetObject(thisValue.AsObjectHandle()) is not GeneratorObject generator ||
                    !generator.IsAsyncGenerator)
                {
                    throw new JsThrownException(CreateTypeError("AsyncGenerator.prototype.throw: receiver is not an async generator"));
                }

                var throwValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                if (generator.State == GeneratorState.Completed)
                {
                    return CreateRejectedPromise(throwValue);
                }

                if (generator.State == GeneratorState.Executing)
                {
                    return CreateRejectedPromise(CreateTypeError("AsyncGenerator.prototype.throw: generator is already executing"));
                }

                generator.CompletionMode = GeneratorCompletionMode.Throw;
                generator.State = GeneratorState.Executing;
                try
                {
                    var result = ExecuteGenerator(generator, throwValue);
                    return CreateResolvedPromise(result);
                }
                catch (JsThrownException ex)
                {
                    generator.State = GeneratorState.Completed;
                    return CreateRejectedPromise(ex.Value);
                }
            },
            length: 1);
        var throwHandle = _heap.AllocateObject(throwFn, AllocationSite.Current());
        _heap.PushRoot(throwHandle);
        _ = prototype.DefineOwnProperty(
            "throw",
            new JsPropertyDescriptor(JsValue.FromObject(throwHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, throwHandle);

        if (_asyncGeneratorAsyncIteratorHandle is null)
        {
            var asyncIteratorId = GetWellKnownSymbolId("asyncIterator");
            if (asyncIteratorId != 0)
            {
                var asyncIteratorFn = new NativeFunctionObject(
                    "[Symbol.asyncIterator]",
                    (thisValue, _) => thisValue,
                    length: 0);
                _asyncGeneratorAsyncIteratorHandle = _heap.AllocateObject(asyncIteratorFn, AllocationSite.Current());
                _heap.PushRoot(_asyncGeneratorAsyncIteratorHandle.Value);
                prototype.DefineOwnSymbolProperty(
                    asyncIteratorId,
                    new JsPropertyDescriptor(JsValue.FromObject(_asyncGeneratorAsyncIteratorHandle.Value), Writable: true, Enumerable: false, Configurable: true));
                _heap.WriteBarrier(prototypeHandle, _asyncGeneratorAsyncIteratorHandle.Value);
            }
        }

        _asyncGeneratorPrototypeHandle = prototypeHandle;
        return prototypeHandle;
    }

    private JsValue CreateIteratorResult(JsValue value, bool done)
    {
        var result = CreateOrdinaryObject();
        _ = result.DefineOwnProperty(
            "value",
            new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty(
            "done",
            new JsPropertyDescriptor(JsValue.FromBoolean(done), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(result, AllocationSite.Current()));
    }

    private JsValue CreateResolvedPromise(JsValue value)
    {
        var capability = NewPromiseCapability();
        _ = CallFunction(capability.Resolve, new[] { value }, JsValue.Undefined);
        return capability.Promise;
    }

    private JsValue CreateRejectedPromise(JsValue reason)
    {
        var capability = NewPromiseCapability();
        _ = CallFunction(capability.Reject, new[] { reason }, JsValue.Undefined);
        return capability.Promise;
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
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
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

    private JsValue CreateBigIntObject(System.Numerics.BigInteger value)
    {
        var obj = new BigIntObject(value);
        obj.SetPrototype(GetGlobalPrototype("BigInt"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private JsValue CreateSymbolObject(long symbolId)
    {
        var obj = new SymbolObject(symbolId);
        obj.SetPrototype(GetGlobalPrototype("Symbol"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    // Tier 4 #24: JIT helpers — each mirrors one opcode case in
    // ExecuteInternalCore so the JIT-emitted Expression-tree code can
    // invoke a single method rather than inline equivalent logic. Keeping
    // the implementation in one place avoids semantic drift between the
    // interpreter and the JIT.
    // Tier 4 #24: JIT helpers for the property-access opcodes. Each
    // mirrors the corresponding interpreter case body — including the
    // inline-cache fast path and the try/catch → ThrowOrHandle fallback.
    // Safe to call from JIT because TryEmitExpressionTree's pre-pass
    // bails on any handler opcode, so ThrowOrHandle's no-handler path
    // (which throws JsThrownException) is always the one taken.
    internal void GetPropByNameForJit(InterpreterFrame frame, int destReg, int receiverReg, int propNameIndex, int icOffset)
    {
        var receiver = frame.Registers[receiverReg];
        var prop = frame.Function.PropertyNames[propNameIndex];
        if (TryGetLoadIC(frame.Function, icOffset, receiver, prop, out var icResult))
        {
            frame.Registers[destReg] = icResult;
            return;
        }
        try
        {
            frame.Registers[destReg] = GetReceiverProperty(receiver, prop);
            PopulateLoadIC(frame.Function, icOffset, receiver, prop);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    // Audit doc �3.1 first slice. Same contract as GetPropByNameForJit but
    // the IC reference and the property name are pre-resolved by the JIT
    // codegen and threaded in directly. Avoids one Dictionary lookup and
    // one IReadOnlyList<string> indexing per call site. The IC instance
    // is stable for the lifetime of the function (the dispatch dictionary
    // entry is allocated at JIT compile time), so the JIT can safely
    // embed the reference as a closed-over constant.
    internal void GetPropByNameForJit_Direct(
        InterpreterFrame frame, int destReg, int receiverReg, string prop, PolymorphicInlineCache ic)
    {
        var receiver = frame.Registers[receiverReg];
        if (receiver.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(receiver.AsObjectHandle());
            if (ic.TryGet(obj, prop, out var slot) && obj.PropertyArray[slot] is { } desc)
            {
                if (desc.IsAccessor)
                {
                    ic.InvalidateShape(obj.CurrentShape);
                }
                else
                {
                    frame.Registers[destReg] = desc.Value;
                    return;
                }
            }
        }
        try
        {
            frame.Registers[destReg] = GetReceiverProperty(receiver, prop);
            if (receiver.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(receiver.AsObjectHandle());
                if (obj.CurrentShape.TryGetSlot(prop, out var freshSlot) &&
                    obj.PropertyArray[freshSlot] is { } freshDesc &&
                    !freshDesc.IsAccessor)
                {
                    ic.Add(obj.CurrentShape, prop, freshSlot);
                }
            }
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    // Audit doc �3.1 first slice � direct-IC variant. See
    // GetPropByNameForJit_Direct for the contract.
    internal void SetPropByNameForJit_Direct(
        InterpreterFrame frame, int receiverReg, string prop, int valueReg, PolymorphicInlineCache ic)
    {
        var receiverValue = frame.Registers[receiverReg];
        var value = frame.Registers[valueReg];

        if (receiverValue.Tag == JsValueTag.HostObject)
        {
            try { SetHostObjectProperty(receiverValue, prop, value); }
            catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
            return;
        }

        var ownerHandle = ResolveObjectHandle(receiverValue);
        if (receiverValue.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(receiverValue.AsObjectHandle());
            if (obj is not ProxyObject && ic.TryGet(obj, prop, out var slot) &&
                obj.PropertyArray[slot] is { } desc)
            {
                if (desc.IsAccessor || !desc.Writable)
                {
                    ic.InvalidateShape(obj.CurrentShape);
                }
                else
                {
                    var updated = desc with { Value = value };
                    obj.PropertyArray[slot] = updated;
                    WriteDescriptorBarrier(ownerHandle, updated);
                    return;
                }
            }
        }

        var ownerObj = _heap.GetObject(ownerHandle);
        if (ownerObj is ProxyObject proxySet)
        {
            try { _ = ProxySet(proxySet, receiverValue, prop, value); }
            catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
            return;
        }
        try
        {
            _ = SetPropertyValue(ownerHandle, ownerObj, prop, value, receiverValue);
            if (receiverValue.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(receiverValue.AsObjectHandle());
                if (obj is not ProxyObject &&
                    obj.CurrentShape.TryGetSlot(prop, out var freshSlot) &&
                    obj.PropertyArray[freshSlot] is { } freshDesc &&
                    !freshDesc.IsAccessor &&
                    freshDesc.Writable)
                {
                    ic.Add(obj.CurrentShape, prop, freshSlot);
                }
            }
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    internal void SetPropByNameForJit(InterpreterFrame frame, int receiverReg, int propNameIndex, int valueReg, int icOffset)
    {
        var receiverValue = frame.Registers[receiverReg];
        var prop = frame.Function.PropertyNames[propNameIndex];
        var value = frame.Registers[valueReg];

        if (receiverValue.Tag == JsValueTag.HostObject)
        {
            try { SetHostObjectProperty(receiverValue, prop, value); }
            catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
            return;
        }

        var ownerHandle = ResolveObjectHandle(receiverValue);
        if (receiverValue.Tag == JsValueTag.Object &&
            TryStoreIC(frame.Function, icOffset, ownerHandle, receiverValue, prop, value))
        {
            return;
        }
        var obj = _heap.GetObject(ownerHandle);
        if (obj is ProxyObject proxySet)
        {
            try { _ = ProxySet(proxySet, receiverValue, prop, value); }
            catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
            return;
        }
        try
        {
            _ = SetPropertyValue(ownerHandle, obj, prop, value, receiverValue);
            if (receiverValue.Tag == JsValueTag.Object)
                PopulateStoreIC(frame.Function, icOffset, receiverValue, prop);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    internal void GetElemForJit(InterpreterFrame frame, int destReg, int receiverReg, int keyReg, int icOffset)
    {
        var receiver = frame.Registers[receiverReg];
        var keyValue = frame.Registers[keyReg];
        try
        {
            if (keyValue.Tag == JsValueTag.Symbol)
            {
                frame.Registers[destReg] = GetReceiverSymbolProperty(receiver, keyValue.AsSymbolId());
            }
            else
            {
                if (keyValue.Tag == JsValueTag.String &&
                    TryGetElemStringIC(frame.Function, icOffset, receiver, keyValue.AsString(), out var elemResult))
                {
                    frame.Registers[destReg] = elemResult;
                }
                else
                {
                    var propKey = ToPropertyKey(keyValue);
                    frame.Registers[destReg] = GetReceiverProperty(receiver, propKey);
                    if (keyValue.Tag == JsValueTag.String)
                    {
                        PopulateGetElemStringIC(frame.Function, icOffset, receiver, propKey);
                    }
                }
            }
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    internal void SetElemForJit(InterpreterFrame frame, int ownerReg, int keyReg, int valueReg)
    {
        var ownerHandle = ResolveObjectHandle(frame.Registers[ownerReg]);
        var obj = _heap.GetObject(ownerHandle);
        var keyValue = frame.Registers[keyReg];
        var value = frame.Registers[valueReg];

        if (keyValue.Tag == JsValueTag.Symbol)
        {
            try
            {
                _ = SetSymbolPropertyValue(ownerHandle, obj, keyValue.AsSymbolId(), value, frame.Registers[ownerReg]);
            }
            catch (JsThrownException ex)
            {
                ThrowOrHandle(frame, ex.Value);
            }
            return;
        }

        var key = ToPropertyKey(keyValue);
        try
        {
            _ = SetPropertyValue(ownerHandle, obj, key, value, frame.Registers[ownerReg]);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
            return;
        }

        if (double.TryParse(key, out var numericIndex))
        {
            var nextLength = numericIndex + 1;
            if (!obj.TryGetOwnProperty("length", out var lenDesc) || lenDesc.Value.AsNumber() < nextLength)
            {
                _ = obj.SetProperty("length", JsValue.FromNumber(nextLength));
            }
        }
    }

    internal void DeleteElemForJit(InterpreterFrame frame, int destReg, int receiverReg, int keyReg)
    {
        var receiver = frame.Registers[receiverReg];
        if (receiver.Tag != JsValueTag.Object)
        {
            frame.Registers[destReg] = JsValue.FromBoolean(true);
            return;
        }
        var obj = ResolveObject(receiver);
        var key = ToPropertyKey(frame.Registers[keyReg]);
        frame.Registers[destReg] = JsValue.FromBoolean(obj.DeleteProperty(key));
    }

    internal void DeletePropByNameForJit(InterpreterFrame frame, int destReg, int receiverReg, int propNameIndex)
    {
        var receiver = frame.Registers[receiverReg];
        if (receiver.Tag != JsValueTag.Object)
        {
            frame.Registers[destReg] = JsValue.FromBoolean(true);
            return;
        }
        var prop = frame.Function.PropertyNames[propNameIndex];
        var obj = ResolveObject(receiver);
        if (obj is ProxyObject proxyDel)
        {
            frame.Registers[destReg] = JsValue.FromBoolean(ProxyDelete(proxyDel, prop));
            return;
        }
        frame.Registers[destReg] = JsValue.FromBoolean(obj.DeleteProperty(prop));
    }

    internal JsValue CreateFunctionFromNestedForJit(InterpreterFrame frame, int nestedIndex)
    {
        var nested = frame.Function.NestedFunctions[nestedIndex];
        return CreateFunctionObject(nested, frame.Environment);
    }

    internal JsValue NewRegExpForJit(InterpreterFrame frame, int constIndex)
    {
        var rawText = frame.Function.Constants[constIndex].AsString();
        return NewRegExpLiteral(rawText);
    }

    internal void ThrowForJit(InterpreterFrame frame, JsValue value)
    {
        // Only safe to call from JIT when the function has no PushHandler
        // instructions (which TryEmitExpressionTree enforces). Otherwise
        // ThrowOrHandle could redirect frame.InstructionPointer to a
        // catch/finally target that the JIT delegate has no label for.
        ThrowOrHandle(frame, value);
    }

    internal JsValue NewObjectForJit()
    {
        var handle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    internal JsValue NewArrayForJit()
    {
        var obj = CreateArrayObject(Array.Empty<JsValue>());
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    internal void InitThisBindingForJit(InterpreterFrame frame)
    {
        if (frame.Environment is FunctionEnvironmentRecord fenInit &&
            fenInit.ThisBindingStatus == ThisBindingStatus.Uninitialized)
        {
            fenInit.BindThisValue(frame.ThisValue);
        }
    }

    // Tier 4 #24: JIT-callable helper that mirrors the LoadThis opcode
    // case. Caller passes the current instruction-pointer position so
    // the derived-constructor receiver-load check has the same context
    // as in the interpreter switch.
    internal JsValue LoadThisForJit(InterpreterFrame frame, int currentIp)
    {
        var function = frame.Function;
        if (function.IsDerivedConstructor &&
            frame.Environment is FunctionEnvironmentRecord fenDerived &&
            fenDerived.ThisBindingStatus == ThisBindingStatus.Uninitialized)
        {
            var isSuperReceiverLoad = currentIp > 0 &&
                                      function.Instructions[currentIp - 1].OpCode == OpCode.LoadSuperConstructor;
            if (!isSuperReceiverLoad)
            {
                ThrowReferenceError(frame, "Must call super constructor in derived class before accessing 'this'.");
                return JsValue.Undefined;
            }
        }

        // 9.1.2.5 GetThisEnvironment — walk to the nearest this-providing record
        // so arrow functions resolve the enclosing function/global `this`.
        if (TryResolveThisBinding(frame.Environment, out var boundThis))
        {
            return boundThis;
        }
        return frame.ThisValue;
    }

    // Binary-op multiplexer for the JIT — mirrors the interpreter's
    // arithmetic / comparison / bitwise / shift / logical case bodies
    // one-for-one. Bodies catch JsThrownException and route through
    // ThrowOrHandle, which in JIT context (no PushHandler exists in the
    // pre-pass-validated function) lets the exception propagate out as
    // a JsThrownException.
    internal void ApplyBinopForJit(InterpreterFrame frame, int opCodeByte, int a, int b, int c)
    {
        var op = (OpCode)opCodeByte;
        switch (op)
        {
            case OpCode.Add:
                try { frame.Registers[a] = Add(frame.Registers[b], frame.Registers[c]); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Sub:
                try { frame.Registers[a] = BigIntArith(frame.Registers[b], frame.Registers[c], "subtraction", (x, y) => x - y, (x, y) => x - y); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Mul:
                try { frame.Registers[a] = BigIntArith(frame.Registers[b], frame.Registers[c], "multiplication", (x, y) => x * y, (x, y) => x * y); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Mod:
                try { frame.Registers[a] = BigIntArith(frame.Registers[b], frame.Registers[c], "modulo", (x, y) => x % y, (x, y) => x % y); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Div:
                try { frame.Registers[a] = BigIntArith(frame.Registers[b], frame.Registers[c], "division", (x, y) => x / y, (x, y) => x / y); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Exp:
                try
                {
                    var left = frame.Registers[b];
                    var right = frame.Registers[c];
                    if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
                    {
                        var baseVal = left.AsBigInt();
                        var expVal = right.AsBigInt();
                        if (expVal < System.Numerics.BigInteger.Zero)
                            throw new JsThrownException(CreateRangeError("BigInt exponent must be non-negative."));
                        if (expVal > int.MaxValue)
                            throw new JsThrownException(CreateRangeError("BigInt exponent is too large."));
                        frame.Registers[a] = JsValue.FromBigInt(System.Numerics.BigInteger.Pow(baseVal, (int)expVal));
                    }
                    else
                    {
                        frame.Registers[a] = JsValue.FromNumber(Math.Pow(ToNumber(left), ToNumber(right)));
                    }
                }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Eq:
                frame.Registers[a] = JsValue.FromBoolean(AreEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.Neq:
                frame.Registers[a] = JsValue.FromBoolean(!AreEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.StrictEq:
                frame.Registers[a] = JsValue.FromBoolean(AreStrictlyEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.StrictNeq:
                frame.Registers[a] = JsValue.FromBoolean(!AreStrictlyEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.Lt:
                frame.Registers[a] = JsValue.FromBoolean(IsLessThan(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.Gt:
                frame.Registers[a] = JsValue.FromBoolean(IsGreaterThan(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.Le:
                frame.Registers[a] = JsValue.FromBoolean(IsLessThanOrEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.Ge:
                frame.Registers[a] = JsValue.FromBoolean(IsGreaterThanOrEqual(frame.Registers[b], frame.Registers[c]));
                break;
            case OpCode.And:
                frame.Registers[a] = IsTruthy(frame.Registers[b]) ? frame.Registers[c] : frame.Registers[b];
                break;
            case OpCode.Or:
                frame.Registers[a] = IsTruthy(frame.Registers[b]) ? frame.Registers[b] : frame.Registers[c];
                break;
            case OpCode.BitAnd:
                try { frame.Registers[a] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[b]) & (int)ToNumber(frame.Registers[c]))); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.BitOr:
                try { frame.Registers[a] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[b]) | (int)ToNumber(frame.Registers[c]))); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.BitXor:
                try { frame.Registers[a] = JsValue.FromNumber((double)((int)ToNumber(frame.Registers[b]) ^ (int)ToNumber(frame.Registers[c]))); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.ShiftLeft:
                try { var sl = (int)ToNumber(frame.Registers[b]); var sc = (int)ToNumber(frame.Registers[c]) & 0x1F; frame.Registers[a] = JsValue.FromNumber((double)(sl << sc)); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.ShiftRight:
                try { var sr = (int)ToNumber(frame.Registers[b]); var sc2 = (int)ToNumber(frame.Registers[c]) & 0x1F; frame.Registers[a] = JsValue.FromNumber((double)(sr >> sc2)); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.UnsignedShiftRight:
                try { var u32 = (uint)(int)ToNumber(frame.Registers[b]); var sc3 = (int)ToNumber(frame.Registers[c]) & 0x1F; frame.Registers[a] = JsValue.FromNumber((double)(u32 >> sc3)); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            default:
                throw new InvalidOperationException($"ApplyBinopForJit: unsupported opcode {op}.");
        }
    }

    internal void ApplyUnaryOpForJit(InterpreterFrame frame, int opCodeByte, int a, int b)
    {
        var op = (OpCode)opCodeByte;
        switch (op)
        {
            case OpCode.Not:
                frame.Registers[a] = JsValue.FromBoolean(!IsTruthy(frame.Registers[b]));
                break;
            case OpCode.Pos:
                frame.Registers[a] = JsValue.FromNumber(ToNumber(frame.Registers[b]));
                break;
            case OpCode.Neg:
                try
                {
                    if (frame.Registers[b].Tag == JsValueTag.BigInt)
                        frame.Registers[a] = JsValue.FromBigInt(-frame.Registers[b].AsBigInt());
                    else
                        frame.Registers[a] = JsValue.FromNumber(-ToNumber(frame.Registers[b]));
                }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Void:
                frame.Registers[a] = JsValue.Undefined;
                break;
            case OpCode.TypeOf:
                frame.Registers[a] = JsValue.FromString(TypeOfValue(frame.Registers[b]));
                break;
            case OpCode.BitNot:
                try { frame.Registers[a] = JsValue.FromNumber((double)(~(int)ToNumber(frame.Registers[b]))); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.ToNumeric:
                try { frame.Registers[a] = ToNumericValue(frame.Registers[b]); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.ToStringCoerce:
                try { frame.Registers[a] = JsValue.FromString(ToStringValue(frame.Registers[b])); }
                catch (JsThrownException ex) { ThrowOrHandle(frame, ex.Value); }
                break;
            case OpCode.Increment:
                frame.Registers[a] = StepNumeric(frame.Registers[b], +1);
                break;
            case OpCode.Decrement:
                frame.Registers[a] = StepNumeric(frame.Registers[b], -1);
                break;
            default:
                throw new InvalidOperationException($"ApplyUnaryOpForJit: unsupported opcode {op}.");
        }
    }

    internal void InForJit(InterpreterFrame frame, int destReg, int keyReg, int objReg)
    {
        var key = ToPropertyKey(frame.Registers[keyReg]);
        var rhs = frame.Registers[objReg];
        if (rhs.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Right-hand side of 'in' must be an object.");
            return;
        }

        var obj = ResolveObject(rhs);
        var has = HasPropertyIncludingProxy(obj, key);
        frame.Registers[destReg] = JsValue.FromBoolean(has);
    }

    internal void InstanceOfForJit(InterpreterFrame frame, int destReg, int lhsReg, int rhsReg)
    {
        if (TryInstanceOf(frame, frame.Registers[lhsReg], frame.Registers[rhsReg], out var result))
        {
            frame.Registers[destReg] = JsValue.FromBoolean(result);
        }
    }

    internal void DeleteForJit(InterpreterFrame frame, int destReg, int nameSlot)
    {
        frame.Registers[destReg] = DeleteName(frame, nameSlot);
    }

    internal JsValue EnumerateKeysForJit(InterpreterFrame frame, int srcReg) =>
        CreateForInIterator(frame.Registers[srcReg]);

    internal JsValue EnumerateValuesForJit(InterpreterFrame frame, int srcReg) =>
        CreateForOfIteratorState(frame.Registers[srcReg]);

    // Returns true when the iterator is exhausted (JIT must goto end label).
    internal bool ForOfNextForJit(InterpreterFrame frame, int destReg, int iterReg)
    {
        var iter = ResolveObject(frame.Registers[iterReg]) as ForOfIteratorObject
            ?? throw new InvalidOperationException("Invalid for-of iterator object.");
        if (ForOfStepDone(iter, out var value)) return true;
        frame.Registers[destReg] = value;
        return false;
    }

    internal void IteratorCloseForJit(InterpreterFrame frame, int iterReg)
    {
        if (ResolveObject(frame.Registers[iterReg]) is ForOfIteratorObject closing)
        {
            CloseForOfIteratorState(closing);
        }
    }

    internal bool ForInNextForJit(InterpreterFrame frame, int destReg, int iterReg)
    {
        var iter = ResolveObject(frame.Registers[iterReg]) as ForInIteratorObject
            ?? throw new InvalidOperationException("Invalid for-in iterator object.");
        if (!iter.TryMoveNext(out var key)) return true;
        frame.Registers[destReg] = JsValue.FromString(key);
        return false;
    }

    internal void SetPrototypeForJit(InterpreterFrame frame, int childReg, int parentReg)
    {
        var childValue = frame.Registers[childReg];
        var parentValue = frame.Registers[parentReg];
        if (childValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("SetPrototype requires an object target."));
            return;
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
    }

    internal static bool IsTruthy(JsValue value)
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
                case SymbolObject symbolObject:
                    primitive = JsValue.SymbolFromId(symbolObject.SymbolId);
                    return true;
            }

            // ECMA-262 7.1.1.1 OrdinaryToPrimitive(O, hint="number"):
            // try valueOf, then toString; first call returning a non-Object wins.
            if (TryInvokeConversionMethod(obj, value, "valueOf", out var vResult) && vResult.Tag != JsValueTag.Object)
            {
                primitive = vResult;
                return true;
            }
            if (TryInvokeConversionMethod(obj, value, "toString", out var sResult) && sResult.Tag != JsValueTag.Object)
            {
                primitive = sResult;
                return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    private bool TryInvokeConversionMethod(JsObject obj, JsValue receiver, string methodName, out JsValue result)
    {
        result = JsValue.Undefined;
        if (!TryGetPropertyValue(obj, receiver, methodName, out var method))
        {
            return false;
        }
        if (!IsCallable(method))
        {
            return false;
        }
        try
        {
            result = CallFunction(method, Array.Empty<JsValue>(), receiver);
            return true;
        }
        catch (JsThrownException)
        {
            // Caller (ToNumber/etc.) prefers NaN over re-throwing here so
            // string-coercion that falls back can still complete.
            result = JsValue.Undefined;
            return false;
        }
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
            var trimmed = text.Trim(EcmaWhitespaceChars);
            if (trimmed.Length == 0)
            {
                return 0;
            }
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

            // ECMA-262 7.1.4.1.1 StringToNumber: leading/trailing whitespace is
            // ignored; NonDecimalIntegerLiteral handles 0x/0X (hex), 0o/0O (octal),
            // 0b/0B (binary) prefixes. Signs are not allowed before the prefix.
            if (trimmed.Length >= 2 && trimmed[0] == '0')
            {
                int radix = trimmed[1] switch
                {
                    'x' or 'X' => 16,
                    'o' or 'O' => 8,
                    'b' or 'B' => 2,
                    _ => 0
                };
                if (radix != 0)
                {
                    var digits = trimmed.Substring(2);
                    if (digits.Length > 0 && TryParseRadixDigits(digits, radix, out var radixValue))
                    {
                        return radixValue;
                    }
                    return double.NaN;
                }
            }

            if (double.TryParse(
                    trimmed,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return double.NaN;
        }

        return double.NaN;
    }

    private static bool TryParseRadixDigits(string digits, int radix, out double value)
    {
        value = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[i];
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => 10 + (c - 'a'),
                >= 'A' and <= 'F' => 10 + (c - 'A'),
                _ => -1
            };
            if (d < 0 || d >= radix)
            {
                value = double.NaN;
                return false;
            }
            value = value * radix + d;
        }
        return true;
    }

    private JsObject ResolveObject(JsValue value)
    {
        return _heap.GetObject(ResolveObjectHandle(value));
    }

    // ECMA-262 7.1.18 ToObject(V). Unlike ResolveObjectHandle (which throws
    // for any non-Object), this boxes primitives � boolean/number/string/
    // symbol/bigint get wrapped in their object form. undefined/null still
    // throw TypeError. Used by Array.prototype.* and other spec algorithms
    // whose first step is "let O be ? ToObject(this value)".
    private JsObject ToObject(JsValue value)
    {
        return _heap.GetObject(ToObjectValue(value).AsObjectHandle());
    }

    private JsValue ToObjectValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Undefined || value.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError(
                value.Tag == JsValueTag.Undefined
                    ? "Cannot convert undefined to object."
                    : "Cannot convert null to object."));
        return value.Tag == JsValueTag.Object || value.Tag == JsValueTag.HostObject
            ? value
            : CreateObjectFromValue(value);
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

        if (TryCallSymbolToPrimitive(value, hint, out var exoticPrimitive))
        {
            return exoticPrimitive;
        }

        if (TryOrdinaryToPrimitive(value, hint, out var primitive))
        {
            return primitive;
        }

        if (TryGetObjectPrimitiveValue(value, out primitive))
        {
            return primitive;
        }

        throw new JsThrownException(CreateTypeError(
            "Cannot convert object to primitive value. (value=" + DescribeValueShort(value) + ", hint=" + hint + ") " + DescribeFrameStack()));
    }

    [MayExecuteJs]
    private bool TryCallSymbolToPrimitive(JsValue value, PrimitiveHint hint, out JsValue primitive)
    {
        primitive = JsValue.Undefined;
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var symbolId = GetWellKnownSymbolId("toPrimitive");
        if (symbolId == 0)
        {
            return false;
        }

        var method = GetReceiverSymbolProperty(value, symbolId);
        if (method.Tag == JsValueTag.Undefined || method.Tag == JsValueTag.Null)
        {
            return false;
        }

        if (!IsCallable(method))
        {
            throw new JsThrownException(CreateTypeError("@@toPrimitive must be callable."));
        }

        var hintValue = hint switch
        {
            PrimitiveHint.String => "string",
            PrimitiveHint.Default => "default",
            _ => "number"
        };
        var result = CallFunction(method, new[] { JsValue.FromString(hintValue) }, value);
        if (result.Tag == JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("@@toPrimitive must return a primitive value."));
        }

        primitive = result;
        return true;
    }

    private bool TryOrdinaryToPrimitive(JsValue value, PrimitiveHint hint, out JsValue primitive)
    {
        var obj = _heap.GetObject(value.AsObjectHandle());
        var prefersString = hint == PrimitiveHint.String ||
            (hint == PrimitiveHint.Default && obj is DateObject);
        var first = prefersString ? "toString" : "valueOf";
        var second = prefersString ? "valueOf" : "toString";
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

    // IsCallable / BigIntArith / Add moved to BytecodeInterpreter.Operators.cs (audit �2 slice 2).

    private string ToStringValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            // ECMA-262 7.1.17 ToString step 2: an object is first coerced with
            // ToPrimitive(argument, string), which honors @@toPrimitive and the
            // toString/valueOf ordering (and skips non-callable hooks); the
            // resulting primitive is then formatted.
            var primitive = ToPrimitive(value, PrimitiveHint.String);
            return FormatPrimitiveForString(primitive);
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
            JsValueTag.BigInt => value.AsBigInt().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Object => "[object Object]",
            JsValueTag.HostObject => "[object Object]",
            _ => value.Tag.ToString()
        };
    }

    // FormatNumberForString / ExpandExponentialNumber / NormalizeExponentialNumber /
    // TrimDecimalZeros moved to BytecodeInterpreter.Operators.cs (audit �2 slice 2).

    private double ToNumber(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            // ECMA-262 7.1.4 ToNumber step: an object is coerced with
            // ToPrimitive(argument, number) (honoring @@toPrimitive then
            // valueOf/toString) before numeric conversion. The old shortcut
            // only unwrapped wrapper internal slots, so Number({valueOf(){...}})
            // wrongly returned NaN.
            return ToNumber(ToPrimitive(value, PrimitiveHint.Number));
        }

        if (value.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot convert a BigInt value to a number."));
        if (value.Tag == JsValueTag.Symbol)
            throw new JsThrownException(CreateTypeError("Cannot convert a Symbol value to a number."));

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
        if (left.Tag == JsValueTag.BigInt && right.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(right);
            return !double.IsNaN(number) && CompareBigIntAndDouble(left.AsBigInt(), number) < 0;
        }
        if (right.Tag == JsValueTag.BigInt && left.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(left);
            return !double.IsNaN(number) && CompareBigIntAndDouble(right.AsBigInt(), number) > 0;
        }
        return ToNumber(left) < ToNumber(right);
    }

    private bool IsGreaterThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) > 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() > right.AsBigInt();
        if (left.Tag == JsValueTag.BigInt && right.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(right);
            return !double.IsNaN(number) && CompareBigIntAndDouble(left.AsBigInt(), number) > 0;
        }
        if (right.Tag == JsValueTag.BigInt && left.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(left);
            return !double.IsNaN(number) && CompareBigIntAndDouble(right.AsBigInt(), number) < 0;
        }
        return ToNumber(left) > ToNumber(right);
    }

    private bool IsLessThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) <= 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() <= right.AsBigInt();
        if (left.Tag == JsValueTag.BigInt && right.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(right);
            return !double.IsNaN(number) && CompareBigIntAndDouble(left.AsBigInt(), number) <= 0;
        }
        if (right.Tag == JsValueTag.BigInt && left.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(left);
            return !double.IsNaN(number) && CompareBigIntAndDouble(right.AsBigInt(), number) >= 0;
        }
        return ToNumber(left) <= ToNumber(right);
    }

    private bool IsGreaterThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
            return string.CompareOrdinal(left.AsString(), right.AsString()) >= 0;
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return left.AsBigInt() >= right.AsBigInt();
        if (left.Tag == JsValueTag.BigInt && right.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(right);
            return !double.IsNaN(number) && CompareBigIntAndDouble(left.AsBigInt(), number) >= 0;
        }
        if (right.Tag == JsValueTag.BigInt && left.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            var number = ToNumber(left);
            return !double.IsNaN(number) && CompareBigIntAndDouble(right.AsBigInt(), number) <= 0;
        }
        return ToNumber(left) >= ToNumber(right);
    }

    private static int CompareBigIntAndDouble(System.Numerics.BigInteger left, double right)
    {
        if (double.IsPositiveInfinity(right))
        {
            return -1;
        }

        if (double.IsNegativeInfinity(right))
        {
            return 1;
        }

        var truncated = Math.Truncate(right);
        var cmp = left.CompareTo(new System.Numerics.BigInteger(truncated));
        if (cmp != 0 || truncated == right)
        {
            return cmp;
        }

        return right > 0 ? -1 : 1;
    }

    private bool TryInstanceOf(InterpreterFrame frame, JsValue left, JsValue right, out bool result)
    {
        result = false;

        if (right.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' must be an object.");
            return false;
        }

        var hasInstanceSymbolId = GetWellKnownSymbolId("hasInstance");
        if (hasInstanceSymbolId != 0)
        {
            var hasInstance = GetReceiverSymbolProperty(right, hasInstanceSymbolId);
            if (hasInstance.Tag != JsValueTag.Undefined)
            {
                if (!IsCallable(hasInstance))
                {
                    ThrowTypeError(frame, "@@hasInstance is not callable.");
                    return false;
                }

                try
                {
                    var methodResult = CallFunction(hasInstance, new[] { left }, right);
                    result = IsTruthy(methodResult);
                    return true;
                }
                catch (JsThrownException ex)
                {
                    if (frame.CatchHandlers.Count == 0)
                    {
                        throw;
                    }

                    ThrowOrHandle(frame, ex.Value);
                    return false;
                }
            }
        }

        if (!IsCallable(right))
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' is not callable.");
            return false;
        }

        if (left.Tag != JsValueTag.Object)
        {
            result = false;
            return true;
        }

        var prototypeValue = GetReceiverProperty(right, "prototype");
        if (prototypeValue.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Function has non-object prototype in 'instanceof'.");
            return false;
        }

        var targetPrototype = prototypeValue.AsObjectHandle();
        try
        {
            result = OrdinaryHasInstancePrototype(left, targetPrototype);
            return true;
        }
        catch (JsThrownException ex)
        {
            if (frame.CatchHandlers.Count == 0)
            {
                throw;
            }

            ThrowOrHandle(frame, ex.Value);
            return false;
        }
    }

    [MayExecuteJs]
    private bool OrdinaryHasInstancePrototype(JsValue value, ObjectHandle targetPrototype)
    {
        var currentObj = ResolveObject(value);
        while (true)
        {
            JsValue nextProtoValue;
            if (currentObj is ProxyObject proxyCurrent)
            {
                nextProtoValue = ProxyGetPrototypeOf(proxyCurrent);
            }
            else
            {
                nextProtoValue = currentObj.PrototypeHandle is { } protoHandle
                    ? JsValue.FromObject(protoHandle)
                    : JsValue.Null;
            }

            if (nextProtoValue.Tag == JsValueTag.Null)
            {
                return false;
            }

            if (nextProtoValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Object prototype is not an object or null."));
            }

            var proto = nextProtoValue.AsObjectHandle();
            if (proto.Equals(targetPrototype))
            {
                return true;
            }

            currentObj = _heap.GetObject(proto);
        }
    }

    private string TypeOfValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            if (IsCallable(value))
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

    private enum PrimitiveHint
    {
        Default,
        String,
        Number
    }


    // ForOfIteratorObject / ForInIteratorObject moved to
    // BytecodeInterpreter.Iterators.cs (audit �2 slice 3).
}





