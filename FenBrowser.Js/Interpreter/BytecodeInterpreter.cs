using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

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
    private ObjectHandle? _functionCallMethodHandle;
    private ObjectHandle? _evalFunctionHandle;
    private ObjectHandle? _globalObjectHandle;
    private ObjectHandle? _dateConstructorHandle;
    private ObjectHandle? _datePrototypeHandle;

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
        var frame = new InterpreterFrame(function, thisValue);
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

        while (frame.InstructionPointer < function.Instructions.Count)
        {
            var ins = function.Instructions[frame.InstructionPointer++];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    frame.Registers[ins.A] = function.Constants[ins.B];
                    break;
                case OpCode.LoadVar:
                    frame.Registers[ins.A] = frame.Variables[ins.B];
                    break;
                case OpCode.LoadThis:
                    frame.Registers[ins.A] = frame.ThisValue;
                    break;
                case OpCode.StoreVar:
                    frame.Variables[ins.B] = frame.Registers[ins.A];
                    SyncGlobalVariable(frame, ins.B, frame.Registers[ins.A]);
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
                    _ = obj.SetProperty(prop, value);
                    if (value.Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
                    }

                    break;
                }
                case OpCode.GetPropByName:
                {
                    var obj = ResolveObject(frame.Registers[ins.B]);
                    var prop = function.PropertyNames[ins.C];
                    if (obj.TryGetProperty(prop, h => _heap.GetObject(h), out var descriptor))
                    {
                        frame.Registers[ins.A] = descriptor.Value;
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
                    _ = obj.SetProperty(key, value);
                    if (value.Tag == JsValueTag.Object)
                    {
                        _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
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
                case OpCode.GetElem:
                {
                    var obj = ResolveObject(frame.Registers[ins.B]);
                    var key = ToPropertyKey(frame.Registers[ins.C]);
                    if (obj.TryGetProperty(key, h => _heap.GetObject(h), out var descriptor))
                    {
                        frame.Registers[ins.A] = descriptor.Value;
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
                    var captured = CaptureFrameVariables(function, frame);
                    var fnObj = new JsFunctionObject(nested, captured);
                    var prototypeHandle = _heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current());
                    _ = fnObj.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
                    var handle = _heap.AllocateObject(fnObj, AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
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

        if (function.VariableSlots.TryGetValue("Date", out var dateSlot))
        {
            frame.Variables[dateSlot] = JsValue.FromObject(EnsureDateConstructor());
        }
    }

    private JsObject CreateOrdinaryObject()
    {
        var obj = new JsObject();
        obj.SetPrototype(EnsureObjectPrototype());
        return obj;
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
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
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

        var prototype = _heap.GetObject(prototypeHandle);
        _ = prototype.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "toLocaleString", (thisValue, _) => ObjectPrototypeToString(thisValue));
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "hasOwnProperty", ObjectPrototypeHasOwnProperty, length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "isPrototypeOf", ObjectPrototypeIsPrototypeOf, length: 1);

        _objectPrototypeHandle = prototypeHandle;
        _objectConstructorHandle = constructorHandle;
        return constructorHandle;
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
        _ = prototype.SetProperty(name, JsValue.FromObject(functionHandle));
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

    private string GetObjectToStringTag(ObjectHandle handle)
    {
        return _heap.GetObject(handle) switch
        {
            ArrayObject => "Array",
            BooleanObject => "Boolean",
            DateObject => "Date",
            NumberObject => "Number",
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
        var hasValue = descriptorObject.TryGetProperty("value", h => _heap.GetObject(h), out var valueDescriptor);
        var hasWritable = descriptorObject.TryGetProperty("writable", h => _heap.GetObject(h), out _);
        var hasGetter = descriptorObject.TryGetProperty("get", h => _heap.GetObject(h), out var getterDescriptor);
        var hasSetter = descriptorObject.TryGetProperty("set", h => _heap.GetObject(h), out var setterDescriptor);
        if ((hasGetter || hasSetter) && (hasValue || hasWritable))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor cannot mix accessor and data fields."));
        }

        if (hasGetter &&
            getterDescriptor.Value.Tag != JsValueTag.Undefined &&
            !IsCallable(getterDescriptor.Value))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor getter must be callable or undefined."));
        }

        if (hasSetter &&
            setterDescriptor.Value.Tag != JsValueTag.Undefined &&
            !IsCallable(setterDescriptor.Value))
        {
            throw new JsThrownException(CreateTypeError("Property descriptor setter must be callable or undefined."));
        }

        var value = hasValue ? valueDescriptor.Value : JsValue.Undefined;
        var writable = ReadDescriptorFlag(descriptorObject, "writable");
        var enumerable = ReadDescriptorFlag(descriptorObject, "enumerable");
        var configurable = ReadDescriptorFlag(descriptorObject, "configurable");

        _ = target.DefineOwnProperty(key, new JsPropertyDescriptor(value, writable, enumerable, configurable));
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, value.AsObjectHandle());
        }

        return args[0];
    }

    private bool ReadDescriptorFlag(JsObject descriptorObject, string propertyName)
    {
        return descriptorObject.TryGetProperty(propertyName, h => _heap.GetObject(h), out var descriptor) &&
               IsTruthy(descriptor.Value);
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
            if (!obj.TryGetProperty(key, h => _heap.GetObject(h), out var descriptor) ||
                descriptor.Value.Tag is JsValueTag.Undefined or JsValueTag.Null)
            {
                values[i] = string.Empty;
            }
            else
            {
                values[i] = ToStringValue(descriptor.Value);
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
        _ = constructor.SetProperty("MAX_VALUE", JsValue.FromNumber(double.MaxValue));
        _ = constructor.SetProperty("MIN_VALUE", JsValue.FromNumber(double.Epsilon));
        _ = constructor.SetProperty("NaN", JsValue.FromNumber(double.NaN));
        _ = constructor.SetProperty("POSITIVE_INFINITY", JsValue.FromNumber(double.PositiveInfinity));
        _ = constructor.SetProperty("NEGATIVE_INFINITY", JsValue.FromNumber(double.NegativeInfinity));
        var constructorHandle = _heap.AllocateObject(constructor, AllocationSite.Current());
        _heap.PushRoot(constructorHandle);

        var prototypeObject = _heap.GetObject(prototypeHandle);
        _ = prototypeObject.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        _heap.WriteBarrier(prototypeHandle, constructorHandle);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "toString", NumberPrototypeToString);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototypeObject, "valueOf", NumberPrototypeValueOf);

        _numberPrototypeHandle = prototypeHandle;
        _numberConstructorHandle = constructorHandle;
        return constructorHandle;
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
        if (obj.TryGetProperty(name, h => _heap.GetObject(h), out var descriptor) &&
            IsCallable(descriptor.Value))
        {
            var result = CallFunction(descriptor.Value, Array.Empty<JsValue>(), thisValue);
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
            if (obj.TryGetProperty("toString", h => _heap.GetObject(h), out var toStringDescriptor) &&
                toStringDescriptor.Value.Tag == JsValueTag.Object &&
                _heap.GetObject(toStringDescriptor.Value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject)
            {
                var result = CallFunction(toStringDescriptor.Value, Array.Empty<JsValue>(), value);
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

        if (!ctorObj.TryGetProperty("prototype", h => _heap.GetObject(h), out var prototypeDescriptor) ||
            prototypeDescriptor.Value.Tag != JsValueTag.Object)
        {
            ThrowTypeError(frame, "Function has non-object prototype in 'instanceof'.");
            return false;
        }

        var targetPrototype = prototypeDescriptor.Value.AsObjectHandle();
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

    private static IReadOnlyDictionary<string, JsVariableCell> CaptureFrameVariables(BytecodeFunction function, InterpreterFrame frame)
    {
        var snapshot = new Dictionary<string, JsVariableCell>(StringComparer.Ordinal);
        foreach (var kv in function.VariableSlots)
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
}
