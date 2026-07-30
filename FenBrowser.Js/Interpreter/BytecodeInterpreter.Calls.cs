using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Environments;

namespace FenBrowser.Js.Interpreter;

// Call/construct/spread helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 3. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{
    // Tier 4 #24: JIT helpers for the call/construct opcode family.
    // Each builds the args array on the heap (matching how the
    // interpreter does it) and dispatches through StoreCallResult /
    // StoreConstructResult. icOffset is the call-site offset for the
    // Call IC; passed as a JIT-compile-time constant.
    internal void Call0ForJit(InterpreterFrame frame, int destReg, int calleeReg, int directEvalFlag, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            Array.Empty<JsValue>(), JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void Call1ForJit(InterpreterFrame frame, int destReg, int calleeReg, int argReg, int directEvalFlag, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            new[] { frame.Registers[argReg] }, JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void CallNForJit(InterpreterFrame frame, int destReg, int calleeReg, int argStartReg, int argCount, int directEvalFlag, int icOffset)
    {
        var callArgs = new JsValue[argCount];
        for (var i = 0; i < argCount; i++) callArgs[i] = frame.Registers[argStartReg + i];
        StoreCallResult(frame, destReg, frame.Registers[calleeReg], callArgs, JsValue.Undefined,
            allowDirectEval: directEvalFlag == DirectEvalCallFlag, icOffset: icOffset);
    }


    internal void CallMethod0ForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            Array.Empty<JsValue>(), frame.Registers[thisReg], icOffset: icOffset);
    }


    internal void CallMethod1ForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int argReg, int icOffset)
    {
        StoreCallResult(frame, destReg, frame.Registers[calleeReg],
            new[] { frame.Registers[argReg] }, frame.Registers[thisReg], icOffset: icOffset);
    }


    internal void CallMethodNForJit(InterpreterFrame frame, int destReg, int calleeReg, int thisReg, int argStartReg, int argCount, int icOffset)
    {
        var callArgs = new JsValue[argCount];
        for (var i = 0; i < argCount; i++) callArgs[i] = frame.Registers[argStartReg + i];
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


    private JsValue CallFunction(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        if (value.Tag == JsValueTag.Undefined || value.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot read properties of " +
                (value.Tag == JsValueTag.Undefined ? "undefined" : "null") +
                " while resolving call target. this=" + DescribeValueShort(thisValue) +
                " arg0=" + (args.Count > 0 ? DescribeValueShort(args[0]) : "<none>") +
                " " + DescribeFrameStack()));
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
                        // Body suspended at an await â€” resume callbacks already
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
                // ECMA-262 27.5.1.1 â€” calling a generator returns a GeneratorObject.
                // Spec runs FunctionDeclarationInstantiation (i.e. parameter binding,
                // including destructuring) synchronously before returning the
                // generator. If the function carries a PrologueEnd marker we run
                // the prologue here and let the generator suspend at the marker.
                var registers = new JsValue[fn.Function.RegisterCount];
                for (var i = 0; i < registers.Length; i++)
                    registers[i] = JsValue.Undefined;
                var paramCount = Math.Min(args.Count, fn.Function.ParameterNames.Count);
                for (var i = 0; i < paramCount; i++)
                    registers[i + 1] = args[i]; // register 0 is return slot, params start at 1

                var genObj = new GeneratorObject(fn.Function, registers, fn.OuterEnvironment);
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

            if (fn.Kind == FunctionKind.AsyncGenerator)
            {
                var registers = new JsValue[fn.Function.RegisterCount];
                for (var i = 0; i < registers.Length; i++)
                    registers[i] = JsValue.Undefined;
                var paramCount = Math.Min(args.Count, fn.Function.ParameterNames.Count);
                for (var i = 0; i < paramCount; i++)
                    registers[i + 1] = args[i];

                var genObj = new GeneratorObject(fn.Function, registers, fn.OuterEnvironment)
                {
                    ThisValue = thisValue,
                    IsAsyncGenerator = true,
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

            // Tier 4 #24: tier-up counter. The JIT delegate is invoked
            // from inside ExecuteInternalCore (after frame setup) so it
            // can access frame.Registers, frame.Environment, and the
            // interpreter's helper methods.
            var bcFn = fn.Function;
            bcFn.Invocations++;
#if !PUBLISH_AOT
            // Tier-4 #24 (audit §3.2): combined invocation + back-edge
            // trigger. Either 100 calls OR 10,000 cross-call loop
            // iterations OR a balanced mix gets the function JIT-compiled.
            // BackEdgeScale=100 keeps the legacy "100 invocations" cliff
            // intact while letting one-call loop-heavy functions tier up.
            const int BackEdgeScale = 100;
            if (!bcFn.JitCompileAttempted &&
                (long)bcFn.Invocations * BackEdgeScale + bcFn.BackEdges
                    >= (long)JitCompiler.TierUpThreshold * BackEdgeScale)
            {
                bcFn.JitCompileAttempted = true;
                bcFn.JitDelegate = JitCompiler.TryCompile(bcFn);
            }
#endif
            return ExecuteInternal(fn.Function, args, thisValue, ResolveFunctionOuterEnvironment(fn), callee: fn);
        }

        if (obj is NativeFunctionObject native)
        {
            // ECMA-262 native function calls execute in C# without a bytecode
            // InterpreterFrame on top of the call stack, so the JS heap's GC
            // root walk (which traverses _activeFrames) cannot see the JsValue
            // arguments and `thisValue` we are about to hand the native body.
            // If the native callback triggers an auto-MinorCollect (e.g. via
            // CreateTypeError, AllocateObject), object-tagged values on the
            // C# stack could be reclaimed and resurface as "Stale heap handle"
            // on the next access. Pin them for the duration of the call.
            var rootMark = _heap.RootCount;
            try
            {
                PinIfObject(thisValue);
                for (var i = 0; i < args.Count; i++) PinIfObject(args[i]);
                return native.Call(thisValue, args);
            }
            catch (JsThrownException ex)
            {
                // Pin the thrown value and adjust rootMark so the finally
                // block does not pop this pin — the value must survive GC
                // until the catch block processes it.
                PinIfObject(ex.Value);
                rootMark = _heap.RootCount;
                throw;
            }
            finally
            {
                _heap.PopRootsTo(rootMark);
            }
        }

        throw new JsThrownException(CreateTypeError(
            "Value is not callable. " + DescribeCallee(value) +
            " this=" + DescribeValueShort(thisValue) +
            " arg0=" + (args.Count > 0 ? DescribeValueShort(args[0]) : "<none>")));
    }

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
            if (fn.Kind == FunctionKind.Arrow)
                throw new JsThrownException(CreateTypeError("Arrow functions cannot be used as constructors."));
            // Concise methods and accessors (FunctionKind.Method) are not
            // constructors (ECMA-262 15.4 — MethodDefinitions have no [[Construct]]).
            if (fn.Kind == FunctionKind.Method)
                throw new JsThrownException(CreateTypeError("Function is not a constructor."));
            return ExecuteConstruct(fn, args, newTarget);
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
        IReadOnlyList<JsValue> args,
        JsValue thisValue,
        bool allowDirectEval = false,
        int icOffset = -1)
    {
        // ECMA-262 19.2.1.1 â€” direct eval uses the calling frame's lexical environment.
        if (allowDirectEval &&
            callee.Tag == JsValueTag.Object &&
            _heap.GetObject(callee.AsObjectHandle()) is NativeFunctionObject native &&
            string.Equals(native.Name, "eval", StringComparison.Ordinal))
        {
            _directEvalEnv = frame.Environment;
            _directEvalStrictMode = frame.Function.IsStrictMode;
        }

        // ECMA-262 super(...): route through [[Construct]] with this frame's
        // NewTarget rather than [[Call]] so the base constructor receives the
        // correct new.target (subclass) value.
        if (callee.Tag == JsValueTag.Object &&
            frame.SuperConstructorHandle is { } superHandle &&
            callee.AsObjectHandle() == superHandle)
        {
            frame.SuperConstructorHandle = null;
            try
            {
                var superResult = ConstructFunction(callee, args, frame.NewTarget);
                // Store the constructed instance as frame.ThisValue so the
                // InitThisBinding opcode (emitted right after super() in the
                // derived constructor bytecode) can bind it into the
                // FunctionEnvironmentRecord. Only objects can be bound as this;
                // non-object results from super() trigger a TypeError elsewhere.
                if (IsConstructorReturnObject(superResult))
                    frame.ThisValue = superResult;
                frame.Registers[destinationRegister] = superResult;
            }
            catch (JsThrownException ex)
            {
                if (frame.CatchHandlers.Count == 0) throw;
                ThrowOrHandle(frame, ex.Value);
            }
            return;
        }

        try
        {
            // Tier 4 #20: try the Call IC fast path before the generic dispatch.
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
        catch (JsThrownException ex)
        {
            if (frame.CatchHandlers.Count == 0)
            {
                throw;
            }

            ThrowOrHandle(frame, ex.Value);
        }
    }


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
        catch (JsThrownException ex)
        {
            if (frame.CatchHandlers.Count == 0)
            {
                throw;
            }

            ThrowOrHandle(frame, ex.Value);
        }
    }


    [MayExecuteJs]
    // ECMA-262 10.2.2 [[Construct]] â€” the prototype of the created object
    // comes from newTarget.prototype (not callee.prototype) when they differ.
    // OrdinaryCreateFromConstructor(newTarget, ...) calls GetPrototypeFromConstructor
    // which reads newTarget.prototype.
    private JsValue ExecuteConstruct(JsFunctionObject callee, IReadOnlyList<JsValue> args, JsValue newTarget = default)
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

        _pendingNewTarget = newTarget.Tag == JsValueTag.Undefined
            ? JsValue.Undefined
            : newTarget;
        var result = ExecuteInternal(callee.Function, args, defaultInstance, ResolveFunctionOuterEnvironment(callee), callee: callee);
        ApplyDefaultHostObjectPrototypeIfUnset(result, newTarget);
        return IsConstructorReturnObject(result) ? result : defaultInstance;
    }

    private EnvironmentRecord? ResolveFunctionOuterEnvironment(JsFunctionObject function)
    {
        if (function.OwnerHandle is { } handle &&
            TryGetPropertyValue(function, JsValue.FromObject(handle), "__realmGlobal__", out var realmGlobal) &&
            realmGlobal.Tag == JsValueTag.Object)
        {
            return new GlobalEnvironmentRecord(
                CreateBindingAdapter(realmGlobal.AsObjectHandle()),
                realmGlobal);
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
