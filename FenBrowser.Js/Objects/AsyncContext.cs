using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 27.7 — AsyncContext.
public sealed class AsyncContext : JsObject
{
	public BytecodeFunction Function { get; }
	public JsValue[] Registers { get; }
	public EnvironmentRecord? Environment { get; set; }
	public EnvironmentRecord? OuterEnvironment { get; set; }
	public JsValue ThisValue { get; set; }
	public int InstructionPointer { get; set; }
	public int[] SavedCatchHandlers { get; set; } = Array.Empty<int>();
	public int[] SavedFinallyHandlers { get; set; } = Array.Empty<int>();
	public FenBrowser.Js.Environments.EnvironmentRecord[] SavedHandlerEnvironments { get; set; } = Array.Empty<FenBrowser.Js.Environments.EnvironmentRecord>();
	public JsValue? PendingException { get; set; }
	public int AwaitDestReg { get; set; } = -1;
	public JsValue SentValue { get; set; } = JsValue.Undefined;
	public bool IsRejectResume { get; set; }
	public bool IsSuspended { get; set; }

	public AsyncContext? Parent { get; set; }

	public ObjectHandle? CapabilityPromise { get; set; }
	public ObjectHandle? CapabilityResolve { get; set; }
	public ObjectHandle? CapabilityReject { get; set; }

	public AsyncContext(BytecodeFunction function, JsValue[] registers, EnvironmentRecord? environment)
	{
		Function = function;
		Registers = registers;
		Environment = environment;
		OuterEnvironment = environment;
	}

	public override void Trace(IHeapTracer tracer)
	{
		base.Trace(tracer);
		Environment?.Trace(tracer);
		Parent?.Trace(tracer);
		if (CapabilityPromise is { } p) tracer.Trace(p);
		if (CapabilityResolve is { } r) tracer.Trace(r);
		if (CapabilityReject is { } rj) tracer.Trace(rj);
	}
}
