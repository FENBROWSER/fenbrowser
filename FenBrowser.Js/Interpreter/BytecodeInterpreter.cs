using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Js.Interpreter;

public sealed class BytecodeInterpreter
{
    private readonly JsHeap _heap;
    private ObjectHandle? _objectConstructorHandle;
    private ObjectHandle? _objectPrototypeHandle;
    private ObjectHandle? _arrayConstructorHandle;
    private ObjectHandle? _arrayPrototypeHandle;
    private ObjectHandle? _booleanConstructorHandle;
    private ObjectHandle? _booleanPrototypeHandle;
    private ObjectHandle? _numberConstructorHandle;
    private ObjectHandle? _numberPrototypeHandle;
    private ObjectHandle? _stringConstructorHandle;
    private ObjectHandle? _stringPrototypeHandle;
    private ObjectHandle? _errorConstructorHandle;
    private ObjectHandle? _errorPrototypeHandle;
    private ObjectHandle? _typeErrorConstructorHandle;
    private ObjectHandle? _typeErrorPrototypeHandle;
    private ObjectHandle? _rangeErrorConstructorHandle;
    private ObjectHandle? _rangeErrorPrototypeHandle;
    private ObjectHandle? _syntaxErrorConstructorHandle;
    private ObjectHandle? _syntaxErrorPrototypeHandle;
    private ObjectHandle? _functionConstructorHandle;
    private ObjectHandle? _functionPrototypeHandle;
    private ObjectHandle? _functionCallMethodHandle;
    private ObjectHandle? _evalFunctionHandle;
    private ObjectHandle? _parseIntHandle;
    private ObjectHandle? _parseFloatHandle;
    private ObjectHandle? _isNaNHandle;
    private ObjectHandle? _isFiniteHandle;

    // Shared Random for Math.random. Thread-safety: Math.random is single-threaded in
    // ECMA-262, and the runtime is single-threaded today; if we ever introduce SAB +
    // worker threads, swap to Random.Shared (which is per-thread internally).
    private readonly Random _random = new();
    private ObjectHandle? _globalObjectHandle;
    private ObjectHandle? _dateConstructorHandle;
    private ObjectHandle? _datePrototypeHandle;
    private ObjectHandle? _mathObjectHandle;
    private ObjectHandle? _regexpConstructorHandle;
    private ObjectHandle? _regexpPrototypeHandle;
    private ObjectHandle? _jsonObjectHandle;
    private ObjectHandle? _symbolConstructorHandle;
    private ObjectHandle? _setConstructorHandle;
    private ObjectHandle? _setPrototypeHandle;
    private ObjectHandle? _mapConstructorHandle;
    private ObjectHandle? _mapPrototypeHandle;
    private ObjectHandle? _weakMapConstructorHandle;
    private ObjectHandle? _weakMapPrototypeHandle;
    private ObjectHandle? _weakSetConstructorHandle;
    private ObjectHandle? _weakSetPrototypeHandle;
    private ObjectHandle? _reflectObjectHandle;

    // Well-known symbol ids cached at first Symbol-constructor materialisation. JS
    // code that reads Symbol.iterator twice must get === values; a single id per
    // well-known symbol guarantees that.
    private readonly Dictionary<string, long> _wellKnownSymbols = new(StringComparer.Ordinal);

    public BytecodeInterpreter(JsHeap? heap = null)
    {
        _heap = heap ?? new JsHeap();
    }

    [MayExecuteJs]
    public JsValue Execute(BytecodeFunction function)
    {
        return ExecuteInternal(function, Array.Empty<JsValue>(), null, JsValue.FromObject(EnsureGlobalObject()));
    }

    // Bounds JS recursion so a runaway tail-less recursive function surfaces as a
    // catchable JS RangeError instead of crashing the host with a native
    // StackOverflowException. Each ExecuteInternal call consumes one C# stack frame
    // plus the inner CallFunction/StoreCallResult chain - empirically ~6KB per JS
    // call - so the cap is set conservatively below the default 1MB thread stack.
    // Test262 has tests that legitimately recurse 50-80 times; the 80-frame cap
    // accommodates them while leaving headroom for the unwinding path itself
    // (which also consumes stack to run the per-frame `finally` blocks).
    private const int MaxCallDepth = 80;
    private int _callDepth;

    [MayExecuteJs]
    private JsValue ExecuteInternal(
        BytecodeFunction function,
        IReadOnlyList<JsValue> args,
        IReadOnlyDictionary<string, JsVariableCell>? capturedVariables,
        JsValue thisValue)
    {
        if (_callDepth >= MaxCallDepth)
        {
            throw new JsThrownException(CreateRangeError("Maximum call stack size exceeded."));
        }

        _callDepth++;
        try
        {
            return ExecuteInternalCore(function, args, capturedVariables, thisValue);
        }
        finally
        {
            _callDepth--;
        }
    }

