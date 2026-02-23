using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Types;
using FenValue = FenBrowser.FenEngine.Core.FenValue;

namespace FenBrowser.FenEngine.Core.Bytecode.VM
{
    /// <summary>
    /// The core execution engine for FenBrowser's compiled Javascript bytecode.
    /// Eliminates recursive AST evaluation in favor of a flat, stack-based opcode loop.
    /// </summary>
    public class VirtualMachine
    {
        // Fixed-size fast heap for operands (prevents boxing and allocation in hot loop)
        private const int STACK_SIZE = 16384;
        private readonly FenValue[] _stack = new FenValue[STACK_SIZE];
        private int _sp = 0; // Stack pointer

        // Call stack managed entirely on the heap to prevent .NET StackOverflowException
        private readonly Stack<CallFrame> _callFrames = new Stack<CallFrame>(256);

        public VirtualMachine()
        {
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv)
        {
            _sp = 0;
            _callFrames.Clear();
            
            // Push initial frame
            var frame = new CallFrame(initialBlock, initialEnv, 0);
            _callFrames.Push(frame);

            return RunLoop();
        }

        private FenValue RunLoop()
        {
            try
            {
                while (_callFrames.Count > 0)
                {
                    var frame = _callFrames.Peek();
                    var instructions = frame.Block.Instructions;
                    var constants = frame.Block.Constants;
                    
                    while (frame.IP < instructions.Length)
                    {
                        OpCode op = (OpCode)instructions[frame.IP++];
                        
                        switch (op)
                        {
                            case OpCode.LoadConst:
                            {
                                int constIndex = ReadInt32(instructions, ref frame);
                                _stack[_sp++] = constants[constIndex];
                                break;
                            }
                            case OpCode.LoadNull:
                                _stack[_sp++] = FenValue.Null;
                                break;
                            case OpCode.LoadUndefined:
                                _stack[_sp++] = FenValue.Undefined;
                                break;
                            case OpCode.LoadTrue:
                                _stack[_sp++] = FenValue.FromBoolean(true);
                                break;
                            case OpCode.LoadFalse:
                                _stack[_sp++] = FenValue.FromBoolean(false);
                                break;
                            case OpCode.Add:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = ExecuteAdd(left, right);
                                break;
                            }
                            case OpCode.Subtract:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left - right);
                                break;
                            }
                            case OpCode.Multiply:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left * right);
                                break;
                            }
                            case OpCode.Equal:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(left.LooseEquals(right));
                                break;
                            }
                            case OpCode.StrictEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(left.StrictEquals(right));
                                break;
                            }
                            case OpCode.LessThan:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(left, right, null, true);
                                _stack[_sp++] = FenValue.FromBoolean(result.ToBoolean());
                                break;
                            }
                            case OpCode.GreaterThan:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(right, left, null, false);
                                _stack[_sp++] = FenValue.FromBoolean(result.ToBoolean());
                                break;
                            }
                            case OpCode.Jump:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                frame.IP = offset;
                                break;
                            }
                            case OpCode.JumpIfFalse:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                var condition = _stack[--_sp];
                                if (!condition.ToBoolean())
                                {
                                    frame.IP = offset;
                                }
                                break;
                            }
                            case OpCode.JumpIfTrue:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                var condition = _stack[--_sp];
                                if (condition.ToBoolean())
                                {
                                    frame.IP = offset;
                                }
                                break;
                            }
                            case OpCode.LoadVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = constants[nameIndex].AsString();
                                var value = frame.Environment.Get(varName);
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.StoreVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = constants[nameIndex].AsString();
                                var value = _stack[--_sp];
                                frame.Environment.Set(varName, value);
                                // Assignment leaves value on stack
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.Return:
                            {
                                var result = _stack[--_sp];
                                _callFrames.Pop();
                                _sp = frame.StackBase;
                                
                                // Push result back for caller, or return if top level
                                if (_callFrames.Count > 0)
                                {
                                    _stack[_sp++] = result;
                                    break; // break the inner loop to fetch the next frame in the while
                                }
                                else
                                {
                                    return result;
                                }
                            }
                            case OpCode.Halt:
                                return _sp > 0 ? _stack[--_sp] : FenValue.Undefined;
                                
                            default:
                                throw new Exception($"VM Error: Unhandled OpCode {op} at IP {frame.IP - 1}");
                        }
                    }
                    
                    // Reached end of instructions without a return
                    if (_callFrames.Count > 0 && _callFrames.Peek() == frame)
                    {
                        var result = _sp > 0 ? _stack[--_sp] : FenValue.Undefined;
                        _callFrames.Pop();
                        _sp = frame.StackBase;
                        if (_callFrames.Count > 0)
                            _stack[_sp++] = result;
                        else
                            return result;
                    }
                }
            }
            catch (Exception ex)
            {
                // In a true JS engine, we'd package this into a JS Error wrapped into a FenValue.
                // For Phase 1, we let it bubble up to host.
                Console.WriteLine($"[VM Crash] {ex.Message}");
                throw;
            }

            return FenValue.Undefined;
        }

        private int ReadInt32(byte[] instructions, ref CallFrame frame)
        {
            int val = BitConverter.ToInt32(instructions, frame.IP);
            frame.IP += 4;
            return val;
        }
        
        private FenValue ExecuteAdd(FenValue left, FenValue right)
        {
            // ES Spec: if either is string, concat
            if (left.IsString || right.IsString)
            {
                return FenValue.FromString(left.ToString() + right.ToString());
            }
            // else numeric addition
            return FenValue.FromNumber(left.ToNumber() + right.ToNumber());
        }
    }
}
