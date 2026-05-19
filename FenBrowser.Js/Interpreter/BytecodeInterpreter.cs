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

    public JsValue Execute(BytecodeFunction function)
    {
        var frame = new InterpreterFrame(function);

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
                    var obj = ResolveObject(frame.Registers[ins.A]);
                    var prop = function.PropertyNames[ins.B];
                    _ = obj.SetProperty(prop, frame.Registers[ins.C]);
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
                    var obj = ResolveObject(frame.Registers[ins.A]);
                    var key = ToPropertyKey(frame.Registers[ins.B]);
                    _ = obj.SetProperty(key, frame.Registers[ins.C]);
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
                case OpCode.Add:
                    frame.Registers[ins.A] = JsValue.FromNumber(frame.Registers[ins.B].AsNumber() + frame.Registers[ins.C].AsNumber());
                    break;
                case OpCode.Sub:
                    frame.Registers[ins.A] = JsValue.FromNumber(frame.Registers[ins.B].AsNumber() - frame.Registers[ins.C].AsNumber());
                    break;
                case OpCode.Mul:
                    frame.Registers[ins.A] = JsValue.FromNumber(frame.Registers[ins.B].AsNumber() * frame.Registers[ins.C].AsNumber());
                    break;
                case OpCode.Div:
                    frame.Registers[ins.A] = JsValue.FromNumber(frame.Registers[ins.B].AsNumber() / frame.Registers[ins.C].AsNumber());
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
            _ => true
        };
    }

    private JsObject ResolveObject(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            throw new InvalidOperationException($"Expected object value, found {value.Tag}.");
        }

        return _heap.GetObject(value.AsObjectHandle());
    }

    private static string ToPropertyKey(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Number => value.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            _ => value.Tag.ToString()
        };
    }
}