    [MayExecuteJs]
    private JsValue ExecuteInternalCore(
        BytecodeFunction function,
        IReadOnlyList<JsValue> args,
        IReadOnlyDictionary<string, JsVariableCell>? capturedVariables,
        JsValue thisValue)
    {
        var frame = new InterpreterFrame(function, thisValue, capturedVariables);
        InitializeBuiltinGlobals(function, frame);
        if (capturedVariables is not null)
        {
            foreach (var kv in capturedVariables)
            {
                if (function.VariableSlots.TryGetValue(kv.Key, out var slot))
                {
                    frame.Variables.BindCell(slot, kv.Value);
                }
            }
        }

        for (var i = 0; i < function.ParameterNames.Count && i < args.Count; i++)
        {
            var paramName = function.ParameterNames[i];
            if (function.VariableSlots.TryGetValue(paramName, out var slot))
            {
                frame.Variables[slot] = args[i];
            }
        }

        if (function.HasOwnArgumentsObject &&
            !function.ParameterNames.Contains("arguments", StringComparer.Ordinal) &&
            function.VariableSlots.TryGetValue("arguments", out var argumentsSlot))
        {
            frame.Variables[argumentsSlot] = CreateArgumentsObject(args);
        }

        while (frame.InstructionPointer < function.Instructions.Count)
        {
            var ins = function.Instructions[frame.InstructionPointer++];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    frame.Registers[ins.A] = function.Constants[ins.B];
                    break;
                case OpCode.LoadVar:
                    frame.Registers[ins.A] = LoadName(frame, ins.B);
                    break;
                case OpCode.LoadThis:
                    frame.Registers[ins.A] = frame.ThisValue;
                    break;
                case OpCode.StoreVar:
                    StoreName(frame, ins.B, frame.Registers[ins.A]);
                    break;
                case OpCode.Move:
                    frame.Registers[ins.A] = frame.Registers[ins.B];
                    break;
                case OpCode.Jump:
                    frame.InstructionPointer = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (!IsTruthy(frame.Registers[ins.A]))
                    {
                        frame.InstructionPointer = ins.B;
                    }
                    break;
                case OpCode.PushHandler:
                    frame.ExceptionHandlers.Push(ins.A);
                    break;
                case OpCode.PopHandler:
                    if (frame.ExceptionHandlers.Count > 0)
                    {
                        _ = frame.ExceptionHandlers.Pop();
                    }

                    break;
                case OpCode.Throw:
                    ThrowOrHandle(frame, frame.Registers[ins.A]);
                    break;
                case OpCode.NewObject:
                {
                    var handle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.NewArray:
                {
                    var obj = CreateArrayObject(Array.Empty<JsValue>());
                    var handle = _heap.AllocateObject(obj, AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.SetPropByName:
                {
                    var ownerHandle = ResolveObjectHandle(frame.Registers[ins.A]);
                    var obj = _heap.GetObject(ownerHandle);
                    var prop = function.PropertyNames[ins.B];
                    var value = frame.Registers[ins.C];
                    try
                    {
                        _ = SetPropertyValue(ownerHandle, obj, prop, value, frame.Registers[ins.A]);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }

                    break;
                }
                case OpCode.GetPropByName:
                {
                    var receiver = frame.Registers[ins.B];
                    var prop = function.PropertyNames[ins.C];
                    try
                    {
                        frame.Registers[ins.A] = GetReceiverProperty(receiver, prop);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }

                    break;
                }
                case OpCode.DeletePropByName:
                {
                    var receiver = frame.Registers[ins.B];
                    if (receiver.Tag != JsValueTag.Object)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(true);
                        break;
                    }

                    var obj = ResolveObject(receiver);
                    var prop = function.PropertyNames[ins.C];
                    frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteProperty(prop));
                    break;
                }
                case OpCode.SetElem:
                {
                    var ownerHandle = ResolveObjectHandle(frame.Registers[ins.A]);
                    var obj = _heap.GetObject(ownerHandle);
                    var keyValue = frame.Registers[ins.B];
                    var value = frame.Registers[ins.C];

                    if (keyValue.Tag == JsValueTag.Symbol)
                    {
                        // Symbol-keyed [[Set]] - install on the parallel symbol table.
                        obj.DefineOwnSymbolProperty(keyValue.AsSymbolId(),
                            new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
                        if (value.Tag == JsValueTag.Object)
                        {
                            _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
                        }

                        break;
                    }

                    var key = ToPropertyKey(keyValue);
                    try
                    {
                        _ = SetPropertyValue(ownerHandle, obj, key, value, frame.Registers[ins.A]);
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                        break;
                    }

                    if (double.TryParse(key, out var numericIndex))
                    {
                        var nextLength = numericIndex + 1;
                        if (!obj.TryGetOwnProperty("length", out var lenDesc) || lenDesc.Value.AsNumber() < nextLength)
                        {
                            _ = obj.SetProperty("length", JsValue.FromNumber(nextLength));
                        }
                    }

                    break;
                }
                case OpCode.DeleteElem:
                {
                    var receiver = frame.Registers[ins.B];
                    if (receiver.Tag != JsValueTag.Object)
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(true);
                        break;
                    }

                    var obj = ResolveObject(receiver);
                    var key = ToPropertyKey(frame.Registers[ins.C]);
                    frame.Registers[ins.A] = JsValue.FromBoolean(obj.DeleteProperty(key));
                    break;
                }
                case OpCode.EnumerateKeys:
                {
                    frame.Registers[ins.A] = CreateForInIterator(frame.Registers[ins.B]);
                    break;
                }
                case OpCode.EnumerateValues:
                {
                    frame.Registers[ins.A] = CreateForOfIterator(frame.Registers[ins.B]);
                    break;
                }
                case OpCode.ForOfNext:
                {
                    var iter = ResolveObject(frame.Registers[ins.B]) as ForOfIteratorObject
                        ?? throw new InvalidOperationException("Invalid for-of iterator object.");
                    if (!iter.TryMoveNext(out var value))
                    {
                        frame.InstructionPointer = ins.C;
                        break;
                    }

                    frame.Registers[ins.A] = value;
                    break;
                }
                case OpCode.ForInNext:
                {
                    var iterator = ResolveObject(frame.Registers[ins.B]) as ForInIteratorObject
                        ?? throw new InvalidOperationException("Invalid for-in iterator object.");
                    if (!iterator.TryMoveNext(out var key))
                    {
                        frame.InstructionPointer = ins.C;
                        break;
                    }

                    frame.Registers[ins.A] = JsValue.FromString(key);
                    break;
                }
                case OpCode.GetElem:
                {
                    var receiver = frame.Registers[ins.B];
                    var keyValue = frame.Registers[ins.C];
                    try
                    {
                        frame.Registers[ins.A] = keyValue.Tag == JsValueTag.Symbol
                            ? GetReceiverSymbolProperty(receiver, keyValue.AsSymbolId())
                            : GetReceiverProperty(receiver, ToPropertyKey(keyValue));
                    }
                    catch (JsThrownException ex)
                    {
                        ThrowOrHandle(frame, ex.Value);
                    }

                    break;
                }
                case OpCode.CreateFunction:
                {
                    var nested = function.NestedFunctions[ins.B];
                    var captured = CaptureFrameVariables(frame);
                    frame.Registers[ins.A] = CreateFunctionObject(nested, captured);
                    break;
                }
                case OpCode.Call0:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>(), JsValue.Undefined);
                    break;
                }
                case OpCode.Call1:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.C] }, JsValue.Undefined);
                    break;
                }
                case OpCode.CallMethod0:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>(), frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallMethod1:
                {
                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.D] }, frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallMethodN:
                {
                    var callArgs = new JsValue[ins.E];
                    for (var i = 0; i < ins.E; i++)
                    {
                        callArgs[i] = frame.Registers[ins.D + i];
                    }

                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], callArgs, frame.Registers[ins.C]);
                    break;
                }
                case OpCode.CallN:
                {
                    var callArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        callArgs[i] = frame.Registers[ins.C + i];
                    }

                    StoreCallResult(frame, ins.A, frame.Registers[ins.B], callArgs, JsValue.Undefined);
                    break;
                }
                case OpCode.Construct0:
                {
                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], Array.Empty<JsValue>());
                    break;
                }
                case OpCode.Construct1:
                {
                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], new[] { frame.Registers[ins.C] });
                    break;
                }
                case OpCode.ConstructN:
                {
                    var ctorArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        ctorArgs[i] = frame.Registers[ins.C + i];
                    }

                    StoreConstructResult(frame, ins.A, frame.Registers[ins.B], ctorArgs);
                    break;
                }
                case OpCode.Not:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!IsTruthy(frame.Registers[ins.B]));
                    break;
                case OpCode.Pos:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]));
                    break;
                case OpCode.Neg:
                    frame.Registers[ins.A] = JsValue.FromNumber(-ToNumber(frame.Registers[ins.B]));
                    break;
                case OpCode.Void:
                    frame.Registers[ins.A] = JsValue.Undefined;
                    break;
                case OpCode.Delete:
                    frame.Registers[ins.A] = JsValue.FromBoolean(true);
                    break;
                case OpCode.TypeOf:
                    frame.Registers[ins.A] = JsValue.FromString(TypeOfValue(frame.Registers[ins.B]));
                    break;
                case OpCode.Add:
                    frame.Registers[ins.A] = Add(frame.Registers[ins.B], frame.Registers[ins.C]);
                    break;
                case OpCode.Sub:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]) - ToNumber(frame.Registers[ins.C]));
                    break;
                case OpCode.Mul:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]) * ToNumber(frame.Registers[ins.C]));
                    break;
                case OpCode.Mod:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]) % ToNumber(frame.Registers[ins.C]));
                    break;
                case OpCode.Div:
                    frame.Registers[ins.A] = JsValue.FromNumber(ToNumber(frame.Registers[ins.B]) / ToNumber(frame.Registers[ins.C]));
                    break;
                case OpCode.Eq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(AreEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Neq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!AreEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.StrictEq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(AreStrictlyEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.StrictNeq:
                    frame.Registers[ins.A] = JsValue.FromBoolean(!AreStrictlyEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.In:
                {
                    var key = ToPropertyKey(frame.Registers[ins.B]);
                    var rhs = frame.Registers[ins.C];
                    if (rhs.Tag != JsValueTag.Object)
                    {
                        ThrowTypeError(frame, "Right-hand side of 'in' must be an object.");
                        break;
                    }

                    var obj = ResolveObject(rhs);
                    var has = obj.TryGetProperty(key, h => _heap.GetObject(h), out _);
                    frame.Registers[ins.A] = JsValue.FromBoolean(has);
                    break;
                }
                case OpCode.InstanceOf:
                {
                    if (TryInstanceOf(frame, frame.Registers[ins.B], frame.Registers[ins.C], out var instanceOfResult))
                    {
                        frame.Registers[ins.A] = JsValue.FromBoolean(instanceOfResult);
                    }

                    break;
                }
                case OpCode.Lt:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsLessThan(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Gt:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsGreaterThan(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Le:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsLessThanOrEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.Ge:
                    frame.Registers[ins.A] = JsValue.FromBoolean(IsGreaterThanOrEqual(frame.Registers[ins.B], frame.Registers[ins.C]));
                    break;
                case OpCode.And:
                    frame.Registers[ins.A] = IsTruthy(frame.Registers[ins.B]) ? frame.Registers[ins.C] : frame.Registers[ins.B];
                    break;
                case OpCode.Or:
                    frame.Registers[ins.A] = IsTruthy(frame.Registers[ins.B]) ? frame.Registers[ins.B] : frame.Registers[ins.C];
                    break;
                case OpCode.Return:
                    return frame.Registers[ins.A];
                default:
                    throw new InvalidOperationException($"Unsupported opcode {ins.OpCode}.");
            }
        }

        return JsValue.Undefined;
    }

    private void InitializeBuiltinGlobals(BytecodeFunction function, InterpreterFrame frame)
    {
        if (function.VariableSlots.TryGetValue("Infinity", out var infinitySlot))
        {
            frame.Variables[infinitySlot] = JsValue.FromNumber(double.PositiveInfinity);
        }

        if (function.VariableSlots.TryGetValue("NaN", out var nanSlot))
        {
            frame.Variables[nanSlot] = JsValue.FromNumber(double.NaN);
        }

        if (function.VariableSlots.TryGetValue("undefined", out var undefinedSlot))
        {
            frame.Variables[undefinedSlot] = JsValue.Undefined;
        }

        // ECMA-262 19.1.2.13 globalThis. Resolves to the global object so script code
        // can reach the realm's global without depending on a host-specific name
        // (window, self, etc.). Writable+non-enumerable+configurable per spec.
        if (function.VariableSlots.TryGetValue("globalThis", out var globalThisSlot))
        {
            frame.Variables[globalThisSlot] = JsValue.FromObject(EnsureGlobalObject());
        }

        if (function.VariableSlots.TryGetValue("eval", out var evalSlot))
        {
            frame.Variables[evalSlot] = JsValue.FromObject(EnsureEvalFunction());
        }

        // ECMA-262 19.2.4 / 19.2.5 / 19.2.2 / 19.2.3 - the four numeric global
        // functions. Number.parseInt and Number.parseFloat (21.1.2.13 / 21.1.2.14)
        // are the SAME function objects, installed below in EnsureNumberConstructor.
        if (function.VariableSlots.TryGetValue("parseInt", out var parseIntSlot))
        {
            frame.Variables[parseIntSlot] = JsValue.FromObject(EnsureParseIntFunction());
        }

        if (function.VariableSlots.TryGetValue("parseFloat", out var parseFloatSlot))
        {
            frame.Variables[parseFloatSlot] = JsValue.FromObject(EnsureParseFloatFunction());
        }

        if (function.VariableSlots.TryGetValue("isNaN", out var isNanSlot))
        {
            frame.Variables[isNanSlot] = JsValue.FromObject(EnsureIsNaNFunction());
        }

        if (function.VariableSlots.TryGetValue("isFinite", out var isFiniteSlot))
        {
            frame.Variables[isFiniteSlot] = JsValue.FromObject(EnsureIsFiniteFunction());
        }

        if (function.VariableSlots.TryGetValue("Object", out var objectSlot))
        {
            frame.Variables[objectSlot] = JsValue.FromObject(EnsureObjectConstructor());
        }

        if (function.VariableSlots.TryGetValue("Array", out var arraySlot))
        {
            frame.Variables[arraySlot] = JsValue.FromObject(EnsureArrayConstructor());
        }

        if (function.VariableSlots.TryGetValue("Boolean", out var booleanSlot))
        {
            frame.Variables[booleanSlot] = JsValue.FromObject(EnsureBooleanConstructor());
        }

        if (function.VariableSlots.TryGetValue("Number", out var numberSlot))
        {
            frame.Variables[numberSlot] = JsValue.FromObject(EnsureNumberConstructor());
        }

        if (function.VariableSlots.TryGetValue("String", out var stringSlot))
        {
            frame.Variables[stringSlot] = JsValue.FromObject(EnsureStringConstructor());
        }

        if (function.VariableSlots.TryGetValue("Function", out var functionSlot))
        {
            frame.Variables[functionSlot] = JsValue.FromObject(EnsureFunctionConstructor());
        }

        if (function.VariableSlots.TryGetValue("Error", out var errorSlot))
        {
            frame.Variables[errorSlot] = JsValue.FromObject(EnsureErrorConstructor());
        }

        if (function.VariableSlots.TryGetValue("TypeError", out var typeErrorSlot))
        {
            frame.Variables[typeErrorSlot] = JsValue.FromObject(EnsureTypeErrorConstructor());
        }

        if (function.VariableSlots.TryGetValue("RangeError", out var rangeErrorSlot))
        {
            frame.Variables[rangeErrorSlot] = JsValue.FromObject(EnsureRangeErrorConstructor());
        }

        if (function.VariableSlots.TryGetValue("SyntaxError", out var syntaxErrorSlot))
        {
            frame.Variables[syntaxErrorSlot] = JsValue.FromObject(EnsureSyntaxErrorConstructor());
        }

        if (function.VariableSlots.TryGetValue("Date", out var dateSlot))
        {
            frame.Variables[dateSlot] = JsValue.FromObject(EnsureDateConstructor());
        }

        if (function.VariableSlots.TryGetValue("RegExp", out var regexpSlot))
        {
            frame.Variables[regexpSlot] = JsValue.FromObject(EnsureRegExpConstructor());
        }

        if (function.VariableSlots.TryGetValue("Math", out var mathSlot))
        {
            frame.Variables[mathSlot] = JsValue.FromObject(EnsureMathObject());
        }

        if (function.VariableSlots.TryGetValue("JSON", out var jsonSlot))
        {
            frame.Variables[jsonSlot] = JsValue.FromObject(EnsureJsonObject());
        }

        if (function.VariableSlots.TryGetValue("Symbol", out var symbolSlot))
        {
            frame.Variables[symbolSlot] = JsValue.FromObject(EnsureSymbolConstructor());
        }

        if (function.VariableSlots.TryGetValue("Set", out var setSlot))
        {
            frame.Variables[setSlot] = JsValue.FromObject(EnsureSetConstructor());
        }

        if (function.VariableSlots.TryGetValue("Map", out var mapSlot))
        {
            frame.Variables[mapSlot] = JsValue.FromObject(EnsureMapConstructor());
        }

        if (function.VariableSlots.TryGetValue("WeakMap", out var wmSlot))
        {
            frame.Variables[wmSlot] = JsValue.FromObject(EnsureWeakMapConstructor());
        }

        if (function.VariableSlots.TryGetValue("WeakSet", out var wsSlot))
        {
            frame.Variables[wsSlot] = JsValue.FromObject(EnsureWeakSetConstructor());
        }

        if (function.VariableSlots.TryGetValue("Reflect", out var reflectSlot))
        {
            frame.Variables[reflectSlot] = JsValue.FromObject(EnsureReflectObject());
        }
    }

    private JsObject CreateOrdinaryObject()
    {
        var obj = new JsObject();
        obj.SetPrototype(EnsureObjectPrototype());
        return obj;
    }

    private JsValue CreateArgumentsObject(IReadOnlyList<JsValue> args)
    {
        var obj = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());

        _ = obj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(args.Count),
                Writable: true,
                Enumerable: false,
                Configurable: true));

        for (var i = 0; i < args.Count; i++)
        {
            var descriptor = new JsPropertyDescriptor(
                args[i],
                Writable: true,
                Enumerable: true,
                Configurable: true);
            _ = obj.DefineOwnProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), descriptor);
            WriteDescriptorBarrier(handle, descriptor);
        }

        return JsValue.FromObject(handle);
    }

    private enum ArrayIteratorKind
    {
        Key,
        Value,
        Entry,
    }

    // ECMA-262 23.1.5 Array Iterator Objects. Allocates an object whose [[Prototype]]
    // is %ArrayIteratorPrototype%; .next() returns {value, done} reading the source
    // array on each call so length changes during iteration are observed (the spec
    // does NOT pre-materialise).
    private JsValue CreateArrayIterator(JsValue source, ArrayIteratorKind kind)
    {
        if (source.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype iterator method called on non-object receiver."));
        }

        var iter = new ArrayIteratorObject(source.AsObjectHandle(), kind);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        var handle = _heap.AllocateObject(iter, AllocationSite.Current());
        _heap.WriteBarrier(handle, source.AsObjectHandle());
        return JsValue.FromObject(handle);
    }

    private ObjectHandle? _arrayIteratorPrototypeHandle;

    private ObjectHandle EnsureArrayIteratorPrototype()
    {
        if (_arrayIteratorPrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        // ECMA-262 23.1.5.2.1 %ArrayIteratorPrototype%.next.
        // Doubles as the .next for SnapshotIteratorObject (used by Set/Map iterators):
        // both expose an index + value sequence and the only difference is how
        // values are materialised.
        var next = new NativeFunctionObject("next", (thisValue, _) =>
        {
            if (thisValue.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Iterator.prototype.next called on non-object."));
            }

            var target = _heap.GetObject(thisValue.AsObjectHandle());
            if (target is SnapshotIteratorObject snap)
            {
                if (snap.Index >= snap.Values.Count)
                {
                    return BuildIteratorResult(JsValue.Undefined, done: true);
                }

                return BuildIteratorResult(snap.Values[snap.Index++], done: false);
            }

            if (target is not ArrayIteratorObject iter)
            {
                throw new JsThrownException(CreateTypeError(
                    "Iterator.prototype.next called on incompatible receiver."));
            }

            var sourceObj = _heap.GetObject(iter.SourceHandle);
            var length = GetArrayLength(sourceObj);
            if (iter.Index >= length)
            {
                return BuildIteratorResult(JsValue.Undefined, done: true);
            }

            var idx = iter.Index++;
            var key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
            JsValue value;
            switch (iter.Kind)
            {
                case ArrayIteratorKind.Key:
                    value = JsValue.FromNumber(idx);
                    break;
                case ArrayIteratorKind.Value:
                    TryGetPropertyValue(sourceObj, JsValue.FromObject(iter.SourceHandle), key, out value);
                    break;
                default:
                {
                    TryGetPropertyValue(sourceObj, JsValue.FromObject(iter.SourceHandle), key, out var v);
                    var pair = CreateArrayFromElements(new[] { JsValue.FromNumber(idx), v });
                    var pairHandle = _heap.AllocateObject(pair, AllocationSite.Current());
                    value = JsValue.FromObject(pairHandle);
                    break;
                }
            }

            return BuildIteratorResult(value, done: false);
        }, length: 0);

        var nextHandle = _heap.AllocateObject(next, AllocationSite.Current());
        proto.DefineOwnProperty("next", new JsPropertyDescriptor(
            JsValue.FromObject(nextHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, nextHandle);

        // ECMA-262 27.1.2.1 - every iterator's @@iterator returns the iterator
        // itself, so for-of on the iterator just keeps stepping through it.
        var selfIter = new NativeFunctionObject("[Symbol.iterator]", (thisValue, _) => thisValue, length: 0);
        var selfIterHandle = _heap.AllocateObject(selfIter, AllocationSite.Current());
        proto.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(selfIterHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, selfIterHandle);

        _arrayIteratorPrototypeHandle = protoHandle;
        return protoHandle;
    }

    // Build the IteratorResult shape { value, done } the spec mandates for every
    // iterator's .next() return value.
    private JsValue BuildIteratorResult(JsValue value, bool done)
    {
        var result = CreateOrdinaryObject();
        result.SetProperty("value", value);
        result.SetProperty("done", JsValue.FromBoolean(done));
        var handle = _heap.AllocateObject(result, AllocationSite.Current());
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(handle, value.AsObjectHandle());
        }

        return JsValue.FromObject(handle);
    }

    private sealed class ArrayIteratorObject : JsObject
    {
        public ArrayIteratorObject(ObjectHandle sourceHandle, ArrayIteratorKind kind)
        {
            SourceHandle = sourceHandle;
            Kind = kind;
        }

        public ObjectHandle SourceHandle { get; }
        public ArrayIteratorKind Kind { get; }
        public int Index { get; set; }
    }

    // Build a for-of iteration state. Tries the spec @@iterator dispatch first:
    // if the source object exposes a callable Symbol.iterator, call it and drive
    // the returned iterator via .next() until done. Strings, Arrays, and array-
    // likes fall through to fast in-place iteration over their indexed slots.
    private JsValue CreateForOfIterator(JsValue source)
    {
        var values = new List<JsValue>();

        if (source.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(source.AsObjectHandle());
            // Spec @@iterator dispatch (7.4.2 GetIterator + 7.4.4 IteratorStep).
            var iterId = GetWellKnownSymbolId("iterator");
            if (iterId != 0 &&
                obj.TryGetSymbolProperty(iterId, h => _heap.GetObject(h), out var iterDesc) &&
                iterDesc.Value.Tag == JsValueTag.Object)
            {
                var iter = CallFunction(iterDesc.Value, Array.Empty<JsValue>(), source);
                DrainIteratorIntoList(iter, values);
                var producer = new ForOfIteratorObject(values);
                return JsValue.FromObject(_heap.AllocateObject(producer, AllocationSite.Current()));
            }

            if (obj is ArrayObject)
            {
                var length = GetArrayLength(obj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    values.Add(TryGetPropertyValue(obj, source, key, out var v) ? v : JsValue.Undefined);
                }
            }
            else if (obj.TryGetOwnProperty("length", out var lenDesc) &&
                     (lenDesc.Value.Tag == JsValueTag.Number || lenDesc.Value.Tag == JsValueTag.Int32))
            {
                // Array-like fallback (arguments, NodeList-shape).
                var length = (int)lenDesc.Value.AsNumber();
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    values.Add(TryGetPropertyValue(obj, source, key, out var v) ? v : JsValue.Undefined);
                }
            }
            else
            {
                throw new JsThrownException(CreateTypeError(
                    "Value is not iterable (no @@iterator and not array-like)."));
            }
        }
        else if (source.Tag == JsValueTag.String)
        {
            var s = source.AsString();
            for (var i = 0; i < s.Length; i++)
            {
                values.Add(JsValue.FromString(s[i].ToString()));
            }
        }
        else if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Cannot iterate over " + (source.Tag == JsValueTag.Null ? "null" : "undefined") + "."));
        }
        else
        {
            throw new JsThrownException(CreateTypeError("Value is not iterable."));
        }

        var fallbackIter = new ForOfIteratorObject(values);
        return JsValue.FromObject(_heap.AllocateObject(fallbackIter, AllocationSite.Current()));
    }

    // Drains a user iterator into a value list by calling .next() repeatedly until
    // the returned IteratorResult.done is true. Bounded by MaxCallDepth-friendly
    // semantics: each .next() goes through CallFunction so the recursion guard
    // applies.
    private void DrainIteratorIntoList(JsValue iter, List<JsValue> sink)
    {
        if (iter.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Iterator @@iterator did not return an object."));
        }

        var iterObj = _heap.GetObject(iter.AsObjectHandle());
        while (true)
        {
            if (!TryGetPropertyValue(iterObj, iter, "next", out var nextFn) ||
                nextFn.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator result missing callable 'next'."));
            }

            var result = CallFunction(nextFn, Array.Empty<JsValue>(), iter);
            if (result.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Iterator result is not an object."));
            }

            var resultObj = _heap.GetObject(result.AsObjectHandle());
            TryGetPropertyValue(resultObj, result, "done", out var doneVal);
            if (IsTruthy(doneVal))
            {
                return;
            }

            TryGetPropertyValue(resultObj, result, "value", out var value);
            sink.Add(value);
        }
    }

    private JsValue CreateForInIterator(JsValue value)
    {
        var keys = new List<string>();
        if (value.Tag == JsValueTag.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            CollectEnumerableKeys(_heap.GetObject(value.AsObjectHandle()), keys, seen);
        }

        var iterator = new ForInIteratorObject(keys);
        return JsValue.FromObject(_heap.AllocateObject(iterator, AllocationSite.Current()));
    }

    private void CollectEnumerableKeys(JsObject obj, List<string> keys, HashSet<string> seen)
    {
        foreach (var property in obj.EnumerateOwnProperties())
        {
            if (seen.Add(property.Key) && property.Value.Enumerable)
            {
                keys.Add(property.Key);
            }
        }

        if (obj.PrototypeHandle is { } prototype)
        {
            CollectEnumerableKeys(_heap.GetObject(prototype), keys, seen);
        }
    }

    // ECMA-262 20.4 Symbol. Minimal surface: callable as Symbol([description])
    // returning a fresh Symbol primitive; well-known symbols (Symbol.iterator etc.)
    // installed as own properties. Symbol() is NOT constructable (the spec
    // requires `new Symbol()` to throw TypeError).
    private ObjectHandle EnsureSymbolConstructor()
    {
        if (_symbolConstructorHandle is { } existing)
        {
            return existing;
        }

        var constructor = new NativeFunctionObject(
            "Symbol",
            (_, args) =>
            {
                var desc = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
                    ? ToStringValue(args[0])
                    : null;
                return JsValue.FromSymbol(desc);
            },
            _ => throw new JsThrownException(CreateTypeError("Symbol is not a constructor.")),
            length: 0);

        var handle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(handle);

        // ECMA-262 20.4.2 - well-known symbols are own properties of %Symbol%.
        // Each is allocated once and cached so reads return the same identity.
        InstallWellKnownSymbol(constructor, "iterator");
        InstallWellKnownSymbol(constructor, "asyncIterator");
        InstallWellKnownSymbol(constructor, "hasInstance");
        InstallWellKnownSymbol(constructor, "isConcatSpreadable");
        InstallWellKnownSymbol(constructor, "match");
        InstallWellKnownSymbol(constructor, "matchAll");
        InstallWellKnownSymbol(constructor, "replace");
        InstallWellKnownSymbol(constructor, "search");
        InstallWellKnownSymbol(constructor, "species");
        InstallWellKnownSymbol(constructor, "split");
        InstallWellKnownSymbol(constructor, "toPrimitive");
        InstallWellKnownSymbol(constructor, "toStringTag");
        InstallWellKnownSymbol(constructor, "unscopables");

        _symbolConstructorHandle = handle;
        return handle;
    }

    private void InstallWellKnownSymbol(NativeFunctionObject constructor, string name)
    {
        var symbol = JsValue.FromSymbol("Symbol." + name);
        _wellKnownSymbols[name] = symbol.AsSymbolId();
        constructor.DefineOwnProperty(name, new JsPropertyDescriptor(
            symbol, Writable: false, Enumerable: false, Configurable: false));
    }

    // Returns the cached id for a well-known symbol, materialising the Symbol
    // constructor first if no script has touched it yet. Native code can use this
    // to recognise "is this value Symbol.iterator?" without going through a
    // user-observable property lookup.
    public long GetWellKnownSymbolId(string name)
    {
        if (_wellKnownSymbols.Count == 0)
        {
            _ = EnsureSymbolConstructor();
        }

        return _wellKnownSymbols.TryGetValue(name, out var id) ? id : 0;
    }

    // ECMA-262 24.2 Set. Backed by a List<JsValue> per instance for SameValueZero
    // equality (which is what Set keys use). Linear scan suffices for the test262
    // sizes; a hash-backed variant lands when Set perf becomes load-bearing.
    private ObjectHandle EnsureSetConstructor()
    {
        if (_setConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Set",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor Set requires 'new'.")),
            args =>
            {
                var set = new SetObject();
                set.SetPrototype(EnsureSetPrototype());
                var handle = _heap.AllocateObject(set, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            TryGetPropertyValue(src, args[0], key, out var v);
                            set.Add(v);
                            if (v.Tag == JsValueTag.Object)
                            {
                                _heap.WriteBarrier(handle, v.AsObjectHandle());
                            }
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // ECMA-262 24.2.3.1 add (returns the set for chaining).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "add", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            set.Add(value);
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            }

            return thisValue;
        }, length: 1);

        // 24.2.3.4 has.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(set.Has(value));
        }, length: 1);

        // 24.2.3.3 delete - returns whether the entry was present.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(set.Remove(value));
        }, length: 1);

        // 24.2.3.2 clear.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "clear", (thisValue, _) =>
        {
            RequireSet(thisValue).Clear();
            return JsValue.Undefined;
        }, length: 0);

        // ECMA-262 24.2.3.10 / .8 / .11 Set.prototype.values / keys / entries plus
        // [Symbol.iterator]. values and keys are the SAME function; entries yields
        // [v, v] pairs per spec.
        var setValuesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, _) => CreateSetIterator(t, isEntries: false));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, _) => CreateSetIterator(t, isEntries: false));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, _) => CreateSetIterator(t, isEntries: true));
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(setValuesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, setValuesHandle);

        // 24.2.3.6 forEach(callback[, thisArg]).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", (thisValue, args) =>
        {
            var set = RequireSet(thisValue);
            var cb = args.Count > 0 ? args[0] : JsValue.Undefined;
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (cb.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Set.prototype.forEach callback is not a function."));
            }

            foreach (var entry in set.Snapshot())
            {
                CallFunction(cb, new[] { entry, entry, thisValue }, thisArg);
            }

            return JsValue.Undefined;
        }, length: 1);

        // 24.2.3.9 size getter installed as a data property for simplicity; the
        // spec's getter/setter machinery covers it as an accessor on
        // Set.prototype but a writable=false data form is observationally close for
        // most tests until accessors-on-prototype is wired.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "size_getter_internal_unused", (_, _) => JsValue.Undefined, length: 0);
        // Instead, install a 'size' accessor that reads the live count from the set.
        var sizeGetter = new NativeFunctionObject("get size", (thisValue, _) =>
            JsValue.FromNumber(RequireSet(thisValue).Count), length: 0);
        var sizeGetterHandle = _heap.AllocateObject(sizeGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("size", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(sizeGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, sizeGetterHandle);

        _setPrototypeHandle = prototypeHandle;
        _setConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureSetPrototype()
    {
        _ = EnsureSetConstructor();
        return _setPrototypeHandle!.Value;
    }

    // ECMA-262 24.1 Map. Same shape as Set with an explicit key + value pair per
    // entry; SameValueZero is the key-identity rule (NaN-key matches NaN, +0/-0
    // collapse).
    private ObjectHandle EnsureMapConstructor()
    {
        if (_mapConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Map",
            (_, _) => throw new JsThrownException(CreateTypeError(
                "Constructor Map requires 'new'.")),
            args =>
            {
                var map = new MapObject();
                map.SetPrototype(EnsureMapPrototype());
                var handle = _heap.AllocateObject(map, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!TryGetPropertyValue(src, args[0], key, out var entry) ||
                                entry.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError(
                                    "Iterator value " + i + " is not an entry object."));
                            }

                            var entryObj = _heap.GetObject(entry.AsObjectHandle());
                            TryGetPropertyValue(entryObj, entry, "0", out var k);
                            TryGetPropertyValue(entryObj, entry, "1", out var v);
                            map.Set(k, v);
                            if (k.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, k.AsObjectHandle());
                            if (v.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, v.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 24.1.3.9 set (returns the map for chaining).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "set", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            var value = args.Count > 1 ? args[1] : JsValue.Undefined;
            map.Set(key, value);
            if (key.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), key.AsObjectHandle());
            if (value.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            return thisValue;
        }, length: 2);

        // 24.1.3.6 get.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "get", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return map.TryGet(key, out var v) ? v : JsValue.Undefined;
        }, length: 1);

        // 24.1.3.7 has.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(map.Has(key));
        }, length: 1);

        // 24.1.3.3 delete.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            return JsValue.FromBoolean(map.Remove(key));
        }, length: 1);

        // 24.1.3.1 clear.
        DefineNativePrototypeMethod(prototypeHandle, prototype, "clear", (thisValue, _) =>
        {
            RequireMap(thisValue).Clear();
            return JsValue.Undefined;
        }, length: 0);

        // ECMA-262 24.1.3.11 / .8 / .4 Map.prototype.values / keys / entries plus
        // [Symbol.iterator] (the same function as entries per spec).
        var mapEntriesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Entry));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Key));
        DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, _) => CreateMapIterator(t, MapIteratorKind.Value));
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(mapEntriesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, mapEntriesHandle);

        // 24.1.3.5 forEach(callback, thisArg) - callback(value, key, map).
        DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", (thisValue, args) =>
        {
            var map = RequireMap(thisValue);
            var cb = args.Count > 0 ? args[0] : JsValue.Undefined;
            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (cb.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Map.prototype.forEach callback is not a function."));
            }

            foreach (var (k, v) in map.Snapshot())
            {
                CallFunction(cb, new[] { v, k, thisValue }, thisArg);
            }

            return JsValue.Undefined;
        }, length: 1);

        // 24.1.3.10 size accessor.
        var sizeGetter = new NativeFunctionObject("get size", (thisValue, _) =>
            JsValue.FromNumber(RequireMap(thisValue).Count), length: 0);
        var sizeGetterHandle = _heap.AllocateObject(sizeGetter, AllocationSite.Current());
        prototype.DefineOwnProperty("size", JsPropertyDescriptor.Accessor(
            JsValue.FromObject(sizeGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, sizeGetterHandle);

        _mapPrototypeHandle = prototypeHandle;
        _mapConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureMapPrototype()
    {
        _ = EnsureMapConstructor();
        return _mapPrototypeHandle!.Value;
    }

    private MapObject RequireMap(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not MapObject map)
        {
            throw new JsThrownException(CreateTypeError("Map method called on incompatible receiver."));
        }

        return map;
    }

    private sealed class MapObject : JsObject
    {
        private readonly List<(JsValue Key, JsValue Value)> _entries = new();

        public int Count => _entries.Count;

        public void Set(JsValue key, JsValue value)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i].Key, key))
                {
                    _entries[i] = (_entries[i].Key, value);
                    return;
                }
            }

            _entries.Add((key, value));
        }

        public bool TryGet(JsValue key, out JsValue value)
        {
            foreach (var entry in _entries)
            {
                if (SameValueZero(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = JsValue.Undefined;
            return false;
        }

        public bool Has(JsValue key)
        {
            foreach (var entry in _entries)
            {
                if (SameValueZero(entry.Key, key))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Remove(JsValue key)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i].Key, key))
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void Clear() => _entries.Clear();

        public IReadOnlyList<(JsValue, JsValue)> Snapshot() => _entries.ToArray();
    }

    private SetObject RequireSet(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not SetObject set)
        {
            throw new JsThrownException(CreateTypeError("Set method called on incompatible receiver."));
        }

        return set;
    }

    // Set / Map iterator wrappers reuse %ArrayIteratorPrototype% since the only
    // shape it exposes is .next() returning {value, done}; .next reads the source
    // collection's snapshot at iterator-creation time. A fresh snapshot per
    // iterator avoids "mutation during iteration" edge cases the spec actually
    // requires to surface (a future commit can switch to a live cursor).
    private JsValue CreateSetIterator(JsValue receiver, bool isEntries)
    {
        var set = RequireSet(receiver);
        var snap = set.Snapshot();
        var values = new List<JsValue>(snap.Count);
        for (var i = 0; i < snap.Count; i++)
        {
            if (isEntries)
            {
                var pair = CreateArrayFromElements(new[] { snap[i], snap[i] });
                values.Add(JsValue.FromObject(_heap.AllocateObject(pair, AllocationSite.Current())));
            }
            else
            {
                values.Add(snap[i]);
            }
        }

        var iter = new SnapshotIteratorObject(values);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    private enum MapIteratorKind { Key, Value, Entry }

    private JsValue CreateMapIterator(JsValue receiver, MapIteratorKind kind)
    {
        var map = RequireMap(receiver);
        var snap = map.Snapshot();
        var values = new List<JsValue>(snap.Count);
        for (var i = 0; i < snap.Count; i++)
        {
            switch (kind)
            {
                case MapIteratorKind.Key: values.Add(snap[i].Item1); break;
                case MapIteratorKind.Value: values.Add(snap[i].Item2); break;
                default:
                {
                    var pair = CreateArrayFromElements(new[] { snap[i].Item1, snap[i].Item2 });
                    values.Add(JsValue.FromObject(_heap.AllocateObject(pair, AllocationSite.Current())));
                    break;
                }
            }
        }

        var iter = new SnapshotIteratorObject(values);
        iter.SetPrototype(EnsureArrayIteratorPrototype());
        return JsValue.FromObject(_heap.AllocateObject(iter, AllocationSite.Current()));
    }

    // Shared iterator whose next() reads from a pre-materialised value list. The
    // %ArrayIteratorPrototype% next handler is keyed on ArrayIteratorObject, so
    // for Snapshot iterators we install a tiny shim via DefineOwnProperty('next').
    private sealed class SnapshotIteratorObject : JsObject
    {
        public IReadOnlyList<JsValue> Values { get; }
        public int Index { get; set; }
        public SnapshotIteratorObject(IReadOnlyList<JsValue> values) => Values = values;
    }

    private sealed class SetObject : JsObject
    {
        // Spec uses SameValueZero for entry identity. A List with a manual
        // SameValueZero comparison keeps both spec correctness and trivial GC
        // traceability (entries are reachable via the list).
        private readonly List<JsValue> _entries = new();

        public int Count => _entries.Count;

        public void Add(JsValue value)
        {
            if (!Has(value))
            {
                _entries.Add(value);
            }
        }

        public bool Has(JsValue value)
        {
            foreach (var e in _entries)
            {
                if (SameValueZero(e, value))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Remove(JsValue value)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (SameValueZero(_entries[i], value))
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void Clear() => _entries.Clear();

        public IReadOnlyList<JsValue> Snapshot() => _entries.ToArray();
    }

    // ECMA-262 24.3 WeakMap and 24.4 WeakSet. Spec requires keys to be Objects
    // (or non-registered Symbols in newer drafts). Internally we use an
    // ObjectHandle-keyed dictionary so identity matches the spec's "same object".
    // True weak references would require runtime GC-aware semantics; this is a
    // strong-reference shim that satisfies API conformance.
    private ObjectHandle EnsureWeakMapConstructor()
    {
        if (_weakMapConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "WeakMap",
            (_, _) => throw new JsThrownException(CreateTypeError("Constructor WeakMap requires 'new'.")),
            args =>
            {
                var wm = new WeakMapObject();
                wm.SetPrototype(EnsureWeakMapPrototype());
                var handle = _heap.AllocateObject(wm, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!TryGetPropertyValue(src, args[0], k, out var entry) ||
                                entry.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakMap entry is not an object."));
                            }

                            var entryObj = _heap.GetObject(entry.AsObjectHandle());
                            TryGetPropertyValue(entryObj, entry, "0", out var key);
                            TryGetPropertyValue(entryObj, entry, "1", out var val);
                            if (key.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakMap key must be an object."));
                            }

                            wm.Set(key.AsObjectHandle(), val);
                            _heap.WriteBarrier(handle, key.AsObjectHandle());
                            if (val.Tag == JsValueTag.Object) _heap.WriteBarrier(handle, val.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "set", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            var val = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Invalid value used as weak map key."));
            }

            wm.Set(key.AsObjectHandle(), val);
            _heap.WriteBarrier(thisValue.AsObjectHandle(), key.AsObjectHandle());
            if (val.Tag == JsValueTag.Object) _heap.WriteBarrier(thisValue.AsObjectHandle(), val.AsObjectHandle());
            return thisValue;
        }, length: 2);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "get", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.Undefined;
            return wm.TryGet(key.AsObjectHandle(), out var v) ? v : JsValue.Undefined;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(wm.Has(key.AsObjectHandle()));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var wm = RequireWeakMap(thisValue);
            var key = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (key.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(wm.Remove(key.AsObjectHandle()));
        }, length: 1);

        _weakMapPrototypeHandle = prototypeHandle;
        _weakMapConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureWeakMapPrototype()
    {
        _ = EnsureWeakMapConstructor();
        return _weakMapPrototypeHandle!.Value;
    }

    private WeakMapObject RequireWeakMap(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not WeakMapObject wm)
        {
            throw new JsThrownException(CreateTypeError("WeakMap method called on incompatible receiver."));
        }

        return wm;
    }

    private ObjectHandle EnsureWeakSetConstructor()
    {
        if (_weakSetConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "WeakSet",
            (_, _) => throw new JsThrownException(CreateTypeError("Constructor WeakSet requires 'new'.")),
            args =>
            {
                var ws = new WeakSetObject();
                ws.SetPrototype(EnsureWeakSetPrototype());
                var handle = _heap.AllocateObject(ws, AllocationSite.Current());
                if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
                {
                    var src = _heap.GetObject(args[0].AsObjectHandle());
                    if (src is ArrayObject)
                    {
                        var len = GetArrayLength(src);
                        for (var i = 0; i < len; i++)
                        {
                            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            TryGetPropertyValue(src, args[0], key, out var v);
                            if (v.Tag != JsValueTag.Object)
                            {
                                throw new JsThrownException(CreateTypeError("WeakSet entries must be objects."));
                            }

                            ws.Add(v.AsObjectHandle());
                            _heap.WriteBarrier(handle, v.AsObjectHandle());
                        }
                    }
                }

                return JsValue.FromObject(handle);
            },
            length: 0);

        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "add", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Invalid value used in weak set."));
            }

            ws.Add(value.AsObjectHandle());
            _heap.WriteBarrier(thisValue.AsObjectHandle(), value.AsObjectHandle());
            return thisValue;
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "has", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(ws.Has(value.AsObjectHandle()));
        }, length: 1);

        DefineNativePrototypeMethod(prototypeHandle, prototype, "delete", (thisValue, args) =>
        {
            var ws = RequireWeakSet(thisValue);
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (value.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(ws.Remove(value.AsObjectHandle()));
        }, length: 1);

        _weakSetPrototypeHandle = prototypeHandle;
        _weakSetConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureWeakSetPrototype()
    {
        _ = EnsureWeakSetConstructor();
        return _weakSetPrototypeHandle!.Value;
    }

    private WeakSetObject RequireWeakSet(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not WeakSetObject ws)
        {
            throw new JsThrownException(CreateTypeError("WeakSet method called on incompatible receiver."));
        }

        return ws;
    }

    private sealed class WeakMapObject : JsObject
    {
        private readonly Dictionary<ObjectHandle, JsValue> _entries = new();
        public void Set(ObjectHandle key, JsValue value) => _entries[key] = value;
        public bool TryGet(ObjectHandle key, out JsValue value) => _entries.TryGetValue(key, out value);
        public bool Has(ObjectHandle key) => _entries.ContainsKey(key);
        public bool Remove(ObjectHandle key) => _entries.Remove(key);
    }

    private sealed class WeakSetObject : JsObject
    {
        private readonly HashSet<ObjectHandle> _entries = new();
        public void Add(ObjectHandle key) => _entries.Add(key);
        public bool Has(ObjectHandle key) => _entries.Contains(key);
        public bool Remove(ObjectHandle key) => _entries.Remove(key);
    }

    // ECMA-262 28.1 The Reflect Object. Most members are thin wrappers over the
    // same internal helpers Object.* use; the spec-mandated differences are that
    // Reflect.set / defineProperty / deleteProperty return booleans (matching
    // the underlying [[Set]] / [[DefineOwnProperty]] / [[Delete]] success
    // result) instead of throwing.
    private ObjectHandle EnsureReflectObject()
    {
        if (_reflectObjectHandle is { } existing)
        {
            return existing;
        }

        var reflect = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(reflect, AllocationSite.Current());
        _heap.PushRoot(handle);

        // 28.1.9 has
        DefineIntrinsicFunction(handle, reflect, "has", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.has");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return JsValue.FromBoolean(obj.TryGetProperty(key, h => _heap.GetObject(h), out var __));
        }, length: 2);

        // 28.1.6 get
        DefineIntrinsicFunction(handle, reflect, "get", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.get");
            var target = args[0];
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return GetReceiverProperty(target, key);
        }, length: 2);

        // 28.1.14 set
        DefineIntrinsicFunction(handle, reflect, "set", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.set");
            var targetHandle = args[0].AsObjectHandle();
            var obj = _heap.GetObject(targetHandle);
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            var value = args.Count > 2 ? args[2] : JsValue.Undefined;
            return JsValue.FromBoolean(obj.SetProperty(key, value));
        }, length: 3);

        // 28.1.4 deleteProperty
        DefineIntrinsicFunction(handle, reflect, "deleteProperty", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.deleteProperty");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            return JsValue.FromBoolean(obj.DeleteProperty(key));
        }, length: 2);

        // 28.1.11 ownKeys - returns string-keyed own properties as an Array.
        // Symbol-keyed properties land here too once their iteration order is wired.
        DefineIntrinsicFunction(handle, reflect, "ownKeys", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.ownKeys");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var items = new List<JsValue>();
            foreach (var p in obj.EnumerateOwnProperties())
            {
                items.Add(JsValue.FromString(p.Key));
            }

            var arr = CreateArrayFromElements(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // 28.1.3 defineProperty - returns true on success, false when the underlying
        // [[DefineOwnProperty]] rejects (rather than throwing like Object.defineProperty).
        DefineIntrinsicFunction(handle, reflect, "defineProperty", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.defineProperty");
            if (args.Count < 3 || args[2].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Reflect.defineProperty descriptor must be an object."));
            }

            try
            {
                ObjectDefineProperty(JsValue.Undefined, args);
                return JsValue.FromBoolean(true);
            }
            catch (JsThrownException)
            {
                return JsValue.FromBoolean(false);
            }
        }, length: 3);

        // 28.1.7 getOwnPropertyDescriptor
        DefineIntrinsicFunction(handle, reflect, "getOwnPropertyDescriptor", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.getOwnPropertyDescriptor");
            return ObjectGetOwnPropertyDescriptor(JsValue.Undefined, args);
        }, length: 2);

        // 28.1.8 getPrototypeOf
        DefineIntrinsicFunction(handle, reflect, "getPrototypeOf", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.getPrototypeOf");
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return obj.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
        }, length: 1);

        // 28.1.13 setPrototypeOf - returns boolean (no TypeError on non-Object proto;
        // it returns false instead, per spec step 5).
        DefineIntrinsicFunction(handle, reflect, "setPrototypeOf", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.setPrototypeOf");
            var protoArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                return JsValue.FromBoolean(false);
            }

            var ownerHandle = args[0].AsObjectHandle();
            var obj = _heap.GetObject(ownerHandle);
            if (protoArg.Tag == JsValueTag.Object)
            {
                obj.SetPrototype(protoArg.AsObjectHandle());
                _heap.WriteBarrier(ownerHandle, protoArg.AsObjectHandle());
            }
            else
            {
                obj.SetPrototype(null);
            }

            return JsValue.FromBoolean(true);
        }, length: 2);

        // 28.1.10 isExtensible
        DefineIntrinsicFunction(handle, reflect, "isExtensible", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.isExtensible");
            return JsValue.FromBoolean(_heap.GetObject(args[0].AsObjectHandle()).Extensible);
        }, length: 1);

        // 28.1.12 preventExtensions
        DefineIntrinsicFunction(handle, reflect, "preventExtensions", (_, args) =>
        {
            RequireObjectTarget(args, "Reflect.preventExtensions");
            _heap.GetObject(args[0].AsObjectHandle()).PreventExtensions();
            return JsValue.FromBoolean(true);
        }, length: 1);

        // 28.1.1 apply(target, thisArg, argsList)
        DefineIntrinsicFunction(handle, reflect, "apply", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply target must be a function."));
            }

            var targetObj = _heap.GetObject(args[0].AsObjectHandle());
            if (targetObj is not JsFunctionObject && targetObj is not NativeFunctionObject)
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply target is not callable."));
            }

            var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            var argsList = args.Count > 2 ? args[2] : JsValue.Undefined;
            JsValue[] callArgs;
            if (argsList.Tag == JsValueTag.Object)
            {
                var lobj = _heap.GetObject(argsList.AsObjectHandle());
                var len = GetArrayLength(lobj);
                callArgs = new JsValue[len];
                for (var i = 0; i < len; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryGetPropertyValue(lobj, argsList, k, out callArgs[i]);
                }
            }
            else if (argsList.Tag == JsValueTag.Undefined || argsList.Tag == JsValueTag.Null)
            {
                callArgs = Array.Empty<JsValue>();
            }
            else
            {
                throw new JsThrownException(CreateTypeError("Reflect.apply args must be an Array-like."));
            }

            return CallFunction(args[0], callArgs, thisArg);
        }, length: 3);

        _reflectObjectHandle = handle;
        return handle;
    }

    private void RequireObjectTarget(IReadOnlyList<JsValue> args, string name)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(name + " called on non-object."));
        }
    }

    private ObjectHandle EnsureGlobalObject()
    {
        if (_globalObjectHandle is { } existing)
        {
            return existing;
        }

        var global = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(global, AllocationSite.Current());
        _heap.PushRoot(handle);
        _globalObjectHandle = handle;
        return handle;
    }

    // B.6.3 env-record shim. LoadName/StoreName funnel every slot-based LoadVar/StoreVar
    // opcode through a name-aware helper so the interpreter hot path goes through one
    // entry point instead of two. The helper first consults the frame's
    // EnvironmentRecord via the ECMA-262 9.1.1.1 abstract operations and only falls back
    // to the legacy VariableStore when the env record does not own the binding.
    //
    // In this commit the compiler still emits slot-based bindings into VariableStore and
    // closure capture still goes through shared JsVariableCells, so env records are not
    // yet populated for ordinary `var`/`let`/closures and the fallback is the live path
    // for nearly every read and write. The shim is intentionally non-mirroring: if we
    // wrote each StoreVar into both stores, a closure mutating a shared cell would never
    // refresh its outer frame's env-record copy, so the outer frame would observe stale
    // values. B.6.4 lands closure capture on env records and removes that concern; until
    // then the env path activates only for bindings later commits insert explicitly
    // (e.g. function-environment `this`, declarative scope entries).
    private JsValue LoadName(InterpreterFrame frame, int slot)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is not null)
        {
            var status = frame.Environment.GetBindingValue(name, strict: false, out var envValue);
            if (status == BindingOpResult.Ok)
            {
                return envValue;
            }
        }

        return frame.Variables[slot];
    }

    private void StoreName(InterpreterFrame frame, int slot, JsValue value)
    {
        var name = SlotNameTable.GetName(frame.Function, slot);
        if (name is not null && frame.Environment.HasBinding(name))
        {
            var status = frame.Environment.SetMutableBinding(name, value, strict: false);
            if (status == BindingOpResult.Ok)
            {
                // Keep VariableStore in sync for any opcode path that still reads the
                // slot directly (e.g. global object mirroring below, or unmigrated
                // closure capture). B.6.6 deletes both stores once nothing reads them.
                frame.Variables[slot] = value;
                SyncGlobalVariable(frame, slot, value);
                return;
            }
        }

        frame.Variables[slot] = value;
        SyncGlobalVariable(frame, slot, value);
    }

    private void SyncGlobalVariable(InterpreterFrame frame, int slot, JsValue value)
    {
        if (_globalObjectHandle is null ||
            frame.ThisValue.Tag != JsValueTag.Object ||
            !frame.ThisValue.AsObjectHandle().Equals(_globalObjectHandle.Value))
        {
            return;
        }

        foreach (var variable in frame.Function.VariableSlots)
        {
            if (variable.Value != slot)
            {
                continue;
            }

            var global = _heap.GetObject(_globalObjectHandle.Value);
            _ = global.SetProperty(variable.Key, value);
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(_globalObjectHandle.Value, value.AsObjectHandle());
            }

            return;
        }
    }

    private void ThrowTypeError(InterpreterFrame frame, string message)
    {
        ThrowOrHandle(frame, CreateTypeError(message));
    }

    private void ThrowOrHandle(InterpreterFrame frame, JsValue value)
    {
        if (frame.ExceptionHandlers.Count > 0)
        {
            var handlerIp = frame.ExceptionHandlers.Pop();
            frame.Registers[0] = value;
            frame.InstructionPointer = handlerIp;
            return;
        }

        throw new JsThrownException(value);
    }

    private JsValue CreateTypeError(string message)
    {
        return CreateErrorObject("TypeError", EnsureTypeErrorPrototype(), message);
    }

    private JsValue CreateRangeError(string message)
    {
        return CreateErrorObject("RangeError", EnsureRangeErrorPrototype(), message);
    }

    private JsValue CreateSyntaxError(string message)
    {
        return CreateErrorObject("SyntaxError", EnsureSyntaxErrorPrototype(), message);
    }

    private JsValue CreateErrorObject(string name, ObjectHandle prototypeHandle, string message)
    {
        var error = new JsObject();
        error.SetPrototype(prototypeHandle);
        _ = error.SetProperty("name", JsValue.FromString(name));
        _ = error.SetProperty("message", JsValue.FromString(message));
        return JsValue.FromObject(_heap.AllocateObject(error, AllocationSite.Current()));
    }

    private static string GetOptionalMessage(IReadOnlyList<JsValue> args)
    {
        return args.Count > 0 ? FormatPrimitiveForString(args[0]) : string.Empty;
    }

    private ObjectHandle EnsureEvalFunction()
    {
        if (_evalFunctionHandle is { } existing)
        {
            return existing;
        }

        var eval = new NativeFunctionObject(
            "eval",
            (_, args) => Eval(args),
            length: 1);
        var evalHandle = _heap.AllocateObject(eval, AllocationSite.Current());
        _heap.PushRoot(evalHandle);
        _evalFunctionHandle = evalHandle;
        return evalHandle;
    }

    private JsValue Eval(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.Undefined;
        }

        if (args[0].Tag != JsValueTag.String)
        {
            return args[0];
        }

        var program = JsParser.ParseScript(new SourceText(args[0].AsString(), "<eval>"));
        var compiled = new BytecodeCompiler().CompileProgram(program);
        new BytecodeVerifier().Verify(compiled);
        return ExecuteInternal(compiled, Array.Empty<JsValue>(), null, JsValue.Undefined);
    }

    private ObjectHandle EnsureTypeErrorPrototype()
    {
        _ = EnsureTypeErrorConstructor();
        return _typeErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureTypeErrorConstructor()
    {
        if (_typeErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "TypeError",
            (_, args) => CreateErrorObject("TypeError", EnsureTypeErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("TypeError", EnsureTypeErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _typeErrorPrototypeHandle = prototypeHandle;
        _typeErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureRangeErrorPrototype()
    {
        _ = EnsureRangeErrorConstructor();
        return _rangeErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureRangeErrorConstructor()
    {
        if (_rangeErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "RangeError",
            (_, args) => CreateErrorObject("RangeError", EnsureRangeErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("RangeError", EnsureRangeErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _rangeErrorPrototypeHandle = prototypeHandle;
        _rangeErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureSyntaxErrorPrototype()
    {
        _ = EnsureSyntaxErrorConstructor();
        return _syntaxErrorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureSyntaxErrorConstructor()
    {
        if (_syntaxErrorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetPrototype(EnsureErrorPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "SyntaxError",
            (_, args) => CreateErrorObject("SyntaxError", EnsureSyntaxErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("SyntaxError", EnsureSyntaxErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _syntaxErrorPrototypeHandle = prototypeHandle;
        _syntaxErrorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private ObjectHandle EnsureErrorPrototype()
    {
        _ = EnsureErrorConstructor();
        return _errorPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureErrorConstructor()
    {
        if (_errorConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new NumberObject(0d);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Error",
            (_, args) => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            args => CreateErrorObject("Error", EnsureErrorPrototype(), GetOptionalMessage(args)),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        // ECMA-262 20.5.3 Error.prototype: must carry the spec-default 'name'
        // ("Error") and 'message' ("") so an Error built without arguments still
        // stringifies as "Error" (rather than ": ").
        _ = prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString("Error"), Writable: true, Enumerable: false, Configurable: true));
        _ = prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        // ECMA-262 20.5.3.4 Error.prototype.toString(). Reads .name (defaulting to
        // "Error") and .message (defaulting to ""), then joins them with ": " when
        // both are non-empty. The receiver must be an Object - primitives raise
        // TypeError per step 2.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", ErrorPrototypeToString);

        _errorPrototypeHandle = prototypeHandle;
        _errorConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue ErrorPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Error.prototype.toString called on non-object."));
        }

        var obj = _heap.GetObject(thisValue.AsObjectHandle());
        string name = "Error";
        if (TryGetPropertyValue(obj, thisValue, "name", out var nameValue) &&
            nameValue.Tag != JsValueTag.Undefined)
        {
            name = ToStringValue(nameValue);
        }

        string message = string.Empty;
        if (TryGetPropertyValue(obj, thisValue, "message", out var messageValue) &&
            messageValue.Tag != JsValueTag.Undefined)
        {
            message = ToStringValue(messageValue);
        }

        if (name.Length == 0)
        {
            return JsValue.FromString(message);
        }

        if (message.Length == 0)
        {
            return JsValue.FromString(name);
        }

        return JsValue.FromString(name + ": " + message);
    }

    private ObjectHandle EnsureDatePrototype()
    {
        _ = EnsureDateConstructor();
        return _datePrototypeHandle!.Value;
    }

    private ObjectHandle EnsureDateConstructor()
    {
        if (_dateConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Date",
            (_, args) => CreateDateObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            args => CreateDateObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            length: 7);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(constructorHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);

        // ECMA-262 21.4.3.1 Date.now() - milliseconds since the UNIX epoch as a
        // Number value. Routed through DateTimeOffset so it's culture-invariant and
        // matches the spec's TimeClip semantics for "now" (which produces an integer
        // millisecond count by construction).
        DefineIntrinsicFunction(constructorHandle, constructor, "now", (_, _) =>
            JsValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), length: 0);

        // ECMA-262 21.4.3.4 Date.UTC(year[, month[, day[, hours[, minutes[, seconds[, ms]]]]]]).
        // Constructs a UTC time value from explicit components, defaulting any
        // omitted lower-order field to its spec default (0, or 1 for day). Years in
        // [0, 99] map to 1900+year per spec step 2.
        DefineIntrinsicFunction(constructorHandle, constructor, "UTC", (_, args) =>
        {
            if (args.Count == 0)
            {
                return JsValue.FromNumber(double.NaN);
            }

            var year = (int)ToNumber(args[0]);
            if (year >= 0 && year <= 99)
            {
                year += 1900;
            }

            var month = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
            var day = args.Count > 2 ? (int)ToNumber(args[2]) : 1;
            var hours = args.Count > 3 ? (int)ToNumber(args[3]) : 0;
            var minutes = args.Count > 4 ? (int)ToNumber(args[4]) : 0;
            var seconds = args.Count > 5 ? (int)ToNumber(args[5]) : 0;
            var ms = args.Count > 6 ? (int)ToNumber(args[6]) : 0;

            try
            {
                var dt = new DateTimeOffset(year, month + 1, day, hours, minutes, seconds, ms, TimeSpan.Zero);
                return JsValue.FromNumber(dt.ToUnixTimeMilliseconds());
            }
            catch (ArgumentOutOfRangeException)
            {
                return JsValue.FromNumber(double.NaN);
            }
        }, length: 7);

        // ECMA-262 21.4.3.2 Date.parse(string). Returns the time value of a parsed
        // date string, or NaN on failure. Recognises the standard ISO-8601 forms
        // (the spec's "Date Time String Format" 21.4.1.18) plus a few common
        // looser variants .NET's DateTimeOffset.TryParse already understands.
        DefineIntrinsicFunction(constructorHandle, constructor, "parse", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "Invalid Date";
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return JsValue.FromNumber(parsed.ToUnixTimeMilliseconds());
            }

            return JsValue.FromNumber(double.NaN);
        }, length: 1);

        _datePrototypeHandle = prototypeHandle;
        _dateConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue CreateDateObject(double timeValue)
    {
        var obj = new DateObject(timeValue);
        obj.SetPrototype(EnsureDatePrototype());
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle EnsureRegExpPrototype()
    {
        _ = EnsureRegExpConstructor();
        return _regexpPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureRegExpConstructor()
    {
        if (_regexpConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "RegExp",
            (_, args) => CreateRegExpObject(args),
            args => CreateRegExpObject(args),
            length: 2);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "test", RegExpPrototypeTest, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", RegExpPrototypeToString);

        _regexpPrototypeHandle = prototypeHandle;
        _regexpConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue CreateRegExpObject(IReadOnlyList<JsValue> args)
    {
        var pattern = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? ToStringValue(args[0])
            : string.Empty;
        var flags = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? ToStringValue(args[1])
            : string.Empty;
        var normalizedFlags = NormalizeRegExpFlags(flags);
        var options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (normalizedFlags.Contains('m', StringComparison.Ordinal))
        {
            options |= RegexOptions.Multiline;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        var obj = new RegExpObject(pattern, normalizedFlags, regex);
        obj.SetPrototype(EnsureRegExpPrototype());
        _ = obj.DefineOwnProperty(
            "source",
            new JsPropertyDescriptor(JsValue.FromString(pattern), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "global",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('g', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "ignoreCase",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('i', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "multiline",
            new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('m', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        _ = obj.DefineOwnProperty(
            "lastIndex",
            new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private JsValue RegExpPrototypeTest(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var regexp = RegExpThisValue(thisValue);
        var input = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        return JsValue.FromBoolean(regexp.Regex.IsMatch(input));
    }

    private JsValue RegExpPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var regexp = RegExpThisValue(thisValue);
        var escapedSource = regexp.Pattern.Replace("/", "\\/", StringComparison.Ordinal);
        return JsValue.FromString($"/{escapedSource}/{regexp.Flags}");
    }

    private RegExpObject RegExpThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is RegExpObject regexp)
        {
            return regexp;
        }

        throw new JsThrownException(CreateTypeError("RegExp.prototype method called on incompatible receiver."));
    }

    private string NormalizeRegExpFlags(string flags)
    {
        var seenGlobal = false;
        var seenIgnoreCase = false;
        var seenMultiline = false;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'g' when !seenGlobal:
                    seenGlobal = true;
                    break;
                case 'i' when !seenIgnoreCase:
                    seenIgnoreCase = true;
                    break;
                case 'm' when !seenMultiline:
                    seenMultiline = true;
                    break;
                case 'g':
                case 'i':
                case 'm':
                    throw new JsThrownException(CreateSyntaxError("RegExp flags must not be duplicated."));
                default:
                    throw new JsThrownException(CreateSyntaxError($"Invalid RegExp flag '{flag}'."));
            }
        }

        return string.Concat(
            seenGlobal ? "g" : string.Empty,
            seenIgnoreCase ? "i" : string.Empty,
            seenMultiline ? "m" : string.Empty);
    }

    private ObjectHandle EnsureMathObject()
    {
        if (_mathObjectHandle is { } existing)
        {
            return existing;
        }

        var math = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(math, AllocationSite.Current());
        _heap.PushRoot(handle);

        DefineMathConstant(math, "E", Math.E);
        DefineMathConstant(math, "LN10", Math.Log(10d));
        DefineMathConstant(math, "LN2", Math.Log(2d));
        DefineMathConstant(math, "LOG10E", 1d / Math.Log(10d));
        DefineMathConstant(math, "LOG2E", 1d / Math.Log(2d));
        DefineMathConstant(math, "PI", Math.PI);
        DefineMathConstant(math, "SQRT1_2", Math.Sqrt(0.5d));
        DefineMathConstant(math, "SQRT2", Math.Sqrt(2d));

        DefineMathFunction(handle, math, "abs", args => MathUnary(args, Math.Abs), length: 1);
        DefineMathFunction(handle, math, "acos", args => MathUnary(args, Math.Acos), length: 1);
        DefineMathFunction(handle, math, "asin", args => MathUnary(args, Math.Asin), length: 1);
        DefineMathFunction(handle, math, "atan", args => MathUnary(args, Math.Atan), length: 1);
        DefineMathFunction(handle, math, "atan2", MathAtan2, length: 2);
        DefineMathFunction(handle, math, "ceil", args => MathUnary(args, Math.Ceiling), length: 1);
        DefineMathFunction(handle, math, "cos", args => MathUnary(args, Math.Cos), length: 1);
        DefineMathFunction(handle, math, "exp", args => MathUnary(args, Math.Exp), length: 1);
        DefineMathFunction(handle, math, "floor", args => MathUnary(args, Math.Floor), length: 1);
        DefineMathFunction(handle, math, "log", args => MathUnary(args, Math.Log), length: 1);
        DefineMathFunction(handle, math, "max", MathMax, length: 2);
        DefineMathFunction(handle, math, "min", MathMin, length: 2);
        DefineMathFunction(handle, math, "pow", MathPow, length: 2);
        DefineMathFunction(handle, math, "round", MathRound, length: 1);
        DefineMathFunction(handle, math, "sin", args => MathUnary(args, Math.Sin), length: 1);
        DefineMathFunction(handle, math, "sqrt", args => MathUnary(args, Math.Sqrt), length: 1);
        DefineMathFunction(handle, math, "tan", args => MathUnary(args, Math.Tan), length: 1);
        // ECMA-262 21.3.2.28 Math.sign - returns -1/0/+1/-0/NaN matching argument sign.
        DefineMathFunction(handle, math, "sign", args => MathUnary(args, MathSign), length: 1);
        // ECMA-262 21.3.2.35 Math.trunc - round toward zero.
        DefineMathFunction(handle, math, "trunc", args => MathUnary(args, MathTrunc), length: 1);
        // ECMA-262 21.3.2.9 Math.cbrt - cube root.
        DefineMathFunction(handle, math, "cbrt", args => MathUnary(args, Math.Cbrt), length: 1);
        // ECMA-262 21.3.2.22 Math.log2 - base-2 logarithm.
        DefineMathFunction(handle, math, "log2", args => MathUnary(args, Math.Log2), length: 1);
        // ECMA-262 21.3.2.21 Math.log10 - base-10 logarithm.
        DefineMathFunction(handle, math, "log10", args => MathUnary(args, Math.Log10), length: 1);
        // ECMA-262 21.3.2.18 Math.hypot - sqrt of sum of squares; variadic.
        DefineMathFunction(handle, math, "hypot", MathHypot, length: 2);
        // ECMA-262 21.3.2.11 Math.clz32 - count leading zero bits of a Uint32.
        DefineMathFunction(handle, math, "clz32", args => JsValue.FromNumber(MathClz32(args)), length: 1);
        // ECMA-262 21.3.2.19 Math.imul - 32-bit signed integer multiplication.
        DefineMathFunction(handle, math, "imul", args => JsValue.FromNumber(MathImul(args)), length: 2);
        // ECMA-262 21.3.2.16 Math.fround - round to nearest IEEE-754 single-precision.
        DefineMathFunction(handle, math, "fround", args => MathUnary(args, v => (double)(float)v), length: 1);
        // ECMA-262 21.3.2.31/.12/.33 sinh/cosh/tanh.
        DefineMathFunction(handle, math, "sinh", args => MathUnary(args, Math.Sinh), length: 1);
        DefineMathFunction(handle, math, "cosh", args => MathUnary(args, Math.Cosh), length: 1);
        DefineMathFunction(handle, math, "tanh", args => MathUnary(args, Math.Tanh), length: 1);
        // ECMA-262 21.3.2.7/.2/.8 asinh/acosh/atanh.
        DefineMathFunction(handle, math, "asinh", args => MathUnary(args, Math.Asinh), length: 1);
        DefineMathFunction(handle, math, "acosh", args => MathUnary(args, Math.Acosh), length: 1);
        DefineMathFunction(handle, math, "atanh", args => MathUnary(args, Math.Atanh), length: 1);
        // ECMA-262 21.3.2.14 expm1 - more accurate for small x than Math.exp(x) - 1.
        DefineMathFunction(handle, math, "expm1", args => MathUnary(args, MathExpm1), length: 1);
        // ECMA-262 21.3.2.20 log1p - more accurate for small x than Math.log(1 + x).
        DefineMathFunction(handle, math, "log1p", args => MathUnary(args, MathLog1p), length: 1);
        // ECMA-262 21.3.2.27 Math.random - pseudorandom in [0, 1). Backed by a single
        // process-shared Random instance; not cryptographically secure (the spec
        // explicitly forbids using Math.random for cryptography).
        DefineMathFunction(handle, math, "random", _ => JsValue.FromNumber(_random.NextDouble()), length: 0);

        _mathObjectHandle = handle;
        return handle;
    }

    private static void DefineMathConstant(JsObject math, string name, double value)
    {
        _ = math.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromNumber(value),
                Writable: false,
                Enumerable: false,
                Configurable: false));
    }

    private void DefineMathFunction(
        ObjectHandle mathHandle,
        JsObject math,
        string name,
        Func<IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var function = new NativeFunctionObject(name, (_, args) => call(args), length: length);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        _ = math.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(mathHandle, functionHandle);
    }

    private JsValue MathUnary(IReadOnlyList<JsValue> args, Func<double, double> operation)
    {
        var value = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        return JsValue.FromNumber(operation(value));
    }

    private JsValue MathAtan2(IReadOnlyList<JsValue> args)
    {
        var y = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        var x = args.Count > 1 ? ToNumber(args[1]) : double.NaN;
        return JsValue.FromNumber(Math.Atan2(y, x));
    }

    private JsValue MathPow(IReadOnlyList<JsValue> args)
    {
        var x = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        var y = args.Count > 1 ? ToNumber(args[1]) : double.NaN;
        return JsValue.FromNumber(Math.Pow(x, y));
    }

    private JsValue MathRound(IReadOnlyList<JsValue> args)
    {
        var value = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d)
        {
            return JsValue.FromNumber(value);
        }

        if (value is > -0.5d and < 0d)
        {
            return JsValue.FromNumber(-0d);
        }

        return JsValue.FromNumber(Math.Floor(value + 0.5d));
    }

    private JsValue MathMax(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.FromNumber(double.NegativeInfinity);
        }

        var result = double.NegativeInfinity;
        foreach (var arg in args)
        {
            var value = ToNumber(arg);
            if (double.IsNaN(value))
            {
                return JsValue.FromNumber(double.NaN);
            }

            if (value > result || (value == 0d && result == 0d && !IsNegativeZero(value)))
            {
                result = value;
            }
        }

        return JsValue.FromNumber(result);
    }

    private JsValue MathMin(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.FromNumber(double.PositiveInfinity);
        }

        var result = double.PositiveInfinity;
        foreach (var arg in args)
        {
            var value = ToNumber(arg);
            if (double.IsNaN(value))
            {
                return JsValue.FromNumber(double.NaN);
            }

            if (value < result || (value == 0d && result == 0d && IsNegativeZero(value)))
            {
                result = value;
            }
        }

        return JsValue.FromNumber(result);
    }

    private static bool IsNegativeZero(double value)
    {
        return value == 0d && BitConverter.DoubleToInt64Bits(value) < 0;
    }

    // 21.3.2.28 step 4-5: -0 stays -0, +0 stays +0, NaN stays NaN. Negative finite or
    // -Infinity returns -1; positive finite or +Infinity returns +1.
    private static double MathSign(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (value == 0d)
        {
            return value;
        }

        return value < 0 ? -1d : 1d;
    }

    // 21.3.2.35: truncate fractional part. Returns +-0 and +-Infinity unchanged,
    // NaN unchanged; otherwise integer with the same sign as the argument.
    private static double MathTrunc(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d)
        {
            return value;
        }

        return value < 0 ? Math.Ceiling(value) : Math.Floor(value);
    }

    // 21.3.2.18 Math.hypot - sqrt(x^2 + y^2 + ...). Any +-Infinity argument wins
    // (return +Infinity); NaN sticks unless +-Infinity is also present.
    private JsValue MathHypot(IReadOnlyList<JsValue> args)
    {
        var sawNaN = false;
        var sum = 0d;
        for (var i = 0; i < args.Count; i++)
        {
            var v = ToNumber(args[i]);
            if (double.IsInfinity(v))
            {
                return JsValue.FromNumber(double.PositiveInfinity);
            }

            if (double.IsNaN(v))
            {
                sawNaN = true;
                continue;
            }

            sum += v * v;
        }

        if (sawNaN)
        {
            return JsValue.FromNumber(double.NaN);
        }

        return JsValue.FromNumber(Math.Sqrt(sum));
    }

    // 21.3.2.11 Math.clz32 - returns the number of leading zero bits when the
    // argument is converted to a Uint32. 32 when the value coerces to 0 or NaN.
    private double MathClz32(IReadOnlyList<JsValue> args)
    {
        var value = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 32d;
        }

        var u = ToUint32(value);
        if (u == 0u)
        {
            return 32d;
        }

        var n = 0;
        while ((u & 0x80000000u) == 0u)
        {
            u <<= 1;
            n++;
        }

        return n;
    }

    // 21.3.2.19 Math.imul - convert both arguments to Int32 and return the low
    // 32 bits of the signed product.
    private double MathImul(IReadOnlyList<JsValue> args)
    {
        var x = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
        var y = args.Count > 1 ? ToNumber(args[1]) : double.NaN;
        return unchecked((int)((uint)ToInt32(x) * (uint)ToInt32(y)));
    }

    // 21.3.2.14 expm1(x) = e^x - 1. NaN preserved; -Infinity yields -1; +Infinity
    // yields +Infinity. Routed through Math.Exp(x) - 1 since BCL has no expm1; the
    // accuracy difference matters for x near zero but not for spec conformance of
    // boundary cases.
    private static double MathExpm1(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsNegativeInfinity(value))
        {
            return -1d;
        }

        if (double.IsPositiveInfinity(value))
        {
            return double.PositiveInfinity;
        }

        if (value == 0d)
        {
            return value;   // preserves -0
        }

        return Math.Exp(value) - 1d;
    }

    // 21.3.2.20 log1p(x) = log(1 + x). NaN, -1, +Infinity, and the (x < -1) range
    // each have explicit spec branches; otherwise delegates to Math.Log(1 + x).
    private static double MathLog1p(double value)
    {
        if (double.IsNaN(value) || value < -1d)
        {
            return double.NaN;
        }

        if (value == -1d)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(value))
        {
            return double.PositiveInfinity;
        }

        if (value == 0d)
        {
            return value;   // preserves -0
        }

        return Math.Log(1d + value);
    }

    private static uint ToUint32(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0u;
        }

        var truncated = value >= 0 ? Math.Floor(value) : Math.Ceiling(value);
        var modulo = truncated - Math.Floor(truncated / 4294967296d) * 4294967296d;
        return (uint)modulo;
    }

    private static int ToInt32(double value)
    {
        var unsigned = ToUint32(value);
        return unchecked((int)unsigned);
    }

    private ObjectHandle EnsureJsonObject()
    {
        if (_jsonObjectHandle is { } existing)
        {
            return existing;
        }

        var json = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(json, AllocationSite.Current());
        _heap.PushRoot(handle);

        DefineIntrinsicFunction(handle, json, "parse", JsonParse, length: 2);
        DefineIntrinsicFunction(handle, json, "stringify", JsonStringify, length: 3);

        _jsonObjectHandle = handle;
        return handle;
    }

    private void DefineIntrinsicFunction(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var function = new NativeFunctionObject(name, call, length: length);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        _ = owner.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(ownerHandle, functionHandle);
    }

    // ECMA-262 25.5.1 JSON.parse(text[, reviver]). When a reviver function is
    // provided, the spec defines an InternalizeJSONProperty walk that visits every
    // node bottom-up: replacing the node with reviver.call(holder, key, value);
    // returning undefined deletes the entry. A non-callable reviver is ignored
    // per spec step 2.
    private JsValue JsonParse(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        JsValue unfiltered;
        try
        {
            using var document = JsonDocument.Parse(text);
            unfiltered = ConvertJsonElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }

        if (args.Count < 2 || args[1].Tag != JsValueTag.Object)
        {
            return unfiltered;
        }

        var reviverObj = _heap.GetObject(args[1].AsObjectHandle());
        if (reviverObj is not JsFunctionObject && reviverObj is not NativeFunctionObject)
        {
            return unfiltered;
        }

        // Spec 25.5.1.1 InternalizeJSONProperty: wrap the result in a single-property
        // root object { "": value } so the reviver can also see the top-level value
        // at the empty key, then walk children bottom-up.
        var rootObj = CreateOrdinaryObject();
        rootObj.SetProperty(string.Empty, unfiltered);
        var rootHandle = _heap.AllocateObject(rootObj, AllocationSite.Current());
        return InternalizeJsonProperty(rootHandle, string.Empty, args[1]);
    }

    private JsValue InternalizeJsonProperty(ObjectHandle holderHandle, string key, JsValue reviver)
    {
        var holder = _heap.GetObject(holderHandle);
        TryGetPropertyValue(holder, JsValue.FromObject(holderHandle), key, out var value);

        if (value.Tag == JsValueTag.Object)
        {
            var valueObj = _heap.GetObject(value.AsObjectHandle());
            var valueHandle = value.AsObjectHandle();
            if (valueObj is ArrayObject)
            {
                var length = GetArrayLength(valueObj);
                for (var i = 0; i < length; i++)
                {
                    var k = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var newValue = InternalizeJsonProperty(valueHandle, k, reviver);
                    if (newValue.Tag == JsValueTag.Undefined)
                    {
                        valueObj.DeleteProperty(k);
                    }
                    else
                    {
                        valueObj.SetProperty(k, newValue);
                    }
                }
            }
            else
            {
                var keys = new List<string>();
                foreach (var pair in valueObj.EnumerateOwnProperties())
                {
                    keys.Add(pair.Key);
                }

                foreach (var k in keys)
                {
                    var newValue = InternalizeJsonProperty(valueHandle, k, reviver);
                    if (newValue.Tag == JsValueTag.Undefined)
                    {
                        valueObj.DeleteProperty(k);
                    }
                    else
                    {
                        valueObj.SetProperty(k, newValue);
                    }
                }
            }
        }

        return CallFunction(reviver, new[] { JsValue.FromString(key), value }, JsValue.FromObject(holderHandle));
    }

    private JsValue ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => JsValue.Null,
            JsonValueKind.True => JsValue.FromBoolean(true),
            JsonValueKind.False => JsValue.FromBoolean(false),
            JsonValueKind.Number => element.TryGetDouble(out var number) ? JsValue.FromNumber(number) : JsValue.FromNumber(double.NaN),
            JsonValueKind.String => JsValue.FromString(element.GetString() ?? string.Empty),
            JsonValueKind.Array => ConvertJsonArray(element),
            JsonValueKind.Object => ConvertJsonObject(element),
            _ => JsValue.Undefined
        };
    }

    private JsValue ConvertJsonArray(JsonElement element)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var value = ConvertJsonElement(item);
            var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
            _ = obj.DefineOwnProperty(index.ToString(System.Globalization.CultureInfo.InvariantCulture), descriptor);
            WriteDescriptorBarrier(handle, descriptor);
            index++;
        }

        _ = obj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(JsValue.FromNumber(index), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(handle);
    }

    private JsValue ConvertJsonObject(JsonElement element)
    {
        var obj = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        foreach (var property in element.EnumerateObject())
        {
            var value = ConvertJsonElement(property.Value);
            var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
            _ = obj.DefineOwnProperty(property.Name, descriptor);
            WriteDescriptorBarrier(handle, descriptor);
        }

        return JsValue.FromObject(handle);
    }

    private JsValue JsonStringify(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count == 0)
        {
            return JsValue.Undefined;
        }

        var json = StringifyJsonValue(args[0], new HashSet<ObjectHandle>(), depth: 0, inArray: false);
        return json is null ? JsValue.Undefined : JsValue.FromString(json);
    }

    private string? StringifyJsonValue(JsValue value, HashSet<ObjectHandle> stack, int depth, bool inArray)
    {
        if (depth > 200)
        {
            throw new JsThrownException(CreateTypeError("JSON.stringify exceeded the maximum serialization depth."));
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => inArray ? "null" : null,
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => StringifyJsonNumber(value.AsNumber()),
            JsValueTag.String => JsonSerializer.Serialize(value.AsString()),
            JsValueTag.Object => StringifyJsonObject(value, stack, depth, inArray),
            _ => inArray ? "null" : null
        };
    }

    private string StringifyJsonNumber(double number)
    {
        return double.IsFinite(number)
            ? FormatNumberForString(number)
            : "null";
    }

    private string? StringifyJsonObject(JsValue value, HashSet<ObjectHandle> stack, int depth, bool inArray)
    {
        var handle = value.AsObjectHandle();
        var obj = _heap.GetObject(handle);
        if (obj is JsFunctionObject or NativeFunctionObject)
        {
            return inArray ? "null" : null;
        }

        if (!stack.Add(handle))
        {
            throw new JsThrownException(CreateTypeError("Cannot stringify circular structure."));
        }

        try
        {
            if (obj is ArrayObject)
            {
                return StringifyJsonArray(obj, value, stack, depth);
            }

            var parts = new List<string>();
            foreach (var property in obj.EnumerateOwnProperties())
            {
                if (!property.Value.Enumerable ||
                    !TryGetPropertyValue(obj, value, property.Key, out var propertyValue))
                {
                    continue;
                }

                var serialized = StringifyJsonValue(propertyValue, stack, depth + 1, inArray: false);
                if (serialized is not null)
                {
                    parts.Add(JsonSerializer.Serialize(property.Key) + ":" + serialized);
                }
            }

            return "{" + string.Join(",", parts) + "}";
        }
        finally
        {
            _ = stack.Remove(handle);
        }
    }

    private string StringifyJsonArray(JsObject obj, JsValue receiver, HashSet<ObjectHandle> stack, int depth)
    {
        var length = GetArrayLength(obj);
        var parts = new string[length];
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parts[i] = TryGetPropertyValue(obj, receiver, key, out var value)
                ? StringifyJsonValue(value, stack, depth + 1, inArray: true) ?? "null"
                : "null";
        }

        return "[" + string.Join(",", parts) + "]";
    }

    private ObjectHandle EnsureObjectPrototype()
    {
        _ = EnsureObjectConstructor();
        return _objectPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureObjectConstructor()
    {
        if (_objectConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(new JsObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Object",
            (_, args) => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            args => CreateObjectFromValue(args.Count > 0 ? args[0] : JsValue.Undefined),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);
        var definePropertyHandle = _heap.AllocateObject(
            new NativeFunctionObject("defineProperty", ObjectDefineProperty, length: 3),
            AllocationSite.Current());
        _ = constructor.SetProperty("defineProperty", JsValue.FromObject(definePropertyHandle));
        _heap.WriteBarrier(constructorHandle, definePropertyHandle);
        var getOwnPropertyDescriptorHandle = _heap.AllocateObject(
            new NativeFunctionObject("getOwnPropertyDescriptor", ObjectGetOwnPropertyDescriptor, length: 2),
            AllocationSite.Current());
        _ = constructor.SetProperty("getOwnPropertyDescriptor", JsValue.FromObject(getOwnPropertyDescriptorHandle));
        _heap.WriteBarrier(constructorHandle, getOwnPropertyDescriptorHandle);

        // ECMA-262 20.1.2.13 Object.is(value1, value2). Implements the SameValue
        // abstract operation (7.2.10): +0 and -0 are NOT equal, NaN is equal to NaN.
        DefineIntrinsicFunction(constructorHandle, constructor, "is", (_, args) =>
        {
            var a = args.Count > 0 ? args[0] : JsValue.Undefined;
            var b = args.Count > 1 ? args[1] : JsValue.Undefined;
            return JsValue.FromBoolean(SameValue(a, b));
        }, length: 2);

        // ECMA-262 20.1.2.4 Object.defineProperties(O, Properties). Walks each own
        // enumerable property of the Properties object and calls Object.defineProperty
        // with the corresponding descriptor. The receiver O is returned.
        DefineIntrinsicFunction(constructorHandle, constructor, "defineProperties", (_, args) =>
        {
            if (args.Count < 2 || args[0].Tag != JsValueTag.Object || args[1].Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.defineProperties requires an object target and a properties object."));
            }

            var propsObj = _heap.GetObject(args[1].AsObjectHandle());
            var propsReceiver = args[1];
            var keys = new List<string>();
            foreach (var pair in propsObj.EnumerateOwnProperties())
            {
                if (pair.Value.Enumerable)
                {
                    keys.Add(pair.Key);
                }
            }

            foreach (var key in keys)
            {
                if (!TryGetPropertyValue(propsObj, propsReceiver, key, out var descValue) ||
                    descValue.Tag != JsValueTag.Object)
                {
                    continue;
                }

                ObjectDefineProperty(JsValue.Undefined, new[]
                {
                    args[0],
                    JsValue.FromString(key),
                    descValue,
                });
            }

            return args[0];
        }, length: 2);

        // ECMA-262 20.1.2.14 Object.hasOwn(O, P) - ES2022. Equivalent to calling
        // Object.prototype.hasOwnProperty.call(O, P) without the awkward .call form
        // and without the prototype hazard if O is a null-prototype object.
        DefineIntrinsicFunction(constructorHandle, constructor, "hasOwn", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out var __));
        }, length: 2);

        // ECMA-262 20.1.2.11 Object.getOwnPropertyDescriptors(O). Returns an object
        // whose own keys mirror the input's own keys and whose values are full
        // descriptor objects (built by the same factory as getOwnPropertyDescriptor
        // so the shape stays in sync).
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertyDescriptors", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            var result = CreateOrdinaryObject();
            var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
            if (target.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(target.AsObjectHandle());
                foreach (var pair in obj.EnumerateOwnProperties())
                {
                    var descObj = BuildDescriptorObject(pair.Value);
                    var descHandle = _heap.AllocateObject(descObj, AllocationSite.Current());
                    WriteDescriptorBarrier(descHandle, pair.Value);
                    result.SetProperty(pair.Key, JsValue.FromObject(descHandle));
                    _heap.WriteBarrier(resultHandle, descHandle);
                }
            }

            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 20.1.2.7 Object.fromEntries(iterable). The full spec walks an
        // arbitrary iterable via @@iterator; this implementation accepts an Array of
        // two-element entries (the overwhelmingly common case) until the full
        // iterator protocol is wired. Each entry's [0] becomes the property key
        // (coerced via ToPropertyKey) and [1] becomes the value. Non-Array or non-
        // entry inputs surface as a TypeError, matching engines like V8 / SM.
        DefineIntrinsicFunction(constructorHandle, constructor, "fromEntries", (_, args) =>
        {
            var iterable = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (iterable.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.fromEntries: argument must be an iterable of entries."));
            }

            var source = _heap.GetObject(iterable.AsObjectHandle());
            if (source is not ArrayObject)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.fromEntries: argument must be an Array (full iterator protocol pending)."));
            }

            var length = GetArrayLength(source);
            var result = CreateOrdinaryObject();
            for (var i = 0; i < length; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!TryGetPropertyValue(source, iterable, key, out var entryValue) ||
                    entryValue.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(CreateTypeError(
                        $"Object.fromEntries: entry at index {i} is not an object."));
                }

                var entry = _heap.GetObject(entryValue.AsObjectHandle());
                if (entry is not ArrayObject)
                {
                    throw new JsThrownException(CreateTypeError(
                        $"Object.fromEntries: entry at index {i} is not an Array."));
                }

                TryGetPropertyValue(entry, entryValue, "0", out var entryKey);
                TryGetPropertyValue(entry, entryValue, "1", out var entryValueSlot);
                var keyName = ToPropertyKey(entryKey);
                result.SetProperty(keyName, entryValueSlot);
            }

            var resultHandle = _heap.AllocateObject(result, AllocationSite.Current());
            return JsValue.FromObject(resultHandle);
        }, length: 1);

        // ECMA-262 20.1.2.10 Object.getOwnPropertyNames(O). Unlike Object.keys this
        // does NOT filter by Enumerable - every own string-keyed property surfaces in
        // [[OwnPropertyKeys]] order. Primitives and null/undefined still throw via
        // the ToObject step on the spec side; we surface that as a TypeError matching
        // the Object.keys path.
        DefineIntrinsicFunction(constructorHandle, constructor, "getOwnPropertyNames", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot convert undefined or null to object."));
            }

            var items = new List<JsValue>();
            if (target.Tag == JsValueTag.Object)
            {
                var obj = _heap.GetObject(target.AsObjectHandle());
                foreach (var pair in obj.EnumerateOwnProperties())
                {
                    items.Add(JsValue.FromString(pair.Key));
                }
            }

            var arr = CreateArrayObject(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // ECMA-262 20.1.2.20 / 20.1.2.15 Object.preventExtensions / isExtensible.
        DefineIntrinsicFunction(constructorHandle, constructor, "preventExtensions", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            _heap.GetObject(target.AsObjectHandle()).PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isExtensible", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            return JsValue.FromBoolean(_heap.GetObject(target.AsObjectHandle()).Extensible);
        }, length: 1);

        // ECMA-262 20.1.2.6 / 20.1.2.16 Object.freeze / isFrozen.
        DefineIntrinsicFunction(constructorHandle, constructor, "freeze", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            var keys = new List<string>();
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                keys.Add(pair.Key);
            }

            foreach (var key in keys)
            {
                if (!obj.TryGetOwnProperty(key, out var desc))
                {
                    continue;
                }

                var newDesc = desc.IsAccessor
                    ? JsPropertyDescriptor.Accessor(desc.Get, desc.Set, desc.Enumerable, Configurable: false)
                    : new JsPropertyDescriptor(desc.Value, Writable: false, desc.Enumerable, Configurable: false);
                obj.DefineOwnProperty(key, newDesc);
            }

            obj.PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isFrozen", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(true);   // primitives are vacuously frozen
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj.Extensible)
            {
                return JsValue.FromBoolean(false);
            }

            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (pair.Value.Configurable)
                {
                    return JsValue.FromBoolean(false);
                }

                if (!pair.Value.IsAccessor && pair.Value.Writable)
                {
                    return JsValue.FromBoolean(false);
                }
            }

            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 20.1.2.23 / 20.1.2.17 Object.seal / isSealed.
        DefineIntrinsicFunction(constructorHandle, constructor, "seal", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return target;
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            var keys = new List<string>();
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                keys.Add(pair.Key);
            }

            foreach (var key in keys)
            {
                if (!obj.TryGetOwnProperty(key, out var desc))
                {
                    continue;
                }

                var newDesc = desc.IsAccessor
                    ? JsPropertyDescriptor.Accessor(desc.Get, desc.Set, desc.Enumerable, Configurable: false)
                    : new JsPropertyDescriptor(desc.Value, desc.Writable, desc.Enumerable, Configurable: false);
                obj.DefineOwnProperty(key, newDesc);
            }

            obj.PreventExtensions();
            return target;
        }, length: 1);

        DefineIntrinsicFunction(constructorHandle, constructor, "isSealed", (_, args) =>
        {
            var target = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (target.Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(true);
            }

            var obj = _heap.GetObject(target.AsObjectHandle());
            if (obj.Extensible)
            {
                return JsValue.FromBoolean(false);
            }

            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (pair.Value.Configurable)
                {
                    return JsValue.FromBoolean(false);
                }
            }

            return JsValue.FromBoolean(true);
        }, length: 1);

        // ECMA-262 20.1.2.1 Object.assign(target, ...sources). Copies enumerable own
        // string-keyed properties from each source to target via [[Set]]. Returns
        // the (possibly coerced) target.
        DefineIntrinsicFunction(constructorHandle, constructor, "assign", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.assign target must not be undefined or null."));
            }

            var targetValue = args[0];
            if (targetValue.Tag != JsValueTag.Object)
            {
                return targetValue;
            }

            var targetHandle = targetValue.AsObjectHandle();
            var target = _heap.GetObject(targetHandle);

            for (var i = 1; i < args.Count; i++)
            {
                var source = args[i];
                if (source.Tag == JsValueTag.Undefined || source.Tag == JsValueTag.Null)
                {
                    continue;
                }

                if (source.Tag != JsValueTag.Object)
                {
                    continue;
                }

                var sourceObj = _heap.GetObject(source.AsObjectHandle());
                foreach (var pair in sourceObj.EnumerateOwnProperties())
                {
                    if (!pair.Value.Enumerable)
                    {
                        continue;
                    }

                    var value = pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value;
                    if (!target.SetProperty(pair.Key, value))
                    {
                        // Property was non-writable; per spec [[Set]] returning false
                        // in strict mode is a TypeError. We surface that consistently.
                        throw new JsThrownException(CreateTypeError(
                            $"Cannot assign to read-only property '{pair.Key}'."));
                    }

                    if (value.Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(targetHandle, value.AsObjectHandle());
                    }
                }
            }

            return targetValue;
        }, length: 2);

        // ECMA-262 20.1.2.12 Object.getPrototypeOf(O).
        DefineIntrinsicFunction(constructorHandle, constructor, "getPrototypeOf", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.getPrototypeOf called on null or undefined."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return JsValue.Null;
            }

            var target = _heap.GetObject(args[0].AsObjectHandle());
            return target.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
        }, length: 1);

        // ECMA-262 20.1.2.21 Object.setPrototypeOf(O, proto). Returns O. Proto must
        // be either an Object or null.
        DefineIntrinsicFunction(constructorHandle, constructor, "setPrototypeOf", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.setPrototypeOf called on null or undefined."));
            }

            var protoArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.setPrototypeOf: prototype must be Object or null."));
            }

            if (args[0].Tag != JsValueTag.Object)
            {
                return args[0];
            }

            var ownerHandle = args[0].AsObjectHandle();
            var target = _heap.GetObject(ownerHandle);
            if (protoArg.Tag == JsValueTag.Object)
            {
                var protoHandle = protoArg.AsObjectHandle();
                target.SetPrototype(protoHandle);
                _heap.WriteBarrier(ownerHandle, protoHandle);
            }
            else
            {
                target.SetPrototype(null);
            }

            return args[0];
        }, length: 2);

        // ECMA-262 20.1.2.2 Object.create(O[, Properties]). Allocates a new ordinary
        // object whose [[Prototype]] is the first argument (must be Object or null).
        // Properties parameter (DefineProperties-style) is deferred; passing it today
        // is silently ignored - tests should not rely on the second argument until a
        // follow-up commit wires DefineProperties.
        DefineIntrinsicFunction(constructorHandle, constructor, "create", (_, args) =>
        {
            var protoArg = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (protoArg.Tag != JsValueTag.Object && protoArg.Tag != JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Object.create: prototype must be Object or null."));
            }

            var obj = CreateOrdinaryObject();
            if (protoArg.Tag == JsValueTag.Object)
            {
                obj.SetPrototype(protoArg.AsObjectHandle());
            }
            else
            {
                obj.SetPrototype(null);
            }

            var handle = _heap.AllocateObject(obj, AllocationSite.Current());
            if (protoArg.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(handle, protoArg.AsObjectHandle());
            }

            return JsValue.FromObject(handle);
        }, length: 2);

        // ECMA-262 20.1.2.18 / 20.1.2.22 / 20.1.2.5 - Object.keys / values / entries.
        // Each calls EnumerableOwnProperties(O, kind) (7.3.24) which iterates the
        // target's own string-keyed properties in [[OwnPropertyKeys]] order and
        // filters to those with Enumerable: true. Undefined/null arguments raise
        // TypeError per the ToObject step (7.1.18). Other primitives currently
        // return an empty list; full ToObject boxing for primitives lands when the
        // String/Number/Boolean prototype enumerations land.
        DefineIntrinsicFunction(constructorHandle, constructor, "keys", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Keys), length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "values", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Values), length: 1);
        DefineIntrinsicFunction(constructorHandle, constructor, "entries", (_, args)
            => CollectOwnEnumerable(args, OwnEnumerableKind.Entries), length: 1);

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "hasOwnProperty", ObjectPrototypeHasOwnProperty, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "isPrototypeOf", ObjectPrototypeIsPrototypeOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "propertyIsEnumerable", ObjectPrototypePropertyIsEnumerable, length: 1);

        _objectPrototypeHandle = prototypeHandle;
        _objectConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private enum OwnEnumerableKind
    {
        Keys,
        Values,
        Entries,
    }

    private JsValue CollectOwnEnumerable(IReadOnlyList<JsValue> args, OwnEnumerableKind kind)
    {
        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (target.Tag == JsValueTag.Undefined || target.Tag == JsValueTag.Null)
        {
            // 7.1.18 ToObject(undefined|null) throws TypeError; the user-visible
            // call site is Object.{keys,values,entries}, so the message names it.
            throw new JsThrownException(CreateTypeError(
                "Cannot convert undefined or null to object."));
        }

        var items = new List<JsValue>();
        if (target.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(target.AsObjectHandle());
            foreach (var pair in obj.EnumerateOwnProperties())
            {
                if (!pair.Value.Enumerable)
                {
                    continue;
                }

                switch (kind)
                {
                    case OwnEnumerableKind.Keys:
                        items.Add(JsValue.FromString(pair.Key));
                        break;
                    case OwnEnumerableKind.Values:
                        items.Add(pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value);
                        break;
                    case OwnEnumerableKind.Entries:
                    {
                        var value = pair.Value.IsAccessor ? JsValue.Undefined : pair.Value.Value;
                        var entry = CreateArrayObject(new[] { JsValue.FromString(pair.Key), value });
                        var entryHandle = _heap.AllocateObject(entry, AllocationSite.Current());
                        items.Add(JsValue.FromObject(entryHandle));
                        break;
                    }
                }
            }
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private ObjectHandle EnsureFunctionPrototype()
    {
        _ = EnsureFunctionConstructor();
        return _functionPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureFunctionConstructor()
    {
        if (_functionConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new NativeFunctionObject(string.Empty, (_, _) => JsValue.Undefined);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Function",
            (_, args) => CreateDynamicFunction(args),
            args => CreateDynamicFunction(args),
            length: 1);
        constructor.SetPrototype(prototypeHandle);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var callHandle = EnsureFunctionCallMethod();
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _ = prototype.SetProperty("call", JsValue.FromObject(callHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _heap.WriteBarrier(prototypeHandle, callHandle);

        // ECMA-262 20.2.3.1 Function.prototype.apply(thisArg, argsArray). The
        // second argument is an Array (or array-like). null/undefined become an
        // empty argument list per spec step 3-4.
        var applyHandle = _heap.AllocateObject(
            new NativeFunctionObject("apply", FunctionPrototypeApply, length: 2),
            AllocationSite.Current());
        _ = prototype.SetProperty("apply", JsValue.FromObject(applyHandle));
        _heap.WriteBarrier(prototypeHandle, applyHandle);

        // ECMA-262 20.2.3.2 Function.prototype.bind(thisArg, ...args). Returns a new
        // function ("exotic bound function" in the spec). The returned function calls
        // the original with thisArg pre-set and any bound args prepended to the
        // call-site args.
        var bindHandle = _heap.AllocateObject(
            new NativeFunctionObject("bind", FunctionPrototypeBind, length: 1),
            AllocationSite.Current());
        _ = prototype.SetProperty("bind", JsValue.FromObject(bindHandle));
        _heap.WriteBarrier(prototypeHandle, bindHandle);

        _functionPrototypeHandle = prototypeHandle;
        _functionConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue CreateFunctionObject(
        BytecodeFunction function,
        IReadOnlyDictionary<string, JsVariableCell>? capturedVariables = null)
    {
        var fnObj = new JsFunctionObject(function, capturedVariables);
        fnObj.SetPrototype(EnsureFunctionPrototype());
        _ = fnObj.DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(
                JsValue.FromString(function.Name ?? string.Empty),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        _ = fnObj.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(function.ParameterNames.Count),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _ = fnObj.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var handle = _heap.AllocateObject(fnObj, AllocationSite.Current());
        _heap.WriteBarrier(handle, prototypeHandle);
        return JsValue.FromObject(handle);
    }

    private JsValue CreateDynamicFunction(IReadOnlyList<JsValue> args)
    {
        var parameters = new List<string>();
        for (var i = 0; i + 1 < args.Count; i++)
        {
            AddFunctionConstructorParameters(parameters, ToStringValue(args[i]));
        }

        var body = args.Count > 0 ? ToStringValue(args[^1]) : string.Empty;
        try
        {
            var compiled = new BytecodeCompiler().CompileFunctionBody(
                new SourceText(body, "<Function>"),
                parameters,
                "anonymous");
            new BytecodeVerifier().Verify(compiled);
            return CreateFunctionObject(compiled);
        }
        catch (Exception ex) when (ex is JsParserException or UnsupportedFeatureException)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }
    }

    private static void AddFunctionConstructorParameters(List<string> parameters, string parameterText)
    {
        foreach (var rawPart in parameterText.Split(','))
        {
            var parameter = rawPart.Trim();
            if (parameter.Length == 0)
            {
                continue;
            }

            if (!IsIdentifierName(parameter))
            {
                throw new JsParserException($"Invalid function parameter '{parameter}'.");
            }

            parameters.Add(parameter);
        }
    }

    private static bool IsIdentifierName(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var first = value[0];
        if (first != '_' && first != '$' && !char.IsLetter(first))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '_' && ch != '$' && !char.IsLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private ObjectHandle DefineNativePrototypeMethod(
        ObjectHandle prototypeHandle,
        JsObject prototype,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length = 0)
    {
        var function = new NativeFunctionObject(name, call, length: length);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        var callHandle = EnsureFunctionCallMethod();
        _ = function.SetProperty("call", JsValue.FromObject(callHandle));
        _heap.WriteBarrier(functionHandle, callHandle);
        _ = prototype.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, functionHandle);
        return functionHandle;
    }

    private ObjectHandle EnsureFunctionCallMethod()
    {
        if (_functionCallMethodHandle is { } existing)
        {
            return existing;
        }

        var call = new NativeFunctionObject(
            "call",
            (thisValue, args) => FunctionPrototypeCall(thisValue, args),
            length: 1);
        var callHandle = _heap.AllocateObject(call, AllocationSite.Current());
        _heap.PushRoot(callHandle);
        _functionCallMethodHandle = callHandle;
        return callHandle;
    }

    // ECMA-262 20.2.3.1 Function.prototype.apply. Distinct from .call in that the
    // arguments come packaged in an Array (or array-like) second parameter; null/
    // undefined yields an empty argument list per step 3-4.
    private JsValue FunctionPrototypeApply(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var thisArgument = args.Count > 0 ? args[0] : JsValue.Undefined;
        var argsArray = args.Count > 1 ? args[1] : JsValue.Undefined;

        JsValue[] callArgs;
        if (argsArray.Tag == JsValueTag.Undefined || argsArray.Tag == JsValueTag.Null)
        {
            callArgs = Array.Empty<JsValue>();
        }
        else if (argsArray.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(argsArray.AsObjectHandle());
            var length = GetArrayLength(obj);
            callArgs = new JsValue[length];
            for (var i = 0; i < length; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TryGetPropertyValue(obj, argsArray, key, out callArgs[i]);
            }
        }
        else
        {
            throw new JsThrownException(CreateTypeError(
                "Function.prototype.apply: second argument must be an object or null/undefined."));
        }

        return CallFunction(thisValue, callArgs, thisArgument);
    }

    // ECMA-262 20.2.3.2 Function.prototype.bind. Returns a fresh NativeFunctionObject
    // that, when called, delegates to the original with thisArg pinned and any bound
    // args prepended to the call-site args. The exotic [[Construct]] / target-name
    // / target-length spec subtleties are deferred; the common bind use case
    // (this + partial application) is covered.
    private JsValue FunctionPrototypeBind(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype.bind called on non-function."));
        }

        var targetObj = _heap.GetObject(thisValue.AsObjectHandle());
        if (targetObj is not JsFunctionObject && targetObj is not NativeFunctionObject)
        {
            throw new JsThrownException(CreateTypeError("Function.prototype.bind called on non-callable."));
        }

        var boundThis = args.Count > 0 ? args[0] : JsValue.Undefined;
        var boundArgs = new JsValue[Math.Max(0, args.Count - 1)];
        for (var i = 1; i < args.Count; i++)
        {
            boundArgs[i - 1] = args[i];
        }

        // Capture the original handle so the bound function never re-resolves to a
        // moved object if the heap compacts under it.
        var targetValue = thisValue;
        var bound = new NativeFunctionObject(
            "bound",
            (_, callArgs) =>
            {
                var merged = new JsValue[boundArgs.Length + callArgs.Count];
                Array.Copy(boundArgs, merged, boundArgs.Length);
                for (var i = 0; i < callArgs.Count; i++)
                {
                    merged[boundArgs.Length + i] = callArgs[i];
                }

                return CallFunction(targetValue, merged, boundThis);
            },
            length: Math.Max(0, GetCallableLength(targetObj) - boundArgs.Length));
        // Inherit Function.prototype so .bind/.call/.apply work on the bound result.
        bound.SetPrototype(EnsureFunctionPrototype());

        var boundHandle = _heap.AllocateObject(bound, AllocationSite.Current());
        return JsValue.FromObject(boundHandle);
    }

    private static int GetCallableLength(JsObject callable)
    {
        if (callable.TryGetOwnProperty("length", out var desc) &&
            (desc.Value.Tag == JsValueTag.Number || desc.Value.Tag == JsValueTag.Int32))
        {
            return Math.Max(0, (int)desc.Value.AsNumber());
        }

        return 0;
    }

    private JsValue FunctionPrototypeCall(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var thisArgument = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (args.Count <= 1)
        {
            return CallFunction(thisValue, Array.Empty<JsValue>(), thisArgument);
        }

        var callArgs = new JsValue[args.Count - 1];
        for (var i = 1; i < args.Count; i++)
        {
            callArgs[i - 1] = args[i];
        }

        return CallFunction(thisValue, callArgs, thisArgument);
    }

    private JsValue CreateObjectFromValue(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current())),
            JsValueTag.Object => value,
            JsValueTag.Boolean => CreateBooleanObject(value.AsBoolean()),
            JsValueTag.Int32 or JsValueTag.Number => CreateNumberObject(ToNumber(value)),
            JsValueTag.String => CreateStringObject(value.AsString()),
            JsValueTag.HostObject => value,
            _ => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()))
        };
    }

    private JsValue ObjectPrototypeToString(JsValue thisValue)
    {
        var tag = thisValue.Tag switch
        {
            JsValueTag.Undefined => "Undefined",
            JsValueTag.Null => "Null",
            JsValueTag.Boolean => "Boolean",
            JsValueTag.Int32 or JsValueTag.Number => "Number",
            JsValueTag.String => "String",
            JsValueTag.HostObject => "Object",
            JsValueTag.Object => GetObjectToStringTag(thisValue.AsObjectHandle()),
            _ => "Object"
        };

        return JsValue.FromString($"[object {tag}]");
    }

    private JsValue ObjectPrototypeHasOwnProperty(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Object.prototype.hasOwnProperty called on null or undefined."));
        }

        if (thisValue.Tag == JsValueTag.HostObject)
        {
            return JsValue.FromBoolean(false);
        }

        var key = ToPropertyKey(args.Count > 0 ? args[0] : JsValue.Undefined);
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out _));
    }

    private JsValue ObjectPrototypePropertyIsEnumerable(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Object.prototype.propertyIsEnumerable called on null or undefined."));
        }

        if (thisValue.Tag == JsValueTag.HostObject)
        {
            return JsValue.FromBoolean(false);
        }

        var key = ToPropertyKey(args.Count > 0 ? args[0] : JsValue.Undefined);
        var objectValue = thisValue.Tag == JsValueTag.Object
            ? thisValue
            : CreateObjectFromValue(thisValue);
        var obj = _heap.GetObject(objectValue.AsObjectHandle());
        return JsValue.FromBoolean(obj.TryGetOwnProperty(key, out var descriptor) && descriptor.Enumerable);
    }

    private string GetObjectToStringTag(ObjectHandle handle)
    {
        return _heap.GetObject(handle) switch
        {
            ArrayObject => "Array",
            BooleanObject => "Boolean",
            DateObject => "Date",
            NumberObject => "Number",
            RegExpObject => "RegExp",
            StringObject => "String",
            JsFunctionObject or NativeFunctionObject => "Function",
            _ => "Object"
        };
    }

    private JsValue ObjectPrototypeIsPrototypeOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        if (thisValue.Tag != JsValueTag.Object || args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            return JsValue.FromBoolean(false);
        }

        var prototype = thisValue.AsObjectHandle();
        var candidate = _heap.GetObject(args[0].AsObjectHandle());
        while (candidate.PrototypeHandle is { } current)
        {
            if (current.Equals(prototype))
            {
                return JsValue.FromBoolean(true);
            }

            candidate = _heap.GetObject(current);
        }

        return JsValue.FromBoolean(false);
    }

    private JsValue ObjectDefineProperty(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count < 3 || args[0].Tag != JsValueTag.Object || args[2].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Object.defineProperty requires an object target and descriptor."));
        }

        var targetHandle = args[0].AsObjectHandle();
        var target = _heap.GetObject(targetHandle);
        var key = ToPropertyKey(args[1]);
        var descriptorObject = _heap.GetObject(args[2].AsObjectHandle());
        var descriptorReceiver = args[2];
        var hasValue = TryGetPropertyValue(descriptorObject, descriptorReceiver, "value", out var value);
        var hasWritable = descriptorObject.TryGetProperty("writable", h => _heap.GetObject(h), out _);
        var hasGetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "get", out var getter);
        var hasSetter = TryGetPropertyValue(descriptorObject, descriptorReceiver, "set", out var setter);
        if ((hasGetter || hasSetter) && (hasValue || hasWritable))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor cannot mix accessor and data fields."));
        }

        if (hasGetter &&
            getter.Tag != JsValueTag.Undefined &&
            !IsCallable(getter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor getter must be callable or undefined."));
        }

        if (hasSetter &&
            setter.Tag != JsValueTag.Undefined &&
            !IsCallable(setter))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor setter must be callable or undefined."));
        }

        var writable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "writable");
        var enumerable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "enumerable");
        var configurable = ReadDescriptorFlag(descriptorObject, descriptorReceiver, "configurable");

        var descriptor = hasGetter || hasSetter
            ? JsPropertyDescriptor.Accessor(
                hasGetter ? getter : JsValue.Undefined,
                hasSetter ? setter : JsValue.Undefined,
                enumerable,
                configurable)
            : new JsPropertyDescriptor(hasValue ? value : JsValue.Undefined, writable, enumerable, configurable);

        _ = target.DefineOwnProperty(key, descriptor);
        WriteDescriptorBarrier(targetHandle, descriptor);

        return args[0];
    }

    private void WriteDescriptorBarrier(ObjectHandle ownerHandle, JsPropertyDescriptor descriptor)
    {
        if (descriptor.IsAccessor)
        {
            if (descriptor.Get.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, descriptor.Get.AsObjectHandle());
            }

            if (descriptor.Set.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, descriptor.Set.AsObjectHandle());
            }

            return;
        }

        if (descriptor.Value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(ownerHandle, descriptor.Value.AsObjectHandle());
        }
    }

    private JsValue ObjectGetOwnPropertyDescriptor(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Object.getOwnPropertyDescriptor requires an object target."));
        }

        var target = _heap.GetObject(args[0].AsObjectHandle());
        var key = ToPropertyKey(args.Count > 1 ? args[1] : JsValue.Undefined);
        if (!target.TryGetOwnProperty(key, out var descriptor))
        {
            return JsValue.Undefined;
        }

        var descriptorObject = BuildDescriptorObject(descriptor);
        var descriptorHandle = _heap.AllocateObject(descriptorObject, AllocationSite.Current());
        WriteDescriptorBarrier(descriptorHandle, descriptor);
        return JsValue.FromObject(descriptorHandle);
    }

    // Spec 6.2.5.4 FromPropertyDescriptor: build the user-visible descriptor object
    // shape (get/set for accessor, value/writable for data, always enumerable +
    // configurable). Shared by Object.getOwnPropertyDescriptor and
    // Object.getOwnPropertyDescriptors so a single edit covers both.
    private JsObject BuildDescriptorObject(JsPropertyDescriptor descriptor)
    {
        var descriptorObject = CreateOrdinaryObject();
        if (descriptor.IsAccessor)
        {
            _ = descriptorObject.SetProperty("get", descriptor.Get);
            _ = descriptorObject.SetProperty("set", descriptor.Set);
        }
        else
        {
            _ = descriptorObject.SetProperty("value", descriptor.Value);
            _ = descriptorObject.SetProperty("writable", JsValue.FromBoolean(descriptor.Writable));
        }

        _ = descriptorObject.SetProperty("enumerable", JsValue.FromBoolean(descriptor.Enumerable));
        _ = descriptorObject.SetProperty("configurable", JsValue.FromBoolean(descriptor.Configurable));
        return descriptorObject;
    }

    // Unified property read that handles every JsValueTag the spec considers a valid
    // receiver of [[Get]]. Object falls through to the existing TryGetPropertyValue
    // path; String / Number / Boolean primitives consult their respective prototype
    // (with special-cased "length" / integer-index for String); undefined / null
    // raise TypeError per ToObject (7.1.18).
    [MayExecuteJs]
    private JsValue GetReceiverProperty(JsValue receiver, string key)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                return TryGetPropertyValue(obj, receiver, key, out var value) ? value : JsValue.Undefined;
            }
            case JsValueTag.String:
            {
                var s = receiver.AsString();
                if (key == "length")
                {
                    return JsValue.FromNumber(s.Length);
                }

                // Integer index access ("abc"[1] == "b"). Out-of-range returns undefined
                // per 22.1.4.1; the spec uses an exotic-object [[GetOwnProperty]] but the
                // observable behaviour is exactly this.
                if (IsCanonicalIntegerIndex(key, out var idx))
                {
                    return idx >= 0 && idx < s.Length
                        ? JsValue.FromString(s[idx].ToString())
                        : JsValue.Undefined;
                }

                // Fall through to String.prototype - lookup returns the inherited method
                // value; the caller (CallMethodN opcode) keeps the receiver string as
                // `thisValue` so the native method receives the primitive directly.
                var stringProto = _heap.GetObject(EnsureStringPrototype());
                return TryGetPropertyValue(stringProto, receiver, key, out var sv) ? sv : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(EnsureNumberPrototype());
                return TryGetPropertyValue(numberProto, receiver, key, out var nv) ? nv : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(EnsureBooleanPrototype());
                return TryGetPropertyValue(boolProto, receiver, key, out var bv) ? bv : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading '" + key + "')."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading '" + key + "')."));
            default:
                return JsValue.Undefined;
        }
    }

    // Symbol-keyed [[Get]] mirroring GetReceiverProperty: Object falls through to
    // the symbol-table lookup; String/Number/Boolean primitives consult their
    // prototype's symbol table; undefined / null raise TypeError.
    [MayExecuteJs]
    private JsValue GetReceiverSymbolProperty(JsValue receiver, long symbolId)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                return obj.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.String:
            {
                var stringProto = _heap.GetObject(EnsureStringPrototype());
                return stringProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(EnsureNumberPrototype());
                return numberProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(EnsureBooleanPrototype());
                return boolProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading symbol key)."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading symbol key)."));
            default:
                return JsValue.Undefined;
        }
    }

    // True when `key` is a non-negative decimal integer with no leading zeros (except
    // the literal "0"). Matches the spec's "canonical numeric string" definition for
    // 7.1.21 CanonicalNumericIndexString restricted to non-negative integers, which is
    // what String exotic objects accept as index keys.
    private static bool IsCanonicalIntegerIndex(string key, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length > 1 && key[0] == '0')
        {
            return false;
        }

        var result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            // Guard against overflow on absurdly long keys.
            if (result > (int.MaxValue - (c - '0')) / 10)
            {
                return false;
            }

            result = result * 10 + (c - '0');
        }

        index = result;
        return true;
    }

    [MayExecuteJs]
    private bool TryGetPropertyValue(JsObject obj, JsValue receiver, string key, out JsValue value)
    {
        if (!obj.TryGetProperty(key, h => _heap.GetObject(h), out var descriptor))
        {
            value = JsValue.Undefined;
            return false;
        }

        value = GetDescriptorValue(descriptor, receiver);
        return true;
    }

    [MayExecuteJs]
    private JsValue GetDescriptorValue(JsPropertyDescriptor descriptor, JsValue receiver)
    {
        if (!descriptor.IsAccessor)
        {
            return descriptor.Value;
        }

        if (descriptor.Get.Tag == JsValueTag.Undefined)
        {
            return JsValue.Undefined;
        }

        if (!IsCallable(descriptor.Get))
        {
            throw new JsThrownException(CreateTypeError("Accessor getter must be callable or undefined."));
        }

        return CallFunction(descriptor.Get, Array.Empty<JsValue>(), receiver);
    }

    [MayExecuteJs]
    private bool SetPropertyValue(ObjectHandle ownerHandle, JsObject obj, string key, JsValue value, JsValue receiver)
    {
        if (obj.TryGetOwnProperty(key, out var ownDescriptor))
        {
            return SetPropertyFromDescriptor(ownerHandle, obj, key, ownDescriptor, value, receiver);
        }

        if (TryGetPrototypePropertyDescriptor(obj, key, out var inheritedDescriptor))
        {
            if (inheritedDescriptor.IsAccessor)
            {
                return CallSetter(inheritedDescriptor, value, receiver);
            }

            if (!inheritedDescriptor.Writable)
            {
                return false;
            }
        }

        var descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
        _ = obj.DefineOwnProperty(key, descriptor);
        WriteDescriptorBarrier(ownerHandle, descriptor);
        return true;
    }

    [MayExecuteJs]
    private bool SetPropertyFromDescriptor(
        ObjectHandle ownerHandle,
        JsObject obj,
        string key,
        JsPropertyDescriptor descriptor,
        JsValue value,
        JsValue receiver)
    {
        if (descriptor.IsAccessor)
        {
            return CallSetter(descriptor, value, receiver);
        }

        if (!descriptor.Writable)
        {
            return false;
        }

        var updated = descriptor with { Value = value };
        _ = obj.DefineOwnProperty(key, updated);
        WriteDescriptorBarrier(ownerHandle, updated);
        return true;
    }

    private bool TryGetPrototypePropertyDescriptor(JsObject obj, string key, out JsPropertyDescriptor descriptor)
    {
        var prototype = obj.PrototypeHandle;
        while (prototype is { } handle)
        {
            var prototypeObject = _heap.GetObject(handle);
            if (prototypeObject.TryGetOwnProperty(key, out descriptor))
            {
                return true;
            }

            prototype = prototypeObject.PrototypeHandle;
        }

        descriptor = default;
        return false;
    }

    [MayExecuteJs]
    private bool CallSetter(JsPropertyDescriptor descriptor, JsValue value, JsValue receiver)
    {
        if (descriptor.Set.Tag == JsValueTag.Undefined)
        {
            return false;
        }

        if (!IsCallable(descriptor.Set))
        {
            throw new JsThrownException(CreateTypeError("Accessor setter must be callable or undefined."));
        }

        _ = CallFunction(descriptor.Set, new[] { value }, receiver);
        return true;
    }

    [MayExecuteJs]
    private bool ReadDescriptorFlag(JsObject descriptorObject, JsValue receiver, string propertyName)
    {
        return TryGetPropertyValue(descriptorObject, receiver, propertyName, out var value) &&
               IsTruthy(value);
    }

    private ObjectHandle EnsureArrayPrototype()
    {
        _ = EnsureArrayConstructor();
        return _arrayPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureArrayConstructor()
    {
        if (_arrayConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        prototype.SetProperty("length", JsValue.FromNumber(0));
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Array",
            (_, args) => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(args), AllocationSite.Current())),
            args => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(args), AllocationSite.Current())),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "push", ArrayPrototypePush, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", ArrayPrototypeToString);
        // ECMA-262 23.1.3.32 Array.prototype.toLocaleString. The spec calls each
        // element's toLocaleString through Invoke; here we approximate by calling
        // the element's toString conversion (which goes through Number / Boolean /
        // user toString as appropriate). Locale-sensitive output requires Intl which
        // is not wired yet; per the Intl-not-present clause this is the documented
        // fallback engines use.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString",
            (t, a) => { _ = a; return ArrayPrototypeToString(t, a); });
        // ECMA-262 23.1.3.18 Array.prototype.join, 23.1.3.16 indexOf, 23.1.3.14 includes.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "join", ArrayPrototypeJoin, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "indexOf", ArrayPrototypeIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "includes", ArrayPrototypeIncludes, length: 1);
        // ECMA-262 23.1.3.21 pop, 23.1.3.27 shift, 23.1.3.34 unshift.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "pop", ArrayPrototypePop);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "shift", ArrayPrototypeShift);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "unshift", ArrayPrototypeUnshift, length: 1);
        // ECMA-262 23.1.3.26 reverse, 23.1.3.7 fill.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reverse", ArrayPrototypeReverse);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "fill", ArrayPrototypeFill, length: 1);
        // ECMA-262 23.1.3.28 slice, 23.1.3.2 concat.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "slice", ArrayPrototypeSlice, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "concat", ArrayPrototypeConcat, length: 1);
        // ECMA-262 23.1.3.15 forEach, 23.1.3.19 map, 23.1.3.8 filter.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "forEach", ArrayPrototypeForEach, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "map", ArrayPrototypeMap, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "filter", ArrayPrototypeFilter, length: 1);
        // ECMA-262 23.1.3.24 reduce, 23.1.3.25 reduceRight.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reduce", (t, a) => ArrayPrototypeReduce(t, a, reverse: false), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "reduceRight", (t, a) => ArrayPrototypeReduce(t, a, reverse: true), length: 1);
        // ECMA-262 23.1.3.6 every, 23.1.3.29 some, 23.1.3.10 find, 23.1.3.11 findIndex.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "every", ArrayPrototypeEvery, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "some", ArrayPrototypeSome, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "find", ArrayPrototypeFind, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findIndex", ArrayPrototypeFindIndex, length: 1);
        // ECMA-262 23.1.3.17 lastIndexOf, 23.1.3.9 flat, 23.1.3.10a flatMap.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "lastIndexOf", ArrayPrototypeLastIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "flat", ArrayPrototypeFlat);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "flatMap", ArrayPrototypeFlatMap, length: 1);
        // ECMA-262 23.1.3.30 sort.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "sort", ArrayPrototypeSort, length: 1);
        // ECMA-262 23.1.3.31 splice.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "splice", ArrayPrototypeSplice, length: 2);
        // ECMA-262 23.1.3.36/.16/.5 Array.prototype.values / keys / entries. Each
        // returns an Array Iterator: an object exposing .next() that yields
        // {value, done}. The iterator also routes through Symbol.iterator so
        // for-of over the iterator itself works.
        var valuesHandle = DefineNativePrototypeMethod(prototypeHandle, prototype, "values",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Value); });
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "keys",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Key); });
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "entries",
            (t, a) => { _ = a; return CreateArrayIterator(t, ArrayIteratorKind.Entry); });
        // ECMA-262 23.1.3.37 - Array.prototype[Symbol.iterator] is the same function
        // object as Array.prototype.values per spec note 1.
        prototype.DefineOwnSymbolProperty(GetWellKnownSymbolId("iterator"),
            new JsPropertyDescriptor(JsValue.FromObject(valuesHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, valuesHandle);
        // ECMA-262 23.1.3.1 at, 23.1.3.12 findLast, 23.1.3.13 findLastIndex.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "at", ArrayPrototypeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findLast", ArrayPrototypeFindLast, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "findLastIndex", ArrayPrototypeFindLastIndex, length: 1);
        // ECMA-262 23.1.3.4 copyWithin.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "copyWithin", ArrayPrototypeCopyWithin, length: 2);

        // ECMA-262 23.1.2.3 Array.of(...items). Returns a fresh Array populated with
        // exactly the supplied items - distinct from new Array(n), which uses a
        // single Number argument to set length.
        DefineIntrinsicFunction(constructorHandle, constructor, "of", (_, args) =>
        {
            var items = new JsValue[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                items[i] = args[i];
            }

            var arr = CreateArrayFromElements(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 0);

        // ECMA-262 23.1.2.1 Array.from(arrayLike[, mapFn[, thisArg]]). Full spec
        // accepts any iterable; until the @@iterator protocol is wired we accept
        // array-like values (objects with a length property and integer-keyed
        // entries) which covers Array, arguments, and any plain {length, 0, 1, ...}
        // object - the overwhelmingly common case.
        DefineIntrinsicFunction(constructorHandle, constructor, "from", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError(
                    "Array.from: argument must not be undefined or null."));
            }

            var source = args[0];
            var mapFn = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? args[1] : (JsValue?)null;
            var thisArg = args.Count > 2 ? args[2] : JsValue.Undefined;

            if (mapFn.HasValue && mapFn.Value.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(CreateTypeError(
                    "Array.from: map function must be a function."));
            }

            if (source.Tag != JsValueTag.Object)
            {
                // Primitives: only strings yield indexable elements per spec; the
                // String boxing path is not wired yet, so non-object inputs surface
                // an empty Array rather than throwing. Matches the "no @@iterator"
                // fallback the iterator wiring will eventually replace.
                var arr0 = CreateArrayObject(Array.Empty<JsValue>());
                return JsValue.FromObject(_heap.AllocateObject(arr0, AllocationSite.Current()));
            }

            var obj = _heap.GetObject(source.AsObjectHandle());
            var length = GetArrayLength(obj);
            var items = new List<JsValue>(length);
            for (var i = 0; i < length; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TryGetPropertyValue(obj, source, key, out var v);
                if (mapFn.HasValue)
                {
                    v = CallFunction(mapFn.Value, new[] { v, JsValue.FromNumber(i) }, thisArg);
                }

                items.Add(v);
            }

            var arr = CreateArrayObject(items);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 1);

        // ECMA-262 23.1.2.2 Array.isArray(arg). Spec walks Proxy targets; we have no
        // Proxy yet, so the operation collapses to "is the value an ArrayObject?".
        DefineIntrinsicFunction(constructorHandle, constructor, "isArray", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            {
                return JsValue.FromBoolean(false);
            }

            var obj = _heap.GetObject(args[0].AsObjectHandle());
            return JsValue.FromBoolean(obj is ArrayObject);
        }, length: 1);

        _arrayPrototypeHandle = prototypeHandle;
        _arrayConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsObject CreateArrayObject(IReadOnlyList<JsValue> elements)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());

        if (elements.Count == 1 && (elements[0].Tag == JsValueTag.Int32 || elements[0].Tag == JsValueTag.Number))
        {
            var length = ToNumber(elements[0]);
            if (!IsValidArrayLength(length))
            {
                throw new JsThrownException(CreateRangeError("Invalid array length."));
            }

            _ = obj.SetProperty("length", JsValue.FromNumber(length));
            return obj;
        }

        return PopulateArrayWithElements(obj, elements);
    }

    // Element-list constructor that ALWAYS treats elements as values - never invokes
    // the "single Number = length" shortcut. Used by Array.of, Array.from,
    // Array.prototype.{slice/concat/map/filter/flat/flatMap/...} where the caller
    // already has the materialised element list and just wants a fresh Array.
    private JsObject CreateArrayFromElements(IReadOnlyList<JsValue> elements)
    {
        var obj = new ArrayObject();
        obj.SetPrototype(EnsureArrayPrototype());
        return PopulateArrayWithElements(obj, elements);
    }

    private static JsObject PopulateArrayWithElements(ArrayObject obj, IReadOnlyList<JsValue> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            _ = obj.SetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), elements[i]);
        }

        _ = obj.SetProperty("length", JsValue.FromNumber(elements.Count));
        return obj;
    }

    private static bool IsValidArrayLength(double value)
    {
        return !double.IsNaN(value) &&
               !double.IsInfinity(value) &&
               value >= 0 &&
               value <= uint.MaxValue &&
               Math.Truncate(value) == value;
    }

    private JsValue ArrayPrototypePush(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);

        for (var i = 0; i < args.Count; i++)
        {
            var key = (length + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, args[i]);
            if (args[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, args[i].AsObjectHandle());
            }
        }

        var newLength = length + args.Count;
        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));
        return JsValue.FromNumber(newLength);
    }

    // ECMA-262 23.1.3.21 Array.prototype.pop. Returns undefined for empty arrays;
    // otherwise removes and returns the last element, decrementing length.
    private JsValue ArrayPrototypePop(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            _ = obj.SetProperty("length", JsValue.FromNumber(0));
            return JsValue.Undefined;
        }

        var lastKey = (length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TryGetPropertyValue(obj, thisValue, lastKey, out var value);
        obj.DeleteProperty(lastKey);
        _ = obj.SetProperty("length", JsValue.FromNumber(length - 1));
        return value;
    }

    // ECMA-262 23.1.3.27 Array.prototype.shift. Removes element at index 0 and
    // shifts every subsequent element down by one; returns the removed value.
    private JsValue ArrayPrototypeShift(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            _ = obj.SetProperty("length", JsValue.FromNumber(0));
            return JsValue.Undefined;
        }

        TryGetPropertyValue(obj, thisValue, "0", out var first);
        for (var i = 1; i < length; i++)
        {
            var fromKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var toKey = (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
            {
                _ = obj.SetProperty(toKey, v);
            }
            else
            {
                obj.DeleteProperty(toKey);
            }
        }

        obj.DeleteProperty((length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = obj.SetProperty("length", JsValue.FromNumber(length - 1));
        return first;
    }

    // ECMA-262 23.1.3.34 Array.prototype.unshift. Inserts arguments at the front,
    // shifting existing elements up by args.Count; returns the new length.
    private JsValue ArrayPrototypeUnshift(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var insert = args.Count;

        if (insert > 0 && length > 0)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                var fromKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insert).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }
        }

        for (var i = 0; i < insert; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, args[i]);
            if (args[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, args[i].AsObjectHandle());
            }
        }

        var newLength = length + insert;
        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));
        return JsValue.FromNumber(newLength);
    }

    // ECMA-262 23.1.3.26 Array.prototype.reverse. Swaps slot i with slot len-1-i
    // for i < len/2; preserves holes (a missing source slot deletes the target).
    private JsValue ArrayPrototypeReverse(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var middle = length / 2;

        for (var lower = 0; lower < middle; lower++)
        {
            var upper = length - 1 - lower;
            var lowerKey = lower.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var upperKey = upper.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hasLower = TryGetPropertyValue(obj, thisValue, lowerKey, out var lowerValue);
            var hasUpper = TryGetPropertyValue(obj, thisValue, upperKey, out var upperValue);

            if (hasUpper)
            {
                _ = obj.SetProperty(lowerKey, upperValue);
            }
            else
            {
                obj.DeleteProperty(lowerKey);
            }

            if (hasLower)
            {
                _ = obj.SetProperty(upperKey, lowerValue);
            }
            else
            {
                obj.DeleteProperty(upperKey);
            }
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.7 Array.prototype.fill(value[, start[, end]]). Negative
    // start/end wrap from length; out-of-range values clamp into [0, length].
    private JsValue ArrayPrototypeFill(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        var start = NormaliseSliceIndex(args, 1, 0, length);
        var end = NormaliseSliceIndex(args, 2, length, length);

        for (var i = start; i < end; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, value);
            if (value.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
            }
        }

        return thisValue;
    }

    // Shared helper for fill / slice. Reads args[argIndex] as a number (default
    // when missing or undefined), then converts to an integer clamped into
    // [0, length] using the spec's "negative-from-length" rule.
    private static int NormaliseSliceIndex(IReadOnlyList<JsValue> args, int argIndex, int defaultValue, int length)
    {
        if (argIndex >= args.Count || args[argIndex].Tag == JsValueTag.Undefined)
        {
            return Math.Clamp(defaultValue, 0, length);
        }

        var raw = args[argIndex].Tag == JsValueTag.Int32
            ? args[argIndex].AsInt32()
            : (int)args[argIndex].AsNumber();
        var idx = raw < 0 ? length + raw : raw;
        return Math.Clamp(idx, 0, length);
    }

    // ECMA-262 23.1.3.4 Array.prototype.copyWithin(target, start[, end]).
    // Shallow-copies the sequence at [start, end) to position target in-place,
    // returning the receiver. Negative indices wrap from length; ranges are clamped
    // into [0, length]. When source and target overlap, copies use a forward or
    // backward pass to avoid clobbering data not yet copied.
    private JsValue ArrayPrototypeCopyWithin(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var target = NormaliseSliceIndex(args, 0, 0, length);
        var start = NormaliseSliceIndex(args, 1, 0, length);
        var end = NormaliseSliceIndex(args, 2, length, length);

        var count = Math.Min(end - start, length - target);
        if (count <= 0)
        {
            return thisValue;
        }

        // Direction: when target < start, forward copy is safe; otherwise walk
        // backwards so already-overwritten elements don't pollute the not-yet-copied
        // tail.
        var direction = target < start ? 1 : -1;
        var from = direction == 1 ? start : start + count - 1;
        var to = direction == 1 ? target : target + count - 1;

        for (var i = 0; i < count; i++)
        {
            var fromKey = from.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var toKey = to.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
            {
                _ = obj.SetProperty(toKey, v);
                if (v.Tag == JsValueTag.Object)
                {
                    _heap.WriteBarrier(ownerHandle, v.AsObjectHandle());
                }
            }
            else
            {
                obj.DeleteProperty(toKey);
            }

            from += direction;
            to += direction;
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.1 Array.prototype.at(index). Negative indices wrap from
    // length; out-of-range returns undefined.
    private JsValue ArrayPrototypeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var raw = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var idx = raw < 0 ? length + raw : raw;
        if (idx < 0 || idx >= length)
        {
            return JsValue.Undefined;
        }

        var key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TryGetPropertyValue(obj, thisValue, key, out var v);
        return v;
    }

    // ECMA-262 23.1.3.12 findLast. Mirrors find but walks backwards; visits holes
    // as undefined-valued slots per spec.
    private JsValue ArrayPrototypeFindLast(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    // ECMA-262 23.1.3.13 findLastIndex.
    private JsValue ArrayPrototypeFindLastIndex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = length - 1; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.31 Array.prototype.splice(start, deleteCount, ...items).
    // Removes deleteCount elements starting at start (negative wraps from length),
    // inserts items in their place, and returns a fresh Array of the removed
    // elements. Subsequent elements shift up or down depending on whether items
    // were inserted or extra elements removed.
    private JsValue ArrayPrototypeSplice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);

        int deleteCount;
        if (args.Count < 1)
        {
            deleteCount = 0;
        }
        else if (args.Count < 2)
        {
            // Single-arg form: delete from start to end (spec step 5.b).
            deleteCount = length - start;
        }
        else
        {
            deleteCount = Math.Clamp((int)ToNumber(args[1]), 0, length - start);
        }

        var insertCount = Math.Max(0, args.Count - 2);

        // Collect the removed slice.
        var removed = new List<JsValue>(deleteCount);
        for (var i = 0; i < deleteCount; i++)
        {
            var key = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            removed.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        var newLength = length - deleteCount + insertCount;
        if (insertCount < deleteCount)
        {
            // Shift down.
            for (var i = start; i < length - deleteCount; i++)
            {
                var fromKey = (i + deleteCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insertCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }

            for (var i = newLength; i < length; i++)
            {
                obj.DeleteProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else if (insertCount > deleteCount)
        {
            // Shift up: walk back-to-front so we never clobber a not-yet-moved slot.
            for (var i = length - deleteCount - 1; i >= start; i--)
            {
                var fromKey = (i + deleteCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var toKey = (i + insertCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, fromKey, out var v))
                {
                    _ = obj.SetProperty(toKey, v);
                }
                else
                {
                    obj.DeleteProperty(toKey);
                }
            }
        }

        // Insert new items.
        for (var i = 0; i < insertCount; i++)
        {
            var key = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = args[2 + i];
            _ = obj.SetProperty(key, v);
            if (v.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, v.AsObjectHandle());
            }
        }

        _ = obj.SetProperty("length", JsValue.FromNumber(newLength));

        var resultArr = CreateArrayFromElements(removed);
        return JsValue.FromObject(_heap.AllocateObject(resultArr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.30 Array.prototype.sort([compareFn]). Default comparator
    // converts each element to a String and compares lexicographically; a user
    // comparator returning < 0 / 0 / > 0 controls the order. Undefined elements
    // always sort after non-undefined ones; missing slots after both. Uses
    // List<T>.Sort with a stable adapter so the spec-mandated stable sort holds
    // for v10+ (the spec made stability mandatory in ES2019 - .NET 5+ Sort is
    // already stable). Sorts in place and returns the receiver.
    private JsValue ArrayPrototypeSort(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var ownerHandle = ResolveObjectHandle(thisValue);
        var obj = _heap.GetObject(ownerHandle);
        var length = GetArrayLength(obj);
        var comparator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? args[0] : (JsValue?)null;

        if (comparator.HasValue && comparator.Value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError(
                "Array.prototype.sort: comparator must be a function or undefined."));
        }

        // Partition into present-values / undefined-values / holes per spec 23.1.3.30
        // step 3.b: SortIndexedProperties keeps undefined elements after sorted
        // non-undefined ones, and trailing holes after that.
        var present = new List<JsValue>(length);
        var undefinedCount = 0;
        var holeCount = 0;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                holeCount++;
                continue;
            }

            if (v.Tag == JsValueTag.Undefined)
            {
                undefinedCount++;
                continue;
            }

            present.Add(v);
        }

        if (comparator.HasValue)
        {
            var fn = comparator.Value;
            present.Sort((a, b) =>
            {
                var r = CallFunction(fn, new[] { a, b }, JsValue.Undefined);
                var n = ToNumber(r);
                if (double.IsNaN(n))
                {
                    return 0;
                }

                return n < 0 ? -1 : n > 0 ? 1 : 0;
            });
        }
        else
        {
            present.Sort((a, b) => string.CompareOrdinal(ToStringValue(a), ToStringValue(b)));
        }

        // Write back: present values first, then 'undefined' slots, then holes.
        for (var i = 0; i < present.Count; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, present[i]);
            if (present[i].Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(ownerHandle, present[i].AsObjectHandle());
            }
        }

        for (var i = 0; i < undefinedCount; i++)
        {
            var key = (present.Count + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = obj.SetProperty(key, JsValue.Undefined);
        }

        for (var i = 0; i < holeCount; i++)
        {
            var key = (present.Count + undefinedCount + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            obj.DeleteProperty(key);
        }

        return thisValue;
    }

    // ECMA-262 23.1.3.17 lastIndexOf. Strict equality, scans backwards from fromIndex
    // (default length-1, negative wraps from length). Returns -1 when not found.
    private JsValue ArrayPrototypeLastIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0 || args.Count == 0)
        {
            return JsValue.FromNumber(-1);
        }

        var target = args[0];
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : length - 1;
        if (fromIndex < 0)
        {
            fromIndex = length + fromIndex;
        }
        else if (fromIndex >= length)
        {
            fromIndex = length - 1;
        }

        for (var i = fromIndex; i >= 0; i--)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && AreStrictlyEqual(value, target))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.9 flat([depth]). Default depth is 1. Array elements are spread
    // up to the given depth; non-Array elements are kept as-is. Holes are skipped.
    private JsValue ArrayPrototypeFlat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var depth = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? Math.Max(0, (int)ToNumber(args[0]))
            : 1;
        var items = new List<JsValue>();
        FlattenInto(obj, thisValue, depth, items);
        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private void FlattenInto(JsObject source, JsValue receiver, int depth, List<JsValue> sink)
    {
        var length = GetArrayLength(source);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(source, receiver, key, out var element))
            {
                continue;   // skip holes per spec step 5.b.ii
            }

            if (depth > 0 && element.Tag == JsValueTag.Object &&
                _heap.GetObject(element.AsObjectHandle()) is ArrayObject child)
            {
                FlattenInto(child, element, depth - 1, sink);
            }
            else
            {
                sink.Add(element);
            }
        }
    }

    // ECMA-262 23.1.3.10a flatMap(callback[, thisArg]). Equivalent to map followed
    // by flat with depth 1, but produced in a single pass to avoid the intermediate
    // array allocation that the spec also avoids.
    private JsValue ArrayPrototypeFlatMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            var mapped = InvokeArrayCallback(callback, v, i, thisValue, thisArg);
            if (mapped.Tag == JsValueTag.Object &&
                _heap.GetObject(mapped.AsObjectHandle()) is ArrayObject inner)
            {
                FlattenInto(inner, mapped, depth: 0, items);   // append elements (1 level)
            }
            else
            {
                items.Add(mapped);
            }
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.6 every: returns true iff callback returns truthy for every
    // present element. Short-circuits on first falsy. Holes are skipped (vacuously
    // satisfied).
    private JsValue ArrayPrototypeEvery(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            if (!IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromBoolean(false);
            }
        }

        return JsValue.FromBoolean(true);
    }

    // ECMA-262 23.1.3.29 some: returns true iff callback returns truthy for at least
    // one present element. Short-circuits on first truthy.
    private JsValue ArrayPrototypeSome(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;
            }

            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromBoolean(true);
            }
        }

        return JsValue.FromBoolean(false);
    }

    // ECMA-262 23.1.3.10 find: returns the first element where callback is truthy,
    // or undefined. UNLIKE every/some/forEach/map/filter, find DOES visit holes (the
    // spec treats them as undefined-valued slots that the callback can match).
    private JsValue ArrayPrototypeFind(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    // ECMA-262 23.1.3.11 findIndex: as find, but returns the index (or -1). Visits
    // holes for the same reason find does.
    private JsValue ArrayPrototypeFindIndex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TryGetPropertyValue(obj, thisValue, key, out var v);
            if (IsTruthy(InvokeArrayCallback(callback, v, i, thisValue, thisArg)))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.24 / 23.1.3.25 Array.prototype.reduce / reduceRight.
    // Initial accumulator comes from args[1] when provided; otherwise the spec scans
    // for the first non-hole element and starts from there - throwing TypeError on
    // an empty array with no initial value. Forward scan for reduce, reverse for
    // reduceRight. Callback receives (accumulator, value, index, receiver).
    private JsValue ArrayPrototypeReduce(JsValue thisValue, IReadOnlyList<JsValue> args, bool reverse)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (callback.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Array reduce callback is not a function."));
        }

        var hasInitial = args.Count > 1;
        var accumulator = hasInitial ? args[1] : JsValue.Undefined;
        int start, step, end;
        if (!reverse)
        {
            start = 0;
            end = length;
            step = 1;
        }
        else
        {
            start = length - 1;
            end = -1;
            step = -1;
        }

        var i = start;
        if (!hasInitial)
        {
            var found = false;
            while (i != end)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TryGetPropertyValue(obj, thisValue, key, out var v))
                {
                    accumulator = v;
                    i += step;
                    found = true;
                    break;
                }

                i += step;
            }

            if (!found)
            {
                throw new JsThrownException(CreateTypeError(
                    reverse
                        ? "Reduce of empty array with no initial value (reduceRight)."
                        : "Reduce of empty array with no initial value."));
            }
        }

        while (i != end)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                var callArgs = new[]
                {
                    accumulator,
                    v,
                    JsValue.FromNumber(i),
                    thisValue,
                };
                accumulator = CallFunction(callback, callArgs, JsValue.Undefined);
            }

            i += step;
        }

        return accumulator;
    }

    // Shared callback-invoker for forEach/map/filter/find/etc. Calls
    // callback(value, index, receiverArray) with the given thisArg, sparing each
    // caller from repeating the args allocation and CallFunction routing.
    private JsValue InvokeArrayCallback(
        JsValue callback,
        JsValue value,
        int index,
        JsValue receiver,
        JsValue thisArg)
    {
        if (callback.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Array callback is not a function."));
        }

        var resolved = _heap.GetObject(callback.AsObjectHandle());
        if (resolved is not JsFunctionObject && resolved is not NativeFunctionObject)
        {
            throw new JsThrownException(CreateTypeError("Array callback is not a function."));
        }

        var args = new[] { value, JsValue.FromNumber(index), receiver };
        return CallFunction(callback, args, thisArg);
    }

    private JsValue ArrayPrototypeForEach(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;   // skip holes per spec
            }

            InvokeArrayCallback(callback, v, i, thisValue, thisArg);
        }

        return JsValue.Undefined;
    }

    private JsValue ArrayPrototypeMap(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>(length);
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                items.Add(JsValue.Undefined);   // spec: preserves length, holes become undefined-ish
                continue;
            }

            items.Add(InvokeArrayCallback(callback, v, i, thisValue, thisArg));
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue ArrayPrototypeFilter(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var callback = args.Count > 0 ? args[0] : JsValue.Undefined;
        var thisArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var items = new List<JsValue>();
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var v))
            {
                continue;   // skip holes
            }

            var keep = InvokeArrayCallback(callback, v, i, thisValue, thisArg);
            if (IsTruthy(keep))
            {
                items.Add(v);
            }
        }

        var arr = CreateArrayObject(items);
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    // ECMA-262 23.1.3.28 Array.prototype.slice(start, end). Returns a fresh
    // ArrayObject containing the half-open range [start, end). Out-of-range or
    // missing arguments degrade to "0, length"; negative arguments wrap.
    private JsValue ArrayPrototypeSlice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        var start = NormaliseSliceIndex(args, 0, 0, length);
        var end = NormaliseSliceIndex(args, 1, length, length);

        var items = new List<JsValue>();
        for (var i = start; i < end; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(TryGetPropertyValue(obj, thisValue, key, out var v) ? v : JsValue.Undefined);
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    // ECMA-262 23.1.3.2 Array.prototype.concat(...items). Returns a fresh
    // ArrayObject; Array arguments are spread (their elements appended one by one),
    // other values are appended as a single element.
    private JsValue ArrayPrototypeConcat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var items = new List<JsValue>();
        AppendConcatSource(items, thisValue);
        for (var i = 0; i < args.Count; i++)
        {
            AppendConcatSource(items, args[i]);
        }

        var arr = CreateArrayObject(items);
        var arrHandle = _heap.AllocateObject(arr, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private void AppendConcatSource(List<JsValue> items, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is ArrayObject)
            {
                var length = GetArrayLength(obj);
                for (var i = 0; i < length; i++)
                {
                    var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    items.Add(TryGetPropertyValue(obj, value, key, out var v) ? v : JsValue.Undefined);
                }

                return;
            }
        }

        items.Add(value);
    }

    private JsValue ArrayPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(JoinArrayElements(thisValue, ","));
    }

    // ECMA-262 23.1.3.18 Array.prototype.join. Default separator is ",". Undefined
    // and null elements stringify to the empty string; everything else goes through
    // the shared ToString conversion.
    private JsValue ArrayPrototypeJoin(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var separator = args.Count > 0 && args[0].Tag != JsValueTag.Undefined
            ? ToStringValue(args[0])
            : ",";
        return JsValue.FromString(JoinArrayElements(thisValue, separator));
    }

    private string JoinArrayElements(JsValue thisValue, string separator)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return string.Empty;
        }

        var values = new string[length];
        for (var i = 0; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGetPropertyValue(obj, thisValue, key, out var value) ||
                value.Tag is JsValueTag.Undefined or JsValueTag.Null)
            {
                values[i] = string.Empty;
            }
            else
            {
                values[i] = ToStringValue(value);
            }
        }

        return string.Join(separator, values);
    }

    // ECMA-262 23.1.3.16 indexOf - strict equality, starts at fromIndex (default 0,
    // negative wraps from length). Returns -1 when not found.
    private JsValue ArrayPrototypeIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0 || args.Count == 0)
        {
            return JsValue.FromNumber(-1);
        }

        var target = args[0];
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        if (fromIndex < 0)
        {
            fromIndex = Math.Max(0, length + fromIndex);
        }

        for (var i = fromIndex; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && AreStrictlyEqual(value, target))
            {
                return JsValue.FromNumber(i);
            }
        }

        return JsValue.FromNumber(-1);
    }

    // ECMA-262 23.1.3.14 includes - SameValueZero (NaN matches NaN; +0 matches -0).
    private JsValue ArrayPrototypeIncludes(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return JsValue.FromBoolean(false);
        }

        var target = args.Count > 0 ? args[0] : JsValue.Undefined;
        var fromIndex = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        if (fromIndex < 0)
        {
            fromIndex = Math.Max(0, length + fromIndex);
        }

        for (var i = fromIndex; i < length; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetPropertyValue(obj, thisValue, key, out var value) && SameValueZero(value, target))
            {
                return JsValue.FromBoolean(true);
            }
        }

        return JsValue.FromBoolean(false);
    }

    // ECMA-262 7.2.11 SameValueZero - identical to SameValue except +0 and -0 are
    // considered equal. Used by Array.prototype.includes, Map/Set keys, etc.
    private static bool SameValueZero(JsValue a, JsValue b)
    {
        if ((a.Tag == JsValueTag.Int32 || a.Tag == JsValueTag.Number) &&
            (b.Tag == JsValueTag.Int32 || b.Tag == JsValueTag.Number))
        {
            var an = a.AsNumber();
            var bn = b.AsNumber();
            if (double.IsNaN(an) && double.IsNaN(bn))
            {
                return true;
            }

            return an == bn;
        }

        return AreStrictlyEqual(a, b);
    }

    private int GetArrayLength(JsObject obj)
    {
        if (!obj.TryGetOwnProperty("length", out var descriptor))
        {
            return 0;
        }

        var number = ToNumber(descriptor.Value);
        if (double.IsNaN(number) || number <= 0)
        {
            return 0;
        }

        return checked((int)Math.Min(Math.Truncate(number), int.MaxValue));
    }

    private ObjectHandle EnsureBooleanPrototype()
    {
        _ = EnsureBooleanConstructor();
        return _booleanPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureBooleanConstructor()
    {
        if (_booleanConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new BooleanObject(false);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Boolean",
            (_, args) => JsValue.FromBoolean(args.Count > 0 && IsTruthy(args[0])),
            args => CreateBooleanObject(args.Count > 0 && IsTruthy(args[0])),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", BooleanPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", BooleanPrototypeValueOf);

        _booleanPrototypeHandle = prototypeHandle;
        _booleanConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue BooleanPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(BooleanThisValue(thisValue) ? "true" : "false");
    }

    private JsValue BooleanPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromBoolean(BooleanThisValue(thisValue));
    }

    private bool BooleanThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Boolean)
        {
            return thisValue.AsBoolean();
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is BooleanObject booleanObject)
        {
            return booleanObject.Value;
        }

        throw new JsThrownException(CreateTypeError("Boolean.prototype method called on incompatible receiver."));
    }

    private JsValue CreateBooleanObject(bool value)
    {
        var obj = new BooleanObject(value);
        obj.SetPrototype(EnsureBooleanPrototype());
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle EnsureNumberPrototype()
    {
        _ = EnsureNumberConstructor();
        return _numberPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureNumberConstructor()
    {
        if (_numberConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "Number",
            (_, args) => JsValue.FromNumber(args.Count > 0 ? ToNumber(args[0]) : 0d),
            args => CreateNumberObject(args.Count > 0 ? ToNumber(args[0]) : 0d),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        // ECMA-262 21.1.2 - Properties of the Number Constructor.
        _ = constructor.SetProperty("MAX_VALUE", JsValue.FromNumber(double.MaxValue));
        _ = constructor.SetProperty("MIN_VALUE", JsValue.FromNumber(double.Epsilon));
        _ = constructor.SetProperty("NaN", JsValue.FromNumber(double.NaN));
        _ = constructor.SetProperty("POSITIVE_INFINITY", JsValue.FromNumber(double.PositiveInfinity));
        _ = constructor.SetProperty("NEGATIVE_INFINITY", JsValue.FromNumber(double.NegativeInfinity));
        // 21.1.2.1 EPSILON - the difference between 1 and the smallest IEEE-754 double
        // strictly greater than 1, i.e. 2**-52.
        _ = constructor.SetProperty("EPSILON", JsValue.FromNumber(Math.Pow(2d, -52)));
        // 21.1.2.6 MAX_SAFE_INTEGER - 2**53 - 1, the largest integer n such that n and
        // n + 1 are both exactly representable as a Number.
        _ = constructor.SetProperty("MAX_SAFE_INTEGER", JsValue.FromNumber(9007199254740991d));
        // 21.1.2.8 MIN_SAFE_INTEGER - -(2**53 - 1).
        _ = constructor.SetProperty("MIN_SAFE_INTEGER", JsValue.FromNumber(-9007199254740991d));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        // ECMA-262 21.1.2.2 / 21.1.2.3 / 21.1.2.4 / 21.1.2.5 - the spec-defined
        // "isXxx" helpers. Unlike the global isFinite/isNaN, these do NOT coerce: a
        // non-Number argument simply returns false. The factoring below keeps each
        // helper a one-liner against ECMA-262.
        DefineNumberStatic(constructorHandle, constructor, "isFinite", static args
            => JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && !double.IsNaN(args[0].AsNumber()) && !double.IsInfinity(args[0].AsNumber())));
        DefineNumberStatic(constructorHandle, constructor, "isNaN", static args
            => JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && double.IsNaN(args[0].AsNumber())));
        DefineNumberStatic(constructorHandle, constructor, "isInteger", static args
            => JsValue.FromBoolean(IsIntegerNumber(args)));
        DefineNumberStatic(constructorHandle, constructor, "isSafeInteger", static args
            => JsValue.FromBoolean(IsIntegerNumber(args) && Math.Abs(args[0].AsNumber()) <= 9007199254740991d));

        // ECMA-262 21.1.2.13 / 21.1.2.14 - Number.parseInt and Number.parseFloat
        // are required to be the SAME function object as the global parseInt /
        // parseFloat. Install by reference using the cached handles.
        var parseIntHandle = EnsureParseIntFunction();
        constructor.DefineOwnProperty("parseInt", new JsPropertyDescriptor(
            JsValue.FromObject(parseIntHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(constructorHandle, parseIntHandle);

        var parseFloatHandle = EnsureParseFloatFunction();
        constructor.DefineOwnProperty("parseFloat", new JsPropertyDescriptor(
            JsValue.FromObject(parseFloatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(constructorHandle, parseFloatHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", NumberPrototypeToString);
        // ECMA-262 21.1.3.3 Number.prototype.toFixed(fractionDigits).
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toFixed", NumberPrototypeToFixed, length: 1);
        // ECMA-262 21.1.3.2 toExponential, 21.1.3.5 toPrecision.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toExponential", NumberPrototypeToExponential, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toPrecision", NumberPrototypeToPrecision, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", NumberPrototypeValueOf);

        _numberPrototypeHandle = prototypeHandle;
        _numberConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    // ECMA-262 19.2.5 parseInt(string, radix). Trims leading whitespace, accepts an
    // optional sign, an optional 0x/0X prefix when radix is 16 or 0, then consumes
    // digits valid for the radix until the first invalid character. Returns NaN when
    // no valid digit is read.
    private ObjectHandle EnsureParseIntFunction()
    {
        if (_parseIntHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("parseInt", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            var radixArg = args.Count > 1 ? args[1] : JsValue.Undefined;
            return JsValue.FromNumber(ParseIntegerLiteral(text, radixArg));
        }, length: 2);

        _parseIntHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_parseIntHandle.Value);
        return _parseIntHandle.Value;
    }

    // ECMA-262 19.2.4 parseFloat(string).
    private ObjectHandle EnsureParseFloatFunction()
    {
        if (_parseFloatHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("parseFloat", (_, args) =>
        {
            var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
            return JsValue.FromNumber(ParseFloatLiteral(text));
        }, length: 1);

        _parseFloatHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_parseFloatHandle.Value);
        return _parseFloatHandle.Value;
    }

    // ECMA-262 19.2.3 isNaN(number) - coerces, unlike Number.isNaN.
    private ObjectHandle EnsureIsNaNFunction()
    {
        if (_isNaNHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("isNaN", (_, args) =>
        {
            var n = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(double.IsNaN(n));
        }, length: 1);

        _isNaNHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_isNaNHandle.Value);
        return _isNaNHandle.Value;
    }

    // ECMA-262 19.2.2 isFinite(number) - coerces, unlike Number.isFinite.
    private ObjectHandle EnsureIsFiniteFunction()
    {
        if (_isFiniteHandle is { } existing)
        {
            return existing;
        }

        var fn = new NativeFunctionObject("isFinite", (_, args) =>
        {
            var n = args.Count > 0 ? ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(!double.IsNaN(n) && !double.IsInfinity(n));
        }, length: 1);

        _isFiniteHandle = _heap.AllocateObject(fn, AllocationSite.Current());
        _heap.PushRoot(_isFiniteHandle.Value);
        return _isFiniteHandle.Value;
    }

    private static double ParseIntegerLiteral(string text, JsValue radixArg)
    {
        var s = text.AsSpan().TrimStart();
        if (s.Length == 0)
        {
            return double.NaN;
        }

        var sign = 1;
        if (s[0] == '+')
        {
            s = s[1..];
        }
        else if (s[0] == '-')
        {
            sign = -1;
            s = s[1..];
        }

        var radix = 0;
        var stripPrefix = true;
        if (radixArg.Tag != JsValueTag.Undefined)
        {
            var r = (int)ToInt32(ParseNumberForCoerce(radixArg));
            if (r != 0)
            {
                if (r < 2 || r > 36)
                {
                    return double.NaN;
                }

                radix = r;
                stripPrefix = r == 16;
            }
        }

        if (stripPrefix && s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        {
            s = s[2..];
            if (radix == 0)
            {
                radix = 16;
            }
        }

        if (radix == 0)
        {
            radix = 10;
        }

        var consumed = 0;
        double result = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var digit = DigitValue(s[i]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            result = result * radix + digit;
            consumed++;
        }

        if (consumed == 0)
        {
            return double.NaN;
        }

        return sign * result;
    }

    private static int DigitValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'z')
        {
            return 10 + (c - 'a');
        }

        if (c >= 'A' && c <= 'Z')
        {
            return 10 + (c - 'A');
        }

        return -1;
    }

    private static double ParseFloatLiteral(string text)
    {
        var s = text.AsSpan().TrimStart().ToString();
        if (s.Length == 0)
        {
            return double.NaN;
        }

        if (s.StartsWith("Infinity", StringComparison.Ordinal))
        {
            return double.PositiveInfinity;
        }

        if (s.StartsWith("+Infinity", StringComparison.Ordinal))
        {
            return double.PositiveInfinity;
        }

        if (s.StartsWith("-Infinity", StringComparison.Ordinal))
        {
            return double.NegativeInfinity;
        }

        // Take the longest prefix that parses as a Number per StrNumericLiteral. Walk
        // back from the end; the spec calls for the longest matching prefix.
        for (var len = s.Length; len > 0; len--)
        {
            var prefix = s[..len];
            if (double.TryParse(prefix, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }

        return double.NaN;
    }

    private static double ParseNumberForCoerce(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.Null => 0d,
            JsValueTag.Undefined => double.NaN,
            JsValueTag.String => double.TryParse(value.AsString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
            _ => double.NaN,
        };
    }

    private static bool IsIntegerNumber(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Number)
        {
            return false;
        }

        var value = args[0].AsNumber();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        return Math.Floor(value) == value;
    }

    private void DefineNumberStatic(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<IReadOnlyList<JsValue>, JsValue> call)
    {
        var function = new NativeFunctionObject(name, (_, args) => call(args), length: 1);
        var functionHandle = _heap.AllocateObject(function, AllocationSite.Current());
        _ = owner.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(ownerHandle, functionHandle);
    }

    // ECMA-262 21.1.3.2 Number.prototype.toExponential(fractionDigits). The
    // canonical scientific form: <mantissa>e+<exp> or <mantissa>e-<exp>. NaN and
    // +-Infinity surface as their default ToString. fractionDigits must be in
    // [0, 100]; when omitted, fractional digits go as small as needed to render
    // the value uniquely (we approximate via Round-Trip "R" then re-format).
    private JsValue NumberPrototypeToExponential(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            // Round-trip then reformat to lowercase e per spec.
            return JsValue.FromString(NormaliseExponential(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var digits = (int)ToNumber(args[0]);
        if (digits < 0 || digits > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toExponential() digits argument must be between 0 and 100."));
        }

        var format = "0." + new string('0', digits) + "e+0";
        var raw = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        if (digits == 0)
        {
            // Trim trailing "." that the format string can leave behind on whole values.
            raw = raw.Replace(".e", "e", StringComparison.Ordinal);
        }

        return JsValue.FromString(NormaliseExponential(raw));
    }

    // Convert any "1.23E+05" / "1.23E5" style produced by .NET into the canonical
    // ECMA-262 form "1.23e+5" (lowercase e, explicit sign, no leading zeros on the
    // exponent).
    private static string NormaliseExponential(string text)
    {
        var eIdx = text.IndexOfAny(['e', 'E']);
        if (eIdx < 0)
        {
            return text;
        }

        var mantissa = text[..eIdx];
        var expPart = text[(eIdx + 1)..];
        var sign = "+";
        if (expPart.Length > 0 && (expPart[0] == '+' || expPart[0] == '-'))
        {
            sign = expPart[0] == '-' ? "-" : "+";
            expPart = expPart[1..];
        }

        expPart = expPart.TrimStart('0');
        if (expPart.Length == 0)
        {
            expPart = "0";
        }

        return mantissa + "e" + sign + expPart;
    }

    // ECMA-262 21.1.3.5 Number.prototype.toPrecision(precision). When precision is
    // undefined, behaves like toString. Otherwise renders the value with `precision`
    // significant digits, using fixed notation when |value| has |exp| < precision
    // and scientific otherwise (matching spec 21.1.3.5 step 10's choice).
    private JsValue NumberPrototypeToPrecision(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        var precision = (int)ToNumber(args[0]);
        if (precision < 1 || precision > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toPrecision() precision argument must be between 1 and 100."));
        }

        if (value == 0d)
        {
            return JsValue.FromString(precision == 1
                ? "0"
                : "0." + new string('0', precision - 1));
        }

        // "Gn" rounds to n significant digits without exponent unless necessary.
        // For spec parity with V8/SM ("0.0001" -> precision 1 -> "0.0001" stays;
        // very small or very large slip into scientific) we route through G then
        // normalise the exponent form when present.
        var formatted = value.ToString("G" + precision, System.Globalization.CultureInfo.InvariantCulture);
        return JsValue.FromString(NormaliseExponential(formatted));
    }

    // ECMA-262 21.1.3.3 Number.prototype.toFixed(fractionDigits). fractionDigits
    // must be in [0, 100]; NaN/Infinity return their default ToString; otherwise the
    // value is fixed-point formatted to exactly N digits after the decimal point.
    private JsValue NumberPrototypeToFixed(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        var digits = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (digits < 0 || digits > 100)
        {
            throw new JsThrownException(CreateRangeError(
                "toFixed() digits argument must be between 0 and 100."));
        }

        if (double.IsNaN(value))
        {
            return JsValue.FromString("NaN");
        }

        if (double.IsInfinity(value))
        {
            return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        }

        // Very large magnitudes fall back to the standard Number.toString output per
        // spec step 9 (when |value| >= 10^21).
        if (Math.Abs(value) >= 1e21)
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        return JsValue.FromString(value.ToString(
            "F" + digits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    // ECMA-262 21.1.3.6 Number.prototype.toString([radix]). Radix must be in [2, 36];
    // 10 (or undefined) routes through the standard decimal formatter; other radices
    // emit the integer portion in that base (and, for fractional values, an
    // approximation that matches V8/SM for finite cases). NaN / +-Infinity always
    // render in decimal regardless of radix.
    private JsValue NumberPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(thisValue);
        var radix = 10;
        if (args.Count > 0 && args[0].Tag != JsValueTag.Undefined)
        {
            radix = (int)ToNumber(args[0]);
        }

        if (radix < 2 || radix > 36)
        {
            throw new JsThrownException(CreateRangeError(
                "toString() radix must be an integer between 2 and 36."));
        }

        if (radix == 10 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return JsValue.FromString(FormatNumberForString(value));
        }

        return JsValue.FromString(FormatNumberInRadix(value, radix));
    }

    private static string FormatNumberInRadix(double value, int radix)
    {
        if (value == 0d)
        {
            return "0";
        }

        var negative = value < 0;
        if (negative)
        {
            value = -value;
        }

        var integerPart = Math.Floor(value);
        var fraction = value - integerPart;

        var intText = LongToRadixString((long)integerPart, radix);
        if (fraction == 0d)
        {
            return negative ? "-" + intText : intText;
        }

        var sb = new System.Text.StringBuilder(intText);
        sb.Append('.');

        // Emit up to ~52 digits of fractional precision - enough for any double.
        for (var i = 0; i < 52 && fraction != 0d; i++)
        {
            fraction *= radix;
            var digit = (int)Math.Floor(fraction);
            sb.Append(DigitToChar(digit));
            fraction -= digit;
        }

        var result = sb.ToString();
        return negative ? "-" + result : result;
    }

    private static string LongToRadixString(long n, int radix)
    {
        if (n == 0)
        {
            return "0";
        }

        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            sb.Insert(0, DigitToChar((int)(n % radix)));
            n /= radix;
        }

        return sb.ToString();
    }

    private static char DigitToChar(int digit)
    {
        return digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10);
    }

    private JsValue NumberPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromNumber(NumberThisValue(thisValue));
    }

    private double NumberThisValue(JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Int32 or JsValueTag.Number)
        {
            return ToNumber(thisValue);
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is NumberObject numberObject)
        {
            return numberObject.Value;
        }

        throw new JsThrownException(CreateTypeError("Number.prototype method called on incompatible receiver."));
    }

    private JsValue CreateNumberObject(double value)
    {
        var obj = new NumberObject(value);
        obj.SetPrototype(EnsureNumberPrototype());
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private ObjectHandle EnsureStringPrototype()
    {
        _ = EnsureStringConstructor();
        return _stringPrototypeHandle!.Value;
    }

    private ObjectHandle EnsureStringConstructor()
    {
        if (_stringConstructorHandle is { } existing)
        {
            return existing;
        }

        var prototype = new StringObject(string.Empty);
        prototype.SetPrototype(EnsureObjectPrototype());
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "String",
            (_, args) => JsValue.FromString(args.Count > 0 ? ToStringValue(args[0]) : string.Empty),
            args => CreateStringObject(args.Count > 0 ? ToStringValue(args[0]) : string.Empty),
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", StringPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", StringPrototypeValueOf);
        // ECMA-262 22.1.3 String.prototype methods. Each receives the receiver as
        // either a primitive string or a boxed StringObject via StringThisValue.
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "charAt", StringPrototypeCharAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "charCodeAt", StringPrototypeCharCodeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "codePointAt", StringPrototypeCodePointAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "at", StringPrototypeAt, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "indexOf", StringPrototypeIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "lastIndexOf", StringPrototypeLastIndexOf, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "includes", StringPrototypeIncludes, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "startsWith", StringPrototypeStartsWith, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "endsWith", StringPrototypeEndsWith, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "slice", StringPrototypeSlice, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "substring", StringPrototypeSubstring, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "substr", StringPrototypeSubstr, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "concat", StringPrototypeConcat, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "repeat", StringPrototypeRepeat, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "padStart", StringPrototypePadStart, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "padEnd", StringPrototypePadEnd, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trim", StringPrototypeTrim);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trimStart", StringPrototypeTrimStart);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "trimEnd", StringPrototypeTrimEnd);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toUpperCase", StringPrototypeToUpperCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLowerCase", StringPrototypeToLowerCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLocaleUpperCase", StringPrototypeToUpperCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toLocaleLowerCase", StringPrototypeToLowerCase);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "split", StringPrototypeSplit, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "replace", StringPrototypeReplace, length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "replaceAll", StringPrototypeReplaceAll, length: 2);

        // ECMA-262 22.1.2.1 String.fromCharCode(...codeUnits). Each argument is
        // truncated to a UTF-16 code unit (ToUint16) and concatenated. Surrogate
        // halves are kept as-is - String.fromCodePoint handles full code points.
        DefineIntrinsicFunction(constructorHandle, constructor, "fromCharCode", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var codeUnit = (char)(ushort)ToInt32(ToNumber(args[i]));
                sb.Append(codeUnit);
            }

            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // ECMA-262 22.1.2.2 String.fromCodePoint(...codePoints). Each argument must
        // be a non-negative integer <= 0x10FFFF; otherwise RangeError. Values
        // above 0xFFFF are encoded as a UTF-16 surrogate pair via char.ConvertFromUtf32.
        DefineIntrinsicFunction(constructorHandle, constructor, "fromCodePoint", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var n = ToNumber(args[i]);
                if (double.IsNaN(n) || n < 0 || n > 0x10FFFF || Math.Floor(n) != n)
                {
                    throw new JsThrownException(CreateRangeError(
                        "Invalid code point in String.fromCodePoint argument list."));
                }

                sb.Append(char.ConvertFromUtf32((int)n));
            }

            return JsValue.FromString(sb.ToString());
        }, length: 1);

        _stringPrototypeHandle = prototypeHandle;
        _stringConstructorHandle = constructorHandle;
        return constructorHandle;
    }

    private JsValue StringPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue));
    }

    private JsValue StringPrototypeValueOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue));
    }

    // Normalises an index argument the way most String.prototype methods do: undefined
    // -> defaultValue, NaN -> 0, then clamp into [0, length].
    private static int StringIndexArg(IReadOnlyList<JsValue> args, int idx, int defaultValue, int length)
    {
        if (idx >= args.Count || args[idx].Tag == JsValueTag.Undefined)
        {
            return Math.Clamp(defaultValue, 0, length);
        }

        var raw = args[idx].Tag == JsValueTag.Int32 ? args[idx].AsInt32() : (int)args[idx].AsNumber();
        return Math.Clamp(raw, 0, length);
    }

    // 22.1.3.1 charAt: returns the single-character string at the integer index, or
    // "" when out of range. Negative or fractional indices floor toward 0/Length-1.
    private JsValue StringPrototypeCharAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.FromString(string.Empty);
        }

        return JsValue.FromString(s[pos].ToString());
    }

    // 22.1.3.2 charCodeAt: UTF-16 code unit at index, or NaN out of range.
    private JsValue StringPrototypeCharCodeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.FromNumber(double.NaN);
        }

        return JsValue.FromNumber(s[pos]);
    }

    // 22.1.3.3 codePointAt: full code point (including surrogate pairs) at the index.
    private JsValue StringPrototypeCodePointAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var pos = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length)
        {
            return JsValue.Undefined;
        }

        var high = s[pos];
        if (char.IsHighSurrogate(high) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
        {
            return JsValue.FromNumber(char.ConvertToUtf32(high, s[pos + 1]));
        }

        return JsValue.FromNumber(high);
    }

    // 22.1.3.1a at: ES2022 negative-aware indexing; out-of-range returns undefined.
    private JsValue StringPrototypeAt(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var raw = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        var idx = raw < 0 ? s.Length + raw : raw;
        if (idx < 0 || idx >= s.Length)
        {
            return JsValue.Undefined;
        }

        return JsValue.FromString(s[idx].ToString());
    }

    // 22.1.3.8 indexOf - ordinal find, returns -1 when missing.
    private JsValue StringPrototypeIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? (int)ToNumber(args[1]) : 0;
        from = Math.Clamp(from, 0, s.Length);
        return JsValue.FromNumber(s.IndexOf(search, from, StringComparison.Ordinal));
    }

    // 22.1.3.10 lastIndexOf.
    private JsValue StringPrototypeLastIndexOf(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Min(s.Length, Math.Max(0, (int)ToNumber(args[1])) + search.Length)
            : s.Length;
        if (search.Length == 0)
        {
            return JsValue.FromNumber(from);
        }

        var slice = s[..from];
        return JsValue.FromNumber(slice.LastIndexOf(search, StringComparison.Ordinal));
    }

    // 22.1.3.7 includes / 22.1.3.22 startsWith / 22.1.3.7a endsWith.
    private JsValue StringPrototypeIncludes(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromBoolean(s.IndexOf(search, from, StringComparison.Ordinal) >= 0);
    }

    private JsValue StringPrototypeStartsWith(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length) : 0;
        if (from + search.Length > s.Length)
        {
            return JsValue.FromBoolean(false);
        }

        return JsValue.FromBoolean(s.AsSpan(from, search.Length).SequenceEqual(search.AsSpan()));
    }

    private JsValue StringPrototypeEndsWith(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var search = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        var endPos = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ToNumber(args[1]), 0, s.Length)
            : s.Length;
        var start = endPos - search.Length;
        if (start < 0)
        {
            return JsValue.FromBoolean(false);
        }

        return JsValue.FromBoolean(s.AsSpan(start, search.Length).SequenceEqual(search.AsSpan()));
    }

    // 22.1.3.20 slice - negative indices wrap; out-of-range clamps to length.
    private JsValue StringPrototypeSlice(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? WrapNegative((int)ToNumber(args[0]), len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? WrapNegative((int)ToNumber(args[1]), len)
            : len;
        return start >= end ? JsValue.FromString(string.Empty) : JsValue.FromString(s[start..end]);
    }

    // 22.1.3.23 substring - negative or NaN clamps to 0; swaps start/end so end<start
    // is treated as start<end.
    private JsValue StringPrototypeSubstring(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? Math.Clamp((int)ToNumber(args[0]), 0, len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ToNumber(args[1]), 0, len)
            : len;
        if (start > end)
        {
            (start, end) = (end, start);
        }

        return JsValue.FromString(s[start..end]);
    }

    // Annex B.2.2.1 substr(start, length) - legacy, but widely used.
    private JsValue StringPrototypeSubstr(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (start < 0)
        {
            start = Math.Max(0, len + start);
        }

        start = Math.Min(start, len);
        var count = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, Math.Min(len - start, (int)ToNumber(args[1])))
            : len - start;
        return JsValue.FromString(s.Substring(start, count));
    }

    // 22.1.3.4 concat - variadic string append.
    private JsValue StringPrototypeConcat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var sb = new System.Text.StringBuilder(StringThisValue(thisValue));
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append(ToStringValue(args[i]));
        }

        return JsValue.FromString(sb.ToString());
    }

    // 22.1.3.16 repeat - count must be non-negative integer-typed value < Infinity.
    private JsValue StringPrototypeRepeat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var n = args.Count > 0 ? ToNumber(args[0]) : 0;
        if (double.IsNaN(n) || n < 0 || double.IsInfinity(n))
        {
            throw new JsThrownException(CreateRangeError("Invalid repeat count."));
        }

        var count = (int)n;
        if (count == 0 || s.Length == 0)
        {
            return JsValue.FromString(string.Empty);
        }

        var sb = new System.Text.StringBuilder(s.Length * count);
        for (var i = 0; i < count; i++)
        {
            sb.Append(s);
        }

        return JsValue.FromString(sb.ToString());
    }

    // 22.1.3.15 / 22.1.3.14 padStart / padEnd.
    private JsValue StringPrototypePadStart(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var targetLen = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (targetLen <= s.Length)
        {
            return JsValue.FromString(s);
        }

        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ToStringValue(args[1]) : " ";
        if (pad.Length == 0)
        {
            return JsValue.FromString(s);
        }

        return JsValue.FromString(BuildPadding(pad, targetLen - s.Length) + s);
    }

    private JsValue StringPrototypePadEnd(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var targetLen = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
        if (targetLen <= s.Length)
        {
            return JsValue.FromString(s);
        }

        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ToStringValue(args[1]) : " ";
        if (pad.Length == 0)
        {
            return JsValue.FromString(s);
        }

        return JsValue.FromString(s + BuildPadding(pad, targetLen - s.Length));
    }

    private static string BuildPadding(string fill, int needed)
    {
        var sb = new System.Text.StringBuilder(needed);
        while (sb.Length < needed)
        {
            var remaining = needed - sb.Length;
            sb.Append(remaining >= fill.Length ? fill : fill[..remaining]);
        }

        return sb.ToString();
    }

    // 22.1.3.31 / .32 / .33 trim / trimStart / trimEnd. The spec defines the
    // WhiteSpace and LineTerminator productions; the BCL's char.IsWhiteSpace is a
    // close-enough superset for almost every spec character.
    private JsValue StringPrototypeTrim(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).Trim());
    }

    private JsValue StringPrototypeTrimStart(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).TrimStart());
    }

    private JsValue StringPrototypeTrimEnd(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).TrimEnd());
    }

    // 22.1.3.26 / .27 toUpperCase / toLowerCase use the invariant culture so output
    // is deterministic across host locales (the spec is locale-insensitive).
    private JsValue StringPrototypeToUpperCase(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).ToUpperInvariant());
    }

    private JsValue StringPrototypeToLowerCase(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(StringThisValue(thisValue).ToLowerInvariant());
    }

    // 22.1.3.21 split. RegExp separator is deferred; string separator is the common
    // case. Empty separator splits into individual characters per spec.
    private JsValue StringPrototypeSplit(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        var limit = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, (int)ToNumber(args[1]))
            : int.MaxValue;

        var items = new List<JsValue>();
        if (limit == 0)
        {
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            items.Add(JsValue.FromString(s));
            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        var sep = ToStringValue(args[0]);
        if (sep.Length == 0)
        {
            for (var i = 0; i < s.Length && items.Count < limit; i++)
            {
                items.Add(JsValue.FromString(s[i].ToString()));
            }

            return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
        }

        var start = 0;
        while (start <= s.Length && items.Count < limit)
        {
            var idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                items.Add(JsValue.FromString(s[start..]));
                break;
            }

            items.Add(JsValue.FromString(s[start..idx]));
            start = idx + sep.Length;
            if (start > s.Length)
            {
                break;
            }
        }

        return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(items), AllocationSite.Current()));
    }

    // 22.1.3.18 replace (string-search form). The single-replacement-only behaviour
    // matches the spec when the search value is a string. RegExp search is deferred.
    private JsValue StringPrototypeReplace(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        if (args.Count < 2)
        {
            return JsValue.FromString(s);
        }

        var search = ToStringValue(args[0]);
        var idx = s.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
        {
            return JsValue.FromString(s);
        }

        var replacement = ResolveStringReplacement(args[1], s, idx, search);
        return JsValue.FromString(string.Concat(s[..idx], replacement, s[(idx + search.Length)..]));
    }

    // 22.1.3.19 replaceAll (string-search form). Empty-string search throws TypeError
    // when the search value is a string per spec step 4.
    private JsValue StringPrototypeReplaceAll(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = StringThisValue(thisValue);
        if (args.Count < 2)
        {
            return JsValue.FromString(s);
        }

        var search = ToStringValue(args[0]);
        if (search.Length == 0)
        {
            // Spec 22.1.3.19 step 4 raises TypeError only for the empty-RegExp-without-
            // global-flag case; an empty string search inserts the replacement between
            // every code unit per "string search" semantics.
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                sb.Append(ResolveStringReplacement(args[1], s, i, search));
                sb.Append(s[i]);
            }

            sb.Append(ResolveStringReplacement(args[1], s, s.Length, search));
            return JsValue.FromString(sb.ToString());
        }

        var result = new System.Text.StringBuilder();
        var start = 0;
        while (true)
        {
            var idx = s.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                result.Append(s, start, s.Length - start);
                break;
            }

            result.Append(s, start, idx - start);
            result.Append(ResolveStringReplacement(args[1], s, idx, search));
            start = idx + search.Length;
        }

        return JsValue.FromString(result.ToString());
    }

    private string ResolveStringReplacement(JsValue replacementValue, string source, int matchStart, string matched)
    {
        if (replacementValue.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(replacementValue.AsObjectHandle());
            if (obj is JsFunctionObject || obj is NativeFunctionObject)
            {
                var result = CallFunction(replacementValue,
                    new[] { JsValue.FromString(matched), JsValue.FromNumber(matchStart), JsValue.FromString(source) },
                    JsValue.Undefined);
                return ToStringValue(result);
            }
        }

        // Spec 22.1.3.18.1 - $$, $&, $`, $' substitutions; numbered captures only apply
        // to RegExp matches, which this string-search path never produces.
        var template = ToStringValue(replacementValue);
        if (template.IndexOf('$') < 0)
        {
            return template;
        }

        var sb = new System.Text.StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '$' || i + 1 >= template.Length)
            {
                sb.Append(template[i]);
                continue;
            }

            var next = template[i + 1];
            switch (next)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(source, 0, matchStart); i++; break;
                case '\'': sb.Append(source, matchStart + matched.Length, source.Length - matchStart - matched.Length); i++; break;
                default: sb.Append(template[i]); break;
            }
        }

        return sb.ToString();
    }

    private static int WrapNegative(int raw, int length)
    {
        if (raw < 0)
        {
            return Math.Max(0, length + raw);
        }

        return Math.Min(raw, length);
    }

    private string StringThisValue(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.String)
        {
            return thisValue.AsString();
        }

        if (thisValue.Tag == JsValueTag.Object && _heap.GetObject(thisValue.AsObjectHandle()) is StringObject stringObject)
        {
            return stringObject.Value;
        }

        throw new JsThrownException(CreateTypeError("String.prototype method called on incompatible receiver."));
    }

    private JsValue CreateStringObject(string value)
    {
        var obj = new StringObject(value);
        obj.SetPrototype(EnsureStringPrototype());
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private static bool IsTruthy(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => false,
            JsValueTag.Null => false,
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => value.AsNumber() != 0 && !double.IsNaN(value.AsNumber()),
            JsValueTag.String => value.AsString().Length != 0,
            _ => true
        };
    }

    private bool AreEqual(JsValue left, JsValue right)
    {
        if (left.Tag == right.Tag)
        {
            return left.Tag switch
            {
                JsValueTag.Undefined => true,
                JsValueTag.Null => true,
                JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
                JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
                JsValueTag.Number => left.AsNumber() == right.AsNumber(),
                JsValueTag.String => left.AsString() == right.AsString(),
                JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
                JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
                _ => false
            };
        }

        if ((left.Tag == JsValueTag.Null && right.Tag == JsValueTag.Undefined) ||
            (left.Tag == JsValueTag.Undefined && right.Tag == JsValueTag.Null))
        {
            return true;
        }

        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return left.AsNumber() == right.AsNumber();
        }

        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) && right.Tag == JsValueTag.String)
        {
            return left.AsNumber() == ToNumberForEquality(right);
        }

        if (left.Tag == JsValueTag.String && (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return ToNumberForEquality(left) == right.AsNumber();
        }

        if (left.Tag == JsValueTag.Boolean)
        {
            return AreEqual(JsValue.FromNumber(left.AsBoolean() ? 1 : 0), right);
        }

        if (right.Tag == JsValueTag.Boolean)
        {
            return AreEqual(left, JsValue.FromNumber(right.AsBoolean() ? 1 : 0));
        }

        if (left.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(left, out var leftPrimitive))
        {
            return AreEqual(leftPrimitive, right);
        }

        if (right.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(right, out var rightPrimitive))
        {
            return AreEqual(left, rightPrimitive);
        }

        return false;
    }

    private bool TryGetObjectPrimitiveValue(JsValue value, out JsValue primitive)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            switch (obj)
            {
                case BooleanObject booleanObject:
                    primitive = JsValue.FromBoolean(booleanObject.Value);
                    return true;
                case NumberObject numberObject:
                    primitive = JsValue.FromNumber(numberObject.Value);
                    return true;
                case StringObject stringObject:
                    primitive = JsValue.FromString(stringObject.Value);
                    return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    // ECMA-262 7.2.10 SameValue. Distinguishes from AreStrictlyEqual on exactly two
    // points for numbers: SameValue(NaN, NaN) is true (strict eq is false) and
    // SameValue(+0, -0) is false (strict eq is true). All other tag combinations
    // delegate to strict equality.
    private static bool SameValue(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            var a = left.AsNumber();
            var b = right.AsNumber();
            if (double.IsNaN(a) && double.IsNaN(b))
            {
                return true;
            }

            if (a == 0d && b == 0d)
            {
                // +0 vs -0 - SameValue is false when sign bits differ.
                return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
            }

            return a == b;
        }

        return AreStrictlyEqual(left, right);
    }

    private static bool AreStrictlyEqual(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            return left.AsNumber() == right.AsNumber();
        }

        if (left.Tag != right.Tag)
        {
            return false;
        }

        return left.Tag switch
        {
            JsValueTag.Undefined => true,
            JsValueTag.Null => true,
            JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
            JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
            JsValueTag.Number => left.AsNumber() == right.AsNumber(),
            JsValueTag.String => left.AsString() == right.AsString(),
            JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
            JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
            _ => false
        };
    }

    private static double ToNumberForEquality(JsValue value)
    {
        if (value.Tag == JsValueTag.Int32 || value.Tag == JsValueTag.Number)
        {
            return value.AsNumber();
        }

        if (value.Tag == JsValueTag.String)
        {
            var text = value.AsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var trimmed = text.Trim();
            if (string.Equals(trimmed, "Infinity", StringComparison.Ordinal) ||
                string.Equals(trimmed, "+Infinity", StringComparison.Ordinal))
            {
                return double.PositiveInfinity;
            }

            if (string.Equals(trimmed, "-Infinity", StringComparison.Ordinal))
            {
                return double.NegativeInfinity;
            }

            if (trimmed.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
            {
                return double.NaN;
            }

            if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return double.NaN;
        }

        return double.NaN;
    }

    private JsObject ResolveObject(JsValue value)
    {
        return _heap.GetObject(ResolveObjectHandle(value));
    }

    private static ObjectHandle ResolveObjectHandle(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            throw new InvalidOperationException($"Expected object value, found {value.Tag}.");
        }

        return value.AsObjectHandle();
    }

    private JsValue CallFunction(JsValue value, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        var obj = ResolveObject(value);
        if (obj is JsFunctionObject fn)
        {
            return ExecuteInternal(fn.Function, args, fn.CapturedVariables, thisValue);
        }

        if (obj is NativeFunctionObject native)
        {
            return native.Call(thisValue, args);
        }

        throw new InvalidOperationException("Value is not callable.");
    }

    private JsValue ConstructFunction(JsValue value, IReadOnlyList<JsValue> args)
    {
        var obj = ResolveObject(value);
        if (obj is JsFunctionObject fn)
        {
            return ExecuteConstruct(fn, args);
        }

        if (obj is NativeFunctionObject native)
        {
            return native.Construct(args);
        }

        throw new InvalidOperationException("Value is not constructible.");
    }

    private void StoreCallResult(InterpreterFrame frame, int destinationRegister, JsValue callee, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        try
        {
            frame.Registers[destinationRegister] = CallFunction(callee, args, thisValue);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    private void StoreConstructResult(InterpreterFrame frame, int destinationRegister, JsValue constructor, IReadOnlyList<JsValue> args)
    {
        try
        {
            frame.Registers[destinationRegister] = ConstructFunction(constructor, args);
        }
        catch (JsThrownException ex)
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    [MayExecuteJs]
    private string ToPropertyKey(JsValue value)
    {
        var primitive = ToPrimitive(value, PrimitiveHint.String);
        return primitive.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.String => primitive.AsString(),
            JsValueTag.Number => FormatNumberForString(primitive.AsNumber()),
            JsValueTag.Int32 => primitive.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Boolean => primitive.AsBoolean() ? "true" : "false",
            _ => primitive.Tag.ToString()
        };
    }

    [MayExecuteJs]
    private JsValue ToPrimitive(JsValue value, PrimitiveHint hint)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        if (TryOrdinaryToPrimitive(value, hint, out var primitive))
        {
            return primitive;
        }

        if (TryGetObjectPrimitiveValue(value, out primitive))
        {
            return primitive;
        }

        throw new JsThrownException(CreateTypeError("Cannot convert object to primitive value."));
    }

    private bool TryOrdinaryToPrimitive(JsValue value, PrimitiveHint hint, out JsValue primitive)
    {
        var obj = _heap.GetObject(value.AsObjectHandle());
        var first = hint == PrimitiveHint.String ? "toString" : "valueOf";
        var second = hint == PrimitiveHint.String ? "valueOf" : "toString";
        return TryCallPrimitiveMethod(obj, value, first, out primitive) ||
               TryCallPrimitiveMethod(obj, value, second, out primitive);
    }

    private bool TryCallPrimitiveMethod(JsObject obj, JsValue thisValue, string name, out JsValue primitive)
    {
        if (TryGetPropertyValue(obj, thisValue, name, out var method) &&
            IsCallable(method))
        {
            var result = CallFunction(method, Array.Empty<JsValue>(), thisValue);
            if (result.Tag != JsValueTag.Object)
            {
                primitive = result;
                return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    private bool IsCallable(JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               _heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject;
    }

    private JsValue Add(JsValue left, JsValue right)
    {
        if (left.Tag is JsValueTag.String or JsValueTag.Object || right.Tag is JsValueTag.String or JsValueTag.Object)
        {
            return JsValue.FromString(ToStringValue(left) + ToStringValue(right));
        }

        return JsValue.FromNumber(ToNumber(left) + ToNumber(right));
    }

    private string ToStringValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            if (TryGetObjectPrimitiveValue(value, out var primitive))
            {
                return ToStringValue(primitive);
            }

            var obj = _heap.GetObject(value.AsObjectHandle());
            if (TryGetPropertyValue(obj, value, "toString", out var toString) &&
                toString.Tag == JsValueTag.Object &&
                _heap.GetObject(toString.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject)
            {
                var result = CallFunction(toString, Array.Empty<JsValue>(), value);
                if (result.Tag != JsValueTag.Object)
                {
                    return ToStringValue(result);
                }
            }

            return "[object Object]";
        }

        return FormatPrimitiveForString(value);
    }

    private static string FormatPrimitiveForString(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => FormatNumberForString(value.AsNumber()),
            JsValueTag.String => value.AsString(),
            JsValueTag.Symbol => "Symbol(" + (value.AsSymbolDescription() ?? string.Empty) + ")",
            JsValueTag.Object => "[object Object]",
            JsValueTag.HostObject => "[object Object]",
            _ => value.Tag.ToString()
        };
    }

    private static string FormatNumberForString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            return "0";
        }

        var absolute = Math.Abs(value);
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        if (text.Contains('E', StringComparison.Ordinal) || text.Contains('e', StringComparison.Ordinal))
        {
            if (absolute >= 1e-6 && absolute < 1e21)
            {
                return ExpandExponentialNumber(text);
            }

            return NormalizeExponentialNumber(text);
        }

        return TrimDecimalZeros(text);
    }

    private static string ExpandExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = normalized[..exponentMarker];
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var negative = coefficient.StartsWith("-", StringComparison.Ordinal);
        if (negative || coefficient.StartsWith("+", StringComparison.Ordinal))
        {
            coefficient = coefficient[1..];
        }

        var decimalIndex = coefficient.IndexOf('.', StringComparison.Ordinal);
        if (decimalIndex < 0)
        {
            decimalIndex = coefficient.Length;
        }

        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        var newDecimalIndex = decimalIndex + exponent;
        string expanded;
        if (newDecimalIndex <= 0)
        {
            expanded = "0." + new string('0', -newDecimalIndex) + digits;
        }
        else if (newDecimalIndex >= digits.Length)
        {
            expanded = digits + new string('0', newDecimalIndex - digits.Length);
        }
        else
        {
            expanded = digits[..newDecimalIndex] + "." + digits[newDecimalIndex..];
        }

        expanded = TrimDecimalZeros(expanded);
        return negative ? "-" + expanded : expanded;
    }

    private static string NormalizeExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = TrimDecimalZeros(normalized[..exponentMarker]);
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var sign = exponent >= 0 ? "+" : string.Empty;
        return $"{coefficient}e{sign}{exponent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string TrimDecimalZeros(string text)
    {
        if (!text.Contains(".", StringComparison.Ordinal))
        {
            return text;
        }

        return text.TrimEnd('0').TrimEnd('.');
    }

    private double ToNumber(JsValue value)
    {
        if (value.Tag == JsValueTag.Object && TryGetObjectPrimitiveValue(value, out var primitive))
        {
            return ToNumber(primitive);
        }

        return value.Tag switch
        {
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.Null => 0d,
            JsValueTag.Undefined => double.NaN,
            JsValueTag.String => ToNumberForEquality(value),
            _ => double.NaN
        };
    }

    private bool IsLessThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) < 0;
        }

        return ToNumber(left) < ToNumber(right);
    }

    private bool IsGreaterThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) > 0;
        }

        return ToNumber(left) > ToNumber(right);
    }

    private bool IsLessThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) <= 0;
        }

        return ToNumber(left) <= ToNumber(right);
    }

    private bool IsGreaterThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) >= 0;
        }

        return ToNumber(left) >= ToNumber(right);
    }

    private bool TryInstanceOf(InterpreterFrame frame, JsValue left, JsValue right, out bool result)
    {
        result = false;

        if (right.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' must be an object.");
            return false;
        }

        var ctorObj = ResolveObject(right);
        if (ctorObj is not JsFunctionObject && ctorObj is not NativeFunctionObject)
        {
            ThrowTypeError(frame, "Right-hand side of 'instanceof' is not callable.");
            return false;
        }

        if (left.Tag != JsValueTag.Object)
        {
            result = false;
            return true;
        }

        if (!TryGetPropertyValue(ctorObj, right, "prototype", out var prototypeValue) ||
            prototypeValue.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Function has non-object prototype in 'instanceof'.");
            return false;
        }

        var targetPrototype = prototypeValue.AsObjectHandle();
        var currentObj = ResolveObject(left);
        while (currentObj.PrototypeHandle is { } proto)
        {
            if (proto.Equals(targetPrototype))
            {
                result = true;
                return true;
            }

            currentObj = _heap.GetObject(proto);
        }

        result = false;
        return true;
    }

    private string TypeOfValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is JsFunctionObject or NativeFunctionObject)
            {
                return "function";
            }

            return "object";
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "object",
            JsValueTag.Boolean => "boolean",
            JsValueTag.Int32 => "number",
            JsValueTag.Number => "number",
            JsValueTag.String => "string",
            JsValueTag.Symbol => "symbol",
            JsValueTag.BigInt => "bigint",
            JsValueTag.HostObject => "object",
            _ => "undefined"
        };
    }

    private static IReadOnlyDictionary<string, JsVariableCell> CaptureFrameVariables(InterpreterFrame frame)
    {
        var snapshot = frame.CapturedVariables is null
            ? new Dictionary<string, JsVariableCell>(StringComparer.Ordinal)
            : new Dictionary<string, JsVariableCell>(frame.CapturedVariables, StringComparer.Ordinal);
        foreach (var kv in frame.Function.VariableSlots)
        {
            snapshot[kv.Key] = frame.Variables.GetCell(kv.Value);
        }

        return snapshot;
    }

    [MayExecuteJs]
    private JsValue ExecuteConstruct(JsFunctionObject callee, IReadOnlyList<JsValue> args)
    {
        var instanceObject = CreateOrdinaryObject();
        if (callee.TryGetProperty("prototype", h => _heap.GetObject(h), out var prototypeDescriptor) &&
            prototypeDescriptor.Value.Tag == JsValueTag.Object)
        {
            instanceObject.SetPrototype(prototypeDescriptor.Value.AsObjectHandle());
        }

        var defaultInstance = JsValue.FromObject(_heap.AllocateObject(instanceObject, AllocationSite.Current()));
        var result = ExecuteInternal(callee.Function, args, callee.CapturedVariables, defaultInstance);
        return result.Tag == JsValueTag.Object ? result : defaultInstance;
    }

    private enum PrimitiveHint
    {
        String,
        Number
    }

    private sealed class NativeFunctionObject : JsObject
    {
        private readonly Func<JsValue, IReadOnlyList<JsValue>, JsValue> _call;
        private readonly Func<IReadOnlyList<JsValue>, JsValue>? _construct;

        public NativeFunctionObject(
            string name,
            Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
            Func<IReadOnlyList<JsValue>, JsValue>? construct = null,
            int length = 0)
        {
            Name = name;
            _call = call;
            _construct = construct;
            _ = DefineOwnProperty(
                "name",
                new JsPropertyDescriptor(
                    JsValue.FromString(name),
                    Writable: false,
                    Enumerable: false,
                    Configurable: true));
            _ = DefineOwnProperty(
                "length",
                new JsPropertyDescriptor(
                    JsValue.FromNumber(length),
                    Writable: false,
                    Enumerable: false,
                    Configurable: true));
        }

        public string Name { get; }

        public JsValue Call(JsValue thisValue, IReadOnlyList<JsValue> args) => _call(thisValue, args);

        public JsValue Construct(IReadOnlyList<JsValue> args)
        {
            if (_construct is null)
            {
                throw new InvalidOperationException("Value is not constructible.");
            }

            return _construct(args);
        }
    }

    private sealed class ArrayObject : JsObject
    {
    }

    private sealed class BooleanObject : JsObject
    {
        public BooleanObject(bool value)
        {
            Value = value;
        }

        public bool Value { get; }
    }

    private sealed class NumberObject : JsObject
    {
        public NumberObject(double value)
        {
            Value = value;
        }

        public double Value { get; }
    }

    private sealed class StringObject : JsObject
    {
        public StringObject(string value)
        {
            Value = value;
        }

        public string Value { get; }
    }

    private sealed class DateObject : JsObject
    {
        public DateObject(double timeValue)
        {
            TimeValue = timeValue;
        }

        public double TimeValue { get; }
    }

    private sealed class RegExpObject : JsObject
    {
        public RegExpObject(string pattern, string flags, Regex regex)
        {
            Pattern = pattern;
            Flags = flags;
            Regex = regex;
        }

        public string Pattern { get; }

        public string Flags { get; }

        public Regex Regex { get; }
    }

    private sealed class ForOfIteratorObject : JsObject
    {
        private readonly IReadOnlyList<JsValue> _values;
        private int _index;

        public ForOfIteratorObject(IReadOnlyList<JsValue> values)
        {
            _values = values;
        }

        public bool TryMoveNext(out JsValue value)
        {
            if (_index >= _values.Count)
            {
                value = JsValue.Undefined;
                return false;
            }

            value = _values[_index++];
            return true;
        }
    }

    private sealed class ForInIteratorObject : JsObject
    {
        private readonly IReadOnlyList<string> _keys;
        private int _index;

        public ForInIteratorObject(IReadOnlyList<string> keys)
        {
            _keys = keys;
        }

        public bool TryMoveNext(out string key)
        {
            if (_index >= _keys.Count)
            {
                key = string.Empty;
                return false;
            }

            key = _keys[_index++];
            return true;
        }
    }
}
