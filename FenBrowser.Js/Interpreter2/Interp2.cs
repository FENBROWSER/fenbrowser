using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// The register-window execution loop: one contiguous value stack, frames that
/// are windows onto it, and a JavaScript call that does not leave the loop.
/// </summary>
/// <remarks>
/// <para>
/// The old loop costs about 400ns to enter an empty function, and the handoff
/// that measured it found no single step worth more than about 30ns: the cost is
/// the sequence itself. Each call rents a register array from a size-keyed pool,
/// allocates or rents an environment record, attaches slot storage to it, binds
/// `this` into it, rents a heap frame object, pushes that frame onto a root
/// stack, recurses into a new CLR frame guarded by a try/finally, walks the
/// parameter list binding each formal by slot, and runs three declaration
/// instantiation passes. Picking those off one at a time cannot work.
/// </para>
/// <para>
/// So this loop does not have them. A frame is a slice of one shared
/// <c>JsValue[]</c>: the low half is the body's bytecode registers, the high half
/// is its declared variables, and entering a call is a bounds check, a clear of
/// the window and a bump of two pointers. Because the callee runs on the same
/// loop, a JavaScript call is a <c>goto</c>, not a recursion - no CLR frame, no
/// try/finally, no depth bookkeeping, and a stack depth bounded by an integer
/// rather than by the host's native stack.
/// </para>
/// <para>
/// Everything that a window cannot represent - a closure capturing the scope, a
/// generator suspending with it intact, <c>with</c>, direct <c>eval</c>, the
/// temporal dead zone - is refused by <see cref="FrameLayout"/> before the frame
/// is entered, and runs on the old loop unchanged. The two share a heap, a set of
/// builtins, an inline-cache table and a bytecode format, so a call can cross
/// between them in either direction at any depth.
/// </para>
/// <para>
/// Semantics are not reimplemented here. Every operation that is more than a
/// register move calls the same helper the old loop calls, through the facade in
/// <c>BytecodeInterpreter.Interp2Host.cs</c>. This file decides where values live
/// and how a frame is entered; it does not decide what <c>+</c> means.
/// </para>
/// </remarks>
internal sealed class Interp2
{
    /// <summary>
    /// Dispatched instructions between guard checks. The wall-clock deadline and
    /// the interrupt callback are sampled rather than tested per instruction,
    /// for the same reason the old loop samples them: a delegate invocation and
    /// a clock read on every step are measurable on a tight loop. The engine's
    /// promise is that a runaway script stops, not that it stops on an exact
    /// instruction.
    /// </summary>
    private const int GuardCheckInterval = 4096;

    private const int InitialStackSlots = 1024;
    private const int InitialFrameCapacity = 64;

    private readonly BytecodeInterpreter _host;

    /// <summary>
    /// The value stack. Every live slot below <see cref="_stackTop"/> is a GC
    /// root and is traced as one linear span; nothing above it is traced, which
    /// is why a popped window needs no clearing and a pushed one does.
    /// </summary>
    private JsValue[] _stack = new JsValue[InitialStackSlots];

    private int _stackTop;

    private Frame[] _frames = new Frame[InitialFrameCapacity];

    private int _depth;

    internal Interp2(BytecodeInterpreter host) => _host = host;

    /// <summary>Live value-stack slots, for the collector's root walk.</summary>
    internal ReadOnlySpan<JsValue> LiveStack => _stack.AsSpan(0, _stackTop);

    internal int Depth => _depth;

    /// <summary>
    /// A frame is bookkeeping, not an object: it lives in an array of structs
    /// that is reused for the life of the interpreter and never allocated per
    /// call. <see cref="ReturnSlot"/> is the absolute stack index in the
    /// <i>caller's</i> window that this frame's return value is written to,
    /// which is what lets a return be a store and a jump.
    /// </summary>
    private struct Frame
    {
        public FrameLayout Layout;
        public JsFunctionObject? Callee;
        public EnvironmentRecord? OuterEnv;

