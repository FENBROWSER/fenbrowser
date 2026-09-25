using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 27.5 — Generator Objects.
public sealed class GeneratorObject : JsObject
{
    public BytecodeFunction Function { get; }
    public int InstructionPointer { get; set; }
    public JsValue[] Registers { get; }
    public EnvironmentRecord? Environment { get; set; }
    public EnvironmentRecord? OuterEnvironment { get; set; }
    public JsValue ThisValue { get; set; }
    public GeneratorState State { get; set; } = GeneratorState.Suspended;
    public JsValue SentValue { get; set; } = JsValue.Undefined;
    public int YieldDestReg { get; set; } = -1;
    public GeneratorCompletionMode CompletionMode { get; set; } = GeneratorCompletionMode.Normal;

    public int[] SavedCatchHandlers { get; set; } = Array.Empty<int>();
    public int[] SavedFinallyHandlers { get; set; } = Array.Empty<int>();
    public EnvironmentRecord[] SavedHandlerEnvironments { get; set; } = Array.Empty<EnvironmentRecord>();
    public JsValue? PendingException { get; set; }
    public JsValue? PendingReturn { get; set; }

    /// <summary>
    /// Set when this body runs on the register-window loop, whose window - the
    /// bytecode registers and the body's variables in one span - is what
    /// <see cref="Registers"/> then holds, sized to match. The two loops lay a
    /// frame out differently, so a generator that starts on one always resumes
    /// on it.
    /// </summary>
    public bool RunsOnRegisterWindow { get; set; }

    /// <summary>
    /// The frame's open try entries at the suspension, as the (catch ip,
    /// finally ip) pairs the register-window loop keeps them in, outermost
    /// first. A generator can yield inside a try, and the handlers have to be
    /// there when it resumes.
    /// </summary>
    public int[] SavedWindowHandlers { get; set; } = Array.Empty<int>();

    /// <summary>
    /// The dead-zone byte of each window slot at the suspension, for a body
    /// that has lexical slots: a generator can yield while one of its own let
    /// or const bindings has not been initialized yet.
    /// </summary>
    public byte[] SavedDeadZone { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The block records a register-window frame had pushed at the suspension,
    /// innermost first, and how many: a body that keeps its blocks as records
    /// can yield inside one.
    /// </summary>
    public EnvironmentRecord? BlockScope { get; set; }

    public int BlockScopeDepth { get; set; }

    public ObjectHandle? YieldStarIterator { get; set; }
    public bool IsAsyncGenerator { get; set; }

    // The full argument list passed when the generator function was called, so the
    // body's `arguments` object reflects every argument — not just the named ones.
    public JsValue[] InitialArgs { get; set; } = Array.Empty<JsValue>();

    // ECMA-262 15.2.5: named function expression binding — the function object's own
    // handle, used to create an immutable binding in the body scope (generator variant).
    public ObjectHandle? SelfHandle { get; set; }

    public GeneratorObject(BytecodeFunction function, JsValue[] registers, EnvironmentRecord? environment)
    {
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Registers = registers ?? throw new ArgumentNullException(nameof(registers));
        Environment = environment;
        OuterEnvironment = environment;
        // Capturing a scope is what makes it outlive the call that built it, so
        // the frame's teardown must not reclaim its slot storage.
        FenBrowser.Js.Environments.EnvironmentRecord.MarkEscapedChain(environment);
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);

        // A suspended generator is effectively a heap-allocated interpreter frame.
        // Every object-valued JsValue saved in that frame must remain reachable while
        // the generator is suspended; otherwise a GC between yield and resume can
        // collect an object that exists only in a register/argument/pending completion
        // and the resumed generator observes a stale heap handle.
        TraceValues(tracer, Registers);
        TraceValues(tracer, InitialArgs);
        TraceValue(tracer, ThisValue);
        TraceValue(tracer, SentValue);

        if (PendingException is { } pendingException)
            TraceValue(tracer, pendingException);
        if (PendingReturn is { } pendingReturn)
            TraceValue(tracer, pendingReturn);

        if (YieldStarIterator is { } iter)
            tracer.Trace(iter);
        if (SelfHandle is { } self)
            tracer.Trace(self);

        if (tracer.TraceEnvironmentChains)
        {
            Environment?.Trace(tracer);
            if (!ReferenceEquals(OuterEnvironment, Environment))
                OuterEnvironment?.Trace(tracer);

            foreach (var handlerEnvironment in SavedHandlerEnvironments)
                handlerEnvironment?.Trace(tracer);

            BlockScope?.Trace(tracer);
        }
    }

    /// <summary>
    /// The arguments the generator function was called with, which the body is
    /// entered with so its `arguments` object is the call's own.
    /// </summary>
    /// <remarks>
    /// This used to fall back to reading the named parameters out of the
    /// registers when the list was empty, on the assumption that they sat at
    /// register 1 upwards. A call with no arguments then handed the body one
    /// undefined per declared parameter, so `arguments.length` was the
    /// parameter count rather than 0 - and the registers those values were
    /// seeded into were past the end of the array as soon as a generator
    /// declared enough of them, which was an outright crash.
    /// </remarks>
    public JsValue[] GetInitialParameters() => InitialArgs;

    private static void TraceValues(IHeapTracer tracer, IReadOnlyList<JsValue> values)
    {
        for (var i = 0; i < values.Count; i++)
            TraceValue(tracer, values[i]);
    }

    private static void TraceValue(IHeapTracer tracer, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
            tracer.Trace(value.AsObjectHandle());
    }
}

public enum GeneratorState
{
    Suspended,
    Executing,
    Completed
}

public enum GeneratorCompletionMode
{
    Normal,
    Return,
    Throw
}
