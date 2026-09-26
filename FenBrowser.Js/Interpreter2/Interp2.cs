using FenBrowser.Js.Bytecode;
using System.Runtime.CompilerServices;
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
/// Everything that a window cannot represent - <c>with</c>, direct <c>eval</c>,
/// a body whose kind this loop does not suspend yet - is refused by
/// <see cref="FrameLayout"/> before the frame is entered, and runs on the old
/// loop unchanged. A generator is no longer one of them: its window travels to
/// the generator object at a yield and back at the resume. The two share a heap, a set of
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

    // A try entry is (catch ip, finally ip, block depth): where a throw or a
    // return goes, and how many block records the frame had pushed when the
    // try began, so reaching the handler pops the blocks it leaves.
    private const int HandlerStride = 3;

    private int _handlerTop;

    internal Interp2(BytecodeInterpreter host) => _host = host;

    /// <summary>Live value-stack slots, for the collector's root walk.</summary>
    internal ReadOnlySpan<JsValue> LiveStack => _stack.AsSpan(0, _stackTop);

    internal int Depth => _depth;

    /// <summary>
    /// The function and instruction pointer of the frame at
    /// <paramref name="index"/> (0 is the outermost), for a stack trace.
    /// </summary>
    internal (BytecodeFunction Function, int Ip) FrameForStackTrace(int index)
    {
        ref var frame = ref _frames[index];
        return (frame.Layout.Function, frame.Ip);
    }

    /// <summary>
    /// The innermost window's function, its instruction pointer (the index
    /// just past the instruction being executed, as the old loop counts it)
    /// and its register span, for diagnostics that name the callee of a
    /// failing call. Null when no window is live.
    /// </summary>
    internal (BytecodeFunction Function, int Ip, ReadOnlyMemory<JsValue> Registers)? CurrentFrameForDiagnostics
    {
        get
        {
            if (_depth <= 0) return null;
            ref var f = ref _frames[_depth - 1];
            var count = Math.Min(f.Layout.RegisterCount, Math.Max(0, _stack.Length - f.Base));
            return (f.Layout.Function, f.Ip, new ReadOnlyMemory<JsValue>(_stack, f.Base, count));
        }
    }

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

        /// <summary>
        /// The innermost block record this frame has pushed, for a body that
        /// keeps its blocks as records (FrameLayout.ScopedBlocks); null outside
        /// every block. <see cref="ScopeDepth"/> counts them, which is what a
        /// handler records so a throw can pop the blocks it leaves.
        /// </summary>
        public EnvironmentRecord? Scope;
        public int ScopeDepth;

        public JsValue This;

        /// <summary>[[Construct]]'s newTarget for this activation; undefined for a call.</summary>
        public JsValue NewTarget;

        /// <summary>
        /// A derived constructor's `this` before super() binds it. A frame with
        /// a function record keeps the binding there instead, where an arrow
        /// that calls super() can reach it.
        /// </summary>
        public bool ThisUninitialized;

        public bool OuterEnvResolved;
        public int Base;
        public int Ip;
        public int ReturnSlot;

        /// <summary>
        /// <see cref="_backEdges"/> when this frame was entered; what the
        /// counter has gained by its return is the back-edges it took itself.
        /// </summary>
        public int BackEdgeMark;

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

        /// <summary>
        /// A return completion injected at a yield by .return(), waiting for
        /// the finally blocks that cover the yield to run. A catch never sees
        /// one: a return is not catchable.
        /// </summary>
        public JsValue PendingReturn;

        public bool HasPendingReturn;

        /// <summary>
        /// The generator this frame is the body of, which its yields suspend
        /// into. Null for an ordinary frame.
        /// </summary>
        public GeneratorObject? Generator;

        /// <summary>
        /// The async activation this frame is the body of, which its awaits
        /// suspend into. Null for an ordinary frame.
        /// </summary>
        public AsyncContext? AsyncContext;
    }

    /// <summary>
    /// Run one eligible function body to completion, together with every
    /// eligible call it makes.
    /// </summary>
    internal JsValue Execute(
        JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue, JsValue newTarget)
    {
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            PushFrame(callee, layout, args, thisValue, returnSlot: -1, newTarget);
            while (true)
            {
                try
                {
                    return Dispatch(entryDepth);
                }
                catch (JsThrownException thrown) when (!thrown.IsUncatchableByScript && HasHandlerAbove(entryDepth))
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

    /// <summary>
    /// Run script or eval code (ECMA-262 16.1.6 ScriptEvaluation, 19.2.1.1
    /// PerformEval): a frame with no callee and no record of its own, whose
    /// names all resolve through <paramref name="environment"/> - where the
    /// host has already instantiated its declarations.
    /// </summary>
    /// <param name="asyncContext">
    /// Module code's activation, which a top-level await suspends into; null
    /// for script and eval code.
    /// </param>
    internal JsValue ExecuteProgram(
        FrameLayout layout, EnvironmentRecord environment, JsValue thisValue, AsyncContext? asyncContext = null)
    {
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            var window = Reserve(layout);
            ref var frame = ref _frames[_depth++];
            frame.Layout = layout;
            frame.Callee = null;
            frame.OuterEnv = environment;
            frame.OuterEnvResolved = true;
            frame.Context = null;
            frame.This = thisValue;
            frame.NewTarget = JsValue.Undefined;
            frame.ThisUninitialized = false;
            frame.Base = window;
            frame.Ip = 0;
            frame.BackEdgeMark = _backEdges;
            frame.ReturnSlot = -1;
            frame.HandlerBase = _handlerTop;
            frame.HandlerCount = 0;
            frame.PendingException = JsValue.Undefined;
            frame.HasPendingException = false;
            frame.PendingReturn = JsValue.Undefined;
            frame.HasPendingReturn = false;
            frame.Generator = null;
            frame.AsyncContext = asyncContext;
            frame.Scope = null;
            frame.ScopeDepth = 0;
            if (Interp2Options.Log)
            {
                Interp2Stats.RecordFrameEntered(_depth, _stackTop);
            }

            return DispatchRouted(entryDepth, injectedThrow: false, injected: JsValue.Undefined);
        }
        finally
        {
            _depth = entryDepth;
            _stackTop = entryTop;
            _handlerTop = entryHandlerTop;
        }
    }

    /// <summary>
    /// Start or resume a generator body. ECMA-262 27.5.3.2 GeneratorResume and
    /// 27.5.3.3/27.5.3.4 GeneratorResumeAbrupt, for the bodies this loop runs.
    /// </summary>
    /// <remarks>
    /// The generator object holds the frame between resumes: its Registers is
    /// this loop's window, sized to hold the whole of one, and its instruction
    /// pointer is where the yield left off. Which loop a generator runs on is
    /// decided when it is created and never changes, because the two lay a
    /// frame out differently.
    /// </remarks>
    internal JsValue RunGenerator(GeneratorObject generator)
    {
        var layout = FrameLayout.For(generator.Function);
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            // A body suspended inside yield* resumes at the yield* itself, which
            // forwards a throw or return to the delegate rather than taking it
            // here (ECMA-262 15.5.5).
            var delegating = generator.YieldStarIterator is not null;
            var mode = generator.CompletionMode;
            if (!delegating)
            {
                generator.CompletionMode = GeneratorCompletionMode.Normal;
            }

            var injectedThrow = false;

            if (generator.InstructionPointer > 0 && delegating)
            {
                ResumeGeneratorFrame(generator, layout);
            }
            else if (generator.InstructionPointer > 0)
            {
                ResumeGeneratorFrame(generator, layout);

                // ECMA-262 27.5.3.4 GeneratorResumeAbrupt. A throw is raised at
                // the yield, so the try blocks around it can catch it - which
                // means raising it inside the routing loop below rather than
                // here. A return runs the finally blocks that cover the yield,
                // and only those; with none, the generator is simply done.
                if (mode == GeneratorCompletionMode.Throw)
                {
                    injectedThrow = true;
                }
                else if (mode == GeneratorCompletionMode.Return)
                {
                    ref var resumed = ref _frames[_depth - 1];
                    if (!TryRouteReturnThroughFinally(ref resumed, generator.SentValue))
                    {
                        _depth--;
                        _stackTop = resumed.Base;
                        return generator.SentValue;
                    }
                }
            }
            else
            {
                // Not started: there is no frame for a completion to land in, so
                // the body never runs at all.
                if (mode == GeneratorCompletionMode.Return)
                {
                    return generator.SentValue;
                }

                if (mode == GeneratorCompletionMode.Throw)
                {
                    generator.State = GeneratorState.Completed;
                    throw new JsThrownException(generator.SentValue);
                }

                var callee = GeneratorCallee(generator);
                var args = generator.InitialArgs;
                PushFrame(callee!, layout, args, 0, args.Length, generator.ThisValue, returnSlot: -1);
                _frames[_depth - 1].Generator = generator;
            }

            return DispatchRouted(entryDepth, injectedThrow, generator.SentValue);
        }
        finally
        {
            _depth = entryDepth;
            _stackTop = entryTop;
            _handlerTop = entryHandlerTop;
        }
    }

    /// <summary>
    /// Run an async function body from its start. It returns when the body
    /// finishes, or when an await suspends it - the caller settles the promise
    /// in the first case and hands back the pending one in the second, exactly
    /// as it does for a body on the old loop.
    /// </summary>
    internal JsValue RunAsync(
        AsyncContext context, JsFunctionObject callee, JsValue[] args, JsValue thisValue)
    {
        var layout = FrameLayout.For(context.Function);
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            PushFrame(callee, layout, args, 0, args.Length, thisValue, returnSlot: -1);
            _frames[_depth - 1].AsyncContext = context;
            return DispatchRouted(entryDepth, injectedThrow: false, JsValue.Undefined);
        }
        finally
        {
            _depth = entryDepth;
            _stackTop = entryTop;
            _handlerTop = entryHandlerTop;
        }
    }

    /// <summary>
    /// Resume an async function body when the promise it awaited settles.
    /// ECMA-262 27.7.5.3: a rejection arrives as a throw at the await, so the
    /// try blocks around it can catch it.
    /// </summary>
    internal JsValue ResumeAsync(AsyncContext context)
    {
        var layout = FrameLayout.For(context.Function);
        var entryDepth = _depth;
        var entryTop = _stackTop;
        var entryHandlerTop = _handlerTop;
        try
        {
            ResumeAsyncFrame(context, layout);
            var rejected = context.IsRejectResume;
            context.IsRejectResume = false;
            return DispatchRouted(entryDepth, rejected, context.SentValue);
        }
        finally
        {
            _depth = entryDepth;
            _stackTop = entryTop;
            _handlerTop = entryHandlerTop;
        }
    }

    /// <summary>
    /// The dispatch, with this loop's throw routing around it, and optionally
    /// an exception raised at the resumption point first.
    /// </summary>
    private JsValue DispatchRouted(int entryDepth, bool injectedThrow, JsValue injected)
    {
        while (true)
        {
            try
            {
                if (injectedThrow)
                {
                    injectedThrow = false;
                    throw new JsThrownException(injected);
                }

                return Dispatch(entryDepth);
            }
            catch (JsThrownException thrown) when (!thrown.IsUncatchableByScript && HasHandlerAbove(entryDepth))
            {
                if (!TryRouteThrow(entryDepth, thrown.Value))
                {
                    throw;
                }
            }
        }
    }

    private void ResumeAsyncFrame(AsyncContext context, FrameLayout layout)
    {
        var window = Reserve(layout);
        var size = Math.Min(layout.WindowSize, context.Registers.Length);
        Array.Copy(context.Registers, 0, _stack, window, size);
        if (layout.HasLexicalSlots && context.SavedDeadZone.Length >= size)
        {
            Array.Copy(context.SavedDeadZone, 0, _tdz, window, size);
        }

        var savedHandlers = context.SavedWindowHandlers;
        var handlerBase = _handlerTop;
        if (savedHandlers.Length > 0)
        {
            while (_handlerTop + savedHandlers.Length > _handlers.Length)
            {
                Array.Resize(ref _handlers, _handlers.Length * 2);
            }

            Array.Copy(savedHandlers, 0, _handlers, _handlerTop, savedHandlers.Length);
            _handlerTop += savedHandlers.Length;
        }

        ref var frame = ref _frames[_depth++];
        frame.Layout = layout;
        frame.Callee = null;
        frame.OuterEnv = context.OuterEnvironment;
        frame.OuterEnvResolved = true;
        frame.Context = context.Environment as DeclarativeEnvironmentRecord;
        frame.This = context.ThisValue;
        frame.Base = window;
        frame.Ip = context.InstructionPointer;
        frame.ReturnSlot = -1;
        frame.BackEdgeMark = _backEdges;
        frame.HandlerBase = handlerBase;
        frame.HandlerCount = savedHandlers.Length / HandlerStride;
        frame.PendingException = context.PendingException ?? JsValue.Undefined;
        frame.HasPendingException = context.PendingException is not null;
        frame.PendingReturn = JsValue.Undefined;
        frame.HasPendingReturn = false;
        frame.Generator = null;
        frame.AsyncContext = context;
        frame.NewTarget = JsValue.Undefined;
        frame.ThisUninitialized = false;
        frame.Scope = context.BlockScope;
        frame.ScopeDepth = context.BlockScopeDepth;

        // What the promise settled with is what the await expression evaluates to.
        if (context.AwaitDestReg >= 0)
        {
            _stack[window + context.AwaitDestReg] = context.SentValue;
        }

        context.AwaitDestReg = -1;
        context.IsSuspended = false;
    }

    /// <summary>The await's half of <see cref="SaveGeneratorWindow"/>.</summary>
    private void SaveAsyncWindow(AsyncContext context, int destinationRegister, int ip)
    {
        ref var frame = ref _frames[_depth - 1];
        var layout = frame.Layout;
        var window = frame.Base;
        var size = Math.Min(layout.WindowSize, context.Registers.Length);
        Array.Copy(_stack, window, context.Registers, 0, size);
        for (var i = 0; i < size; i++)
        {
            context.BarrierInternalSlot(context.Registers[i]);
        }

        if (layout.HasLexicalSlots)
        {
            if (context.SavedDeadZone.Length < size)
            {
                context.SavedDeadZone = new byte[size];
            }

            Array.Copy(_tdz, window, context.SavedDeadZone, 0, size);
        }

        var handlerCount = frame.HandlerCount * HandlerStride;
        if (handlerCount == 0)
        {
            context.SavedWindowHandlers = Array.Empty<int>();
        }
        else
        {
            if (context.SavedWindowHandlers.Length != handlerCount)
            {
                context.SavedWindowHandlers = new int[handlerCount];
            }

            Array.Copy(_handlers, frame.HandlerBase, context.SavedWindowHandlers, 0, handlerCount);
        }

        if (frame.HasPendingException)
        {
            context.PendingException = frame.PendingException;
            context.BarrierInternalSlot(frame.PendingException);
        }
        else
        {
            context.PendingException = null;
        }

        context.InstructionPointer = ip;
        context.AwaitDestReg = destinationRegister;
        context.Environment = frame.Context;
        context.BlockScope = frame.Scope;
        context.BlockScopeDepth = frame.ScopeDepth;
        context.IsSuspended = true;
    }

    private JsFunctionObject? GeneratorCallee(GeneratorObject generator)
        => generator.SelfHandle is { } handle && _host.Heap.GetObject(handle) is JsFunctionObject callee
            ? callee
            : null;

    /// <summary>Put a suspended window back on the stack and continue in it.</summary>
    private void ResumeGeneratorFrame(GeneratorObject generator, FrameLayout layout)
    {
        var window = Reserve(layout);
        var size = Math.Min(layout.WindowSize, generator.Registers.Length);
        Array.Copy(generator.Registers, 0, _stack, window, size);
        if (layout.HasLexicalSlots && generator.SavedDeadZone.Length >= size)
        {
            Array.Copy(generator.SavedDeadZone, 0, _tdz, window, size);
        }

        var savedHandlers = generator.SavedWindowHandlers;
        var handlerBase = _handlerTop;
        if (savedHandlers.Length > 0)
        {
            while (_handlerTop + savedHandlers.Length > _handlers.Length)
            {
                Array.Resize(ref _handlers, _handlers.Length * 2);
            }

            Array.Copy(savedHandlers, 0, _handlers, _handlerTop, savedHandlers.Length);
            _handlerTop += savedHandlers.Length;
        }

        ref var frame = ref _frames[_depth++];
        frame.Layout = layout;
        frame.Callee = GeneratorCallee(generator);
        frame.OuterEnv = generator.OuterEnvironment;
        frame.OuterEnvResolved = true;
        frame.Context = generator.Environment as DeclarativeEnvironmentRecord;
        frame.This = generator.ThisValue;
        frame.Base = window;
        frame.Ip = generator.InstructionPointer;
        frame.ReturnSlot = -1;
        frame.BackEdgeMark = _backEdges;
        frame.HandlerBase = handlerBase;
        frame.HandlerCount = savedHandlers.Length / HandlerStride;
        frame.PendingException = generator.PendingException ?? JsValue.Undefined;
        frame.HasPendingException = generator.PendingException is not null;
        frame.PendingReturn = JsValue.Undefined;
        frame.HasPendingReturn = false;
        frame.Generator = generator;
        frame.AsyncContext = null;
        frame.NewTarget = JsValue.Undefined;
        frame.ThisUninitialized = false;
        frame.Scope = generator.BlockScope;
        frame.ScopeDepth = generator.BlockScopeDepth;

        // The value .next() was given is what the yield expression evaluates to.
        if (generator.YieldDestReg >= 0)
        {
            _stack[window + generator.YieldDestReg] = generator.SentValue;
        }

        generator.YieldDestReg = -1;
        generator.State = GeneratorState.Executing;
    }

    /// <summary>
    /// Copy the running frame's window into the generator, which is what a
    /// suspended generator is: a frame that outlives the call that made it.
    /// </summary>
    private void SaveGeneratorWindow(GeneratorObject generator, int destinationRegister, int ip)
    {
        ref var frame = ref _frames[_depth - 1];
        var layout = frame.Layout;
        var window = frame.Base;
        var size = Math.Min(layout.WindowSize, generator.Registers.Length);
        Array.Copy(_stack, window, generator.Registers, 0, size);

        // Everything in that window is now reachable only through the generator,
        // which the collector reaches by tracing it as an internal slot.
        for (var i = 0; i < size; i++)
        {
            generator.BarrierInternalSlot(generator.Registers[i]);
        }

        if (layout.HasLexicalSlots)
        {
            if (generator.SavedDeadZone.Length < size)
            {
                generator.SavedDeadZone = new byte[size];
            }

            Array.Copy(_tdz, window, generator.SavedDeadZone, 0, size);
        }

        // The try blocks open around the yield, and an exception a finally is
        // still carrying, are as much of the frame as the registers are.
        var handlerCount = frame.HandlerCount * HandlerStride;
        if (handlerCount == 0)
        {
            generator.SavedWindowHandlers = Array.Empty<int>();
        }
        else
        {
            if (generator.SavedWindowHandlers.Length != handlerCount)
            {
                generator.SavedWindowHandlers = new int[handlerCount];
            }

            Array.Copy(_handlers, frame.HandlerBase, generator.SavedWindowHandlers, 0, handlerCount);
        }

        if (frame.HasPendingException)
        {
            generator.PendingException = frame.PendingException;
            generator.BarrierInternalSlot(frame.PendingException);
        }
        else
        {
            generator.PendingException = null;
        }

        generator.InstructionPointer = ip;
        generator.YieldDestReg = destinationRegister;
        generator.Environment = frame.Context;
        generator.BlockScope = frame.Scope;
        generator.BlockScopeDepth = frame.ScopeDepth;
        generator.State = GeneratorState.Suspended;
    }

    /// <summary>
    /// Send a return completion to the innermost finally block covering the
    /// suspension, if there is one. A catch-only entry is skipped: a return
    /// completion is not catchable (ECMA-262 27.5.3.3).
    /// </summary>
    private bool TryRouteReturnThroughFinally(ref Frame frame, JsValue value)
    {
        while (frame.HandlerCount > 0)
        {
            frame.HandlerCount--;
            var entry = frame.HandlerBase + frame.HandlerCount * HandlerStride;
            var finallyIp = _handlers[entry + 1];
            _handlerTop = entry;
            if (finallyIp >= 0)
            {
                UnwindScopes(ref frame, _handlers[entry + 2]);
                frame.PendingReturn = value;
                frame.HasPendingReturn = true;
                frame.Ip = finallyIp;
                return true;
            }
        }

        return false;
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

                    if (layout.IsDerivedConstructor)
                    {
                        var derivedThis = DerivedThis();
                        stack = _stack;
                        stack[frameBase + ins.A] = derivedThis;
                        break;
                    }

                    stack[frameBase + ins.A] = _frames[_depth - 1].This;
                    break;

                case OpCode.LoadNewTarget:
                    // ECMA-262 13.3.12.1 GetNewTarget: the running function's, or
                    // for an arrow the one GetThisEnvironment finds around it.
                    if (layout.ResolvesThisOutwards)
                    {
                        var lexicalNewTarget = _host.Interp2ResolveNewTarget(OuterEnvironmentOf(_depth - 1));
                        stack[frameBase + ins.A] = lexicalNewTarget;
                        break;
                    }

                    stack[frameBase + ins.A] = _frames[_depth - 1].NewTarget;
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

                    if (home == SlotHome.Scoped)
                    {
                        var scopedValue = LoadScoped(layout, slot, slotBase, ip - 1);
                        stack = _stack;
                        stack[frameBase + ins.A] = scopedValue;
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

                case OpCode.LoadVarWithBase:
                {
                    var callee = LoadWithBase(layout, ins.B, slotBase, ip - 1, out var withBase);
                    stack = _stack;
                    stack[frameBase + ins.A] = callee;
                    stack[frameBase + ins.C] = withBase;
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
                        _host.Interp2PreResolveFreeCached(layout, ins.B, OuterEnvironmentOf(_depth - 1));
                        stack = _stack;
                    }
                    else if (HomeOfSlot(layout, ins.B) == SlotHome.Scoped)
                    {
                        PreResolveScoped(layout, ins.B);
                        stack = _stack;
                    }

                    break;

                case OpCode.NextIterationEnv:
                {
                    // Only a body with scoped blocks has this opcode (the layout
                    // refuses it otherwise): each turn of a `for (let ...)` loop
                    // gets its own copy of the head's bindings.
                    ref var turning = ref _frames[_depth - 1];
                    turning.Scope = _host.CopyIterationScopes(turning.Scope, ins.A);
                    break;
                }

                case OpCode.EnterScope:
                    if (layout.ScopedBlocks)
                    {
                        // ECMA-262 14.2.2: a block gets a fresh declarative record
                        // over the running one, holding its binding.
                        var blockOuter = _frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1);
                        ref var entering = ref _frames[_depth - 1];
                        entering.Scope = _host.CreateBlockScope(blockOuter, function, ins);
                        entering.ScopeDepth++;
                        break;
                    }

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
                    if (layout.ScopedBlocks)
                    {
                        UnwindScopes(ref _frames[_depth - 1], _frames[_depth - 1].ScopeDepth - 1);
                    }

                    break;

                case OpCode.DynamicImport:
                case OpCode.ImportSource:
                case OpCode.ImportDefer:
                {
                    // ECMA-262 13.3.10.1 EvaluateImportCall: the referrer is the
                    // module or script whose scope the call sits in.
                    var referrer = _frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1);
                    var importPromise = ins.OpCode switch
                    {
                        OpCode.DynamicImport => _host.HandleDynamicImport(
                            stack[frameBase + ins.B], stack[frameBase + ins.C], referrer),
                        OpCode.ImportSource => _host.HandleImportSource(
                            stack[frameBase + ins.B], stack[frameBase + ins.C], referrer),
                        _ => _host.HandleImportDefer(stack[frameBase + ins.B], stack[frameBase + ins.C], referrer),
                    };
                    stack = _stack;
                    stack[frameBase + ins.A] = importPromise;
                    break;
                }

                case OpCode.ImportMeta:
                {
                    var meta = _host.HandleImportMeta(_frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1));
                    stack = _stack;
                    stack[frameBase + ins.A] = meta;
                    break;
                }

                case OpCode.PushWithEnvironment:
                {
                    // ECMA-262 14.11.2: the body resolves names against the
                    // object first. Popped by the LeaveScope that closes the
                    // statement, like a block's record.
                    var withScope = _host.CreateWithEnvironment(
                        stack[frameBase + ins.A], _frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1));
                    PushScope(withScope);
                    stack = _stack;
                    break;
                }

                case OpCode.EnterFunctionBodyScope:
                {
                    // ECMA-262 10.2.11 steps 28 and 30: once the parameters are
                    // bound, the body gets a variable environment of its own, so
                    // a closure made in a default value never sees the body's vars.
                    var bodyScope = _host.CreateFunctionBodyScope(
                        function, _frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1)!);
                    PushScope(bodyScope);
                    stack = _stack;
                    break;
                }

                case OpCode.StoreVarTop:
                    StoreVarTop(layout, ins.B, slotBase, stack[frameBase + ins.A]);
                    stack = _stack;
                    break;

                case OpCode.Delete:
                {
                    var deleted = DeleteName(layout, ins.B);
                    stack = _stack;
                    stack[frameBase + ins.A] = deleted;
                    break;
                }

                case OpCode.Await:
                {
                    // ECMA-262 27.7.5.2 Await. Resolving the value can run user
                    // code - a thenable's `then` getter - so the window is saved
                    // after that and before the reactions are attached.
                    // An async generator's frame has no activation to suspend
                    // into, here or on the old loop, so its awaits stay inline.
                    var awaiting = _frames[_depth - 1].AsyncContext;
                    var suspends = _host.Interp2AwaitPrepare(
                        stack[frameBase + ins.B], awaiting is not null,
                        out var awaitedPromise, out var inlineResult);
                    stack = _stack;
                    if (!suspends)
                    {
                        stack[frameBase + ins.A] = inlineResult;
                        break;
                    }

                    SaveAsyncWindow(awaiting!, ins.A, ip);
                    _host.Interp2AwaitAttach(awaiting!, awaitedPromise);
                    _depth--;
                    _stackTop = frameBase;
                    return JsValue.Undefined;
                }

                case OpCode.YieldStar:
                {
                    // ECMA-262 15.5.5 yield*: one delegation step per entry. A
                    // suspension leaves the IP on this instruction, so the resume
                    // runs the next step with whatever completion it brought.
                    var delegator = _frames[_depth - 1].Generator!;
                    _frames[_depth - 1].Ip = ip;
                    var outcome = _host.YieldStarStep(delegator, stack[frameBase + ins.B], out var stepValue);
                    stack = _stack;
                    if (outcome == BytecodeInterpreter.YieldStarOutcome.Done)
                    {
                        stack[frameBase + ins.A] = stepValue;
                        break;
                    }

                    if (outcome == BytecodeInterpreter.YieldStarOutcome.Yield)
                    {
                        SaveGeneratorWindow(delegator, ins.A, ip - 1);
                        _depth--;
                        _stackTop = frameBase;
                        return stepValue;
                    }

                    // The delegate finished a return: the body returns too, after
                    // the finally blocks that cover the yield*.
                    ref var returning = ref _frames[_depth - 1];
                    if (TryRouteReturnThroughFinally(ref returning, stepValue))
                    {
                        goto reload;
                    }

                    var yieldStarReturnSlot = returning.ReturnSlot;
                    _depth--;
                    _stackTop = frameBase;
                    if (_depth == entryDepth)
                    {
                        return stepValue;
                    }

                    stack[yieldStarReturnSlot] = stepValue;
                    goto reload;
                }

                case OpCode.EnumerateValuesAsync:
                {
                    var asyncIterator = _host.CreateForAwaitIteratorState(stack[frameBase + ins.B]);
                    stack = _stack;
                    stack[frameBase + ins.A] = asyncIterator;
                    break;
                }

                case OpCode.AsyncIterNext:
                {
                    // A sync-backed iterator knows it is done before the Await.
                    var exhausted = _host.ForAwaitNext(stack[frameBase + ins.B], out var awaitable);
                    stack = _stack;
                    if (exhausted)
                    {
                        ip = ins.C;
                        break;
                    }

                    stack[frameBase + ins.A] = awaitable;
                    break;
                }

                case OpCode.AsyncIterFinish:
                {
                    var finished = _host.ForAwaitFinish(
                        stack[frameBase + ins.C], stack[frameBase + ins.B], out var loopValue);
                    stack = _stack;
                    if (finished)
                    {
                        ip = ins.D;
                        break;
                    }

                    stack[frameBase + ins.A] = loopValue;
                    break;
                }

                case OpCode.Yield:
                {
                    // ECMA-262 27.5.3.7 GeneratorYield: the window goes to the
                    // generator and the value comes back as the iterator result.
                    // A yield only ever appears in the generator's own body, so
                    // this frame is the one RunGenerator entered.
                    var yielding = _frames[_depth - 1].Generator!;
                    var yielded = stack[frameBase + ins.B];
                    SaveGeneratorWindow(yielding, ins.A, ip);
                    _depth--;
                    _stackTop = frameBase;
                    return _host.Interp2CreateIteratorResult(yielded, done: false);
                }

                case OpCode.StoreVar:
                case OpCode.StoreResolvedVar:
                {
                    // A block's binding is found before anything the body
                    // declares under the same slot - its own name included.
                    if (HomeOfSlot(layout, ins.B) == SlotHome.Scoped)
                    {
                        StoreScoped(
                            layout, ins.B, slotBase, ip - 1, stack[frameBase + ins.A],
                            resolved: ins.OpCode == OpCode.StoreResolvedVar);
                        stack = _stack;
                        break;
                    }

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
                            layout, ins.B, ip - 1, OuterEnvironmentOf(_depth - 1), stack[frameBase + ins.A]);
                        stack = _stack;
                        break;
                    }

                    goto case OpCode.InitVar;
                }

                case OpCode.InitVar:
                {
                    var slot = ins.B;
                    var home = HomeOfSlot(layout, slot);
                    if (home == SlotHome.Scoped)
                    {
                        InitScoped(layout, slot, slotBase, ip - 1, stack[frameBase + ins.A]);
                        stack = _stack;
                        break;
                    }

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
                    else if (ins.OpCode == OpCode.InitVar)
                    {
                        // A declaration of program code: its binding was
                        // instantiated by name, uninitialized if lexical.
                        _host.Interp2InitializeName(
                            OuterEnvironmentOf(_depth - 1), NameOfSlot(layout, slot), stack[frameBase + ins.A]);
                    }
                    else
                    {
                        _host.Interp2StoreFreeCached(
                            layout, slot, ip - 1, OuterEnvironmentOf(_depth - 1), stack[frameBase + ins.A]);
                    }

                    stack = _stack;
                    break;
                }

                // ------------------------------------------------ control flow
                case OpCode.Jump:
                    if (ins.A < ip - 1)
                    {
                        heap.CollectAtSafePointIfRequested();
                        _backEdges++;
                    }

                    ip = ins.A;
                    break;

                case OpCode.JumpIfFalse:
                    if (!_host.Interp2IsTruthy(stack[frameBase + ins.A]))
                    {
                        if (ins.B < ip - 1)
                        {
                            heap.CollectAtSafePointIfRequested();
                            _backEdges++;
                        }

                        ip = ins.B;
                    }

                    break;

                case OpCode.Nop:
                    break;

                case OpCode.PrologueEnd:
                    // ECMA-262 27.5.1.1: a generator binds its parameters when it
                    // is called and suspends here, so the call can hand the
                    // generator object back before any of the body runs.
                    if (_frames[_depth - 1].Generator is { } starting)
                    {
                        SaveGeneratorWindow(starting, 0, ip);
                        _depth--;
                        _stackTop = frameBase;
                        return JsValue.Undefined;
                    }

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

                    if (_handlerTop + HandlerStride > _handlers.Length)
                    {
                        Array.Resize(ref _handlers, _handlers.Length * 2);
                    }

                    _handlers[_handlerTop] = ins.A;
                    _handlers[_handlerTop + 1] = ins.D;
                    _handlers[_handlerTop + 2] = entering.ScopeDepth;
                    _handlerTop += HandlerStride;
                    entering.HandlerCount++;
                    break;
                }

                case OpCode.PopHandler:
                {
                    ref var leaving = ref _frames[_depth - 1];
                    if (leaving.HandlerCount > 0)
                    {
                        leaving.HandlerCount--;
                        _handlerTop = leaving.HandlerBase + leaving.HandlerCount * HandlerStride;
                    }

                    break;
                }

                case OpCode.EndFinally:
                {
                    // ECMA-262 14.15.3: a finally block that completes normally
                    // hands the abrupt completion it interrupted back on.
                    ref var finishing = ref _frames[_depth - 1];
                    if (finishing.HasPendingException)
                    {
                        var pending = finishing.PendingException;
                        finishing.HasPendingException = false;
                        finishing.PendingException = JsValue.Undefined;
                        finishing.Ip = ip;
                        throw new JsThrownException(pending);
                    }

                    if (!finishing.HasPendingReturn)
                    {
                        break;
                    }

                    // A generator's .return() waiting on this finally: hand it
                    // to the next one out, or complete the body with it.
                    var pendingReturn = finishing.PendingReturn;
                    finishing.HasPendingReturn = false;
                    finishing.PendingReturn = JsValue.Undefined;
                    finishing.Ip = ip;
                    if (TryRouteReturnThroughFinally(ref finishing, pendingReturn))
                    {
                        goto reload;
                    }

                    var pendingReturnSlot = finishing.ReturnSlot;
                    _depth--;
                    _stackTop = frameBase;
                    if (_depth == entryDepth)
                    {
                        return pendingReturn;
                    }

                    stack[pendingReturnSlot] = pendingReturn;
                    goto reload;
                }

                case OpCode.Return:
                {
                    var returnValue = stack[frameBase + ins.A];
                    if (layout.IsDerivedConstructor)
                    {
                        returnValue = DerivedConstructorResult(returnValue);
                    }

                    ref var returning = ref _frames[_depth - 1];
                    var returnSlot = returning.ReturnSlot;
                    if (_backEdges != returning.BackEdgeMark)
                    {
                        SettleBackEdges(ref returning);
                    }

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
                    if (home == SlotHome.Scoped)
                    {
                        var scopedType = TypeOfScoped(layout, slot, slotBase);
                        stack = _stack;
                        stack[frameBase + ins.A] = scopedType;
                        break;
                    }

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
                        function, ip - 1, stack[frameBase + ins.B], function.PropertyNames[ins.C], out var getter);
                    if (getter.Tag == JsValueTag.Object)
                    {
                        if (CallGetter(getter, ip, frameBase + ins.B, frameBase + ins.A))
                            goto reload;
                        stack = _stack;
                        break;
                    }

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
                    // Inside a block record the closure captures the block, so it
                    // sees the block's bindings - its own copy of a loop's.
                    var closure = _host.Interp2CreateFunction(
                        function.NestedFunctions[ins.B],
                        _frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1));
                    stack = _stack;
                    stack[frameBase + ins.A] = closure;
                    break;
                }

                case OpCode.LoadSuperConstructor:
                {
                    // An arrow's super() is the enclosing constructor's.
                    var superConstructor = layout.ResolvesThisOutwards
                        ? _host.GetSuperConstructor(null, OuterEnvironmentOf(_depth - 1))
                        : _host.GetSuperConstructor(_frames[_depth - 1].Callee, null);
                    stack = _stack;
                    stack[frameBase + ins.A] = superConstructor;
                    break;
                }

                case OpCode.SuperCall:
                case OpCode.SuperCallSpread:
                {
                    var superNewTarget = layout.ResolvesThisOutwards
                        ? _host.Interp2ResolveNewTarget(OuterEnvironmentOf(_depth - 1))
                        : _frames[_depth - 1].NewTarget;
                    _frames[_depth - 1].Ip = ip;
                    var superArgs = ins.OpCode == OpCode.SuperCall
                        ? CallArgs.FromRegisters(stack, frameBase + ins.C, ins.D)
                        : new CallArgs(_host.SpreadArguments(stack[frameBase + ins.C]));
                    var constructed = _host.SuperConstruct(stack[frameBase + ins.B], superArgs, superNewTarget);
                    stack = _stack;
                    stack[frameBase + ins.A] = constructed;
                    heap.CollectAtSafePointIfRequested();
                    break;
                }

                case OpCode.InitThisBinding:
                    BindThisAfterSuper(layout, stack[frameBase + ins.A]);
                    stack = _stack;
                    break;

                case OpCode.SetPrototype:
                    _host.SetPrototypeFromCode(stack[frameBase + ins.A], stack[frameBase + ins.B], ins.D != 0);
                    stack = _stack;
                    break;

                case OpCode.ValidateClassHeritage:
                    _host.ValidateClassHeritage(stack[frameBase + ins.A]);
                    stack = _stack;
                    break;

                case OpCode.SetFunctionName:
                    _host.Interp2SetFunctionName(stack[frameBase + ins.A], stack[frameBase + ins.B]);
                    stack = _stack;
                    break;

                case OpCode.DefinePrivateField:
                    _host.DefinePrivateField(
                        function, stack[frameBase + ins.A], function.PropertyNames[ins.B], stack[frameBase + ins.C]);
                    stack = _stack;
                    break;

                case OpCode.StoreFieldKey:
                    _host.StoreComputedFieldKey(stack[frameBase + ins.A], stack[frameBase + ins.B]);
                    stack = _stack;
                    break;

                case OpCode.LoadFieldKey:
                {
                    // The constructor's computed field names, fixed when the class
                    // was defined; a field initializer runs in the constructor.
                    var fieldKeys = _frames[_depth - 1].Callee?.ComputedFieldKeys;
                    stack[frameBase + ins.A] = fieldKeys is not null && (uint)ins.B < (uint)fieldKeys.Count
                        ? fieldKeys[ins.B]
                        : JsValue.Undefined;
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
                    var superKey = ins.OpCode == OpCode.LoadSuperProperty
                        ? JsValue.FromString(function.PropertyNames[ins.B])
                        : stack[frameBase + ins.B];

                    // An arrow's `super` and `this` are the enclosing method's.
                    // ECMA-262 13.3.7.1 step 2 / 9.1.1.3.4: `this` is read first, so a
                    // derived constructor before super() throws.
                    EnvironmentRecord? superEnvironment = null;
                    var superThis = _frames[_depth - 1].This;
                    if (layout.ResolvesThisOutwards)
                    {
                        superEnvironment = OuterEnvironmentOf(_depth - 1);
                        superThis = _host.Interp2ResolveThis(superEnvironment, superThis);
                    }
                    else if (layout.IsDerivedConstructor)
                    {
                        superThis = DerivedThis();
                    }

                    var superValue = _host.Interp2LoadSuper(
                        function, _frames[_depth - 1].Callee, superThis, superEnvironment, superKey,
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
                        stack[frameBase + ins.C], ins.D);
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

                case OpCode.ConstructSpread:
                {
                    var spreadArgs = _host.SpreadArguments(stack[frameBase + ins.C]);
                    _frames[_depth - 1].Ip = ip;
                    var constructed = _host.Interp2Construct(stack[frameBase + ins.B], new CallArgs(spreadArgs));
                    stack = _stack;
                    stack[frameBase + ins.A] = constructed;
                    heap.CollectAtSafePointIfRequested();
                    break;
                }

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
                    // A constructor can capture the stack (`new Error()`), which
                    // reads this frame's position from here.
                    _frames[_depth - 1].Ip = ip;
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
                    if (ins.E != 0 && TryCallDirectEval(
                            layout, ip, stack[frameBase + ins.B], JsValue.Undefined, _stack, 0, 0, frameBase + ins.A))
                    {
                        stack = _stack;
                        break;
                    }

                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, 0, 0, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.Call1:
                    _frames[_depth - 1].Ip = ip;
                    if (ins.E != 0 && TryCallDirectEval(
                            layout, ip, stack[frameBase + ins.B], JsValue.Undefined, _stack, frameBase + ins.C, 1, frameBase + ins.A))
                    {
                        stack = _stack;
                        break;
                    }

                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, frameBase + ins.C, 1, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallN:
                    _frames[_depth - 1].Ip = ip;
                    if (ins.E != 0 && TryCallDirectEval(
                            layout, ip, stack[frameBase + ins.B], JsValue.Undefined, _stack, frameBase + ins.C, ins.D, frameBase + ins.A))
                    {
                        stack = _stack;
                        break;
                    }

                    if (Call(stack[frameBase + ins.B], JsValue.Undefined, frameBase + ins.C, ins.D, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;

                case OpCode.CallSpread:
                {
                    // ECMA-262 13.3.8.1 ArgumentListEvaluation: the compiler has
                    // already gathered every argument, spreads included, into the
                    // array in C. It stays in this frame's register for the whole
                    // call, so the values unpacked from it stay reachable.
                    var spreadArgs = _host.SpreadArguments(stack[frameBase + ins.C]);
                    var spreadThis = ins.D != 0 ? stack[frameBase + ins.D] : JsValue.Undefined;
                    _frames[_depth - 1].Ip = ip;
                    if (ins.E != 0 && TryCallDirectEval(
                            layout, ip, stack[frameBase + ins.B], spreadThis, spreadArgs, 0, spreadArgs.Length,
                            frameBase + ins.A))
                    {
                        stack = _stack;
                        break;
                    }

                    if (CallFrom(stack[frameBase + ins.B], spreadThis, spreadArgs, 0, spreadArgs.Length, frameBase + ins.A))
                        goto reload;
                    stack = _stack;
                    heap.CollectAtSafePointIfRequested();
                    break;
                }

                case OpCode.TailCall0:
                case OpCode.TailCall1:
                case OpCode.TailCallN:
                {
                    // ECMA-262 15.10.3 PrepareForTailCall: a strict `return f(...)`
                    // finishes this frame before the callee starts, so tail
                    // recursion runs in constant space. The arguments are copied
                    // out of the window being released and pinned while the
                    // callee's frame is set up, which can allocate - nothing else
                    // holds them then.
                    var tailArgCount = ins.OpCode switch
                    {
                        OpCode.TailCall0 => 0,
                        OpCode.TailCall1 => 1,
                        _ => ins.D,
                    };
                    var tailArgs = tailArgCount == 0 ? Array.Empty<JsValue>() : new JsValue[tailArgCount];
                    Array.Copy(stack, frameBase + ins.C, tailArgs, 0, tailArgCount);
                    var tailCallee = stack[frameBase + ins.B];

                    ref var finishing = ref _frames[_depth - 1];
                    var tailReturnSlot = finishing.ReturnSlot;
                    if (_backEdges != finishing.BackEdgeMark)
                    {
                        SettleBackEdges(ref finishing);
                    }

                    if (finishing.HandlerCount > 0)
                    {
                        _handlerTop = finishing.HandlerBase;
                        finishing.HandlerCount = 0;
                    }

                    _depth--;
                    _stackTop = frameBase;

                    var rootMark = heap.RootCount;
                    if (tailCallee.Tag == JsValueTag.Object) heap.PushRoot(tailCallee.AsObjectHandle());
                    foreach (var tailArg in tailArgs)
                    {
                        if (tailArg.Tag == JsValueTag.Object) heap.PushRoot(tailArg.AsObjectHandle());
                    }

                    bool pushed;
                    JsValue tailResult;
                    try
                    {
                        pushed = TryPushTailCallee(tailCallee, tailArgs, tailReturnSlot, out tailResult);
                    }
                    finally
                    {
                        heap.PopRootsTo(rootMark);
                    }

                    if (pushed)
                        goto reload;

                    // Delivered as the finished frame's return value would be.
                    if (_depth == entryDepth)
                        return tailResult;
                    stack = _stack;
                    stack[tailReturnSlot] = tailResult;
                    goto reload;
                }

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Call(JsValue calleeValue, JsValue thisValue, int argStart, int argCount, int returnSlot)
        => CallFrom(calleeValue, thisValue, _stack, argStart, argCount, returnSlot);

    /// <summary>
    /// <see cref="Call"/> with the arguments in <paramref name="argSource"/>
    /// rather than in this frame's window - a spread call's unpacked list.
    /// </summary>
    private bool CallFrom(
        JsValue calleeValue, JsValue thisValue, JsValue[] argSource, int argStart, int argCount, int returnSlot)
    {
        var calleeIsJavaScript = false;
        if (calleeValue.Tag == JsValueTag.Object)
        {
            var target = _host.Heap.GetObject(calleeValue.AsObjectHandle());
            if (target is JsFunctionObject fn)
            {
                var layout = FrameLayout.For(fn.Function);
                if (layout.Eligible
#if !PUBLISH_AOT
                    // A loop-heavy body that has been compiled runs compiled,
                    // entered through the old loop's CallFunction below.
                    && !JitCompiler.PrefersCompiled(fn.Function)
#endif
                    )
                {
                    if (Interp2Options.Log) Interp2Stats.RecordCallInLoop();
                    // Counted here only: a call handed to the old loop is
                    // counted by CallFunction.
                    fn.Function.Invocations++;
                    PushFrame(fn, layout, argSource, argStart, argCount, thisValue, returnSlot);
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
                    native, CallArgs.FromRegisters(argSource, argStart, argCount), thisValue);
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
            calleeValue, CallArgs.FromRegisters(argSource, argStart, argCount), thisValue);

        // _stack is re-read on the next line rather than reused from this one:
        // the callee may have re-entered this loop deeply enough to grow the
        // value stack, and the array the arguments were read out of is then the
        // one it replaced. Every host call in the dispatch follows the same rule.
        _stack[returnSlot] = result;
        return false;
    }

    /// <summary>
    /// The callee of a tail call, in the place of the frame that made it: a
    /// JavaScript body this loop runs gets a frame returning to that frame's
    /// return slot, and true. Anything else runs to completion here and its
    /// result comes back in <paramref name="result"/>, for the caller to
    /// deliver as that frame's return value.
    /// </summary>
    private bool TryPushTailCallee(JsValue calleeValue, JsValue[] args, int returnSlot, out JsValue result)
    {
        result = JsValue.Undefined;
        if (calleeValue.Tag == JsValueTag.Object &&
            _host.Heap.GetObject(calleeValue.AsObjectHandle()) is JsFunctionObject fn)
        {
            var layout = FrameLayout.For(fn.Function);
            if (layout.Eligible
#if !PUBLISH_AOT
                && !JitCompiler.PrefersCompiled(fn.Function)
#endif
                )
            {
                if (Interp2Options.Log) Interp2Stats.RecordCallInLoop();
                fn.Function.Invocations++;
                PushFrame(fn, layout, args, 0, args.Length, JsValue.Undefined, returnSlot);
                return true;
            }
        }

        result = _host.Interp2Call(calleeValue, new CallArgs(args), JsValue.Undefined);
        return false;
    }

    private void PushFrame(
        JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue, int returnSlot,
        JsValue newTarget = default)
    {
        // The entry path takes its arguments as a CallArgs because that is what
        // the old loop hands over. Copying them into the window through the
        // shared helper keeps one binding routine rather than two.
        var window = Reserve(layout);
        var stack = _stack;
        var context = Activate(callee, layout, thisValue, window, returnSlot, newTarget);
        var parameterIndex = layout.ParameterWindowIndex;
        var bind = layout.HasDuplicateParameterSlots
            ? parameterIndex.Length
            : Math.Min(args.Count, parameterIndex.Length);
        var restIndex = layout.Function.RestParameterIndex;
        if (restIndex >= 0)
        {
            bind = Math.Min(bind, restIndex);
        }

        for (var i = 0; i < bind; i++)
        {
            var value = i < args.Count ? args[i] : JsValue.Undefined;
            if (context is null || layout.BindingHomes[layout.ParameterSlots[i]] == SlotHome.Register)
            {
                stack[window + parameterIndex[i]] = value;
            }
            else
            {
                BytecodeInterpreter.Interp2DeclareContextSlot(context, layout.ParameterSlots[i], value);
            }
        }

        if (restIndex >= 0)
        {
            BindRestParameter(layout, context, window, args, restIndex);
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
        var restIndex = layout.Function.RestParameterIndex;
        if (restIndex >= 0)
        {
            bind = Math.Min(bind, restIndex);
        }

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
            var slotHomes = layout.BindingHomes;
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

        if (restIndex >= 0)
        {
            BindRestParameter(layout, context, window, CallArgs.FromRegisters(argSource, argStart, argCount), restIndex);
        }

        BindArgumentsObject(
            layout, callee, context, window, CallArgs.FromRegisters(argSource, argStart, argCount));
        BindSelfName(layout, callee, window);
    }

    /// <summary>
    /// A rest parameter collects every argument from its position on into a
    /// new array, and is bound even when that array is empty (ECMA-262 10.2.11
    /// FunctionDeclarationInstantiation, IteratorBindingInitialization of the
    /// formals). The formals before it are bound as ordinary parameters.
    /// </summary>
    private void BindRestParameter(
        FrameLayout layout, DeclarativeEnvironmentRecord? context, int window, in CallArgs args, int restIndex)
    {
        var rest = _host.CreateRestArray(args, restIndex);
        var slot = layout.ParameterSlots[restIndex];
        if (context is null || layout.BindingHomes[slot] == SlotHome.Register)
        {
            // Re-read: the allocation can collect, and the stack is the array
            // the collector traces, not a copy of it.
            _stack[window + layout.ParameterWindowIndex[restIndex]] = rest;
        }
        else
        {
            BytecodeInterpreter.Interp2DeclareContextSlot(context, slot, rest);
        }
    }

    /// <summary>
    /// ECMA-262 15.2.5: a named function expression can refer to itself by its
    /// own name, which is a variable of this frame like any other.
    /// </summary>
    private void BindSelfName(FrameLayout layout, JsFunctionObject callee, int window)
    {
        if (layout.SelfNameByName is { } byName && callee.SelfHandle is { } namedHandle)
        {
            BytecodeInterpreter.Interp2BindFunctionNameByName(
                _frames[_depth - 1].Context!, byName, JsValue.FromObject(namedHandle));
            return;
        }

        var slot = layout.SelfNameSlot;
        if (slot < 0 || callee.SelfHandle is not { } selfHandle)
        {
            return;
        }

        // A closure that reads the name finds it in the frame's record, as an
        // immutable binding: a sloppy assignment from the closure is dropped,
        // a strict one throws, as for the body's own assignments.
        if (layout.BindingHomes[slot] == SlotHome.Context)
        {
            BytecodeInterpreter.Interp2DeclareFunctionNameSlot(
                _frames[_depth - 1].Context!, slot, JsValue.FromObject(selfHandle));
            return;
        }

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
            if (layout.ArgumentsByName)
            {
                _host.Interp2BindArgumentsByName(
                    context!, _host.Interp2CreateArguments(args, layout.RestrictedArguments, callee));
            }

            return;
        }

        var argumentsObject = _host.Interp2CreateArguments(args, layout.RestrictedArguments, callee);
        if (context is not null && layout.BindingHomes[slot] == SlotHome.Context)
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
        JsFunctionObject callee, FrameLayout layout, JsValue thisValue, int window, int returnSlot,
        JsValue newTarget = default)
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
        frame.NewTarget = newTarget;
        frame.ThisUninitialized = layout.IsDerivedConstructor;
        frame.Base = window;
        frame.Ip = 0;
        frame.BackEdgeMark = _backEdges;
        frame.ReturnSlot = returnSlot;
        frame.HandlerBase = _handlerTop;
        frame.HandlerCount = 0;
        frame.PendingException = JsValue.Undefined;
        frame.HasPendingException = false;
        frame.PendingReturn = JsValue.Undefined;
        frame.HasPendingReturn = false;
        frame.Generator = null;
        frame.AsyncContext = null;
        frame.Scope = null;
        frame.ScopeDepth = 0;

        DeclarativeEnvironmentRecord? context = null;
        if (layout.HasContext)
        {
            // Hoisting: every captured variable exists, holding undefined, from
            // the moment the body starts - the same state the window clear gives
            // the ones that stayed in registers.
            context = _host.Interp2CreateContext(layout.Function, callee, thisValue, frame.OuterEnv, newTarget);
            var homes = layout.BindingHomes;
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
            var lexicalHomes = layout.BindingHomes;
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
    /// <summary>
    /// Whether <see cref="TryRouteThrow"/> would find somewhere to send a throw,
    /// without changing anything. It runs as an exception filter, so a throw no
    /// frame of this entry handles passes by without being caught and rethrown.
    /// </summary>
    /// <remarks>
    /// A rethrow from a catch block starts a fresh dispatch on top of the stack
    /// the first one has not yet released, so a stack overflow unwinding through
    /// a few hundred nested entries would overflow again on the way out if each
    /// one caught and rethrew it. Frames above an inner entry still sit on the
    /// frame stack while this runs (its finally has not reset the depth yet),
    /// but that entry's own filter has already found them without a handler.
    /// </remarks>
    // Back-edges feed the same tier-up evidence the old loop gathers: a body
    // that loops long enough is compiled (see JitCompiler.RequestLoopTierUp),
    // and later calls to it run compiled when it loops enough per call.
    //
    // The jump itself only bumps this instance counter. Writing the function's
    // own counter there cost b_call_direct 8%: keeping the function reference
    // live across the dispatch loop's hottest case is dearer than the add.
    // Each frame's share is credited when it returns, and the counter is wound
    // back so the caller is not credited with its callee's loops. A frame left
    // by a throw or a suspension leaves its count to the frame below.
    private int _backEdges;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void SettleBackEdges(ref Frame frame)
    {
        var taken = unchecked(_backEdges - frame.BackEdgeMark);
        _backEdges = frame.BackEdgeMark;
        if (taken <= 0)
        {
            return;
        }

        var function = frame.Layout.Function;
        var total = Math.Min((long)function.BackEdges + taken, int.MaxValue);
        function.BackEdges = (int)total;
#if !PUBLISH_AOT
        if (total >= JitCompiler.LoopTierUpBackEdges)
        {
            JitCompiler.RequestLoopTierUp(function);
        }
#endif
    }

    // A getter the read's accessor stub found: an ordinary call with no
    // arguments and the receiver as `this`, its result landing in the read's
    // destination. Kept out of the dispatch loop, whose hottest case this is.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool CallGetter(JsValue getter, int ip, int receiverSlot, int returnSlot)
    {
        _frames[_depth - 1].Ip = ip;
        return Call(getter, _stack[receiverSlot], 0, 0, returnSlot);
    }

    private bool HasHandlerAbove(int entryDepth)
    {
        for (var depth = _depth; depth > entryDepth; depth--)
        {
            ref var frame = ref _frames[depth - 1];
            if (frame.HandlerCount > 0)
            {
                var entry = frame.HandlerBase + (frame.HandlerCount - 1) * HandlerStride;
                if (_handlers[entry] >= 0 || _handlers[entry + 1] >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryRouteThrow(int entryDepth, JsValue value)
    {
        for (var depth = _depth; depth > entryDepth; depth--)
        {
            ref var frame = ref _frames[depth - 1];
            while (frame.HandlerCount > 0)
            {
                frame.HandlerCount--;
                var entry = frame.HandlerBase + frame.HandlerCount * HandlerStride;
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
                UnwindScopes(ref frame, _handlers[entry + 2]);

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

    // ------------------------------------------------ derived constructors

    /// <summary>
    /// ECMA-262 9.1.1.3.4 GetThisBinding in a derived constructor: a
    /// ReferenceError until super() has bound it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue DerivedThis()
    {
        ref var frame = ref _frames[_depth - 1];
        if (frame.Context is { } record)
        {
            return _host.Interp2ResolveThis(record, frame.This);
        }

        if (frame.ThisUninitialized)
        {
            _host.Interp2ThrowThisBeforeSuper();
        }

        return frame.This;
    }

    /// <summary>
    /// ECMA-262 13.3.7.1 SuperCall steps 7-8: bind the constructed object as
    /// `this`, in the constructor's record when it keeps one (an arrow inside
    /// may be what called super()), and on the frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BindThisAfterSuper(FrameLayout layout, JsValue value)
    {
        if (layout.ResolvesThisOutwards)
        {
            _host.BindThisFromSuper(OuterEnvironmentOf(_depth - 1), value);
            return;
        }

        ref var frame = ref _frames[_depth - 1];
        if (frame.Context is { } record)
        {
            _host.BindThisFromSuper(record, value);
        }
        else if (!frame.ThisUninitialized)
        {
            _host.Interp2ThrowSuperCalledTwice();
        }

        frame = ref _frames[_depth - 1];
        frame.This = value;
        frame.ThisUninitialized = false;
    }

    /// <summary>
    /// ECMA-262 10.2.2 [[Construct]] steps 10-12 for a derived constructor: an
    /// object it returns is the result; undefined falls back to `this`, which
    /// must have been bound; anything else is a TypeError.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue DerivedConstructorResult(JsValue returned)
    {
        if (_host.Interp2IsConstructorResult(returned))
        {
            return returned;
        }

        if (returned.Tag != JsValueTag.Undefined)
        {
            _host.Interp2ThrowDerivedReturn();
        }

        return DerivedThis();
    }

    // ---------------------------------------------------------- scoped blocks

    /// <summary>
    /// Where a block record of the frame at <paramref name="index"/> chains to:
    /// the frame's own shared record when it has one, the closure's environment
    /// otherwise.
    /// </summary>
    private EnvironmentRecord? BaseScopeOf(int index)
        => (EnvironmentRecord?)_frames[index].Context ?? OuterEnvironmentOf(index);

    /// <summary>
    /// The block record holding <paramref name="name"/> among those the running
    /// frame has pushed, innermost first, or null when none does. The walk
    /// stops at the frame's own scope: its variables are not records here.
    /// </summary>
    private EnvironmentRecord? FindBlockBinding(string? name)
    {
        if (name is null)
        {
            return null;
        }

        ref var frame = ref _frames[_depth - 1];
        var scope = frame.Scope;
        for (var remaining = frame.ScopeDepth; remaining > 0 && scope is not null; remaining--)
        {
            if (scope.HasBinding(name))
            {
                return scope;
            }

            scope = scope.OuterEnv;
        }

        return null;
    }

    /// <summary>Pops block records until the frame has <paramref name="depth"/> of them.</summary>
    private static void UnwindScopes(ref Frame frame, int depth)
    {
        while (frame.ScopeDepth > depth)
        {
            frame.Scope = frame.Scope?.OuterEnv;
            frame.ScopeDepth--;
        }

        if (frame.ScopeDepth == 0)
        {
            frame.Scope = null;
        }
    }

    /// <summary>Makes <paramref name="scope"/> the running frame's innermost record.</summary>
    private void PushScope(EnvironmentRecord scope)
    {
        ref var frame = ref _frames[_depth - 1];
        frame.Scope = scope;
        frame.ScopeDepth++;
    }

    // ------------------------------------------------------- dynamic scopes

    /// <summary>
    /// Where a lookup in a <see cref="FrameLayout.DynamicScope"/> body starts:
    /// the innermost record the frame has pushed, or its own.
    /// </summary>
    private EnvironmentRecord DynamicScopeOf(int index)
        => _frames[index].Scope ?? BaseScopeOf(index)!;

    /// <summary>
    /// Whether a slot of a dynamic-scope body can go straight to the frame's
    /// record: the body declares it, and no `with`, block or body record has
    /// been pushed in front of it. Eval code declares its vars into this same
    /// record, never in front of it, so it cannot shadow one either.
    /// </summary>
    private bool ReachesOwnSlot(FrameLayout layout, int slot)
        => _frames[_depth - 1].Scope is null && layout.ScopedFallback[slot] == SlotHome.Context;

    /// <summary>
    /// ECMA-262 13.3.6.1 step 6: a call the compiler flagged as a possible
    /// direct eval is one when the callee is %eval%, which then runs in this
    /// frame's innermost scope. False, having done nothing, when it is not.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryCallDirectEval(
        FrameLayout layout, int ip, JsValue callee, JsValue thisValue,
        JsValue[] argSource, int argStart, int argCount, int returnSlot)
    {
        if ((_frames[_depth - 1].Scope ?? BaseScopeOf(_depth - 1)) is not { } evalScope ||
            !_host.ArmDirectEval(callee, evalScope, layout.Function, ip))
        {
            return false;
        }

        try
        {
            // %eval% is native: this returns without pushing a frame.
            CallFrom(callee, thisValue, argSource, argStart, argCount, returnSlot);
        }
        finally
        {
            _host.DisarmDirectEval();
        }

        return true;
    }

    /// <summary>
    /// Annex B.3.3.1: a block function's value assigned to the var of the same
    /// name, in the body's variable environment rather than the block's record.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreVarTop(FrameLayout layout, int slot, int slotBase, JsValue value)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope)
        {
            _host.StoreInVariableEnvironment(DynamicScopeOf(_depth - 1), name, value);
            return;
        }

        // The layout has placed the var in this body (blocks as records).
        if (layout.BindingHomes[slot] == SlotHome.Register)
        {
            _stack[slotBase + slot] = value;
            return;
        }

        _host.Interp2StoreContext(_frames[_depth - 1].Context!, layout.Function, slot, value, name, strict: false);
    }

    /// <summary>
    /// ECMA-262 13.5.1.2 `delete identifier`. A binding the body declares is
    /// never deletable (10.2.11 creates them with CreateMutableBinding(n, false)),
    /// so only a name found in a record answers otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue DeleteName(FrameLayout layout, int slot)
    {
        var name = NameOfSlot(layout, slot);
        switch (HomeOfSlot(layout, slot))
        {
            case SlotHome.Register:
            case SlotHome.Context:
                return JsValue.FromBoolean(false);
            case SlotHome.Scoped:
                if (layout.DynamicScope)
                {
                    return _host.DeleteBindingByName(DynamicScopeOf(_depth - 1), name);
                }

                if (FindBlockBinding(name) is { } block)
                {
                    return _host.DeleteBindingByName(block, name);
                }

                if (layout.ScopedFallback[slot] != SlotHome.Free)
                {
                    return JsValue.FromBoolean(false);
                }

                goto default;
            default:
                return _host.DeleteBindingByName(OuterEnvironmentOf(_depth - 1), name);
        }
    }

    /// <summary>
    /// A callee named inside a `with`: its value, and the with object it
    /// resolved on (ECMA-262 13.3.6.2 step 1.b). Only a name looked up through
    /// the records can resolve on one; a slot the body keeps has no with in
    /// front of it, the layout having made every body with one dynamic.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue LoadWithBase(FrameLayout layout, int slot, int slotBase, int icOffset, out JsValue withBase)
    {
        withBase = JsValue.Undefined;
        var name = NameOfSlot(layout, slot);
        switch (HomeOfSlot(layout, slot))
        {
            case SlotHome.Register:
                if (layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
                {
                    _host.Interp2ThrowDeadZoneAccess(name);
                }

                return _stack[slotBase + slot];
            case SlotHome.Context:
                return _host.Interp2LoadContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, name, layout.IsStrict);
            case SlotHome.Scoped:
                if (layout.DynamicScope && !ReachesOwnSlot(layout, slot))
                {
                    return _host.Interp2LoadFreeWithBase(DynamicScopeOf(_depth - 1), name, layout.IsStrict, out withBase);
                }

                if (layout.DynamicScope || FindBlockBinding(name) is not null ||
                    layout.ScopedFallback[slot] != SlotHome.Free)
                {
                    return LoadScoped(layout, slot, slotBase, icOffset);
                }

                goto default;
            default:
                return _host.Interp2LoadFreeWithBase(OuterEnvironmentOf(_depth - 1), name, layout.IsStrict, out withBase);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue LoadScoped(FrameLayout layout, int slot, int slotBase, int icOffset)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope)
        {
            return ReachesOwnSlot(layout, slot)
                ? _host.Interp2LoadContext(_frames[_depth - 1].Context!, layout.Function, slot, name, layout.IsStrict)
                : _host.Interp2LoadFree(DynamicScopeOf(_depth - 1), name, layout.IsStrict);
        }

        if (FindBlockBinding(name) is { } block)
        {
            return _host.Interp2LoadFree(block, name, layout.IsStrict);
        }

        switch (layout.ScopedFallback[slot])
        {
            case SlotHome.Register:
                if (layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
                {
                    _host.Interp2ThrowDeadZoneAccess(name);
                }

                return _stack[slotBase + slot];
            case SlotHome.Context:
                return _host.Interp2LoadContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, name, layout.IsStrict);
            default:
                return _host.Interp2LoadFreeCached(layout, slot, icOffset, OuterEnvironmentOf(_depth - 1));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreScoped(FrameLayout layout, int slot, int slotBase, int icOffset, JsValue value, bool resolved)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope)
        {
            // The record answers for const, the dead zone and the immutable
            // self name, as it does for every other store through it.
            if (resolved)
            {
                _host.Interp2StoreResolvedFree(layout, slot, icOffset, DynamicScopeOf(_depth - 1), value);
            }
            else if (ReachesOwnSlot(layout, slot))
            {
                _host.Interp2StoreContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, value, name, layout.IsStrict);
            }
            else
            {
                _host.Interp2StoreFree(DynamicScopeOf(_depth - 1), name, value, layout.IsStrict);
            }

            return;
        }

        if (FindBlockBinding(name) is { } block)
        {
            // A block's binding is not deletable, so the one PreResolveVar
            // found is still this one.
            if (resolved)
            {
                _host.Interp2StoreResolvedFree(layout, slot, icOffset, block, value);
            }
            else
            {
                _host.Interp2StoreFree(block, name, value, layout.IsStrict);
            }

            return;
        }

        var fallback = layout.ScopedFallback[slot];
        if (fallback == SlotHome.Free)
        {
            if (resolved)
            {
                _host.Interp2StoreResolvedFree(layout, slot, icOffset, OuterEnvironmentOf(_depth - 1), value);
            }
            else
            {
                _host.Interp2StoreFreeCached(layout, slot, icOffset, OuterEnvironmentOf(_depth - 1), value);
            }

            return;
        }

        // The body's own variable: the same checks StoreVar makes.
        if (slot == layout.SelfNameSlot)
        {
            if (layout.IsStrict)
            {
                _host.Interp2ThrowSelfNameAssignment(name);
            }

            return;
        }

        if (fallback == SlotHome.Register && layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
        {
            _host.Interp2ThrowDeadZoneAccess(name);
        }

        if ((uint)slot < (uint)layout.SlotIsConst.Length && layout.SlotIsConst[slot])
        {
            _host.Interp2ThrowConstAssignment(name);
        }

        if (fallback == SlotHome.Register)
        {
            _stack[slotBase + slot] = value;
        }
        else
        {
            _host.Interp2StoreContext(
                _frames[_depth - 1].Context!, layout.Function, slot, value, name, layout.IsStrict);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitScoped(FrameLayout layout, int slot, int slotBase, int icOffset, JsValue value)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope && !ReachesOwnSlot(layout, slot))
        {
            _host.Interp2InitializeName(DynamicScopeOf(_depth - 1), name, value);
            return;
        }

        if (!layout.DynamicScope && FindBlockBinding(name) is { } block)
        {
            _host.Interp2InitializeBinding(block, name!, value);
            return;
        }

        var isLexical = layout.HasLexicalSlots &&
            (uint)slot < (uint)layout.SlotIsLexical.Length && layout.SlotIsLexical[slot];
        switch (layout.ScopedFallback[slot])
        {
            case SlotHome.Register:
                if (isLexical)
                {
                    _tdz[slotBase + slot] = 0;
                }

                _stack[slotBase + slot] = value;
                return;
            case SlotHome.Context when isLexical:
                BytecodeInterpreter.Interp2InitializeContextSlot(
                    _frames[_depth - 1].Context!, slot, value,
                    (uint)slot < (uint)layout.SlotIsConst.Length && layout.SlotIsConst[slot]);
                return;
            case SlotHome.Context:
                _host.Interp2StoreContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, value, name, layout.IsStrict);
                return;
            default:
                _host.Interp2InitializeName(OuterEnvironmentOf(_depth - 1), name, value);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue TypeOfScoped(FrameLayout layout, int slot, int slotBase)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope)
        {
            return JsValue.FromString(ReachesOwnSlot(layout, slot)
                ? _host.Interp2TypeOfValue(_host.Interp2LoadContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, name, layout.IsStrict))
                : _host.Interp2TypeOfFree(DynamicScopeOf(_depth - 1), name, layout.IsStrict));
        }

        if (FindBlockBinding(name) is { } block)
        {
            return JsValue.FromString(_host.Interp2TypeOfFree(block, name, layout.IsStrict));
        }

        switch (layout.ScopedFallback[slot])
        {
            case SlotHome.Register:
                if (layout.HasLexicalSlots && _tdz[slotBase + slot] != 0)
                {
                    _host.Interp2ThrowDeadZoneAccess(name);
                }

                return JsValue.FromString(_host.Interp2TypeOfValue(_stack[slotBase + slot]));
            case SlotHome.Context:
                return JsValue.FromString(_host.Interp2TypeOfValue(_host.Interp2LoadContext(
                    _frames[_depth - 1].Context!, layout.Function, slot, name, layout.IsStrict)));
            default:
                return JsValue.FromString(
                    _host.Interp2TypeOfFree(OuterEnvironmentOf(_depth - 1), name, layout.IsStrict));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PreResolveScoped(FrameLayout layout, int slot)
    {
        var name = NameOfSlot(layout, slot);
        if (layout.DynamicScope)
        {
            _host.Interp2PreResolveFree(DynamicScopeOf(_depth - 1), name);
            return;
        }

        if (FindBlockBinding(name) is { } block)
        {
            _host.Interp2PreResolveFree(block, name);
        }
        else if (layout.ScopedFallback[slot] == SlotHome.Free)
        {
            _host.Interp2PreResolveFreeCached(layout, slot, OuterEnvironmentOf(_depth - 1));
        }
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
            frame.Scope?.Trace(tracer);

            // An exception travelling through a finally block is held nowhere
            // else while the block runs.
            if (frame.HasPendingException && frame.PendingException.Tag == JsValueTag.Object)
            {
                tracer.TraceRoot("interp2.frame.pendingException", frame.PendingException.AsObjectHandle());
            }

            // Audit JSRT-004: while an async frame is active, its context must be
            // rooted even though no promise reaction holds it yet.
            if (frame.AsyncContext?.SelfHandle is { } asyncCtxHandle)
            {
                tracer.TraceRoot("interp2.frame.asyncContext", asyncCtxHandle);
            }
        }
    }
}
