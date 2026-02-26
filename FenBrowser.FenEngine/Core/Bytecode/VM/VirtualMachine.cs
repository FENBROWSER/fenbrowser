using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;
using FenValue = FenBrowser.FenEngine.Core.FenValue;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

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
        private FenValue _completionValue = FenValue.Undefined; // Stores the result of the last evaluated expression

        // Call stack managed entirely on the heap to prevent .NET StackOverflowException
        private const int MAX_FRAMES = 1024;
        private readonly CallFrame[] _callFrames = new CallFrame[MAX_FRAMES];
        private int _frameCount = 0;

        public VirtualMachine()
        {
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv)
        {
            _sp = 0;
            _completionValue = FenValue.Undefined;
            _frameCount = 0;
            
            // Push initial frame
            PushFrame(initialBlock, initialEnv, 0);

            return RunLoop();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private CallFrame PushFrame(CodeBlock block, FenEnvironment env, int stackBase)
        {
            if (_frameCount >= MAX_FRAMES)
                throw new Exception("VM Error: Call stack exceeded maximum depth.");
                
            var frame = _callFrames[_frameCount];
            if (frame == null)
            {
                frame = new CallFrame(block, env, stackBase);
                _callFrames[_frameCount] = frame;
            }
            else
            {
                frame.Reset(block, env, stackBase);
            }
            _frameCount++;
            return frame;
        }

        private FenValue RunLoop()
        {
            try
            {
                while (_frameCount > 0)
                {
        fetch_frame:
                    var frame = _callFrames[_frameCount - 1];
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
                            case OpCode.Divide:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left / right);
                                break;
                            }
                            case OpCode.Modulo:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left % right);
                                break;
                            }
                            case OpCode.Exponent:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(Math.Pow(left, right));
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
                            case OpCode.NotEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(!left.LooseEquals(right));
                                break;
                            }
                            case OpCode.StrictNotEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(!left.StrictEquals(right));
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
                            case OpCode.LessThanOrEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(right, left, null, false);
                                _stack[_sp++] = FenValue.FromBoolean(!result.ToBoolean());
                                break;
                            }
                            case OpCode.GreaterThanOrEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(left, right, null, true);
                                _stack[_sp++] = FenValue.FromBoolean(!result.ToBoolean());
                                break;
                            }
                            case OpCode.BitwiseAnd:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left & right);
                                break;
                            }
                            case OpCode.BitwiseOr:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left | right);
                                break;
                            }
                            case OpCode.BitwiseXor:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left ^ right);
                                break;
                            }
                            case OpCode.LeftShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left << right);
                                break;
                            }
                            case OpCode.RightShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left >> right);
                                break;
                            }
                            case OpCode.UnsignedRightShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (uint)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left >> right);
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
                            case OpCode.Dup:
                            {
                                var value = _stack[_sp - 1];
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.Pop:
                            {
                                _sp--;
                                break;
                            }
                            case OpCode.PopAccumulator:
                            {
                                _completionValue = _stack[--_sp];
                                break;
                            }
                            case OpCode.MakeClosure:
                            {
                                int idx = ReadInt32(instructions, ref frame);
                                var templateFunc = constants[idx].AsObject() as FenFunction;
                                var newFunc = new FenFunction(templateFunc.Parameters, templateFunc.BytecodeBlock, frame.Environment);
                                newFunc.Name = templateFunc.Name;
                                _stack[_sp++] = FenValue.FromFunction(newFunc);
                                break;
                            }
                            case OpCode.Call:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                var callee = _stack[_sp - argCount - 1];
                                
                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;
                                
                                var args = new FenValue[argCount];
                                for (int i = 0; i < argCount; i++) args[argCount - 1 - i] = _stack[--_sp];
                                _sp--; // Pop callee

                                if (func.IsNative)
                                {
                                    _stack[_sp++] = func.NativeImplementation(args, FenValue.Undefined); // Phase 1: no 'this' ctx
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.Parameters != null)
                                    {
                                        for (int i = 0; i < func.Parameters.Count; i++)
                                        {
                                            var argVal = i < args.Length ? args[i] : FenValue.Undefined;
                                            newEnv.Set(func.Parameters[i].Value, argVal);
                                        }
                                    }
                                    
                                    PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    goto fetch_frame; // Break out of inner loop to process new frame
                                }
                                else
                                {
                                    throw new Exception("VM Error: AST execution inside Bytecode VM not fully supported yet.");
                                }
                                break;
                            }
                            case OpCode.Construct:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                var constructorVal = _stack[_sp - argCount - 1];
                                
                                if (!constructorVal.IsFunction) throw new Exception("VM Error: Attempted to construct non-function.");
                                var func = constructorVal.AsObject() as FenFunction;
                                
                                // Create new empty object
                                var newObj = new FenObject();
                                
                                // Wire up prototype
                                var prototypeVal = func.Get("prototype");
                                if (prototypeVal.IsObject)
                                {
                                    newObj.SetPrototype(prototypeVal.AsObject());
                                }
                                else
                                {
                                    newObj.SetPrototype(frame.Environment.Get("Object").AsObject().Get("prototype").AsObject());
                                }
                                
                                var args = new FenValue[argCount];
                                for (int i = 0; i < argCount; i++) args[argCount - 1 - i] = _stack[--_sp];
                                _sp--; // Pop constructor
                                
                                if (func.IsNative)
                                {
                                    // Native constructors usually ignore 'this' passed in and return their own newly created object,
                                    // or we pass newObj as 'this' depending on FenRuntime design.
                                    var result = func.NativeImplementation(args, FenValue.FromObject(newObj));
                                    if (result.IsObject) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    
                                    // Bind 'this' to newObj
                                    newEnv.Set("this", FenValue.FromObject(newObj));
                                    
                                    if (func.Parameters != null)
                                    {
                                        for (int i = 0; i < func.Parameters.Count; i++)
                                        {
                                            var argVal = i < args.Length ? args[i] : FenValue.Undefined;
                                            newEnv.Set(func.Parameters[i].Value, argVal);
                                        }
                                    }
                                    
                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: AST constructor execution inside Bytecode VM not fully supported yet.");
                                }
                                break;
                            }
                            case OpCode.MakeArray:
                            {
                                int count = ReadInt32(instructions, ref frame);
                                var arr = FenObject.CreateArray();
                                for (int i = 0; i < count; i++)
                                {
                                    arr.Set(i.ToString(), _stack[_sp - count + i]);
                                }
                                _sp -= count;
                                _stack[_sp++] = FenValue.FromObject(arr);
                                break;
                            }
                            case OpCode.MakeObject:
                            {
                                int propCount = ReadInt32(instructions, ref frame);
                                var obj = new FenObject();
                                int numValues = propCount * 2;
                                for (int i = 0; i < numValues; i += 2)
                                {
                                    var key = _stack[_sp - numValues + i].AsString();
                                    var value = _stack[_sp - numValues + i + 1];
                                    obj.Set(key, value);
                                }
                                _sp -= numValues;
                                _stack[_sp++] = FenValue.FromObject(obj);
                                break;
                            }
                            case OpCode.LoadProp:
                            {
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                if (obj.IsObject)
                                {
                                    _stack[_sp++] = obj.AsObject().Get(prop.AsString());
                                }
                                else
                                {
                                    _stack[_sp++] = FenValue.Undefined;
                                }
                                break;
                            }
                            case OpCode.StoreProp:
                            {
                                var value = _stack[--_sp];
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                if (obj.IsObject)
                                {
                                    obj.AsObject().Set(prop.AsString(), value);
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.MakeKeysIterator:
                            {
                                var objVal = _stack[--_sp];
                                var iterObj = new FenObject();
                                if (objVal.IsObject)
                                {
                                    iterObj.NativeObject = System.Linq.Enumerable.Select(objVal.AsObject().Keys(), k => FenValue.FromString(k)).GetEnumerator();
                                }
                                else
                                {
                                    iterObj.NativeObject = new List<FenValue>().GetEnumerator();
                                }
                                _stack[_sp++] = FenValue.FromObject(iterObj);
                                break;
                            }
                            case OpCode.MakeValuesIterator:
                            {
                                var objVal = _stack[--_sp];
                                var iterObj = new FenObject();
                                if (objVal.IsObject)
                                {
                                    var obj = objVal.AsObject();
                                    iterObj.NativeObject = System.Linq.Enumerable.Select(obj.Keys(), k => obj.Get(k)).GetEnumerator();
                                }
                                else
                                {
                                    iterObj.NativeObject = new List<FenValue>().GetEnumerator();
                                }
                                _stack[_sp++] = FenValue.FromObject(iterObj);
                                break;
                            }
                            case OpCode.IteratorMoveNext:
                            {
                                var obj = (FenObject)_stack[--_sp].AsObject();
                                var iter = (IEnumerator<FenValue>)obj.NativeObject;
                                _stack[_sp++] = FenValue.FromBoolean(iter.MoveNext());
                                break;
                            }
                            case OpCode.IteratorCurrent:
                            {
                                var obj = (FenObject)_stack[--_sp].AsObject();
                                var iter = (IEnumerator<FenValue>)obj.NativeObject;
                                _stack[_sp++] = iter.Current;
                                break;
                            }
                            case OpCode.Negate:
                            {
                                var val = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(-val);
                                break;
                            }
                            case OpCode.LogicalNot:
                            {
                                var val = _stack[--_sp].ToBoolean();
                                _stack[_sp++] = FenValue.FromBoolean(!val);
                                break;
                            }
                            case OpCode.BitwiseNot:
                            {
                                var val = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(~val);
                                break;
                            }
                            case OpCode.Typeof:
                            {
                                var val = _stack[--_sp];
                                string typeStr = "object";
                                switch (val.Type)
                                {
                                    case JsValueType.Undefined: typeStr = "undefined"; break;
                                    case JsValueType.Null: typeStr = "object"; break;
                                    case JsValueType.Boolean: typeStr = "boolean"; break;
                                    case JsValueType.Number: typeStr = "number"; break;
                                    case JsValueType.String: typeStr = "string"; break;
                                    case JsValueType.Function: typeStr = "function"; break;
                                    case JsValueType.Symbol: typeStr = "symbol"; break;
                                    case JsValueType.BigInt: typeStr = "bigint"; break;
                                    default: typeStr = "object"; break;
                                }
                                _stack[_sp++] = FenValue.FromString(typeStr);
                                break;
                            }
                            case OpCode.Return:
                            {
                                var result = _stack[--_sp];
                                _frameCount--;
                                _sp = frame.StackBase;

                                // If this was a constructor call, returning a primitive yields the constructed object.
                                // Returning an object yields that object.
                                if (frame.IsConstruct && !result.IsObject)
                                {
                                    result = FenValue.FromObject(frame.ConstructedObject);
                                }
                                
                                // Push result back for caller, or return if top level
                                if (_frameCount > 0)
                                {
                                    _stack[_sp++] = result;
                                    goto fetch_frame; // Re-fetch the caller's frame locals
                                }
                                else
                                {
                                    return result;
                                }
                            }
                            case OpCode.PushExceptionHandler:
                            {
                                int catchOffset = ReadInt32(instructions, ref frame);
                                int finallyOffset = ReadInt32(instructions, ref frame);
                                frame.ExceptionHandlers.Push(new ExceptionHandler(catchOffset, finallyOffset, _sp));
                                break;
                            }
                            case OpCode.PopExceptionHandler:
                            {
                                frame.ExceptionHandlers.Pop();
                                break;
                            }
                            case OpCode.Throw:
                            {
                                var exceptionValue = _stack[--_sp];
                                HandleException(exceptionValue, ref frame);
                                break;
                            }
                            case OpCode.Halt:
                            {
                                // Return the completion value rather than top of stack, unless there's a return value pending (which Returns handle)
                                return _completionValue;
                            }    
                            default:
                                throw new Exception($"VM Error: Unhandled OpCode {op} at IP {frame.IP - 1}");
                        }
                    }
                    
                    // Reached end of instructions without a return
                    if (_frameCount > 0 && _callFrames[_frameCount - 1] == frame)
                    {
                        var result = FenValue.Undefined;
                        _frameCount--;
                        _sp = frame.StackBase;
                        
                        if (frame.IsConstruct)
                        {
                            result = FenValue.FromObject(frame.ConstructedObject);
                        }

                        if (_frameCount > 0)
                            _stack[_sp++] = result;
                        else
                            return result;
                    }
                }
            }
            catch (Exception ex)
            {
                // Unwind and handle .NET exceptions gracefully
                var errorObj = FenValue.FromObject(new FenObject());
                errorObj.AsObject().Set("message", FenValue.FromString(ex.Message));
                errorObj.AsObject().Set("name", FenValue.FromString(ex.GetType().Name));
                
                if (_frameCount > 0)
                {
                    var topFrame = _callFrames[_frameCount - 1];
                    HandleException(errorObj, ref topFrame);
                }
                else
                {
                    throw new Exception($"Uncaught JS Exception: {errorObj.AsString()}", ex);
                }
            }

            return FenValue.Undefined;
        }

        private void HandleException(FenValue exceptionValue, ref CallFrame currentFrame)
        {
            // Find nearest handler in current frame or unwind CallStack
            while (_frameCount > 0)
            {
                var frame = _callFrames[_frameCount - 1];
                if (frame.HasExceptionHandlers)
                {
                    var handler = frame.ExceptionHandlers.Pop();
                    _sp = handler.StackBase;
                    
                    if (handler.CatchOffset != -1)
                    {
                        frame.IP = handler.CatchOffset;
                        _stack[_sp++] = exceptionValue; // Push error for catch block
                        return; // Resume execution in RunLoop
                    }
                    else if (handler.FinallyOffset != -1)
                    {
                        frame.IP = handler.FinallyOffset;
                        // For pure finally, we might need to store the exception somewhere to rethrow later
                        // But JS 'finally' without 'catch' is rare in Phase 1
                        _stack[_sp++] = exceptionValue; 
                        return; // Resume execution in RunLoop
                    }
                }
                
                // No handler in this frame, unwind call stack!
                _frameCount--;
                if (_frameCount > 0)
                {
                    _sp = _callFrames[_frameCount - 1].StackBase;
                }
            }
            
            // Uncaught exception!
            Console.WriteLine($"[VM Uncaught Exception] {exceptionValue.AsString()}");
            throw new Exception($"Uncaught JS Exception: {exceptionValue.AsString()}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int ReadInt32(byte[] instructions, ref CallFrame frame)
        {
            int ip = frame.IP;
            int val = instructions[ip] | (instructions[ip + 1] << 8) | (instructions[ip + 2] << 16) | (instructions[ip + 3] << 24);
            frame.IP = ip + 4;
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
