using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class InterpreterFrame
{
	private Stack<int>? _catchHandlers;
	private Stack<int>? _finallyHandlers;
	private Stack<EnvironmentRecord>? _handlerEnvironments;

	public InterpreterFrame(
		BytecodeFunction function,
		JsValue thisValue,
		EnvironmentRecord? environment = null)
		: this(function, thisValue, environment, registers: null)
	{
	}

	// A register file may be supplied by the interpreter's pool. It must be
	// exactly RegisterCount long - generator suspend and resume copy by
	// Registers.Length against an array sized to the function - and it must
	// arrive cleared, because every slot is a GC root and a stale reference
	// would both resurrect a dead object and read as a live register.
	internal InterpreterFrame(
		BytecodeFunction function,
		JsValue thisValue,
		EnvironmentRecord? environment,
		JsValue[]? registers)
	{
		Function = function;
		ThisValue = thisValue;
		Registers = registers ?? new JsValue[function.RegisterCount];

		Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
	}

	public BytecodeFunction Function { get; private set; }
	public JsValue ThisValue { get; set; }

	public JsValue[] Registers { get; private set; }

	public EnvironmentRecord Environment { get; set; }

	// LoadVar is a quarter of everything a real page executes, and each one
	// re-derived the same three facts: that the frame's environment is
	// declarative, that it owns this function's slots, and where its arrays
	// live - a type check and two calls before any variable was read. The JIT
	// already hoists this per loop; the interpreter did it per instruction.
	//
	// The environment is captured alongside the arrays because it changes:
	// entering a block, a catch, or a `with` pushes a record that does not own
	// these slots. Comparing the captured record against the frame's current one
	// is a single reference test, and a mismatch simply falls through to the
	// general path, so correctness does not depend on the cache being fresh.
	internal DeclarativeEnvironmentRecord? SlotEnvironment;
	internal DeclarativeEnvironmentRecord.Binding[]? SlotBindings;
	internal bool[]? SlotPresence;

	public Stack<int> CatchHandlers => _catchHandlers ??= new Stack<int>();
	public Stack<int> FinallyHandlers => _finallyHandlers ??= new Stack<int>();

	// The lexical environment in effect at each enclosing PushHandler, so that an
	// exception unwinding to a catch/finally restores frame.Environment to the
	// try's level — discarding any block (let/const) or with environments pushed
	// inside the try body. ECMA-262 14.15 abrupt-completion environment cleanup.
	public Stack<EnvironmentRecord> HandlerEnvironments => _handlerEnvironments ??= new Stack<EnvironmentRecord>();

	public JsValue? PendingException { get; set; }

	/// <summary>
	/// Set when ThrowOrHandle moved this frame's instruction pointer to a
	/// handler instead of raising. The dispatch loop reads the pointer every
	/// step and needs no telling; compiled code has to be told, or it carries on
	/// with the instruction after the one that threw.
	/// </summary>
	public bool ThrowRoutedToHandler;

	// A return completion travelling through finally blocks (generator .return()
	// injected at a yield, ECMA-262 27.5.3.3 GeneratorResumeAbrupt). Unlike
	// PendingException it is not observable by catch handlers; EndFinally either
	// forwards it to the next enclosing finally or completes the function with it.
	public JsValue? PendingReturn { get; set; }

	public int InstructionPointer { get; set; }

	/// <summary>
	/// How many register-window frames were live when this frame was pushed.
	/// The two loops nest in either order, so diagnostics use it to tell
	/// whether this frame or a window pushed after it is the innermost one.
	/// </summary>
	internal int Interp2DepthAtEntry { get; set; }

	public JsFunctionObject? CalleeFunctionObject { get; set; }

	public JsValue NewTarget { get; set; } = JsValue.Undefined;

	internal void Reset(
		BytecodeFunction function,
		JsValue thisValue,
		EnvironmentRecord? environment,
		JsValue[] registers)
	{
		Function = function;
		ThisValue = thisValue;
		Registers = registers;
		Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
		SlotEnvironment = null;
		SlotBindings = null;
		SlotPresence = null;
		_catchHandlers?.Clear();
		_finallyHandlers?.Clear();
		_handlerEnvironments?.Clear();
		PendingException = null;
		PendingReturn = null;
		ThrowRoutedToHandler = false;
		InstructionPointer = 0;
		CalleeFunctionObject = null;
		NewTarget = JsValue.Undefined;
	}
}
