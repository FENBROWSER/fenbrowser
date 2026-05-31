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
		Registers[0] = JsValue.Undefined;

		Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
	}

	public BytecodeFunction Function { get; }
	public JsValue ThisValue { get; }

	public JsValue[] Registers { get; }

	public EnvironmentRecord Environment { get; set; }

	public Stack<int> CatchHandlers { get; }
	public Stack<int> FinallyHandlers { get; }

	public JsValue? PendingException { get; set; }

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