        /// <summary>
        /// The record holding this activation's captured variables, or null
        /// when nothing created here can read one. It is also what a closure
        /// made in this frame captures, so the closure and the frame see the
        /// same variable.
        /// </summary>
        public DeclarativeEnvironmentRecord? Context;
        public JsValue This;
        public bool OuterEnvResolved;
        public int Base;
        public int Ip;
        public int ReturnSlot;
    }

    /// <summary>
    /// Run one eligible function body to completion, together with every
    /// eligible call it makes.
    /// </summary>
    internal JsValue Execute(JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue)
    {
        var entryDepth = _depth;
        var entryTop = _stackTop;
        try
        {
            PushFrame(callee, layout, args, thisValue, returnSlot: -1);
            return Run(entryDepth);
        }
        finally
        {
            // An escaping exception leaves frames on the stack that nothing will
            // ever return through. Cutting back to the entry marks is the whole
            // of unwinding: the abandoned windows are above the new top, so they
            // are no longer traced and are cleared by whatever pushes next.
            _depth = entryDepth;
            _stackTop = entryTop;
        }
    }

    // ---------------------------------------------------------------- dispatch

    private JsValue Run(int entryDepth)
    {
        var heap = _host.Heap;
        var guarded = _host.Interp2HasGuards;
        var guardCountdown = GuardCheckInterval;
        long dispatched = 0;

        // The frame's hot fields live in locals for the length of its execution
        // and are written back only when a call or a return changes which frame
        // is running. Reaching through the frame array on every operand access
        // is a load the dispatch cannot afford at two or three accesses per
        // instruction - which is the same reason the old loop hoists its
        // register array.
        var stack = _stack;
        BytecodeFunction function;
        FrameLayout layout;
        Instruction[] code;
        int frameBase, slotBase, ip;

        {
            ref var f = ref _frames[_depth - 1];
            layout = f.Layout;
            function = layout.Function;
            code = layout.Code;
            frameBase = f.Base;
            slotBase = frameBase + layout.RegisterCount;
            ip = f.Ip;
        }

        while (true)
        {
            ref readonly var ins = ref code[ip++];

            if (guarded && --guardCountdown <= 0)
            {
                guardCountdown = GuardCheckInterval;
                _host.Interp2Guard(dispatched + GuardCheckInterval);
                dispatched = 0;
            }
            else if (guarded)
            {
                dispatched++;
            }

            switch (ins.OpCode)
            {
                // ------------------------------------------------ data movement
                case OpCode.LoadConst:
                    stack[frameBase + ins.A] = function.Constants[ins.B];
                    break;

                case OpCode.Move:
                    stack[frameBase + ins.A] = stack[frameBase + ins.B];
                    break;

                case OpCode.LoadThis:
                    // An ordinary body binds its receiver on entry and nothing
                    // in an eligible one can rebind it: no super(), no `with`.
                    // So the frame's own copy is the answer GetThisEnvironment
                    // would have walked to. An arrow has no receiver of its own
                    // and has to do the walk (ECMA-262 9.1.2.5).
                    if (layout.ResolvesThisOutwards)
                    {
                        var receiver = _host.Interp2ResolveThis(
                            OuterEnvironmentOf(_depth - 1), _frames[_depth - 1].This);
                        stack = _stack;
                        stack[frameBase + ins.A] = receiver;
                        break;
                    }

                    stack[frameBase + ins.A] = _frames[_depth - 1].This;
                    break;

                case OpCode.LoadVar:
                {
                    var slot = ins.B;
                    var home = HomeOfSlot(layout, slot);
                    if (home == SlotHome.Register)
                    {
                        // A variable no closure can read is a register. There is
                        // no binding record to consult, no presence bit to test
                        // and no name to translate back to.
                        stack[frameBase + ins.A] = stack[slotBase + slot];
                        break;
                    }

                    var loaded = home == SlotHome.Context
                        ? _host.Interp2LoadContext(
                            _frames[_depth - 1].Context!, function, slot, NameOfSlot(layout, slot), layout.IsStrict)
                        : _host.Interp2LoadFree(
                            OuterEnvironmentOf(_depth - 1), NameOfSlot(layout, slot), layout.IsStrict);
                    stack = _stack;
                    stack[frameBase + ins.A] = loaded;
                    break;
                }

                case OpCode.PreResolveVar:
                    // ECMA-262 13.3.2.4 resolves the binding before the
                    // initializer runs so a visibility change in between cannot
                    // redirect the write. The layout has already established
                    // that this slot is a register in this window, which nothing
                    // outside the frame can see, delete or shadow - so there is
                    // no resolution to capture and the matching
                    // StoreResolvedVar is an ordinary slot store.
                    break;

                case OpCode.StoreVar:
                case OpCode.InitVar:
                case OpCode.StoreResolvedVar:
                {
                    var slot = ins.B;
                    var home = HomeOfSlot(layout, slot);
                    if (home == SlotHome.Register)
                    {
                        stack[slotBase + slot] = stack[frameBase + ins.A];
                        break;
                    }

                    if (home == SlotHome.Context)
                    {
                        _host.Interp2StoreContext(
                            _frames[_depth - 1].Context!, function, slot, stack[frameBase + ins.A],
                            NameOfSlot(layout, slot), layout.IsStrict);
                    }
                    else
                    {
                        _host.Interp2StoreFree(
                            OuterEnvironmentOf(_depth - 1),
                            NameOfSlot(layout, slot),
                            stack[frameBase + ins.A],
                            layout.IsStrict);
                    }

                    stack = _stack;
                    break;
                }

                // ------------------------------------------------ control flow
                case OpCode.Jump:
                    if (ins.A < ip - 1) heap.CollectAtSafePointIfRequested();
                    ip = ins.A;
                    break;

                case OpCode.JumpIfFalse:
                    if (!_host.Interp2IsTruthy(stack[frameBase + ins.A]))
                    {
                        if (ins.B < ip - 1) heap.CollectAtSafePointIfRequested();
                        ip = ins.B;
                    }

                    break;

                case OpCode.Nop:
                case OpCode.PrologueEnd:
                    break;

                case OpCode.Throw:
                    // No frame on this loop carries a handler yet - the layout
                    // refuses any body containing a `try` - so a throw leaves
                    // the loop and the entry frame's finally unwinds the stack.
                    throw new JsThrownException(stack[frameBase + ins.A]);

                case OpCode.Return:
                {
                    var returnValue = stack[frameBase + ins.A];
                    var returnSlot = _frames[_depth - 1].ReturnSlot;
                    _depth--;
                    _stackTop = frameBase;
                    if (_depth == entryDepth)
                    {
                        return returnValue;
                    }

                    stack[returnSlot] = returnValue;
                    goto reload;
                }

                // --------------------------------------------------- arithmetic
                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.Mod:
                case OpCode.Lt:
                case OpCode.Gt:
                case OpCode.Le:
                case OpCode.Ge:
                case OpCode.Eq:
                case OpCode.Neq:
                case OpCode.StrictEq:
                case OpCode.StrictNeq:
                case OpCode.BitAnd:
                case OpCode.BitOr:
                case OpCode.BitXor:
                case OpCode.ShiftLeft:
                case OpCode.ShiftRight:
                case OpCode.UnsignedShiftRight:
                {
                    ref readonly var left = ref stack[frameBase + ins.B];
                    ref readonly var right = ref stack[frameBase + ins.C];
                    if (BytecodeInterpreter.Interp2TryFastBinary(ins.OpCode, in left, in right, out var fast))
                    {
                        stack[frameBase + ins.A] = fast;
                        break;
                    }

                    var slow = _host.Interp2SlowBinary(ins.OpCode, left, right);
                    stack = _stack;
                    stack[frameBase + ins.A] = slow;
                    break;
                }

                case OpCode.Exp:
                {
                    var exponent = _host.Interp2SlowBinary(
                        OpCode.Exp, stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = exponent;
                    break;
                }

                case OpCode.And:
                    stack[frameBase + ins.A] = _host.Interp2IsTruthy(stack[frameBase + ins.B])
                        ? stack[frameBase + ins.C]
                        : stack[frameBase + ins.B];
                    break;

                case OpCode.Or:
                    stack[frameBase + ins.A] = _host.Interp2IsTruthy(stack[frameBase + ins.B])
                        ? stack[frameBase + ins.B]
                        : stack[frameBase + ins.C];
                    break;

                case OpCode.Not:
                    stack[frameBase + ins.A] =
                        JsValue.FromBoolean(!_host.Interp2IsTruthy(stack[frameBase + ins.B]));
                    break;

                case OpCode.Void:
                    stack[frameBase + ins.A] = JsValue.Undefined;
                    break;

                case OpCode.TypeOfName:
                {
                    var slot = ins.B;
                    var home = HomeOfSlot(layout, slot);
                    var typeName = home switch
                    {
                        SlotHome.Register => _host.Interp2TypeOfValue(stack[slotBase + slot]),
                        SlotHome.Context => _host.Interp2TypeOfValue(_host.Interp2LoadContext(
                            _frames[_depth - 1].Context!, function, slot, NameOfSlot(layout, slot), layout.IsStrict)),
                        _ => _host.Interp2TypeOfFree(
                            OuterEnvironmentOf(_depth - 1), NameOfSlot(layout, slot), layout.IsStrict),
                    };
                    stack = _stack;
                    stack[frameBase + ins.A] = JsValue.FromString(typeName);
                    break;
                }

                case OpCode.Neg:
                case OpCode.Pos:
                case OpCode.BitNot:
                case OpCode.TypeOf:
                case OpCode.ToNumeric:
                case OpCode.ToStringCoerce:
                case OpCode.Increment:
                case OpCode.Decrement:
                {
                    var unary = _host.Interp2Unary(ins.OpCode, stack[frameBase + ins.B]);
                    stack = _stack;
                    stack[frameBase + ins.A] = unary;
                    break;
                }

                // ------------------------------------------------------ objects
                case OpCode.GetPropByName:
                {
                    var read = _host.Interp2GetPropertyByName(
                        function, ip - 1, stack[frameBase + ins.B], function.PropertyNames[ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = read;
                    break;
                }

                case OpCode.GetElem:
                {
                    var element = _host.Interp2GetElement(
                        function, ip - 1, stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = element;
                    break;
                }

                case OpCode.GetElemConst:
                {
                    var element = _host.Interp2GetElement(
                        function, ip - 1, stack[frameBase + ins.B], function.Constants[ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = element;
                    break;
                }

                case OpCode.SetPropByName:
                    _host.Interp2SetPropertyByName(
                        function, ip - 1, stack[frameBase + ins.A], function.PropertyNames[ins.B],
                        stack[frameBase + ins.C], layout.IsStrict);
                    stack = _stack;
                    break;

                case OpCode.SetElem:
                    _host.Interp2SetElement(
                        stack[frameBase + ins.A], stack[frameBase + ins.B], stack[frameBase + ins.C],
                        layout.IsStrict);
                    stack = _stack;
                    break;

                case OpCode.SetElemByIndex:
                    _host.Interp2SetElementByIndex(
                        stack[frameBase + ins.A], ins.B, stack[frameBase + ins.C], layout.IsStrict);
                    stack = _stack;
                    break;

                case OpCode.CreateFunction:
                {
                    var closure = _host.Interp2CreateFunction(
                        function.NestedFunctions[ins.B],
                        _frames[_depth - 1].Context ?? OuterEnvironmentOf(_depth - 1));
                    stack = _stack;
                    stack[frameBase + ins.A] = closure;
                    break;
                }

                case OpCode.NewObject:
                    stack[frameBase + ins.A] = _host.Interp2NewObject();
                    break;

                case OpCode.NewArray:
                    stack[frameBase + ins.A] = _host.Interp2NewArray(ins.B);
                    break;

                case OpCode.Construct0:
                case OpCode.Construct1:
                case OpCode.ConstructN:
                {
                    var argumentCount = ins.OpCode switch
                    {
                        OpCode.Construct0 => 0,
                        OpCode.Construct1 => 1,
                        _ => ins.D,
                    };
                    var constructed = _host.Interp2Construct(
                        stack[frameBase + ins.B],
                        CallArgs.FromRegisters(stack, frameBase + ins.C, argumentCount));
                    stack = _stack;
                    stack[frameBase + ins.A] = constructed;
                    heap.CollectAtSafePointIfRequested();
                    break;
                }

                // -------------------------------------------------------- calls
                case OpCode.Call0:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, 0, 0, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.Call1:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, frameBase + ins.C, 1, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallN:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, frameBase + ins.C, ins.D, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallMethod0:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], stack[frameBase + ins.C], 0, 0, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallMethod1:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], stack[frameBase + ins.C], frameBase + ins.D, 1, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallMethodN:
                    _frames[_depth - 1].Ip = ip;
                    if (Call(stack[frameBase + ins.B], stack[frameBase + ins.C], frameBase + ins.D, ins.E, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                default:
                    // Unreachable: FrameLayout refuses a body containing any
                    // opcode this switch does not handle, before it is entered.
                    // If it is ever reached the gate and the loop have drifted
                    // apart, which is a defect in the engine and not in the
                    // script, so it is not a catchable JavaScript error.
                    throw new JsEngineFatalException(
                        $"Interp2 dispatched an unsupported opcode {ins.OpCode} in '{function.Name ?? "<anonymous>"}'.");
            }

            continue;

        reload:
            // A call was entered or a frame returned; whichever frame is running
            // now, its hot fields have to come back into locals. The stack array
            // itself is re-read because pushing a frame may have grown it.
            {
                stack = _stack;
                ref var f = ref _frames[_depth - 1];
                layout = f.Layout;
                function = layout.Function;
                code = layout.Code;
                frameBase = f.Base;
                slotBase = frameBase + layout.RegisterCount;
                ip = f.Ip;
            }
        }
    }

    // ------------------------------------------------------------------- calls

    /// <summary>
    /// Perform one call. Returns true when the callee was entered as a new
    /// window on this loop, in which case the caller reloads and keeps going;
    /// false when the call was completed by the old loop and its result has
    /// already been stored.
    /// </summary>
    /// <remarks>
    /// Capacity and depth failures throw rather than returning false. Returning
    /// false would send an eligible callee back through the old loop's
    /// <c>CallFunction</c>, which routes eligible functions straight back here -
    /// so a stack that is full would recurse instead of reporting that it is
    /// full.
    /// </remarks>
    private bool Call(JsValue calleeValue, JsValue thisValue, int argStart, int argCount, int returnSlot)
    {
        if (calleeValue.Tag == JsValueTag.Object &&
            _host.Heap.GetObject(calleeValue.AsObjectHandle()) is JsFunctionObject fn)
        {
            var layout = FrameLayout.For(fn.Function);
            if (layout.Eligible)
            {
                if (Interp2Options.Log) Interp2Stats.RecordCallInLoop();
                PushFrame(fn, layout, _stack, argStart, argCount, thisValue, returnSlot);
                return true;
            }
        }

        if (Interp2Options.Log) Interp2Stats.RecordCallDelegated();

        // Natives, bound functions, proxies, generators, async bodies and every
        // function this loop declined: the old loop knows all of them, and the
        // arguments it needs are already contiguous in this frame's window.
        var result = _host.Interp2Call(
            calleeValue, CallArgs.FromRegisters(_stack, argStart, argCount), thisValue);

        // _stack is re-read on the next line rather than reused from this one:
        // the callee may have re-entered this loop deeply enough to grow the
        // value stack, and the array the arguments were read out of is then the
        // one it replaced. Every host call in the dispatch follows the same rule.
        _stack[returnSlot] = result;
        return false;
    }

    private void PushFrame(JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue, int returnSlot)
    {
        // The entry path takes its arguments as a CallArgs because that is what
        // the old loop hands over. Copying them into the window through the
        // shared helper keeps one binding routine rather than two.
        var window = Reserve(layout);
        var stack = _stack;
        var context = Activate(callee, layout, thisValue, window, returnSlot);
        var parameterIndex = layout.ParameterWindowIndex;
        var bind = layout.HasDuplicateParameterSlots
            ? parameterIndex.Length
            : Math.Min(args.Count, parameterIndex.Length);
        for (var i = 0; i < bind; i++)
        {
            var value = i < args.Count ? args[i] : JsValue.Undefined;
            if (context is null || layout.SlotHomes[layout.ParameterSlots[i]] == SlotHome.Register)
            {
                stack[window + parameterIndex[i]] = value;
            }
            else
            {
                BytecodeInterpreter.Interp2DeclareContextSlot(context, layout.ParameterSlots[i], value);
            }
        }
    }

    private void PushFrame(
        JsFunctionObject callee,
        FrameLayout layout,
        JsValue[] argSource,
        int argStart,
        int argCount,
        JsValue thisValue,
        int returnSlot)
    {
        var window = Reserve(layout);
        var context = Activate(callee, layout, thisValue, window, returnSlot);
        var stack = _stack;
        var parameterIndex = layout.ParameterWindowIndex;
        // Missing formals are already undefined from the window clear (and from
        // the context declaration), so the ordinary case binds only what was
        // supplied. A body whose formals share a slot has to write every one of
        // them, in order, or a duplicate's earlier value survives where the spec
        // says the last one wins.
        var bind = layout.HasDuplicateParameterSlots
            ? parameterIndex.Length
            : Math.Min(argCount, parameterIndex.Length);
        if (context is null)
        {
            for (var i = 0; i < bind; i++)
            {
                stack[window + parameterIndex[i]] = i < argCount ? argSource[argStart + i] : JsValue.Undefined;
            }

            return;
        }

        var parameterSlots = layout.ParameterSlots;
        var slotHomes = layout.SlotHomes;
        for (var i = 0; i < bind; i++)
        {
            var value = i < argCount ? argSource[argStart + i] : JsValue.Undefined;
            if (slotHomes[parameterSlots[i]] == SlotHome.Register)
            {
                stack[window + parameterIndex[i]] = value;
            }
            else
            {
                BytecodeInterpreter.Interp2DeclareContextSlot(context, parameterSlots[i], value);
            }
        }
    }

    /// <summary>
    /// Claim this frame's window and hand back where it starts. The window is
    /// cleared, not merely reserved: every slot in it is about to be traced as a
    /// GC root, and a value left behind by a frame that has already returned
    /// would be a root to a cell the collector may since have swept. Missing
    /// arguments and not-yet-assigned vars read as undefined for the same
    /// reason the clear happens - it is one operation serving both.
    /// </summary>
    private int Reserve(FrameLayout layout)
    {
        var windowSize = layout.WindowSize;
        var newTop = _stackTop + windowSize;
        if (newTop > _stack.Length)
        {
            GrowStack(newTop);
        }

        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _frames.Length * 2);
        }

        if (_depth >= _host.MaxCallDepth)
        {
            throw _host.Interp2CallStackOverflow();
        }

        var window = _stackTop;
        Array.Clear(_stack, window, windowSize);
        _stackTop = newTop;
        return window;
    }

    private DeclarativeEnvironmentRecord? Activate(
        JsFunctionObject callee, FrameLayout layout, JsValue thisValue, int window, int returnSlot)
    {
        // ECMA-262 10.2.1.3 OrdinaryCallBindThis: a strict body takes its
        // receiver as it comes, a sloppy one substitutes the global object for
        // null/undefined and boxes any other primitive.
        if (layout.BindsThisLoosely && thisValue.Tag != JsValueTag.Object)
        {
            thisValue = _host.Interp2CoerceReceiver(thisValue);
        }

        ref var frame = ref _frames[_depth++];
        frame.Layout = layout;
        frame.Callee = callee;
        // A body with no free identifiers never consults the scope chain, and
        // resolving the closure environment is a shape check and sometimes a
        // property probe - real cost on a leaf function that has no use for it.
        // A body that creates a closure needs it whatever else it does, because
        // the closure has to be given something to chain to.
        var needsOuterNow = layout.HasFreeVariables || layout.HasContext || layout.ResolvesThisOutwards;
        frame.OuterEnv = needsOuterNow ? _host.Interp2OuterEnvironment(callee) : null;
        frame.OuterEnvResolved = needsOuterNow;
        frame.This = thisValue;
        frame.Base = window;
        frame.Ip = 0;
        frame.ReturnSlot = returnSlot;

        DeclarativeEnvironmentRecord? context = null;
        if (layout.HasContext)
        {
            // Hoisting: every captured variable exists, holding undefined, from
            // the moment the body starts - the same state the window clear gives
            // the ones that stayed in registers.
            context = _host.Interp2CreateContext(layout.Function, callee, thisValue, frame.OuterEnv);
            var homes = layout.SlotHomes;
            for (var slot = 0; slot < homes.Length; slot++)
            {
                if (homes[slot] == SlotHome.Context)
                {
                    BytecodeInterpreter.Interp2DeclareContextSlot(context, slot, JsValue.Undefined);
                }
            }
        }

        frame.Context = context;

        if (Interp2Options.Log)
        {
            Interp2Stats.RecordFrameEntered(_depth, _stackTop);
        }

        return context;
    }

    private void GrowStack(int required)
    {
        var capacity = _stack.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        if (capacity > Interp2Options.MaxValueStackSlots)
        {
            throw _host.Interp2CallStackOverflow();
        }

        Array.Resize(ref _stack, capacity);
    }

    /// <summary>
    /// The closure environment of the frame at <paramref name="index"/>,
    /// resolved on demand.
    /// </summary>
    /// <remarks>
    /// A frame with no free identifiers of its own skips this on entry, because
    /// resolving it is a shape check and sometimes a property probe. Creating a
    /// function is the other thing that needs it: the closure has to be given
    /// something to chain to even when the body around it reaches for nothing.
    /// </remarks>
    private EnvironmentRecord? OuterEnvironmentOf(int index)
    {
        ref var frame = ref _frames[index];
        if (!frame.OuterEnvResolved)
        {
            frame.OuterEnv = frame.Callee is { } callee ? _host.Interp2OuterEnvironment(callee) : null;
            frame.OuterEnvResolved = true;
        }

        return frame.OuterEnv;
    }

    private static string? NameOfSlot(FrameLayout layout, int slot)
        => (uint)slot < (uint)layout.SlotNames.Length ? layout.SlotNames[slot] : null;

    /// <summary>
    /// Where a slot lives. An index the layout does not cover is treated as
    /// free, which sends it to the scope chain - the answer the old loop gives
    /// for a slot with no local binding.
    /// </summary>
    private static SlotHome HomeOfSlot(FrameLayout layout, int slot)
        => (uint)slot < (uint)layout.SlotHomes.Length ? layout.SlotHomes[slot] : SlotHome.Free;

    // -------------------------------------------------------------- GC roots

    /// <summary>
    /// Every value the collector can reach only through this loop. The value
    /// stack covers registers, declared variables and the arguments of a call in
    /// flight, because all three are the same array; the frames add each
    /// activation's receiver and callee, which live beside the window rather
    /// than in it.
    /// </summary>
    internal void TraceRoots(IHeapTracer tracer)
    {
        var stack = _stack;
        var top = _stackTop;
        for (var i = 0; i < top; i++)
        {
            if (stack[i].Tag == JsValueTag.Object)
            {
                tracer.TraceRoot("interp2.stack", stack[i].AsObjectHandle());
            }
        }

        for (var i = 0; i < _depth; i++)
        {
            ref var frame = ref _frames[i];
            if (frame.This.Tag == JsValueTag.Object)
            {
                tracer.TraceRoot("interp2.frame.this", frame.This.AsObjectHandle());
            }

            if (frame.Callee?.SelfHandle is { } calleeHandle)
            {
                tracer.TraceRoot("interp2.frame.callee", calleeHandle);
            }

            // Captured variables are not in the window, so the span above does
            // not reach them. Until a closure that holds this record is itself
            // reachable, the running frame is the only thing keeping the values
            // in it alive.
            frame.Context?.Trace(tracer);
        }
    }
}
