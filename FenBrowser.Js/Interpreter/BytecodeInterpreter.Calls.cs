using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Diagnostics;

namespace FenBrowser.Js.Interpreter;

/// <summary>
/// One call's arguments, as a window onto where they already are.
/// </summary>
/// <remarks>
/// A call site's arguments are already contiguous in the caller's register
/// file, so this names that range rather than copying it. The struct that did
/// copy held four inline <see cref="JsValue"/>s — 112 bytes carrying four
/// object references — and every hop of a call passed it through a
/// write-barriered bulk move, which measured a third of engine time on a
/// bundle-shaped workload. Three fields fit in registers and copy for free.
///
/// The window is only valid for the call it describes. That is safe because a
/// caller's registers cannot change while the callee runs: only the caller's
/// own instruction stream writes them, and it is suspended.
/// </remarks>
internal readonly struct CallArgs : IReadOnlyList<JsValue>
{
    private readonly JsValue[]? _values;
    private readonly int _start;
    private readonly int _count;

    private CallArgs(JsValue[]? values, int start, int count)
    {
        _values = values;
        _start = start;
        _count = count;
    }

    public CallArgs(JsValue arg0)
        : this([arg0], 0, 1)
    {
    }

    public CallArgs(JsValue arg0, JsValue arg1)
        : this([arg0, arg1], 0, 2)
    {
    }

    public CallArgs(JsValue arg0, JsValue arg1, JsValue arg2)
        : this([arg0, arg1, arg2], 0, 3)
    {
    }

    public CallArgs(JsValue arg0, JsValue arg1, JsValue arg2, JsValue arg3)
        : this([arg0, arg1, arg2, arg3], 0, 4)
    {
    }

    public CallArgs(JsValue[] args)
        : this(args, 0, args.Length)
    {
    }

    public CallArgs(IReadOnlyList<JsValue> args)
        : this(args as JsValue[] ?? args.ToArray(), 0, args.Count)
    {
    }

    public int Count => _count;

    public JsValue this[int index]
        => (uint)index < (uint)_count ? _values![_start + index] : JsValue.Undefined;

    public IEnumerator<JsValue> GetEnumerator()
    {
        for (var i = 0; i < _count; i++) yield return _values![_start + i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator CallArgs(JsValue[] args) => new(args);
    public static CallArgs Empty => default;

    // The arguments a bytecode call site passes are already laid out in the
    // caller's registers, so this is the whole of argument marshalling.
    public static CallArgs FromRegisters(JsValue[] registers, int start, int count)
        => new(registers, start, count);

    public static CallArgs FromList(IReadOnlyList<JsValue> values, int start, int count)
        => values is JsValue[] array ? new(array, start, count) : CopyList(values, start, count);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static CallArgs CopyList(IReadOnlyList<JsValue> values, int start, int count)
    {
        var args = new JsValue[count];
        for (var i = 0; i < count; i++) args[i] = values[start + i];
        return new CallArgs(args, 0, count);
    }
}

public sealed partial class BytecodeInterpreter
{
    // Tier 4 #24: JIT helpers for the call/construct opcode family.
    // Small argument lists stay inline in CallArgs; only calls with more than
    // four arguments allocate an array. Dispatch then uses StoreCallResult /
    // StoreConstructResult. icOffset is the call-site offset for the
    // Call IC; passed as a JIT-compile-time constant.
    internal void Call0ForJit(InterpreterFrame frame, int destReg, int calleeReg, int directEvalFlag, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            CallArgs.Empty, JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void Call1ForJit(InterpreterFrame frame, int destReg, int calleeReg, int argReg, int directEvalFlag, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            new CallArgs(frame.Registers[argReg]), JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void CallNForJit(InterpreterFrame frame, int destReg, int calleeReg, int argStartReg, int argCount, int directEvalFlag, int icOffset)
    {
        var callArgs = CallArgs.FromRegisters(frame.Registers, argStartReg, argCount);
        StoreCallResult(frame, destReg, frame.Registers[calleeReg], callArgs, JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void CallMethod0ForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            CallArgs.Empty, frame.Registers[thisReg], icOffset: icOffset);
    }


    internal void CallMethod1ForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int argReg, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            new CallArgs(frame.Registers[argReg]), frame.Registers[thisReg], icOffset: icOffset);
    }


    internal void CallMethodNForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int argStartReg, int argCount, int icOffset)
    {
        var callArgs = CallArgs.FromRegisters(frame.Registers, argStartReg, argCount);
        StoreCallResult(frame, destReg, frame.Registers[calleeReg], callArgs, frame.Registers[thisReg], icOffset: icOffset);
    }


    internal void Construct0ForJit(InterpreterFrame frame, int destReg, int ctorReg)
    {
        StoreConstructResult(frame, destReg, frame.Registers[ctorReg], Array.Empty<JsValue>());
    }


    internal void Construct1ForJit(InterpreterFrame frame, int destReg, int ctorReg, int argReg)
    {
        StoreConstructResult(frame, destReg, frame.Registers[ctorReg], new[] { frame.Registers[argReg] });
    }


    internal void ConstructNForJit(InterpreterFrame frame, int destReg, int ctorReg, int argStartReg, int argCount)
    {
        var args = new JsValue[argCount];
        for (var i = 0; i < argCount; i++) args[i] = frame.Registers[argStartReg + i];
        StoreConstructResult(frame, destReg, frame.Registers[ctorReg], args);
    }


    internal void CallSpreadForJit(InterpreterFrame frame, int destReg, int calleeReg, int spreadReg, int thisReg)
    {
        var spreadArray = frame.Registers[spreadReg];
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

        var thisValue = thisReg == 0 ? JsValue.Undefined : frame.Registers[thisReg];
        StoreCallResult(frame, destReg, frame.Registers[calleeReg], unpackedArgs, thisValue, icOffset: -1);
    }


    // Native-interleave return pin: a value returned from JS into a CLR/native
    // caller is held in a C# local the tracer cannot see. A safe-point
    // collection inside a nested call (or a lazy builtin install) can then
    // sweep the result before the caller roots it. A bounded FIFO of recent
    // returns keeps such results reachable far longer than any native body
    // needs; slots are simply overwritten as new calls return.
    private void PinReturnValue(JsValue value, JsValue callee)
    {
        // The ring is a GC root, so whatever lands here is dereferenced by
        // every later collection. A handle this heap cannot resolve therefore
        // does not merely fail to keep anything alive — it kills the collector
        // from inside its own mark phase, on every collection from here on,
        // long after the call that produced it returned. Screen it at the
        // door: the heap's own allocation pin ring has guarded its entries
        // this way from the start (JsHeap.TraceAllocationPinRing), and this
        // ring, built to the same design, never did.
        if (value.Tag == JsValueTag.Object &&
            !_heap.IsLiveObjectHandle(value.AsObjectHandle()))
        {
            ReportUnresolvableReturnPin(value, callee);
            return;
        }

        _returnPinRing[_returnPinIndex] = value;
        _returnPinProvenance[_returnPinIndex] = ReturnPinAuditEnabled
            ? DescribeReturnPin(value, callee)
            : null;
        _returnPinIndex = (_returnPinIndex + 1) & ReturnPinRingMask;
    }

    // A pinned return whose handle does not resolve in this heap is the moment
    // the root set would have been poisoned, and it is the only moment at
    // which the producing call is still on the stack. Report it here, with the
    // callee's structural signature, rather than at the next collection where
    // all that survives is an index.
    private void ReportUnresolvableReturnPin(JsValue value, JsValue callee)
    {
        var handle = value.AsObjectHandle();
        var key = $"{handle.Index}/{handle.Generation}";
        if (!_reportedReturnPinFailures.Add(key))
        {
            return;
        }

        var report =
            "Unresolvable return pin (not stored as a GC root). " +
            _heap.DescribeHandleForDiagnostics(handle) +
            " callee=" + DescribeCalleeForDiagnostics(callee) +
            " jsStack=" + FormatCallStackForDiagnostics();
        _returnPinFailureReports.Add(report);
        FenBrowser.Js.Heap.JsHeap.DiagnosticSink?.Invoke(report);
    }

    private string DescribeReturnPin(JsValue value, JsValue callee)
    {
        var payload = "?";
        if (value.Tag == JsValueTag.Object)
        {
            try
            {
                var obj = _heap.GetObject(value.AsObjectHandle());
                payload = obj is JsFunctionObject fn
                    ? FenBrowser.Js.Bytecode.BytecodeFunctionSignature.Describe(fn.Function)
                    : obj.GetType().Name;
            }
            catch (JsEngineFatalException)
            {
                payload = "<unresolvable>";
            }
        }
        else
        {
            payload = value.Tag.ToString();
        }

        return $"value={payload} callee={DescribeCalleeForDiagnostics(callee)}";
    }

    // Names the function whose return was pinned. Minified bundles are almost
    // all anonymous, so a name alone identifies nothing; the structural
    // signature does.
    private string DescribeCalleeForDiagnostics(JsValue callee)
    {
        if (callee.Tag != JsValueTag.Object)
        {
            return callee.Tag.ToString();
        }

        try
        {
            return _heap.GetObject(callee.AsObjectHandle()) switch
            {
                JsFunctionObject fn =>
                    FenBrowser.Js.Bytecode.BytecodeFunctionSignature.Describe(fn.Function),
                NativeFunctionObject native => $"native:{native.Name}",
                { } other => other.GetType().Name,
                _ => "<null>"
            };
        }
        catch (JsEngineFatalException)
        {
            return "<unresolvable-callee>";
        }
    }

    private string FormatCallStackForDiagnostics()
    {
        try
        {
            return FormatCallStack("ReturnPin", "unresolvable");
        }
        catch (JsEngineFatalException)
        {
            return "<unavailable>";
        }
    }

    private JsValue CallFunction(JsValue value, in CallArgs args, JsValue thisValue)
    {
        if (!FenBrowser.Js.Diagnostics.CallTargetProfiler.Enabled)
        {
            var fastResult = CallFunctionCore(value, args, thisValue);
            PinReturnValue(fastResult, value);
            return fastResult;
        }

        var target = CaptureCallTargetForDiagnostics(value);
        var previousTarget = Volatile.Read(ref _diagnosticActiveCallTarget);
        Volatile.Write(ref _diagnosticActiveCallTarget, target);
        var sample = FenBrowser.Js.Diagnostics.CallTargetProfiler.Begin(target);
        try
        {
            var result = CallFunctionCore(value, args, thisValue);
            PinReturnValue(result, value);
            return result;
        }
        finally
        {
            FenBrowser.Js.Diagnostics.CallTargetProfiler.End(sample);
            Volatile.Write(ref _diagnosticActiveCallTarget, previousTarget);
        }
    }

    private JsValue CallFunction(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
        => CallFunction(value, new CallArgs(args.Count > 4 ? args.ToArray() : args), thisValue);

    private JsValue CallFunctionCore(JsValue value, in CallArgs args, JsValue thisValue)
    {
        if (value.Tag == JsValueTag.Undefined || value.Tag == JsValueTag.Null)
        {
            return ThrowCallTargetIsNullish(value, args, thisValue);
        }

        var obj = ResolveObject(value);

        // ECMA-262 9.5.12 Proxy [[Call]].
        if (obj is ProxyObject proxyCall)
        {
            if (!IsCallableTarget(proxyCall.TargetHandle))
            {
                throw new JsThrownException(CreateTypeError("Proxy target is not callable."));
            }
            return ProxyCall(proxyCall, args, thisValue);
        }

        // ECMA-262 10.4.1.3 [[Call]] â€” merge bound args + call-site args,
        // then delegate to [[BoundTargetFunction]] with [[BoundThis]].
        if (obj is BoundFunctionObject bound)
        {
            var merged = MergeBoundArgs(bound.BoundArgs, args);
            return CallFunction(bound.TargetFunction, merged, bound.BoundThis);
        }

        if (obj is JsFunctionObject fn)
        {
            if (fn.Kind == FunctionKind.Constructor)
            {
                throw new JsThrownException(CreateTypeError("Class constructor cannot be invoked without 'new'."));
            }

            if (fn.Kind == FunctionKind.Async)
            {
                return CallAsyncFunctionBody(fn, args, thisValue);
            }

            if (fn.Kind == FunctionKind.Generator)
            {
                return CallGeneratorFunctionBody(value, fn, args, thisValue);
            }

            if (fn.Kind == FunctionKind.AsyncGenerator)
            {
                return CallAsyncGeneratorFunctionBody(value, fn, args, thisValue);
            }

            // Tier 4 #24: tier-up counter. The JIT delegate is invoked
            // from inside ExecuteInternalCore (after frame setup) so it
            // can access frame.Registers, frame.Environment, and the
            // interpreter's helper methods.
            var bcFn = fn.Function;
            bcFn.Invocations++;
#if !PUBLISH_AOT
            // Tier-4 #24 (audit §3.2): combined invocation + back-edge
            // trigger. Either 1000 calls OR 100,000 cross-call loop
            // iterations OR a balanced mix gets the function JIT-compiled.
            // BackEdgeScale=100 keeps the legacy "100 invocations" cliff
            // intact while letting one-call loop-heavy functions tier up.
            // Default TierUpThreshold=100 avoids over-compilation on complex
            // pages like reCAPTCHA (which compiled ~500 functions at the old
            // threshold of 10 and ran slower overall than with JIT disabled).
            //
            // On the register-window loop a call count alone never earns a
            // compile: compiled code is entered through this loop's frame setup,
            // which costs more than the call it replaces unless the body loops.
            // Interp2 asks for loop-heavy bodies itself (JitCompiler.RequestLoopTierUp).
            const int BackEdgeScale = 100;
            if (!Interpreter2.Interp2Options.Enabled &&
                !bcFn.JitCompileAttempted &&
                (long)bcFn.Invocations * BackEdgeScale + bcFn.BackEdges
                    >= (long)JitCompiler.TierUpThreshold * BackEdgeScale)
            {
                bcFn.JitCompileAttempted = true;
                JitCompiler.RequestCompile(bcFn);
            }
#endif
            // The register-window loop, when it is switched on and this body is
            // one it can run. Both loops read this same bytecode and share the
            // heap, the builtins and the inline caches, so a call can cross
            // between them in either direction at any depth - which is what lets
            // test262 run on both and say exactly what the new one changes.
            // A loop-heavy body that has been compiled runs compiled instead,
            // through ExecuteInternal below.
            if (Interpreter2.Interp2Options.Enabled
#if !PUBLISH_AOT
                && !JitCompiler.PrefersCompiled(bcFn)
#endif
                )
            {
                var layout = Interpreter2.FrameLayout.For(bcFn);
                if (layout.Eligible)
                {
                    return Interp2Execute(fn, layout, args, thisValue);
                }
            }

            if (FenBrowser.Js.Diagnostics.CompiledCodeCoverage.Enabled &&
                !fn.Function.CoverageEntryRecorded)
            {
                fn.Function.CoverageEntryRecorded = true;
                FenBrowser.Js.Diagnostics.CompiledCodeCoverage.RecordFirstEntry(
                    fn.Function.Instructions.Count);
            }

            return ExecuteInternal(fn.Function, args, thisValue, ResolveFunctionOuterEnvironment(fn), callee: fn);
        }

        if (obj is NativeFunctionObject native)
        {
            return CallNativeFunctionBody(native, value, args, thisValue);
        }

        return ThrowCallTargetIsNotCallable(value, args, thisValue);
    }

    // ECMA-262 27.7.5.1 AsyncFunction [[Call]]. Kept out of the dispatcher: it
    // is one branch in a hundred calls, and its locals would otherwise widen
    // every ordinary call's stack frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue CallAsyncFunctionBody(JsFunctionObject fn, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        var capability = NewPromiseCapability();

        // Create an AsyncContext to hold suspended state. If the body
        // never awaits, the context is unused and the fast path applies.
        // On the register-window loop the suspended state is a whole window,
        // so the array is sized for one.
        var windowLayout = Interpreter2.Interp2Options.Enabled
            ? Interpreter2.FrameLayout.For(fn.Function)
            : null;
        var runsOnRegisterWindow = windowLayout is { AsyncEligible: true };
        var registers = new JsValue[runsOnRegisterWindow
            ? Math.Max(windowLayout!.WindowSize, fn.Function.RegisterCount)
            : fn.Function.RegisterCount];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = JsValue.Undefined;
        var asyncCtx = new AsyncContext(fn.Function, registers, fn.OuterEnvironment)
        {
            ThisValue = thisValue,
            RunsOnRegisterWindow = runsOnRegisterWindow
        };
        var ctxHandle = _heap.AllocateObject(asyncCtx, AllocationSite.Current());
        asyncCtx.SelfHandle = ctxHandle;

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
            var result = runsOnRegisterWindow
                ? Interp2RunAsync(asyncCtx, fn, args as JsValue[] ?? System.Linq.Enumerable.ToArray(args), thisValue)
                : ExecuteInternal(fn.Function, new CallArgs(args), thisValue, fn.OuterEnvironment, callee: fn, asyncContext: asyncCtx);

            if (asyncCtx.IsSuspended)
            {
                // Body suspended at an await — resume callbacks already
                // attached. Audit JSRT-004: do NOT push a permanent root
                // here. Liveness now flows through the awaited promise's
                // reactions → resume callback capturedRoots → ctxHandle,
                // and through frame.AsyncContext while a frame is active.
                return capability.Promise;
            }

            // Body completed without suspension (no await encountered,
            // or all awaited promises were already settled).
            _ = CallFunction(capability.Resolve, new[] { result }, JsValue.Undefined);
        }
        catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
        {
            if (asyncCtx.IsSuspended)
            {
                _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
                return capability.Promise;
            }

            _ = CallFunction(capability.Reject, new[] { ex.Value }, JsValue.Undefined);
        }

        return capability.Promise;
    }

    // ECMA-262 27.5.1.1 generator [[Call]] - returns a GeneratorObject.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue CallGeneratorFunctionBody(JsValue value, JsFunctionObject fn, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        // ECMA-262 27.5.1.1 â€” calling a generator returns a GeneratorObject.
        // Spec runs FunctionDeclarationInstantiation (i.e. parameter binding,
        // including destructuring) synchronously before returning the
        // generator. If the function carries a PrologueEnd marker we run
        // the prologue here and let the generator suspend at the marker.
        // On the register-window loop a suspended generator holds a whole
        // window - the bytecode registers and the body's variables - so the
        // array it is saved into is sized for one. The loop binds the
        // parameters itself when it enters the body, from InitialArgs.
        var windowLayout = Interpreter2.Interp2Options.Enabled && fn.SelfHandle is not null
            ? Interpreter2.FrameLayout.For(fn.Function)
            : null;
        var runsOnRegisterWindow = windowLayout is { GeneratorEligible: true };
        var registers = new JsValue[runsOnRegisterWindow
            ? Math.Max(windowLayout!.WindowSize, fn.Function.RegisterCount)
            : fn.Function.RegisterCount];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = JsValue.Undefined;

        var genObj = new GeneratorObject(fn.Function, registers, fn.OuterEnvironment);
        genObj.RunsOnRegisterWindow = runsOnRegisterWindow;
        genObj.ThisValue = thisValue;
        genObj.InitialArgs = args as JsValue[] ?? System.Linq.Enumerable.ToArray(args);
        genObj.SelfHandle = fn.SelfHandle;
        // ECMA-262 14.4.11: FunctionDeclarationInstantiation runs
        // before OrdinaryCreateFromConstructor. Since our prologue
        // performs param binding (including default-param side-effects
        // that may mutate g.prototype), we allocate with a default
        // prototype, run the prologue, then set the correct prototype
        // per GetPrototypeFromConstructor.
        genObj.SetPrototype(GetGlobalPrototype("GeneratorPrototype"));
        var genHandle = _heap.AllocateObject(genObj, AllocationSite.Current());
        if (fn.Function.PrologueEndIp > 0)
        {
            RunGeneratorPrologue(genObj, fn.Function);
        }
        // Read g.prototype AFTER FunctionDeclarationInstantiation so
        // default-param mutations (e.g. g.prototype = null) take effect.
        var genProtoValue = GetReceiverProperty(value, "prototype");
        if (genProtoValue.Tag == JsValueTag.Object)
            genObj.SetPrototype(genProtoValue.AsObjectHandle());
        return JsValue.FromObject(genHandle);
    }

    // ECMA-262 27.6 async generator [[Call]].
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue CallAsyncGeneratorFunctionBody(JsValue value, JsFunctionObject fn, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        // Sized to hold a whole register window when the new loop runs it, for
        // the same reason a sync generator's is - see CallGeneratorFunctionBody.
        var windowLayout = Interpreter2.Interp2Options.Enabled && fn.SelfHandle is not null
            ? Interpreter2.FrameLayout.For(fn.Function)
            : null;
        var runsOnRegisterWindow = windowLayout is { GeneratorEligible: true };
        var registers = new JsValue[runsOnRegisterWindow
            ? Math.Max(windowLayout!.WindowSize, fn.Function.RegisterCount)
            : fn.Function.RegisterCount];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = JsValue.Undefined;

        var genObj = new GeneratorObject(fn.Function, registers, fn.OuterEnvironment)
        {
            ThisValue = thisValue,
            IsAsyncGenerator = true,
            RunsOnRegisterWindow = runsOnRegisterWindow,
            InitialArgs = args as JsValue[] ?? System.Linq.Enumerable.ToArray(args),
            SelfHandle = fn.SelfHandle
        };
        genObj.SetPrototype(EnsureAsyncGeneratorPrototype());
        var genHandle = _heap.AllocateObject(genObj, AllocationSite.Current());
        if (fn.Function.PrologueEndIp > 0)
        {
            RunGeneratorPrologue(genObj, fn.Function);
        }
        // Read g.prototype AFTER FunctionDeclarationInstantiation so
        // default-param mutations (e.g. g.prototype = null) take effect.
        var asyncGenProtoValue = GetReceiverProperty(value, "prototype");
        if (asyncGenProtoValue.Tag == JsValueTag.Object)
            genObj.SetPrototype(asyncGenProtoValue.AsObjectHandle());
        return JsValue.FromObject(genHandle);
    }

    // ECMA-262 native [[Call]], with the root pinning a native body needs.
    private JsValue CallNativeFunctionBody(NativeFunctionObject native, JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        // ECMA-262 native function calls execute in C# without a bytecode
        // InterpreterFrame on top of the call stack, so the JS heap's GC
        // root walk (which traverses _activeFrames) cannot see the JsValue
        // arguments and `thisValue` we are about to hand the native body.
        // If the native callback triggers an auto-MinorCollect (e.g. via
        // CreateTypeError, AllocateObject), object-tagged values on the
        // C# stack could be reclaimed and resurface as "Stale heap handle"
        // on the next access. Pin them for the duration of the call.
        if (NativeCallStats.Enabled) return CallNativeFunctionBodyMeasured(native, value, args, thisValue);

        var rootMark = _heap.RootCount;
        try
        {
            PinIfObject(value);
            PinIfObject(thisValue);
            for (var i = 0; i < args.Count; i++) PinIfObject(args[i]);
            // Fresh objects allocated inside the native body may live only
            // in C# locals across nested safe-point collections; the heap's
            // scoped allocation pin covers exactly this window.
            _heap.BeginNativeExecution();
            try
            {
                return native.Call(thisValue, args);
            }
            finally
            {
                _heap.EndNativeExecution();
            }
        }
        finally
        {
            // Audit JSRT-002: pop THIS call's pins on every path. A thrown
            // JsThrownException must NOT re-anchor the window above fresh pins
            // (that made this finally a no-op and leaked thisValue+args roots on
            // every throwing native call). Ownership of the thrown value passes
            // to ThrowOrHandle, which pins it before any allocation can run.
            _heap.PopRootsTo(rootMark);
        }
    }

    /// <summary>
    /// The same native call from a frame whose arguments the collector can
    /// already see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pinning above exists because a native body runs with no interpreter
    /// frame on the stack, so the root walk - which traverses the frame list -
    /// cannot reach the callee, the receiver or the arguments while it runs.
    /// That is not true of a call made from the register-window loop: all three
    /// are slots in the caller's window, and the whole live window is traced as
    /// one span for as long as the frame exists. Pinning them again is work
    /// whose only effect is to add and remove root-set entries.
    /// </para>
    /// <para>
    /// The scoped allocation pin stays. It covers something different - objects
    /// the native body allocates and holds only in C# locals - which no frame
    /// can see whichever loop made the call.
    /// </para>
    /// </remarks>
    internal JsValue CallNativeWithRootedArguments(
        NativeFunctionObject native, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        _heap.BeginNativeExecution();
        try
        {
            return native.Call(thisValue, args);
        }
        finally
        {
            _heap.EndNativeExecution();
        }
    }

    // The same call, with the per-builtin accounting the diagnostics line
    // reports. Two reads of the performance counter and a dictionary keyed by
    // the builtin's name cost more than a short builtin's whole body, so the
    // measurement is a separate path a page opts into rather than a tax every
    // page pays. Timing the block and subtracting the body leaves the rooting
    // machinery's own cost, which never re-enters JS and so sums exactly.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue CallNativeFunctionBodyMeasured(NativeFunctionObject native, JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        var blockStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var nativeBodyTicks = 0L;
        var rootMark = _heap.RootCount;
        try
        {
            PinIfObject(value);
            PinIfObject(thisValue);
            for (var i = 0; i < args.Count; i++) PinIfObject(args[i]);
            _heap.BeginNativeExecution();
            var nativeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return native.Call(thisValue, args);
            }
            finally
            {
                nativeBodyTicks = System.Diagnostics.Stopwatch.GetTimestamp() - nativeStart;
                NoteNativeCall(native, nativeBodyTicks);
                _heap.EndNativeExecution();
            }
        }
        finally
        {
            _heap.PopRootsTo(rootMark);
            NoteNativeCallRooting(
                System.Diagnostics.Stopwatch.GetTimestamp() - blockStart - nativeBodyTicks);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue ThrowCallTargetIsNullish(JsValue value, in CallArgs args, JsValue thisValue)
    {
        // Name the callee the way V8 does ("x.foo is not a function") when the
        // bytecode shows which property or variable produced it; a page's own
        // error handling and any human reading the log get the missing member
        // rather than only a register-level dump.
        var calleeName = DescribeCalleeExpression();
        var head = calleeName != null
            ? calleeName + " is not a function"
            : "Cannot read properties of " +
              (value.Tag == JsValueTag.Undefined ? "undefined" : "null") +
              " while resolving call target";
        throw new JsThrownException(CreateTypeError(
            head + ". this=" + DescribeValueShort(thisValue) +
            " arg0=" + (args.Count > 0 ? DescribeValueShort(args[0]) : "<none>") +
            " " + DescribeFrameStack()));
    }

    /// <summary>
    /// Best-effort source-level name for the callee of the call instruction that
    /// is executing in the innermost frame: walks back from the call to the
    /// instruction that last wrote the callee register and renders
    /// <c>recv.prop</c> for a named property load or the slot name for a
    /// variable load. Returns null when the dataflow is not that simple.
    /// </summary>
    private string? DescribeCalleeExpression()
    {
        try
        {
            BytecodeFunction fn;
            int callIp;
            ReadOnlyMemory<JsValue> registers;
            // The two loops nest in either order; the innermost activation is
            // the classic frame unless a register window was pushed after it.
            var classic = _activeFrames.Count > 0 ? _activeFrames.Peek() : null;
            var windowIsInnermost = _interp2 != null &&
                                    _interp2.Depth > (classic?.Interp2DepthAtEntry ?? 0);
            if (classic != null && !windowIsInnermost)
            {
                fn = classic.Function;
                callIp = classic.InstructionPointer - 1;
                registers = classic.Registers;
            }
            else if (_interp2?.CurrentFrameForDiagnostics is { } window)
            {
                // The register-window loop stores the call's own ip before it
                // delegates a callee it cannot enter, so the same walk applies.
                fn = window.Function;
                callIp = window.Ip - 1;
                registers = window.Registers;
            }
            else
            {
                return null;
            }

            if ((uint)callIp >= (uint)fn.Instructions.Count) return null;
            var call = fn.Instructions[callIp];
            switch (call.OpCode)
            {
                case OpCode.Call0:
                case OpCode.Call1:
                case OpCode.CallN:
                case OpCode.CallMethod0:
                case OpCode.CallMethod1:
                case OpCode.CallMethodN:
                    break;
                default:
                    return null;
            }

            var calleeRegister = call.B;
            var from = System.Math.Max(0, callIp - 16);
            for (var i = callIp - 1; i >= from; i--)
            {
                var ins = fn.Instructions[i];
                if (ins.OpCode == OpCode.GetPropByName && ins.A == calleeRegister)
                {
                    if ((uint)ins.C >= (uint)fn.PropertyNames.Count) return null;
                    var receiverName = DescribeRegisterSource(fn, i, ins.B);
                    var receiverText = receiverName
                        ?? ((uint)ins.B < (uint)registers.Length ? DescribeValueShort(registers.Span[ins.B]) : "<receiver>");
                    return receiverText + "." + fn.PropertyNames[ins.C];
                }
                if (ins.OpCode == OpCode.LoadVar && ins.A == calleeRegister)
                {
                    return SlotNameTable.GetName(fn, ins.B);
                }
                if (ins.A == calleeRegister)
                {
                    return null;
                }
            }
        }
        catch
        {
            // Diagnostics only.
        }

        return null;
    }

    private static string? DescribeRegisterSource(BytecodeFunction fn, int beforeIp, int register)
    {
        var from = System.Math.Max(0, beforeIp - 8);
        for (var i = beforeIp - 1; i >= from; i--)
        {
            var ins = fn.Instructions[i];
            if (ins.A != register) continue;
            if (ins.OpCode == OpCode.LoadVar) return SlotNameTable.GetName(fn, ins.B);
            if (ins.OpCode == OpCode.GetPropByName && (uint)ins.C < (uint)fn.PropertyNames.Count)
            {
                var inner = DescribeRegisterSource(fn, i, ins.B);
                return inner != null ? inner + "." + fn.PropertyNames[ins.C] : null;
            }
            return null;
        }

        return null;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue ThrowCallTargetIsNullish(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
        => ThrowCallTargetIsNullish(value, new CallArgs(args), thisValue);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue ThrowCallTargetIsNotCallable(JsValue value, in CallArgs args, JsValue thisValue)
    {
        throw new JsThrownException(CreateTypeError(
            "Value is not callable. " + DescribeCallee(value) +
            " this=" + DescribeValueShort(thisValue) +
            " arg0=" + (args.Count > 0 ? DescribeValueShort(args[0]) : "<none>")));
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue ThrowCallTargetIsNotCallable(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
        => ThrowCallTargetIsNotCallable(value, new CallArgs(args), thisValue);

    private string KeyText(JsValue v)
    {
        try
        {
            return v.Tag switch
            {
                JsValueTag.String => "\"" + v.AsString() + "\"",
                JsValueTag.Int32 => v.AsInt32().ToString(),
                JsValueTag.Number => v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsValueTag.Symbol => "symbol",
                _ => v.Tag.ToString()
            };
        }
        catch { return "?"; }
    }

    private string DescribeValueShort(JsValue value)
    {
        try
        {
            if (value.Tag != JsValueTag.Object) return value.Tag.ToString();
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is null) return "object<null>";
            var keys = new List<string>();
            foreach (var kv in obj.EnumerateOwnProperties())
            {
                keys.Add(kv.Key);
                if (keys.Count >= 6) break;
            }
            return obj.GetType().Name + "{" + string.Join(",", keys) + "}";
        }
        catch { return "?"; }
    }

    // Diagnostic: render the non-callable callee so a bundle that calls a missing
    // global/host function (undefined) is distinguishable from one calling a plain
    // object. Best-effort — never throws.
    private string DescribeCallee(JsValue value)
    {
        try
        {
            if (value.Tag != JsValueTag.Object)
            {
                return "(callee tag=" + value.Tag + ")";
            }

            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is null) return "(callee=object<null>)";
            var keys = new List<string>();
            foreach (var kv in obj.EnumerateOwnProperties())
            {
                keys.Add(kv.Key);
                if (keys.Count >= 8) break;
            }
            return "(callee=object class=" + obj.GetType().Name + " keys=[" + string.Join(",", keys) + "]) " + DescribeFrameStack();
        }
        catch
        {
            return "(callee=?)";
        }
    }

    private string DescribeFrameStack()
    {
        try
        {
            var names = new List<string>();
            foreach (var f in _activeFrames)
            {
                names.Add(string.IsNullOrEmpty(f.Function?.Name) ? "<anon>" : f.Function.Name);
                if (names.Count >= 12) break;
            }
            return "frames=[" + string.Join(" <- ", names) + "] " + DisassembleNearCurrentIp();
        }
        catch
        {
            return "frames=?";
        }
    }

    // Dump the bytecode window around the failing call so we can see which property
    // (resolved via PropertyNames) was loaded as the non-callable callee — the only way
    // to localize a runtime fault inside a minified bundle that carries no source map.
    private string DisassembleNearCurrentIp()
    {
        try
        {
            if (_activeFrames.Count == 0) return string.Empty;
            var frame = _activeFrames.Peek();
            var fn = frame.Function;
            var ip = frame.InstructionPointer;
            var from = System.Math.Max(0, ip - 16);
            var to = System.Math.Min(fn.Instructions.Count - 1, ip + 1);
            var sb = new System.Text.StringBuilder("asm@ip" + ip + "{");
            for (var i = from; i <= to; i++)
            {
                var ins = fn.Instructions[i];
                sb.Append(i).Append(':').Append(ins.OpCode);
                // Emit raw operands so the dataflow of which register feeds a bad
                // key/callee can be traced by hand across the window.
                sb.Append("(A").Append(ins.A).Append(" B").Append(ins.B)
                  .Append(" C").Append(ins.C).Append(" D").Append(ins.D).Append(')');
                if ((ins.OpCode == OpCode.GetPropByName || ins.OpCode == OpCode.SetPropByName)
                    && (uint)ins.C < (uint)fn.PropertyNames.Count)
                {
                    sb.Append('.').Append(fn.PropertyNames[ins.C]);
                }
                if ((ins.OpCode == OpCode.LoadVar || ins.OpCode == OpCode.StoreVar || ins.OpCode == OpCode.InitVar)
                    && SlotNameTable.GetName(fn, ins.B) is { } slotName)
                {
                    sb.Append('.').Append(slotName);
                }
                if (ins.OpCode == OpCode.LoadConst && (uint)ins.B < (uint)fn.Constants.Count)
                {
                    sb.Append('=').Append(DescribeValueShort(fn.Constants[ins.B]));
                }
                // For GetElem, the key is a live register value — surface it so we can see
                // exactly which dynamic key resolved to the non-callable object.
                if (ins.OpCode == OpCode.GetElem &&
                    (uint)ins.C < (uint)frame.Registers.Length &&
                    (uint)ins.B < (uint)frame.Registers.Length)
                {
                    sb.Append("[key=").Append(DescribeValueShort(frame.Registers[ins.C]))
                      .Append('=').Append(KeyText(frame.Registers[ins.C]))
                      .Append(" recv=").Append(DescribeValueShort(frame.Registers[ins.B])).Append(']');
                }
                sb.Append(' ');
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }


    private JsValue ConstructFunction(JsValue value, IReadOnlyList<JsValue> args)
        => ConstructFunction(value, args, newTarget: value);

    // ECMA-262 7.3.15 Construct(F, argumentsList, newTarget) â€”
    // separate newTarget parameter so Reflect.construct can wire a different
    // newTarget.prototype for the created object.
    private JsValue ConstructFunction(JsValue value, IReadOnlyList<JsValue> args, JsValue newTarget)
    {
        if (value.Tag == JsValueTag.Undefined || value.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot construct " +
                (value.Tag == JsValueTag.Undefined ? "undefined" : "null") +
                " target. arg0=" + (args.Count > 0 ? DescribeValueShort(args[0]) : "<none>") +
                " " + DescribeFrameStack()));
        }

        var obj = ResolveObject(value);

        // ECMA-262 9.5.13 Proxy [[Construct]].
        if (obj is ProxyObject proxyCons)
        {
            if (!IsConstructableTarget(proxyCons.TargetHandle))
            {
                throw new JsThrownException(CreateTypeError("Proxy target is not a constructor."));
            }
            return ProxyConstruct(proxyCons, args, newTarget);
        }

        // ECMA-262 10.4.1.4 [[Construct]] â€” merge bound args + call-site args,
        // then construct [[BoundTargetFunction]].
        if (obj is BoundFunctionObject bound)
        {
            var merged = MergeBoundArgs(bound.BoundArgs, args);
            // ECMA-262 10.4.1.4 step 5: if newTarget === the bound function,
            // replace it with [[BoundTargetFunction]] so the unbound target
            // supplies the prototype for the created instance.
            if (newTarget.Tag == JsValueTag.Object &&
                value.Tag == JsValueTag.Object &&
                newTarget.AsObjectHandle() == value.AsObjectHandle())
            {
                newTarget = bound.TargetFunction;
            }
            return ConstructFunction(bound.TargetFunction, merged, newTarget);
        }

        if (obj is JsFunctionObject fn)
        {
            if (fn.Kind is FunctionKind.Generator or FunctionKind.AsyncGenerator)
                throw new JsThrownException(CreateTypeError("Generator functions cannot be used as constructors."));
            // Arrow functions have no [[Construct]] internal method (ECMA-262 10.2.1).
            if (fn.Function.IsArrow)
                throw new JsThrownException(CreateTypeError("Arrow functions cannot be used as constructors."));
            // Concise methods and accessors (FunctionKind.Method) are not
            // constructors (ECMA-262 15.4 — MethodDefinitions have no [[Construct]]).
            if (fn.Kind == FunctionKind.Method)
                throw new JsThrownException(CreateTypeError("Function is not a constructor."));
            return ExecuteConstruct(fn, new CallArgs(args), newTarget);
        }

        if (obj is NativeFunctionObject native)
        {
            if (!native.IsConstructor)
            {
                throw new JsThrownException(CreateTypeError("Function is not a constructor."));
            }

            // Pin the newTarget and every object-tagged arg as temporary GC roots
            // for the duration of the native construct call — the same defense the
            // CallFunction path applies (see comment there). Without this,
            // auto-MinorCollect triggered inside the native constructor (e.g.
            // AllocateObject in ConstructTypedArray) can reclaim objects whose only
            // live reference is the handle we're about to pass the native body,
            // surfacing as "Stale heap handle." on the next access.
            var rootMark = _heap.RootCount;
            JsValue constructed;
            try
            {
                PinIfObject(newTarget);
                for (var i = 0; i < args.Count; i++) PinIfObject(args[i]);
                constructed = native.ConstructWithNewTarget(args, newTarget);
            }
            catch (JsThrownException ex)
            {
                // Pin the thrown value and update rootMark so the finally
                // block preserves this pin — the value must survive GC until
                // the catch block processes it.
                PinIfObject(ex.Value);
                rootMark = _heap.RootCount;
                throw;
            }
            finally
            {
                _heap.PopRootsTo(rootMark);
            }
            if (constructed.Tag == JsValueTag.Object &&
                newTarget.Tag == JsValueTag.Object &&
                value.Tag == JsValueTag.Object &&
                newTarget.AsObjectHandle() != value.AsObjectHandle())
            {
                var protoValue = GetReceiverProperty(newTarget, "prototype");
                if (protoValue.Tag == JsValueTag.Object)
                {
                    _heap.GetObject(constructed.AsObjectHandle()).SetPrototype(protoValue.AsObjectHandle());
                    _heap.WriteBarrier(constructed.AsObjectHandle(), protoValue.AsObjectHandle());
                }
            }
            else if (constructed.Tag == JsValueTag.HostObject)
            {
                ApplyDefaultHostObjectPrototypeIfUnset(constructed, newTarget);
            }

            return constructed;
        }

        throw new JsThrownException(CreateTypeError("Value is not constructible."));
    }


    private void StoreCallResult(
        InterpreterFrame frame,
        int destinationRegister,
        JsValue callee,
        in CallArgs args,
        JsValue thisValue,
        bool allowDirectEval = false,
        int icOffset = -1)
    {
        // ECMA-262 19.2.1.1 - direct eval uses the calling frame's lexical environment.
        // The pointer is already past the call instruction.
        var armedDirectEval = allowDirectEval && ArmDirectEval(
            callee,
            frame.Environment,
            frame.Function.IsStrictMode,
            frame.Function.IsInFieldInitializer(frame.InstructionPointer - 1));

        try
        {
            // Function.prototype.call is a transparent call trampoline. Once
            // property lookup has resolved the exact builtin, dispatch the
            // target directly instead of entering a native-call rooting scope
            // only to re-enter CallFunction. The caller frame already roots
            // thisValue and every argument register for the nested call.
            // User replacements cannot take this path because their handle is
            // different from the installed intrinsic handle.
            if (_functionCallMethodHandle is { } functionCallHandle &&
                callee.Tag == JsValueTag.Object &&
                callee.AsObjectHandle() == functionCallHandle)
            {
                var targetThis = args.Count > 0 ? args[0] : JsValue.Undefined;
                var targetArgs = args.Count > 1
                    ? CallArgs.FromList(args, 1, args.Count - 1)
                    : CallArgs.Empty;
                frame.Registers[destinationRegister] = CallFunction(thisValue, targetArgs, targetThis);
                return;
            }

            if (icOffset >= 0 &&
                TryDispatchCallIC(frame.Function, icOffset, callee, args, thisValue, out var icResult))
            {
                frame.Registers[destinationRegister] = icResult;
                return;
            }

            frame.Registers[destinationRegister] = CallFunction(callee, args, thisValue);
            if (icOffset >= 0)
            {
                PopulateCallIC(frame.Function, icOffset, callee);
            }
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
        finally
        {
            if (armedDirectEval)
            {
                DisarmDirectEval();
            }
        }
    }

    /// <summary>
    /// Readies the next entry to %eval% as a direct eval in
    /// <paramref name="environment"/> (ECMA-262 13.3.6.1 step 6), when the
    /// callee is %eval% at all. The caller disarms it once the call returns,
    /// however it returns: Eval consumes it on entry, but a call that never
    /// reaches Eval must not leave it for the next, indirect one.
    /// </summary>
    internal bool ArmDirectEval(JsValue callee, EnvironmentRecord environment, bool strict, bool inFieldInitializer)
    {
        if (callee.Tag != JsValueTag.Object ||
            _heap.GetObject(callee.AsObjectHandle()) is not NativeFunctionObject native ||
            !string.Equals(native.Name, "eval", StringComparison.Ordinal))
        {
            return false;
        }

        _directEvalEnv = environment;
        _directEvalStrictMode = strict;
        _directEvalInFieldInitializer = inFieldInitializer;
        return true;
    }

    internal void DisarmDirectEval()
    {
        _directEvalEnv = null;
        _directEvalStrictMode = false;
        _directEvalInFieldInitializer = false;
    }

    private void StoreCallResult(
        InterpreterFrame frame,
        int destinationRegister,
        JsValue callee,
        IReadOnlyList<JsValue> args,
        JsValue thisValue,
        bool allowDirectEval = false,
        int icOffset = -1)
        => StoreCallResult(frame, destinationRegister, callee, new CallArgs(args), thisValue, allowDirectEval, icOffset);


    private void StoreConstructResult(InterpreterFrame frame, int destinationRegister, JsValue constructor, IReadOnlyList<JsValue> args)
    {
        try
        {
            // ECMA-262 13.3.7.1 EvaluateNew: for a regular `new X()` expression,
            // newTarget is the constructor itself (not frame.NewTarget, which is
            // the enclosing new.target for derived-class super() calls).
            // SuperCall opcodes handle derived-class newTarget propagation
            // separately via SuperCallStoreConstructResult.
            var newTarget = constructor;
            var constructed = ConstructFunction(constructor, args, newTarget);
            frame.Registers[destinationRegister] = constructed;
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }


    [MayExecuteJs]
    // ECMA-262 10.2.2 [[Construct]] â€” the prototype of the created object
    // comes from newTarget.prototype (not callee.prototype) when they differ.
    // OrdinaryCreateFromConstructor(newTarget, ...) calls GetPrototypeFromConstructor
    // which reads newTarget.prototype.
    internal JsValue ExecuteConstruct(JsFunctionObject callee, in CallArgs args, JsValue newTarget = default)
    {
        // ECMA-262 9.2.2 [[Construct]]: derived constructors have [[ThisMode]] = "~uninitialized~"
        // and must NOT receive a pre-allocated instance. super() will create the instance and
        // InitThisBinding will bind it as `this` in the derived constructor's env.
        var isDerived = callee.Function.IsDerivedConstructor;

        JsValue defaultInstance;
        if (isDerived)
        {
            defaultInstance = JsValue.Undefined;
        }
        else
        {
            var instanceObject = CreateOrdinaryObject();
            var protoReceiver = newTarget.Tag == JsValueTag.Object
                ? newTarget
                : (callee.OwnerHandle is { } calleeHandle ? JsValue.FromObject(calleeHandle) : JsValue.Undefined);
            var prototypeValue = protoReceiver.Tag == JsValueTag.Object
                ? GetReceiverProperty(protoReceiver, "prototype")
                : JsValue.Undefined;
            if (prototypeValue.Tag == JsValueTag.Object)
            {
                instanceObject.SetPrototype(prototypeValue.AsObjectHandle());
            }

            // ECMA-262 PrivateBrandAdd: every instance of a class that declares any private
            // element is branded on construction — not only when a private *field*
            // initializer happens to run. Without this, a class whose only private members
            // are methods/accessors (no fields) never brands its instances, so every
            // `this.#getter`/`this.#setter = v` would wrongly throw. Stamp before the
            // constructor body executes so field inits and private calls inside it see it.
            if (callee.Function.BrandTokens.Count > 0)
            {
                instanceObject.PrivateBrand = callee.Function.BrandTokens[0];
            }

            defaultInstance = JsValue.FromObject(_heap.AllocateObject(instanceObject, AllocationSite.Current()));
        }

        // An ordinary constructor runs on the register-window loop like any
        // other call, with newTarget carried on its frame (and on its record,
        // for an arrow inside to read), instead of always paying the old loop's
        // frame setup - which made `new F()` with an empty F cost four times a
        // call.
        JsValue result;
        var layout = Interpreter2.Interp2Options.Enabled
            ? Interpreter2.FrameLayout.ForConstruct(callee.Function)
            : null;
        if (layout is { Eligible: true })
        {
            callee.Function.Invocations++;
            result = Interp2Execute(callee, layout, args, defaultInstance, newTarget);
            ApplyDefaultHostObjectPrototypeIfUnset(result, newTarget);
            return IsConstructorReturnObject(result) ? result : defaultInstance;
        }

        _pendingNewTarget = newTarget.Tag == JsValueTag.Undefined
            ? JsValue.Undefined
            : newTarget;
        result = ExecuteInternal(callee.Function, args, defaultInstance, ResolveFunctionOuterEnvironment(callee), callee: callee);
        ApplyDefaultHostObjectPrototypeIfUnset(result, newTarget);
        return IsConstructorReturnObject(result) ? result : defaultInstance;
    }

    private const string RealmGlobalKey = "__realmGlobal__";

    // The last shape that turned out to carry no realm marker. A shape's own
    // keys are fixed for its lifetime, so one remembered shape answers for
    // every function built the same way, and defining the marker on a function
    // changes its shape and so misses this.
    private Objects.Shape? _shapeWithoutRealmGlobal;

    /// <summary>
    /// The environment a call to <paramref name="function"/> runs under.
    /// </summary>
    /// <remarks>
    /// A realm facade marks the functions it hands out by defining
    /// <c>__realmGlobal__</c> directly on them, so an own-property test answers
    /// this. Asking for the property instead walked the whole prototype chain
    /// to miss at the end of it, on every JS-to-JS call -- around a fifth of
    /// what a call cost.
    /// </remarks>
    private EnvironmentRecord? ResolveFunctionOuterEnvironment(JsFunctionObject function)
    {
        var shape = function.CurrentShape;
        if (!ReferenceEquals(shape, _shapeWithoutRealmGlobal))
        {
            if (function.TryGetOwnProperty(RealmGlobalKey, out var marker) &&
                marker.Value.Tag == JsValueTag.Object)
            {
                return StampEnvironment(new GlobalEnvironmentRecord(
                    CreateBindingAdapter(marker.Value.AsObjectHandle()),
                    marker.Value));
            }

            _shapeWithoutRealmGlobal = shape;
        }

        return function.OuterEnvironment;
    }

    private static bool IsConstructorReturnObject(JsValue value)
    {
        return value.Tag is JsValueTag.Object or JsValueTag.HostObject;
    }

    // Run the generator's parameter-binding prologue synchronously. ECMA-262
    // performs FunctionDeclarationInstantiation BEFORE GeneratorStart, so
    // destructuring failures must surface at the call site rather than on the
    // first .next(). The PrologueEnd opcode in the body causes the interpreter
    // to suspend the generator at the marker and return control here.
    private void RunGeneratorPrologue(GeneratorObject gen, BytecodeFunction function)
    {
        // ExecuteGenerator runs the body until first yield / PrologueEnd /
        // completion / throw. If the prologue throws, the JsThrownException
        // propagates out synchronously, which is the spec-required behaviour.
        _ = ExecuteGenerator(gen, JsValue.Undefined);
        // After ExecuteGenerator returns, gen is either Suspended (paused at
        // PrologueEnd, ready for the user's first .next()) or Completed (the
        // body had no yields and ran to the end). Either is a valid state for
        // a fresh generator handed back to the caller.
    }

}
