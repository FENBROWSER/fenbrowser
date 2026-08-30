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

	// Heap handle of this context itself, assigned right after allocation.
	// Await-resume callbacks declare it in their capturedRoots so a suspended
	// context stays live exactly as long as some pending reaction can resume
	// it (audit JSRT-004/015) instead of relying on a permanent root pin.
	public ObjectHandle? SelfHandle { get; set; }

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

		TraceValues(tracer, Registers);
		TraceValue(tracer, ThisValue);
		TraceValue(tracer, SentValue);
		if (PendingException is { } pendingException)
			TraceValue(tracer, pendingException);

		if (tracer.TraceEnvironmentChains)
		{
			Environment?.Trace(tracer);
			if (!ReferenceEquals(OuterEnvironment, Environment))
				OuterEnvironment?.Trace(tracer);

			foreach (var handlerEnvironment in SavedHandlerEnvironments)
				handlerEnvironment?.Trace(tracer);
		}

		Parent?.Trace(tracer);
		if (CapabilityPromise is { } p) tracer.Trace(p);
		if (CapabilityResolve is { } r) tracer.Trace(r);
		if (CapabilityReject is { } rj) tracer.Trace(rj);
	}

	private static void TraceValues(IHeapTracer tracer, JsValue[] values)
	{
		for (var i = 0; i < values.Length; i++)
			TraceValue(tracer, values[i]);
	}

	private static void TraceValue(IHeapTracer tracer, JsValue value)
	{
		if (value.Tag == JsValueTag.Object)
			tracer.Trace(value.AsObjectHandle());
	}
}
