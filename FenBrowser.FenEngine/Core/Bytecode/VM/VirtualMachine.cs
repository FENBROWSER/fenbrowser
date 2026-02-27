using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
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
                            case OpCode.InOperator:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                if (right.IsObject)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(right.AsObject().Has(left.AsString()));
                                }
                                else
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                }
                                break;
                            }
                            case OpCode.InstanceOf:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];

                                if (!right.IsFunction || !left.IsObject)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var constructor = right.AsFunction() as FenFunction;
                                if (constructor == null)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var prototypeVal = constructor.Get("prototype");
                                var expectedPrototype = prototypeVal.IsObject
                                    ? prototypeVal.AsObject()
                                    : (constructor.Prototype as FenObject);
                                if (expectedPrototype == null)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var currentPrototype = left.AsObject().GetPrototype();
                                bool isMatch = false;
                                while (currentPrototype != null)
                                {
                                    if (ReferenceEquals(currentPrototype, expectedPrototype))
                                    {
                                        isMatch = true;
                                        break;
                                    }

                                    currentPrototype = currentPrototype.GetPrototype();
                                }

                                _stack[_sp++] = FenValue.FromBoolean(isMatch);
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
                                newFunc.IsArrowFunction = templateFunc.IsArrowFunction;
                                newFunc.IsAsync = templateFunc.IsAsync;
                                newFunc.IsGenerator = templateFunc.IsGenerator;

                                if (!newFunc.IsArrowFunction)
                                {
                                    var fnPrototype = new FenObject();
                                    fnPrototype.Set("constructor", FenValue.FromFunction(newFunc));
                                    newFunc.Prototype = fnPrototype;
                                    newFunc.Set("prototype", FenValue.FromObject(fnPrototype));
                                }

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
                                    BindFunctionArguments(func, newEnv, args);
                                    
                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame; // Break out of inner loop to process new frame
                                }
                                else
                                {
                                    throw new Exception("VM Error: AST execution inside Bytecode VM not fully supported yet.");
                                }
                                break;
                            }
                            case OpCode.CallFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var callee = _stack[--_sp];

                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;
                                var args = ExtractArrayLikeValues(argsArrayVal);

                                if (func.IsNative)
                                {
                                    _stack[_sp++] = func.NativeImplementation(args, FenValue.Undefined);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    BindFunctionArguments(func, newEnv, args);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
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
                                if (func.IsArrowFunction)
                                {
                                    throw new Exception("TypeError: Arrow function is not a constructor");
                                }
                                
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
                                    BindFunctionArguments(func, newEnv, args);
                                    
                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;
                                    
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: AST constructor execution inside Bytecode VM not fully supported yet.");
                                }
                                break;
                            }
                            case OpCode.ConstructFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var constructorVal = _stack[--_sp];

                                if (!constructorVal.IsFunction) throw new Exception("VM Error: Attempted to construct non-function.");
                                var func = constructorVal.AsObject() as FenFunction;
                                if (func.IsArrowFunction)
                                {
                                    throw new Exception("TypeError: Arrow function is not a constructor");
                                }

                                var newObj = new FenObject();
                                var prototypeVal = func.Get("prototype");
                                if (prototypeVal.IsObject)
                                {
                                    newObj.SetPrototype(prototypeVal.AsObject());
                                }
                                else
                                {
                                    newObj.SetPrototype(frame.Environment.Get("Object").AsObject().Get("prototype").AsObject());
                                }

                                var args = ExtractArrayLikeValues(argsArrayVal);

                                if (func.IsNative)
                                {
                                    var result = func.NativeImplementation(args, FenValue.FromObject(newObj));
                                    if (result.IsObject) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    newEnv.Set("this", FenValue.FromObject(newObj));
                                    BindFunctionArguments(func, newEnv, args);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;

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
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    _stack[_sp++] = objectRef.Get(prop.AsString());
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
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    objectRef.Set(prop.AsString(), value);
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.ArrayAppend:
                            {
                                var value = _stack[--_sp];
                                var arrayValue = _stack[--_sp];

                                if (arrayValue.IsObject)
                                {
                                    var arrayObj = arrayValue.AsObject();
                                    int length = GetArrayLikeLength(arrayObj);
                                    arrayObj.Set(length.ToString(), value);
                                    arrayObj.Set("length", FenValue.FromNumber(length + 1));
                                }

                                _stack[_sp++] = arrayValue;
                                break;
                            }
                            case OpCode.ArrayAppendSpread:
                            {
                                var spreadValue = _stack[--_sp];
                                var arrayValue = _stack[--_sp];

                                if (arrayValue.IsObject)
                                {
                                    var arrayObj = arrayValue.AsObject();
                                    int length = GetArrayLikeLength(arrayObj);

                                    bool expanded = false;
                                    if (spreadValue.IsObject)
                                    {
                                        var spreadObj = spreadValue.AsObject();
                                        if (spreadObj != null && spreadObj.Has("length"))
                                        {
                                            var spreadLenVal = spreadObj.Get("length");
                                            if (spreadLenVal.IsNumber)
                                            {
                                                int spreadLength = (int)spreadLenVal.ToNumber();
                                                for (int i = 0; i < spreadLength; i++)
                                                {
                                                    arrayObj.Set((length + i).ToString(), spreadObj.Get(i.ToString()));
                                                }
                                                length += spreadLength;
                                                expanded = true;
                                            }
                                        }
                                    }

                                    if (!expanded)
                                    {
                                        arrayObj.Set(length.ToString(), spreadValue);
                                        length += 1;
                                    }

                                    arrayObj.Set("length", FenValue.FromNumber(length));
                                }

                                _stack[_sp++] = arrayValue;
                                break;
                            }
                            case OpCode.ObjectSpread:
                            {
                                var sourceValue = _stack[--_sp];
                                var targetValue = _stack[--_sp];

                                if (targetValue.IsObject && sourceValue.IsObject)
                                {
                                    var targetObj = targetValue.AsObject();
                                    var sourceObj = sourceValue.AsObject();
                                    foreach (var key in sourceObj.Keys())
                                    {
                                        targetObj.Set(key, sourceObj.Get(key));
                                    }
                                }

                                _stack[_sp++] = targetValue;
                                break;
                            }
                            case OpCode.DeleteProp:
                            {
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                bool deleted = true;
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    deleted = objectRef.Delete(prop.AsString());
                                }

                                _stack[_sp++] = FenValue.FromBoolean(deleted);
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
                            case OpCode.ToNumber:
                            {
                                var val = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromNumber(val.ToNumber());
                                break;
                            }
                            case OpCode.LoadNewTarget:
                            {
                                _stack[_sp++] = frame.NewTarget;
                                break;
                            }
                            case OpCode.Await:
                            {
                                var awaitValue = _stack[--_sp];
                                _stack[_sp++] = ResolveAwaitValue(awaitValue);
                                break;
                            }
                            case OpCode.EnterWith:
                            {
                                var withObjectValue = _stack[--_sp];
                                if (!withObjectValue.IsObject)
                                {
                                    // Keep bytecode path stable for unsupported non-object with operands.
                                    break;
                                }

                                var withEnv = new FenEnvironment(frame.Environment);
                                var withObject = withObjectValue.AsObject();
                                if (withObject != null)
                                {
                                    foreach (var key in withObject.Keys())
                                    {
                                        withEnv.Set(key, withObject.Get(key));
                                    }
                                }

                                frame.WithEnvironments.Push(frame.Environment);
                                frame.SetEnvironment(withEnv);
                                break;
                            }
                            case OpCode.ExitWith:
                            {
                                if (frame.HasWithEnvironments)
                                {
                                    frame.SetEnvironment(frame.WithEnvironments.Pop());
                                }
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
                                if (frame.IsAsyncFunction)
                                {
                                    result = WrapAsyncReturnValue(result);
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
                                goto fetch_frame;
                            }
                            case OpCode.Halt:
                            {
                                // Halt marks the end of the current frame's code block.
                                // Top-level frame returns script completion value; nested frames
                                // fall through as implicit undefined (or constructed object).
                                if (_frameCount > 1)
                                {
                                    var result = FenValue.Undefined;
                                    _frameCount--;
                                    _sp = frame.StackBase;

                                    if (frame.IsConstruct)
                                    {
                                        result = FenValue.FromObject(frame.ConstructedObject);
                                    }
                                    if (frame.IsAsyncFunction)
                                    {
                                        result = WrapAsyncReturnValue(result);
                                    }

                                    _stack[_sp++] = result;
                                    goto fetch_frame;
                                }

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
                        if (frame.IsAsyncFunction)
                        {
                            result = WrapAsyncReturnValue(result);
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
                    // Resume execution at the installed JS catch/finally handler.
                    return RunLoop();
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

                // Async functions capture uncaught exceptions as rejected promise results,
                // rather than propagating host exceptions through callers.
                if (frame.IsAsyncFunction)
                {
                    var rejection = WrapAsyncReturnValue(FenValue.FromError(exceptionValue.AsString()));
                    _frameCount--;
                    _sp = frame.StackBase;

                    if (_frameCount > 0)
                    {
                        _stack[_sp++] = rejection;
                    }
                    else
                    {
                        _completionValue = rejection;
                    }
                    return;
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

        private static void BindFunctionArguments(FenFunction func, FenEnvironment env, FenValue[] args)
        {
            if (func == null || env == null)
            {
                return;
            }

            var effectiveArgs = args ?? Array.Empty<FenValue>();

            if (!func.IsArrowFunction)
            {
                var argumentsObj = new FenObject
                {
                    InternalClass = "Arguments"
                };

                for (int i = 0; i < effectiveArgs.Length; i++)
                {
                    argumentsObj.Set(i.ToString(), effectiveArgs[i]);
                }

                argumentsObj.Set("length", FenValue.FromNumber(effectiveArgs.Length));
                argumentsObj.Set("callee", FenValue.FromFunction(func));

                var paramNames = new FenObject();
                if (func.Parameters != null)
                {
                    for (int i = 0; i < func.Parameters.Count && i < effectiveArgs.Length; i++)
                    {
                        var parameter = func.Parameters[i];
                        if (parameter == null || parameter.IsRest || string.IsNullOrEmpty(parameter.Value))
                        {
                            continue;
                        }

                        paramNames.Set(i.ToString(), FenValue.FromString(parameter.Value));
                    }
                }

                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames));
                env.Set("arguments", FenValue.FromObject(argumentsObj));
            }

            if (func.Parameters == null)
            {
                return;
            }

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var parameter = func.Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                if (parameter.IsRest)
                {
                    var restArray = FenObject.CreateArray();
                    int restIndex = 0;
                    for (int j = i; j < effectiveArgs.Length; j++)
                    {
                        restArray.Set(restIndex.ToString(), effectiveArgs[j]);
                        restIndex++;
                    }

                    restArray.Set("length", FenValue.FromNumber(restIndex));
                    env.Set(parameter.Value, FenValue.FromObject(restArray));
                    break;
                }

                var argValue = i < effectiveArgs.Length ? effectiveArgs[i] : FenValue.Undefined;
                env.Set(parameter.Value, argValue);
            }
        }

        private FenValue[] ExtractArrayLikeValues(FenValue argsArrayValue)
        {
            if (!argsArrayValue.IsObject)
            {
                return Array.Empty<FenValue>();
            }

            var argsObject = argsArrayValue.AsObject();
            int length = GetArrayLikeLength(argsObject);
            if (length <= 0)
            {
                return Array.Empty<FenValue>();
            }

            var args = new FenValue[length];
            for (int i = 0; i < length; i++)
            {
                args[i] = argsObject.Get(i.ToString());
            }

            return args;
        }

        private static int GetArrayLikeLength(FenBrowser.FenEngine.Core.Interfaces.IObject obj)
        {
            if (obj == null)
            {
                return 0;
            }

            var lengthValue = obj.Get("length");
            if (!lengthValue.IsNumber)
            {
                return 0;
            }

            int length = (int)lengthValue.ToNumber();
            return length < 0 ? 0 : length;
        }

        private static FenValue WrapAsyncReturnValue(FenValue result)
        {
            if (result.Type == JsValueType.Error)
            {
                return FenValue.FromObject(JsPromise.Reject(result, null));
            }

            if (result.IsObject && result.AsObject() is JsPromise)
            {
                return result;
            }

            return FenValue.FromObject(JsPromise.Resolve(result, null));
        }

        private static FenValue ResolveAwaitValue(FenValue value)
        {
            JsPromise promise = null;
            if (value.IsObject && value.AsObject() is JsPromise existingPromise)
            {
                promise = existingPromise;
            }
            else if (value.IsObject)
            {
                var obj = value.AsObject();
                var thenVal = obj?.Get("then");
                if (thenVal.HasValue && thenVal.Value.IsFunction)
                {
                    promise = JsPromise.Resolve(value, null);
                }
                else
                {
                    return value;
                }
            }
            else
            {
                return value;
            }

            if (promise.IsSettled)
            {
                return promise.IsFulfilled ? promise.Result : FenValue.FromError(promise.Result.ToString());
            }

            const int maxPumps = 5000;
            for (int i = 0; i < maxPumps; i++)
            {
                try
                {
                    EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (promise.IsSettled)
                {
                    break;
                }

                if (EventLoopCoordinator.Instance.HasPendingTasks)
                {
                    try
                    {
                        EventLoopCoordinator.Instance.ProcessNextTask();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }

                if (promise.IsSettled)
                {
                    break;
                }
            }

            if (promise.IsSettled)
            {
                return promise.IsFulfilled ? promise.Result : FenValue.FromError(promise.Result.ToString());
            }

            return FenValue.Undefined;
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
