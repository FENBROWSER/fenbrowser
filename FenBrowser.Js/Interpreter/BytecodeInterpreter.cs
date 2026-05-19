using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class BytecodeInterpreter
{
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
}
