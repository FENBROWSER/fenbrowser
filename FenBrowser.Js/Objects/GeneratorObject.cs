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
	public JsValue? PendingException { get; set; }

	public ObjectHandle? YieldStarIterator { get; set; }
    public bool IsAsyncGenerator { get; set; }

	// The full argument list passed when the generator function was called, so the
	// body's `arguments` object reflects every argument — not just the named ones.
	public JsValue[] InitialArgs { get; set; } = System.Array.Empty<JsValue>();

	public GeneratorObject(BytecodeFunction function, JsValue[] registers, EnvironmentRecord? environment)
	{
		Function = function;
		Registers = registers;
		Environment = environment;
		OuterEnvironment = environment;
	}

	public override void Trace(IHeapTracer tracer)
	{
		base.Trace(tracer);
		if (YieldStarIterator is { } iter)
			tracer.Trace(iter);
		Environment?.Trace(tracer);
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
