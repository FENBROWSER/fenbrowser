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

        Environment?.Trace(tracer);
        if (!ReferenceEquals(OuterEnvironment, Environment))
            OuterEnvironment?.Trace(tracer);

        foreach (var handlerEnvironment in SavedHandlerEnvironments)
            handlerEnvironment?.Trace(tracer);
    }

    public JsValue[] GetInitialParameters()
    {
        // Return the full argument list when available so the body's `arguments`
        // object is complete; fall back to the named parameters from registers.
        if (InitialArgs.Length > 0)
            return InitialArgs;

        var paramCount = Function.ParameterNames.Count;
        var result = new JsValue[paramCount];
        for (var i = 0; i < paramCount; i++)
            result[i] = Registers[i + 1];
        return result;
    }

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
