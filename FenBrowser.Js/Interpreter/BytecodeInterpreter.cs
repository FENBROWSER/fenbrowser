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
    private ObjectHandle? _globalObjectHandle;
    private ObjectHandle? _dateConstructorHandle;
    private ObjectHandle? _datePrototypeHandle;
    private ObjectHandle? _mathObjectHandle;
    private ObjectHandle? _regexpConstructorHandle;
    private ObjectHandle? _regexpPrototypeHandle;
    private ObjectHandle? _jsonObjectHandle;

    public BytecodeInterpreter(JsHeap? heap = null)
    {
        _heap = heap ?? new JsHeap();
    }

    [MayExecuteJs]
    public JsValue Execute(BytecodeFunction function)
    {
        return ExecuteInternal(function, Array.Empty<JsValue>(), null, JsValue.FromObject(EnsureGlobalObject()));
    }

    [MayExecuteJs]
    private JsValue ExecuteInternal(
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
                    var obj = ResolveObject(receiver);
                    var prop = function.PropertyNames[ins.C];
                    if (TryGetPropertyValue(obj, receiver, prop, out var value))
                    {
                        frame.Registers[ins.A] = value;
                    }
                    else
                    {
                        frame.Registers[ins.A] = JsValue.Undefined;
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
                    var key = ToPropertyKey(frame.Registers[ins.B]);
                    var value = frame.Registers[ins.C];
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
                    var obj = ResolveObject(receiver);
                    var key = ToPropertyKey(frame.Registers[ins.C]);
                    if (TryGetPropertyValue(obj, receiver, key, out var value))
                    {
                        frame.Registers[ins.A] = value;
                    }
                    else
                    {
                        frame.Registers[ins.A] = JsValue.Undefined;
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

        if (function.VariableSlots.TryGetValue("eval", out var evalSlot))
        {
            frame.Variables[evalSlot] = JsValue.FromObject(EnsureEvalFunction());
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

        _errorPrototypeHandle = prototypeHandle;
        _errorConstructorHandle = constructorHandle;
        return constructorHandle;
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

    private JsValue JsonParse(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        var text = args.Count > 0 ? ToStringValue(args[0]) : "undefined";
        try
        {
            using var document = JsonDocument.Parse(text);
            return ConvertJsonElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new JsThrownException(CreateSyntaxError(ex.Message));
        }
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
        var descriptorHandle = _heap.AllocateObject(descriptorObject, AllocationSite.Current());
        WriteDescriptorBarrier(descriptorHandle, descriptor);
        return JsValue.FromObject(descriptorHandle);
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

    private JsValue ArrayPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var obj = ResolveObject(thisValue);
        var length = GetArrayLength(obj);
        if (length == 0)
        {
            return JsValue.FromString(string.Empty);
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

        return JsValue.FromString(string.Join(",", values));
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

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", NumberPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", NumberPrototypeValueOf);

        _numberPrototypeHandle = prototypeHandle;
        _numberConstructorHandle = constructorHandle;
        return constructorHandle;
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

    private JsValue NumberPrototypeToString(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromString(FormatNumberForString(NumberThisValue(thisValue)));
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
