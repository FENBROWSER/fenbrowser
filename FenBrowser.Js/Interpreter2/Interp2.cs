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

    /// <summary>
    /// One byte per value-stack slot, set while a function-level let or const
    /// has not been initialized. A register holds no value that means
    /// "uninitialized", and the temporal dead zone is observable: reading or
    /// assigning the binding before its declaration runs is a ReferenceError.
    /// Only a body whose layout says it has lexical slots reads or writes this,
    /// so the ordinary frame pays nothing for it.
    /// </summary>
    private byte[] _tdz = new byte[InitialStackSlots];

    private int _stackTop;

    private Frame[] _frames = new Frame[InitialFrameCapacity];

    private int _depth;

    /// <summary>
    /// Instructions still to dispatch before the next guard check, carried
    /// across frames and across re-entries.
    /// </summary>
    /// <remarks>
    /// It cannot live in the dispatch loop's locals. A program whose work is a
    /// native builtin calling a short JavaScript callback - an array method with
    /// a predicate, a proxy trap, a sort comparator - enters the loop afresh for
    /// every call, and a per-entry countdown would restart at the full interval
    /// each time and never reach zero. That is a script the wall-clock deadline
    /// can never stop, which is the one thing these checks exist to prevent.
    /// Found by a test262 case that spins a proxy trap 2^32 times.
    ///
    /// It is only read when something is actually watching - a budget, a
    /// deadline or an interrupt - so an unguarded run pays nothing for it.
    /// </remarks>
    private int _guardCountdown = GuardCheckInterval;

    /// <summary>
    /// Open `try` entries across every live frame, as (catch ip, finally ip)
    /// pairs. One shared array rather than a stack per frame: entering a try is
    /// then two array writes, which is the operation that actually runs - a
    /// handler is used only when something throws.
    /// </summary>
    private int[] _handlers = new int[128];

    private int _handlerTop;

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

        /// <summary>
        /// This frame's slice of the shared handler stack: where its innermost
        /// enclosing `try` entries begin, and how many are open. Handlers are
        /// pushed and popped far more often than they are used, so they are two
        /// integers in a shared array rather than an object per frame.
        /// </summary>
        public int HandlerBase;

        public int HandlerCount;

        /// <summary>
        /// An exception on its way through a `finally` that has no `catch`. It
        /// is re-raised when the finally block ends, unless the block itself
        /// completes abruptly first.
        /// </summary>
        public JsValue PendingException;

        public bool HasPendingException;
    }

    /// <summary>
    /// Run one eligible function body to completion, together with every
    /// eligible call it makes.
    /// </summary>
    internal JsValue Execute(JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue)
    {
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            PushFrame(callee, layout, args, thisValue, returnSlot: -1);
            while (true)
            {
                try
                {
                    return Dispatch(entryDepth);
                }
                catch (JsThrownException thrown) when (!thrown.IsUncatchableByScript)
                {
                    // Every throw arrives here as a CLR exception, whether it
                    // came from a `throw` in this loop, from a getter three
                    // frames down, or from the old loop running a callee this
                    // one declined. One place to unwind from means the dispatch
                    // itself carries no exception handling at all - and the
                    // handler region is around the outermost entry rather than
                    // around the loop, which is worth about 5% of it.
                    if (!TryRouteThrow(entryDepth, thrown.Value))
                    {
                        throw;
                    }
                }
            }
        }
        finally
        {
            // An escaping exception leaves frames on the stack that nothing will
            // ever return through. Cutting back to the entry marks is the whole
            // of unwinding: the abandoned windows are above the new top, so they
            // are no longer traced and are cleared by whatever pushes next.
            _depth = entryDepth;
            _stackTop = entryTop;
            _handlerTop = entryHandlerTop;

            // Close the last instruction of the job rather than leaving it open
            // across the thread's idle wait, where it would be billed until the
            // next job starts.
            if (FenBrowser.Js.Diagnostics.InterpreterProfiler.OpTimingEnabled && _depth == 0)
            {
                FenBrowser.Js.Diagnostics.InterpreterProfiler.EndOpBatch();
            }
        }
    }

    // ---------------------------------------------------------------- dispatch

    private JsValue Dispatch(int entryDepth)
    {
        var heap = _host.Heap;
        var guarded = _host.Interp2HasGuards;

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

            // The opcode histogram, on the same switch the dispatch loop uses, so
            // a page can be profiled on either loop. Enabled is a static
            // readonly bool, so with the switch off the JIT drops all of this.
            if (FenBrowser.Js.Diagnostics.InterpreterProfiler.Enabled)
            {
                FenBrowser.Js.Diagnostics.InterpreterProfiler.RecordOpCode(ins.OpCode, function);
            }

            // Per-opcode self time, on its own switch because it takes a
            // timestamp per instruction. Counting which opcode runs most says
            // nothing about where a page's seconds went - a lesson this loop
            // learned by specialising the most frequent one and measuring no
            // change at all.
            if (FenBrowser.Js.Diagnostics.InterpreterProfiler.OpTimingEnabled)
            {
                FenBrowser.Js.Diagnostics.InterpreterProfiler.BeginOp(ins.OpCode);
            }

            // The countdown is a field, not a local, so it survives this frame
            // ending and this method being re-entered. A program whose work is a
            // native builtin calling a short JavaScript callback would otherwise
            // restart it at the full interval on every call and never be
            // stoppable by the wall-clock deadline.
            if (guarded && --_guardCountdown <= 0)
            {
                _guardCountdown = GuardCheckInterval;
                _host.Interp2Guard(GuardCheckInterval);
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
                        // and no name to translate back to - only a let or const
                        // that has not been initialized needs anything more.
                        if (layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
                        {
                            _host.Interp2ThrowDeadZoneAccess(NameOfSlot(layout, slot));
                        }

                        stack[frameBase + ins.A] = stack[slotBase + slot];
                        break;
                    }

                    var loaded = home == SlotHome.Context
                        ? _host.Interp2LoadContext(
                            _frames[_depth - 1].Context!, function, slot, NameOfSlot(layout, slot), layout.IsStrict)
                        : _host.Interp2LoadFreeCached(
                            layout, slot, ip - 1, OuterEnvironmentOf(_depth - 1));
                    stack = _stack;
                    stack[frameBase + ins.A] = loaded;
                    break;
                }

                case OpCode.PreResolveVar:
                    // ECMA-262 13.3.2.4 and 13.4 resolve the binding before the
                    // expression in between runs, so a visibility change it
                    // causes cannot redirect the write. A register or a context
                    // slot is this call's own, and nothing outside the frame can
                    // see, delete or shadow it - there is no resolution to
                    // capture and the matching store is an ordinary slot store.
                    // A free name is the case that needs one: `x++` on an outer
                    // variable runs ToNumeric between the two, and a valueOf
                    // there can delete the property the name resolved to.
                    if (HomeOfSlot(layout, ins.B) == SlotHome.Free)
                    {
                        _host.Interp2PreResolveFree(
                            OuterEnvironmentOf(_depth - 1), NameOfSlot(layout, ins.B));
                        stack = _stack;
                    }

                    break;

                case OpCode.EnterScope:
                    // The block's binding is a slot in this window, which the
                    // layout has already proved nothing outside the block can
                    // reach. C = 1 asks for it to start as undefined; otherwise
                    // it starts in the temporal dead zone and the declaration
                    // ends it, whenever in the block that turns out to be.
                    if (ins.C == 1)
                    {
                        stack[slotBase + ins.A] = JsValue.Undefined;
                        if (layout.HasLexicalSlots)
                        {
                            _tdz[slotBase + ins.A] = 0;
                        }
                    }
                    else if (layout.HasLexicalSlots)
                    {
                        // Re-entering the block - the next turn of a loop - puts
                        // the binding back in the dead zone, as a fresh one is.
                        _tdz[slotBase + ins.A] = 1;
                    }

                    break;

                case OpCode.LeaveScope:
                    break;

                case OpCode.StoreVar:
                case OpCode.StoreResolvedVar:
                {
                    // A named function expression's own name is immutable, but
                    // only strict code is told: sloppy code drops the write.
                    if (ins.B == layout.SelfNameSlot)
                    {
                        if (layout.IsStrict)
                        {
                            _host.Interp2ThrowSelfNameAssignment(NameOfSlot(layout, ins.B));
                        }

                        break;
                    }

                    // The dead zone comes first: assigning to a let or const
                    // before its declaration runs is a ReferenceError, whichever
                    // of the two it is.
                    if (layout.HasLexicalSlots &&
                        HomeOfSlot(layout, ins.B) == SlotHome.Register &&
                        _tdz[slotBase + ins.B] != 0)
                    {
                        _host.Interp2ThrowDeadZoneAccess(NameOfSlot(layout, ins.B));
                    }

                    // ECMA-262 9.1.1.1.5: assigning to an immutable binding is a
                    // TypeError. Initialising one is not, which is why InitVar
                    // is a separate case below.
                    var constants = layout.SlotIsConst;
                    if ((uint)ins.B < (uint)constants.Length && constants[ins.B])
                    {
                        _host.Interp2ThrowConstAssignment(NameOfSlot(layout, ins.B));
                    }

                    if (ins.OpCode == OpCode.StoreResolvedVar &&
                        HomeOfSlot(layout, ins.B) == SlotHome.Free)
                    {
                        _host.Interp2StoreResolvedFree(
                            OuterEnvironmentOf(_depth - 1),
                            NameOfSlot(layout, ins.B),
                            stack[frameBase + ins.A],
                            layout.IsStrict);
                        stack = _stack;
                        break;
                    }

                    goto case OpCode.InitVar;
                }

                case OpCode.InitVar:
                {
                    var slot = ins.B;
                    var home = HomeOfSlot(layout, slot);
                    var isLexicalSlot = layout.HasLexicalSlots &&
                        (uint)slot < (uint)layout.SlotIsLexical.Length && layout.SlotIsLexical[slot];
                    if (home == SlotHome.Register)
                    {
                        if (isLexicalSlot)
                        {
                            // The declaration has run: the binding exists now.
                            _tdz[slotBase + slot] = 0;
                        }

                        stack[slotBase + slot] = stack[frameBase + ins.A];
                        break;
                    }

                    if (home == SlotHome.Context && isLexicalSlot)
                    {
                        // Initializing an uninitialized binding, which a store
                        // through the record would refuse as a dead-zone write.
                        BytecodeInterpreter.Interp2InitializeContextSlot(
                            _frames[_depth - 1].Context!,
                            slot,
                            stack[frameBase + ins.A],
                            (uint)slot < (uint)layout.SlotIsConst.Length && layout.SlotIsConst[slot]);
                    }
                    else if (home == SlotHome.Context)
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
                    // Raised rather than routed here, so that a `throw` and an
                    // exception out of a getter take exactly one path.
                    _frames[_depth - 1].Ip = ip;
                    throw new JsThrownException(stack[frameBase + ins.A]);

                case OpCode.PushHandler:
                {
                    ref var entering = ref _frames[_depth - 1];
                    if (entering.HandlerCount == 0)
                    {
                        entering.HandlerBase = _handlerTop;
                    }

                    if (_handlerTop + 2 > _handlers.Length)
                    {
                        Array.Resize(ref _handlers, _handlers.Length * 2);
                    }

                    _handlers[_handlerTop] = ins.A;
                    _handlers[_handlerTop + 1] = ins.D;
                    _handlerTop += 2;
                    entering.HandlerCount++;
                    break;
                }

                case OpCode.PopHandler:
                {
                    ref var leaving = ref _frames[_depth - 1];
                    if (leaving.HandlerCount > 0)
                    {
                        leaving.HandlerCount--;
                        _handlerTop = leaving.HandlerBase + leaving.HandlerCount * 2;
                    }

                    break;
                }

                case OpCode.EndFinally:
                {
                    // ECMA-262 14.15.3: a finally block that completes normally
                    // hands the abrupt completion it interrupted back on.
                    ref var finishing = ref _frames[_depth - 1];
                    if (!finishing.HasPendingException)
                    {
                        break;
                    }

                    var pending = finishing.PendingException;
                    finishing.HasPendingException = false;
                    finishing.PendingException = JsValue.Undefined;
                    finishing.Ip = ip;
                    throw new JsThrownException(pending);
                }

                case OpCode.Return:
                {
                    var returnValue = stack[frameBase + ins.A];
                    ref var returning = ref _frames[_depth - 1];
                    var returnSlot = returning.ReturnSlot;
                    if (returning.HandlerCount > 0)
                    {
                        _handlerTop = returning.HandlerBase;
                        returning.HandlerCount = 0;
                    }

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
                    // `typeof` answers "undefined" for a name that is not
                    // declared, but not for one in the dead zone.
                    if (home == SlotHome.Register && layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
                    {
                        _host.Interp2ThrowDeadZoneAccess(NameOfSlot(layout, slot));
                    }

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
                    // D=1 is an object literal's own property: created, not
                    // assigned (13.2.5.5), so an inherited setter must not run.
                    if (ins.D != 0)
                    {
                        _host.Interp2DefineOwnDataProperty(
                            stack[frameBase + ins.A], function.PropertyNames[ins.B],
                            stack[frameBase + ins.C]);
                    }
                    else
                    {
                        _host.Interp2SetPropertyByName(
                            function, ip - 1, stack[frameBase + ins.A], function.PropertyNames[ins.B],
                            stack[frameBase + ins.C], layout.IsStrict);
                    }

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

                case OpCode.InstanceOf:
                {
                    var isInstance = _host.Interp2InstanceOf(
                        stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = isInstance;
                    break;
                }

                case OpCode.In:
                {
                    var has = _host.Interp2In(stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = has;
                    break;
                }

                case OpCode.DefineGetter:
                case OpCode.DefineSetter:
                    // A class body or an object literal installing an accessor.
                    // This runs in the defining body, not the accessor, so a
                    // class full of getters stays in the loop.
                    _host.Interp2DefineAccessor(
                        stack[frameBase + ins.A], function.PropertyNames[ins.B], JsValue.Undefined,
                        stack[frameBase + ins.C], ins.OpCode == OpCode.DefineGetter, ins.D != 0);
                    stack = _stack;
                    break;

                case OpCode.DefineGetterByReg:
                case OpCode.DefineSetterByReg:
                    _host.Interp2DefineAccessor(
                        stack[frameBase + ins.A], null, stack[frameBase + ins.B],
                        stack[frameBase + ins.C], ins.OpCode == OpCode.DefineGetterByReg, ins.D != 0);
                    stack = _stack;
                    break;

                case OpCode.DefineMethod:
                    _host.Interp2DefineMethod(
                        stack[frameBase + ins.A], function.PropertyNames[ins.B], JsValue.Undefined,
                        stack[frameBase + ins.C]);
                    stack = _stack;
                    break;

                case OpCode.DefineMethodByReg:
                    _host.Interp2DefineMethod(
                        stack[frameBase + ins.A], null, stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    break;

                case OpCode.SetHomeObject:
                    // Records the object a method was defined on. It is the
                    // enclosing body that runs this, not the method, so a class
                    // full of methods stays in the loop even when the methods
                    // themselves reach for super.
                    _host.Interp2SetHomeObject(stack[frameBase + ins.A], stack[frameBase + ins.B]);
                    stack = _stack;
                    break;

                case OpCode.LoadSuperProperty:
                case OpCode.LoadSuperElement:
                {
                    ref var superFrame = ref _frames[_depth - 1];
                    var superKey = ins.OpCode == OpCode.LoadSuperProperty
                        ? JsValue.FromString(function.PropertyNames[ins.B])
                        : stack[frameBase + ins.B];
                    var superValue = _host.Interp2LoadSuper(
                        function, superFrame.Callee, superFrame.This, superKey,
                        keyIsName: ins.OpCode == OpCode.LoadSuperProperty);
                    stack = _stack;
                    stack[frameBase + ins.A] = superValue;
                    break;
                }

                case OpCode.SetElemDefine:
                    // `{ [k]: v }` and a computed method name. Defining rather
                    // than setting is the point: a computed key must not run a
                    // setter the prototype carries.
                    _host.Interp2DefineElement(
                        stack[frameBase + ins.A], stack[frameBase + ins.B],
                        stack[frameBase + ins.C], ins.D != 0);
                    stack = _stack;
                    break;

                case OpCode.SpreadAppend:
                {
                    // The index is read and written back through the same
                    // register, because one array literal can hold several
                    // spreads and each starts where the last ended.
                    var nextIndex = _host.Interp2SpreadAppend(
                        stack[frameBase + ins.A], stack[frameBase + ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.B] = nextIndex;
                    break;
                }

                case OpCode.CopyDataProperties:
                    _host.Interp2CopyDataProperties(stack[frameBase + ins.A], stack[frameBase + ins.B]);
                    stack = _stack;
                    break;

                case OpCode.GetTemplateObject:
                {
                    var template = _host.Interp2GetTemplateObject(function, ins.B);
                    stack = _stack;
                    stack[frameBase + ins.A] = template;
                    break;
                }

                case OpCode.GetPrivateField:
                {
                    // `this.#x`. The brand check is the whole of the access
                    // control: a private name is not a key user code can forge,
                    // so there is nothing here a closure or a record would be
                    // needed for, and a method that reads one is otherwise an
                    // ordinary body.
                    var privateValue = _host.Interp2GetPrivateField(
                        function, stack[frameBase + ins.B], function.PropertyNames[ins.C]);
                    stack = _stack;
                    stack[frameBase + ins.A] = privateValue;
                    break;
                }

                case OpCode.SetPrivateField:
                {
                    _host.Interp2SetPrivateField(
                        function, stack[frameBase + ins.A], function.PropertyNames[ins.B],
                        stack[frameBase + ins.C]);
                    stack = _stack;
                    break;
                }

                case OpCode.DeletePropByName:
                {
                    var deleted = _host.Interp2DeletePropertyByName(
                        stack[frameBase + ins.B], function.PropertyNames[ins.C], layout.IsStrict);
                    stack = _stack;
                    stack[frameBase + ins.A] = deleted;
                    break;
                }

                case OpCode.DeleteElem:
                {
                    var deleted = _host.Interp2DeleteElement(
                        stack[frameBase + ins.B], stack[frameBase + ins.C], layout.IsStrict);
                    stack = _stack;
                    stack[frameBase + ins.A] = deleted;
                    break;
                }

                case OpCode.EnumerateKeys:
                {
                    var forIn = _host.Interp2CreateForInIterator(stack[frameBase + ins.B]);
                    stack = _stack;
                    stack[frameBase + ins.A] = forIn;
                    break;
                }

                case OpCode.ForInNext:
                {
                    var hasNext = _host.Interp2ForInNext(stack[frameBase + ins.B], out var key);
                    stack = _stack;
                    if (hasNext)
                    {
                        stack[frameBase + ins.A] = key;
                    }
                    else
                    {
                        ip = ins.C;
                    }

                    break;
                }

                case OpCode.EnumerateValues:
                {
                    var forOf = _host.Interp2CreateForOfIterator(stack[frameBase + ins.B], ins.C == 1);
                    stack = _stack;
                    stack[frameBase + ins.A] = forOf;
                    break;
                }

                case OpCode.ForOfNext:
                {
                    var done = _host.Interp2ForOfNext(stack[frameBase + ins.B], out var element);
                    stack = _stack;
                    if (done)
                    {
                        ip = ins.C;
                    }
                    else
                    {
                        stack[frameBase + ins.A] = element;
                    }

                    break;
                }

                case OpCode.IteratorClose:
                    _host.Interp2IteratorClose(stack[frameBase + ins.B], ins.C == 1);
                    stack = _stack;
                    break;

                case OpCode.NewObject:
                    stack[frameBase + ins.A] = _host.Interp2NewObject();
                    break;

                case OpCode.NewRegExp:
                {
                    var pattern = _host.Interp2NewRegExp(function.Constants[ins.B].AsString());
                    stack = _stack;
                    stack[frameBase + ins.A] = pattern;
                    break;
                }

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
        var calleeIsJavaScript = false;
        if (calleeValue.Tag == JsValueTag.Object)
        {
            var target = _host.Heap.GetObject(calleeValue.AsObjectHandle());
            if (target is JsFunctionObject fn)
            {
                var layout = FrameLayout.For(fn.Function);
                if (layout.Eligible)
                {
                    if (Interp2Options.Log) Interp2Stats.RecordCallInLoop();
                    PushFrame(fn, layout, _stack, argStart, argCount, thisValue, returnSlot);
                    return true;
                }

                calleeIsJavaScript = true;

                // Which reason is costing calls, not which is costing bodies.
                // 332 bodies were declined for one reason and 97 for another,
                // and clearing the 332 moved the delegated calls by 0.7%: the
                // work queue has to be weighted by how often a body is entered.
                if (Interp2Options.Log) Interp2Stats.RecordDeclinedCall(layout.Bailout);
            }
            else if (target is NativeFunctionObject native)
            {
                // More than half of every call a real page makes is this one,
                // and the callee has just been resolved. Going back through the
                // general entry would resolve it a second time and walk the
                // proxy/bound/generator ladder to arrive here anyway.
                if (Interp2Options.Log) Interp2Stats.RecordCallDelegated(calleeIsJavaScript: false);
                var nativeResult = _host.Interp2CallNative(
                    native, CallArgs.FromRegisters(_stack, argStart, argCount), thisValue);
                _stack[returnSlot] = nativeResult;
                return false;
            }
        }

        // Splitting the delegated calls says which of two very different things
        // to do about them: a JavaScript body this loop declined is coverage
        // still to win, a native is not.
        if (Interp2Options.Log) Interp2Stats.RecordCallDelegated(calleeIsJavaScript);

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

        BindArgumentsObject(layout, callee, context, window, args);
        BindSelfName(layout, callee, window);
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
            // The ordinary shape: nothing is captured, so every formal is a
            // window slot and the index is already computed.
            for (var i = 0; i < bind; i++)
            {
                stack[window + parameterIndex[i]] = i < argCount ? argSource[argStart + i] : JsValue.Undefined;
            }
        }
        else
        {
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

        BindArgumentsObject(
            layout, callee, context, window, CallArgs.FromRegisters(argSource, argStart, argCount));
        BindSelfName(layout, callee, window);
    }

    /// <summary>
    /// ECMA-262 15.2.5: a named function expression can refer to itself by its
    /// own name, which is a variable of this frame like any other.
    /// </summary>
    private void BindSelfName(FrameLayout layout, JsFunctionObject callee, int window)
    {
        var slot = layout.SelfNameSlot;
        if (slot < 0 || callee.SelfHandle is not { } selfHandle)
        {
            return;
        }

        // Always a register: the layout refuses a body whose own name a closure
        // reads, because the record it would share has no immutable slot to put
        // the binding in.
        _stack[window + layout.RegisterCount + slot] = JsValue.FromObject(selfHandle);
    }

    /// <summary>
    /// ECMA-262 10.2.11 FunctionDeclarationInstantiation: the arguments object
    /// is created after the formals are bound, and lands wherever this body's
    /// variables live.
    /// </summary>
    private void BindArgumentsObject(
        FrameLayout layout,
        JsFunctionObject callee,
        DeclarativeEnvironmentRecord? context,
        int window,
        in CallArgs args)
    {
        var slot = layout.ArgumentsSlot;
        if (slot < 0)
        {
            return;
        }

        var argumentsObject = _host.Interp2CreateArguments(args, layout.RestrictedArguments, callee);
        if (context is not null && layout.SlotHomes[slot] == SlotHome.Context)
        {
            BytecodeInterpreter.Interp2DeclareContextSlot(context, slot, argumentsObject);
            return;
        }

        // Allocating the object may have grown the value stack under a
        // re-entrant call, so the window is addressed through the current array.
        _stack[window + layout.RegisterCount + slot] = argumentsObject;
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
        if (layout.HasLexicalSlots)
        {
            // A frame that left a dead-zone byte set - it returned or threw
            // before the declaration ran - must not hand it to the next one.
            Array.Clear(_tdz, window, windowSize);
        }

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
        frame.HandlerBase = _handlerTop;
        frame.HandlerCount = 0;
        frame.PendingException = JsValue.Undefined;
        frame.HasPendingException = false;

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

        if (layout.HasLexicalSlots)
        {
            var lexical = layout.SlotIsLexical;
            var constants = layout.SlotIsConst;
            var lexicalHomes = layout.SlotHomes;
            for (var slot = 0; slot < lexical.Length; slot++)
            {
                if (!lexical[slot])
                {
                    continue;
                }

                var isConst = (uint)slot < (uint)constants.Length && constants[slot];
                if (lexicalHomes[slot] == SlotHome.Context)
                {
                    // Declared above holding undefined, like every context slot;
                    // a lexical one is not supposed to exist yet.
                    BytecodeInterpreter.Interp2DeclareUninitializedContextSlot(context!, slot, isConst);
                }
                else
                {
                    _tdz[window + layout.RegisterCount + slot] = 1;
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
        Array.Resize(ref _tdz, capacity);
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

    /// <summary>
    /// Find the innermost `try` covering the throw, discard everything between
    /// it and where the throw happened, and continue there.
    /// </summary>
    /// <remarks>
    /// Unwinding is the same operation a return is: cut the frame stack and the
    /// value stack back to the frame that will carry on. Frames left above are
    /// simply no longer traced. The thrown value goes into register 0 of that
    /// frame, which is where the compiler's catch block expects to read it, and
    /// which is a traced slot - so it is rooted from the moment it is stored
    /// with no separate pin to release.
    /// </remarks>
    private bool TryRouteThrow(int entryDepth, JsValue value)
    {
        for (var depth = _depth; depth > entryDepth; depth--)
        {
            ref var frame = ref _frames[depth - 1];
            while (frame.HandlerCount > 0)
            {
                frame.HandlerCount--;
                var entry = frame.HandlerBase + frame.HandlerCount * 2;
                var catchIp = _handlers[entry];
                var finallyIp = _handlers[entry + 1];
                _handlerTop = entry;

                if (catchIp < 0 && finallyIp < 0)
                {
                    // A handler with neither half is not one the compiler emits.
                    // The dispatch loop abandons the frame in that case rather
                    // than trying its outer entries, so this does the same.
                    break;
                }

                _depth = depth;
                _stackTop = frame.Base + frame.Layout.WindowSize;
                _stack[frame.Base] = value;

                if (catchIp >= 0)
                {
                    frame.Ip = catchIp;
                }
                else
                {
                    frame.PendingException = value;
                    frame.HasPendingException = true;
                    frame.Ip = finallyIp;
                }

                return true;
            }
        }

        return false;
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

            // An exception travelling through a finally block is held nowhere
            // else while the block runs.
            if (frame.HasPendingException && frame.PendingException.Tag == JsValueTag.Object)
            {
                tracer.TraceRoot("interp2.frame.pendingException", frame.PendingException.AsObjectHandle());
            }
        }
    }
}
