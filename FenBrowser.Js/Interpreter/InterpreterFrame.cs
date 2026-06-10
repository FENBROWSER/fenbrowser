using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class InterpreterFrame
{
	public InterpreterFrame(
		BytecodeFunction function,
		JsValue thisValue,
		EnvironmentRecord? environment = null)
	{
		Function = function;
		ThisValue = thisValue;
		Registers = new JsValue[function.RegisterCount];
		CatchHandlers = new Stack<int>();
		FinallyHandlers = new Stack<int>();
		HandlerEnvironments = new Stack<EnvironmentRecord>();
		Registers[0] = JsValue.Undefined;

		Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
	}

	public BytecodeFunction Function { get; }
	public JsValue ThisValue { get; }

	public JsValue[] Registers { get; }

	public EnvironmentRecord Environment { get; set; }

	public Stack<int> CatchHandlers { get; }
	public Stack<int> FinallyHandlers { get; }

	// The lexical environment in effect at each enclosing PushHandler, so that an
	// exception unwinding to a catch/finally restores frame.Environment to the
	// try's level — discarding any block (let/const) or with environments pushed
	// inside the try body. ECMA-262 14.15 abrupt-completion environment cleanup.
	public Stack<EnvironmentRecord> HandlerEnvironments { get; }

	public JsValue? PendingException { get; set; }

	// A return completion travelling through finally blocks (generator .return()
	// injected at a yield, ECMA-262 27.5.3.3 GeneratorResumeAbrupt). Unlike
	// PendingException it is not observable by catch handlers; EndFinally either
	// forwards it to the next enclosing finally or completes the function with it.
	public JsValue? PendingReturn { get; set; }

	public int InstructionPointer { get; set; }

	public JsFunctionObject? CalleeFunctionObject { get; set; }

	public JsValue NewTarget { get; set; } = JsValue.Undefined;

	// Set by LoadSuperConstructor to the base-class constructor handle so the
	// immediately-following super(...) call can route a NATIVE base constructor
	// through [[Construct]] (with this frame's NewTarget) instead of [[Call]],
	// which native abstract bases like Iterator reject. Cleared once consumed.
	public ObjectHandle? SuperConstructorHandle { get; set; }

	public GeneratorObject? OwnerGenerator { get; set; }

	public AsyncContext? AsyncContext { get; set; }
}
