using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class BytecodeInterpreter
{
    private readonly JsHeap _heap;

    public BytecodeInterpreter(JsHeap? heap = null)
    {
        _heap = heap ?? new JsHeap();
    }

    [MayExecuteJs]
    public JsValue Execute(BytecodeFunction function)
    {
        return ExecuteInternal(function, Array.Empty<JsValue>(), null);
    }

    [MayExecuteJs]
    private JsValue ExecuteInternal(
        BytecodeFunction function,
        IReadOnlyList<JsValue> args,
        IReadOnlyDictionary<string, JsValue>? capturedVariables)
    {
        var frame = new InterpreterFrame(function);
        if (capturedVariables is not null)
        {
            foreach (var kv in capturedVariables)
            {
                if (function.VariableSlots.TryGetValue(kv.Key, out var slot))
                {
                    frame.Variables[slot] = kv.Value;
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
                case OpCode.StoreVar:
                    frame.Variables[ins.B] = frame.Registers[ins.A];
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
                    if (frame.ExceptionHandlers.Count > 0)
                    {
                        var handlerIp = frame.ExceptionHandlers.Pop();
                        frame.Registers[0] = frame.Registers[ins.A];
                        frame.InstructionPointer = handlerIp;
                    }
                    else
                    {
                        throw new JsThrownException(frame.Registers[ins.A]);
                    }

                    break;
                case OpCode.NewObject:
                {
                    var handle = _heap.AllocateObject(new JsObject(), AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.NewArray:
                {
                    var obj = new JsObject();
                    obj.SetProperty("length", JsValue.FromNumber(0));
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
                    var handle = _heap.AllocateObject(fnObj, AllocationSite.Current());
                    frame.Registers[ins.A] = JsValue.FromObject(handle);
                    break;
                }
                case OpCode.Call0:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    frame.Registers[ins.A] = ExecuteInternal(callee.Function, Array.Empty<JsValue>(), callee.CapturedVariables);
                    break;
                }
                case OpCode.Call1:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    frame.Registers[ins.A] = ExecuteInternal(callee.Function, new[] { frame.Registers[ins.C] }, callee.CapturedVariables);
                    break;
                }
                case OpCode.CallN:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    var callArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        callArgs[i] = frame.Registers[ins.C + i];
                    }

                    frame.Registers[ins.A] = ExecuteInternal(callee.Function, callArgs, callee.CapturedVariables);
                    break;
                }
                case OpCode.Construct0:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    frame.Registers[ins.A] = ExecuteConstruct(callee, Array.Empty<JsValue>());
                    break;
                }
                case OpCode.Construct1:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    frame.Registers[ins.A] = ExecuteConstruct(callee, new[] { frame.Registers[ins.C] });
                    break;
                }
                case OpCode.ConstructN:
                {
                    var callee = ResolveFunction(frame.Registers[ins.B]);
                    var ctorArgs = new JsValue[ins.D];
                    for (var i = 0; i < ins.D; i++)
                    {
                        ctorArgs[i] = frame.Registers[ins.C + i];
                    }

                    frame.Registers[ins.A] = ExecuteConstruct(callee, ctorArgs);
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

    private static bool AreEqual(JsValue left, JsValue right)
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

    private JsFunctionObject ResolveFunction(JsValue value)
    {
        var obj = ResolveObject(value);
        if (obj is not JsFunctionObject fn)
        {
            throw new InvalidOperationException("Value is not callable.");
        }

        return fn;
    }

    private static string ToPropertyKey(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.String => value.AsString(),
            JsValueTag.Number => value.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            _ => value.Tag.ToString()
        };
    }

    private static JsValue Add(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String || right.Tag == JsValueTag.String)
        {
            return JsValue.FromString(ToStringForConcat(left) + ToStringForConcat(right));
        }

        return JsValue.FromNumber(ToNumber(left) + ToNumber(right));
    }

    private static string ToStringForConcat(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => value.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.String => value.AsString(),
            JsValueTag.Object => "[object Object]",
            JsValueTag.HostObject => "[object Object]",
            _ => value.Tag.ToString()
        };
    }

    private static double ToNumber(JsValue value)
    {
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

    private static bool IsLessThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) < 0;
        }

        return ToNumber(left) < ToNumber(right);
    }

    private static bool IsGreaterThan(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) > 0;
        }

        return ToNumber(left) > ToNumber(right);
    }

    private static bool IsLessThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) <= 0;
        }

        return ToNumber(left) <= ToNumber(right);
    }

    private static bool IsGreaterThanOrEqual(JsValue left, JsValue right)
    {
        if (left.Tag == JsValueTag.String && right.Tag == JsValueTag.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString()) >= 0;
        }

        return ToNumber(left) >= ToNumber(right);
    }

    private string TypeOfValue(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(value.AsObjectHandle());
            if (obj is JsFunctionObject)
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

    private static IReadOnlyDictionary<string, JsValue> CaptureFrameVariables(BytecodeFunction function, InterpreterFrame frame)
    {
        var snapshot = new Dictionary<string, JsValue>(StringComparer.Ordinal);
        foreach (var kv in function.VariableSlots)
        {
            snapshot[kv.Key] = frame.Variables[kv.Value];
        }

        return snapshot;
    }

    [MayExecuteJs]
    private JsValue ExecuteConstruct(JsFunctionObject callee, IReadOnlyList<JsValue> args)
    {
        var defaultInstance = JsValue.FromObject(_heap.AllocateObject(new JsObject(), AllocationSite.Current()));
        var result = ExecuteInternal(callee.Function, args, callee.CapturedVariables);
        return result.Tag == JsValueTag.Object ? result : defaultInstance;
    }
}
