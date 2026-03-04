using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Jit
{
    public delegate FenValue FenJittedDelegate(FenValue[] args, FenEnvironment env, IExecutionContext context);

    public class JitCompiler
    {
        private static readonly MethodInfo JitCallMethod = typeof(JitRuntime).GetMethod("Call");
        private static readonly MethodInfo JitGetPropMethod = typeof(JitRuntime).GetMethod("GetProp");
        private static readonly MethodInfo JitSetPropMethod = typeof(JitRuntime).GetMethod("SetProp");
        private static readonly MethodInfo JitCreateArrayMethod = typeof(JitRuntime).GetMethod("CreateArray");
        private static readonly MethodInfo JitCreateObjectMethod = typeof(JitRuntime).GetMethod("CreateObject");
        
        private static readonly MethodInfo FromNumberMethod = typeof(FenValue).GetMethod("FromNumber", new[] { typeof(double) });
        private static readonly MethodInfo FromStringMethod = typeof(FenValue).GetMethod("FromString", new[] { typeof(string) });
        private static readonly MethodInfo FromBooleanMethod = typeof(FenValue).GetMethod("FromBoolean", new[] { typeof(bool) });
        
        private static readonly MethodInfo EnvGetMethod = typeof(FenEnvironment).GetMethod("Get", new[] { typeof(string) });
        private static readonly MethodInfo EnvUpdateMethod = typeof(FenEnvironment).GetMethod("Update", new[] { typeof(string), typeof(FenValue) });
        private static readonly FieldInfo FastStoreField = typeof(FenEnvironment).GetField("FastStore");
        private static readonly FieldInfo ValueTypeField = typeof(FenValue).GetField("_type");
        private static readonly FieldInfo ValueNumberField = typeof(FenValue).GetField("_numberValue");
        private static readonly FieldInfo ValueRefField = typeof(FenValue).GetField("_refValue", BindingFlags.NonPublic | BindingFlags.Instance);
        
        private static readonly MethodInfo ValueToNumberMethod = typeof(FenValue).GetMethod("AsNumber");
        private static readonly MethodInfo ValueToBooleanMethod = typeof(FenValue).GetMethod("AsBoolean");
        private static readonly MethodInfo ValueToStringMethod = typeof(FenValue).GetMethod("AsString");

        private static readonly FieldInfo UndefinedField = typeof(FenValue).GetField("Undefined");
        private static readonly FieldInfo NullField = typeof(FenValue).GetField("Null");

        static JitCompiler()
        {
            if (JitCallMethod == null) throw new InvalidOperationException("JitCallMethod not found");
            if (JitGetPropMethod == null) throw new InvalidOperationException("JitGetPropMethod not found");
            if (JitSetPropMethod == null) throw new InvalidOperationException("JitSetPropMethod not found");
            if (JitCreateArrayMethod == null) throw new InvalidOperationException("JitCreateArrayMethod not found");
            if (JitCreateObjectMethod == null) throw new InvalidOperationException("JitCreateObjectMethod not found");
            if (FromNumberMethod == null) throw new InvalidOperationException("FromNumberMethod not found");
            if (FromStringMethod == null) throw new InvalidOperationException("FromStringMethod not found");
            if (FromBooleanMethod == null) throw new InvalidOperationException("FromBooleanMethod not found");
            if (EnvGetMethod == null) throw new InvalidOperationException("EnvGetMethod not found");
            if (EnvUpdateMethod == null) throw new InvalidOperationException("EnvUpdateMethod not found");
            if (FastStoreField == null) throw new InvalidOperationException("FastStoreField not found");
            if (ValueTypeField == null) throw new InvalidOperationException("ValueTypeField not found");
            if (ValueNumberField == null) throw new InvalidOperationException("ValueNumberField not found");
            if (ValueToNumberMethod == null) throw new InvalidOperationException("ValueToNumberMethod not found");
            if (ValueToBooleanMethod == null) throw new InvalidOperationException("ValueToBooleanMethod not found");
            if (ValueToStringMethod == null) throw new InvalidOperationException("ValueToStringMethod not found");
            if (UndefinedField == null) throw new InvalidOperationException("UndefinedField not found");
            if (NullField == null) throw new InvalidOperationException("NullField not found");
        }

        private LocalBuilder _tempVal;
        private LocalBuilder _tempL;
        private LocalBuilder _tempR;
        private Dictionary<int, LocalBuilder> _jitLocalsNum; // double
        private Dictionary<int, LocalBuilder> _jitLocalsType; // ValueType
        private Dictionary<int, LocalBuilder> _jitLocalsRef;  // object
        private LocalBuilder _tempObj;
        private LocalBuilder _resValue; // Reusable FenValue local

        private void EmitEpilogSync(ILGenerator il, BytecodeUnit unit)
        {
            foreach (var kvp in unit.LocalMap)
            {
                var idx = kvp.Value;
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, FastStoreField);
                il.Emit(OpCodes.Ldc_I4, idx);
                il.Emit(OpCodes.Ldelema, typeof(FenValue));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldloc, _jitLocalsType[idx]);
                il.Emit(OpCodes.Stfld, ValueTypeField);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldloc, _jitLocalsNum[idx]);
                il.Emit(OpCodes.Stfld, ValueNumberField);
                il.Emit(OpCodes.Ldloc, _jitLocalsRef[idx]);
                il.Emit(OpCodes.Stfld, ValueRefField);
            }
        }

        public FenJittedDelegate Compile(BytecodeUnit unit)
        {
            var method = new DynamicMethod(
                "Jitted_" + unit.Name,
                typeof(FenValue),
                new[] { typeof(FenValue[]), typeof(FenEnvironment), typeof(IExecutionContext) },
                typeof(JitCompiler).Module);

            var il = method.GetILGenerator();
            _tempVal = il.DeclareLocal(typeof(FenValue));
            _tempL = il.DeclareLocal(typeof(FenValue));
            _tempR = il.DeclareLocal(typeof(FenValue));
            _tempObj = il.DeclareLocal(typeof(FenValue));
            _resValue = il.DeclareLocal(typeof(FenValue));
            _jitLocalsNum = new Dictionary<int, LocalBuilder>();
            _jitLocalsType = new Dictionary<int, LocalBuilder>();
            _jitLocalsRef = new Dictionary<int, LocalBuilder>();

            // 1. Declare IL locals for all JS locals (Split)
            foreach (var kvp in unit.LocalMap)
            {
                _jitLocalsNum[kvp.Value] = il.DeclareLocal(typeof(double));
                _jitLocalsType[kvp.Value] = il.DeclareLocal(typeof(FenBrowser.FenEngine.Core.Interfaces.ValueType));
                _jitLocalsRef[kvp.Value] = il.DeclareLocal(typeof(object));
            }

            // 2. Prolog: Load from FenEnvironment.FastStore into split IL locals
            foreach (var kvp in unit.LocalMap)
            {
                var idx = kvp.Value;
                il.Emit(OpCodes.Ldarg_1); 
                il.Emit(OpCodes.Ldfld, FastStoreField);
                il.Emit(OpCodes.Ldc_I4, idx);
                il.Emit(OpCodes.Ldelema, typeof(FenValue));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldfld, ValueTypeField);
                il.Emit(OpCodes.Stloc, _jitLocalsType[idx]);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldfld, ValueNumberField);
                il.Emit(OpCodes.Stloc, _jitLocalsNum[idx]);
                il.Emit(OpCodes.Ldfld, ValueRefField);
                il.Emit(OpCodes.Stloc, _jitLocalsRef[idx]);
            }

            var labels = new Label[unit.Instructions.Count + 1];
            for (int i = 0; i < labels.Length; i++) labels[i] = il.DefineLabel();

            for (int i = 0; i < unit.Instructions.Count; i++)
            {
                var inst = unit.Instructions[i];
                il.MarkLabel(labels[i]);

                switch (inst.OpCode)
                {
                    case OpCode.PushConst: EmitPushConst(il, inst.Operand); break;
                    case OpCode.Dup: il.Emit(OpCodes.Dup); break;
                    case OpCode.Swap: il.Emit(OpCodes.Stloc, _tempVal); il.Emit(OpCodes.Stloc, _tempL); il.Emit(OpCodes.Ldloc, _tempVal); il.Emit(OpCodes.Ldloc, _tempL); break;
                    case OpCode.Pop: il.Emit(OpCodes.Pop); break;

                    case OpCode.LoadVar:
                        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldstr, (string)inst.Operand);
                        il.Emit(OpCodes.Callvirt, EnvGetMethod);
                        break;

                    case OpCode.StoreVar:
                        il.Emit(OpCodes.Stloc, _tempVal);
                        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldstr, (string)inst.Operand);
                        il.Emit(OpCodes.Ldloc, _tempVal);
                        il.Emit(OpCodes.Callvirt, EnvUpdateMethod);
                        break;

                    case OpCode.LoadLocal:
                        {
                            int idx = (int)inst.Operand;
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldloc, _jitLocalsType[idx]);
                            il.Emit(OpCodes.Stfld, ValueTypeField);
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldloc, _jitLocalsNum[idx]);
                            il.Emit(OpCodes.Stfld, ValueNumberField);
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldloc, _jitLocalsRef[idx]);
                            il.Emit(OpCodes.Stfld, ValueRefField);
                            il.Emit(OpCodes.Ldloc, _resValue);
                        }
                        break;

                    case OpCode.StoreLocal:
                        {
                            int idx = (int)inst.Operand;
                            il.Emit(OpCodes.Stloc, _resValue);
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldfld, ValueTypeField);
                            il.Emit(OpCodes.Stloc, _jitLocalsType[idx]);
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldfld, ValueNumberField);
                            il.Emit(OpCodes.Stloc, _jitLocalsNum[idx]);
                            il.Emit(OpCodes.Ldloca, _resValue);
                            il.Emit(OpCodes.Ldfld, ValueRefField);
                            il.Emit(OpCodes.Stloc, _jitLocalsRef[idx]);
                        }
                        break;

                    case OpCode.GetProp:
                        il.Emit(OpCodes.Ldstr, (string)inst.Operand);
                        il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, JitGetPropMethod);
                        break;

                    case OpCode.SetProp:
                        il.Emit(OpCodes.Stloc, _tempVal); il.Emit(OpCodes.Stloc, _tempObj);
                        il.Emit(OpCodes.Ldloc, _tempObj); il.Emit(OpCodes.Ldstr, (string)inst.Operand);
                        il.Emit(OpCodes.Ldloc, _tempVal); il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Call, JitSetPropMethod);
                        break;

                    case OpCode.Add: EmitBinaryOp(il, OpCodes.Add); break;
                    case OpCode.Sub: EmitBinaryOp(il, OpCodes.Sub); break;
                    case OpCode.Mul: EmitBinaryOp(il, OpCodes.Mul); break;
                    case OpCode.Div: EmitBinaryOp(il, OpCodes.Div); break;
                    case OpCode.Mod: EmitBinaryOp(il, OpCodes.Rem); break;

                    case OpCode.Lt: EmitComparisonOp(il, OpCodes.Clt); break;
                    case OpCode.Gt: EmitComparisonOp(il, OpCodes.Cgt); break;
                    case OpCode.LtEq: 
                        EmitComparisonRaw(il, OpCodes.Cgt); 
                        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); 
                        il.Emit(OpCodes.Call, FromBooleanMethod); break;
                    case OpCode.GtEq: 
                        EmitComparisonRaw(il, OpCodes.Clt); 
                        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); 
                        il.Emit(OpCodes.Call, FromBooleanMethod); break;
                    case OpCode.Eq:
                    case OpCode.StrictEq: EmitComparisonOp(il, OpCodes.Ceq); break;
                    case OpCode.NotEq:
                    case OpCode.StrictNotEq: 
                        EmitComparisonRaw(il, OpCodes.Ceq); 
                        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); 
                        il.Emit(OpCodes.Call, FromBooleanMethod); break;

                    case OpCode.Not:
                        il.Emit(OpCodes.Stloc, _tempVal);
                        il.Emit(OpCodes.Ldloca, _tempVal);
                        il.Emit(OpCodes.Call, ValueToBooleanMethod);
                        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);
                        il.Emit(OpCodes.Call, FromBooleanMethod);
                        break;

                    case OpCode.Jump: il.Emit(OpCodes.Br, labels[(int)inst.Operand]); break;
                    case OpCode.JumpIfFalse: 
                        il.Emit(OpCodes.Stloc, _tempVal);
                        il.Emit(OpCodes.Ldloca, _tempVal);
                        il.Emit(OpCodes.Call, ValueToBooleanMethod); 
                        il.Emit(OpCodes.Brfalse, labels[(int)inst.Operand]); break;
                    case OpCode.JumpIfTrue: 
                        il.Emit(OpCodes.Stloc, _tempVal);
                        il.Emit(OpCodes.Ldloca, _tempVal);
                        il.Emit(OpCodes.Call, ValueToBooleanMethod); 
                        il.Emit(OpCodes.Brtrue, labels[(int)inst.Operand]); break;

                    case OpCode.Call: EmitCall(il, (int)inst.Operand); break;
                    case OpCode.CreateArray: EmitCreateArray(il, (int)inst.Operand); break;
                    case OpCode.CreateObject: EmitCreateObject(il, (int)inst.Operand); break;
                    case OpCode.Return:
                        EmitEpilogSync(il, unit);
                        il.Emit(OpCodes.Ret); 
                        break;

                    default: il.Emit(OpCodes.Ldsfld, UndefinedField); break;
                }
            }

            il.MarkLabel(labels[unit.Instructions.Count]);
            il.Emit(OpCodes.Ldsfld, UndefinedField);
            EmitEpilogSync(il, unit);
            il.Emit(OpCodes.Ret);

            return (FenJittedDelegate)method.CreateDelegate(typeof(FenJittedDelegate));
        }

        private void EmitPushConst(ILGenerator il, object val)
        {
            if (val == null) il.Emit(OpCodes.Ldsfld, NullField);
            else if (val is int i) { il.Emit(OpCodes.Ldc_R8, (double)i); il.Emit(OpCodes.Call, FromNumberMethod); }
            else if (val is long l) { il.Emit(OpCodes.Ldc_R8, (double)l); il.Emit(OpCodes.Call, FromNumberMethod); }
            else if (val is double d) { il.Emit(OpCodes.Ldc_R8, d); il.Emit(OpCodes.Call, FromNumberMethod); }
            else if (val is string s) { il.Emit(OpCodes.Ldstr, s); il.Emit(OpCodes.Call, FromStringMethod); }
            else if (val is bool b) { il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, FromBooleanMethod); }
            else il.Emit(OpCodes.Ldsfld, UndefinedField);
        }

        private void EmitBinaryOp(ILGenerator il, System.Reflection.Emit.OpCode ilOp)
        {
            var slowLabel = il.DefineLabel();
            var endLabel = il.DefineLabel();

            il.Emit(OpCodes.Stloc, _tempR);
            il.Emit(OpCodes.Stloc, _tempL);

            // Fast path check: Both are Numbers
            il.Emit(OpCodes.Ldloca, _tempL);
            il.Emit(OpCodes.Ldfld, ValueTypeField);
            il.Emit(OpCodes.Ldc_I4, (int)FenBrowser.FenEngine.Core.Interfaces.ValueType.Number);
            il.Emit(OpCodes.Bne_Un, slowLabel);

            il.Emit(OpCodes.Ldloca, _tempR);
            il.Emit(OpCodes.Ldfld, ValueTypeField);
            il.Emit(OpCodes.Ldc_I4, (int)FenBrowser.FenEngine.Core.Interfaces.ValueType.Number);
            il.Emit(OpCodes.Bne_Un, slowLabel);

            // Fast path: Direct double math
            il.Emit(OpCodes.Ldloca, _tempL);
            il.Emit(OpCodes.Ldfld, ValueNumberField);
            il.Emit(OpCodes.Ldloca, _tempR);
            il.Emit(OpCodes.Ldfld, ValueNumberField);
            il.Emit(ilOp);

            // Inline FromNumber without Initobj
            il.Emit(OpCodes.Stloc, _tempVal); // Double result
            il.Emit(OpCodes.Ldloca, _resValue);
            il.Emit(OpCodes.Ldc_I4, (int)FenBrowser.FenEngine.Core.Interfaces.ValueType.Number);
            il.Emit(OpCodes.Stfld, ValueTypeField);
            il.Emit(OpCodes.Ldloca, _resValue);
            il.Emit(OpCodes.Ldloc, _tempVal);
            il.Emit(OpCodes.Stfld, ValueNumberField);
            il.Emit(OpCodes.Ldloca, _resValue);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stfld, ValueRefField);
            il.Emit(OpCodes.Ldloc, _resValue);
            il.Emit(OpCodes.Br, endLabel);

            // Slow path: Fallback to static operator (which handles all cases)
            il.MarkLabel(slowLabel);
            var opName = "op_Addition";
            if (ilOp == OpCodes.Sub) opName = "op_Subtraction";
            else if (ilOp == OpCodes.Mul) opName = "op_Multiply";
            else if (ilOp == OpCodes.Div) opName = "op_Division";
            
            var opMethod = typeof(FenValue).GetMethod(opName, new[] { typeof(FenValue), typeof(FenValue) });
            
            il.Emit(OpCodes.Ldloc, _tempL);
            il.Emit(OpCodes.Ldloc, _tempR);
            il.Emit(OpCodes.Call, opMethod);

            il.MarkLabel(endLabel);
        }

        private void EmitComparisonRaw(ILGenerator il, System.Reflection.Emit.OpCode ilOp)
        {
            var slowLabel = il.DefineLabel();
            var endLabel = il.DefineLabel();

            il.Emit(OpCodes.Stloc, _tempR);
            il.Emit(OpCodes.Stloc, _tempL);

            // Fast path check: Both are Numbers
            il.Emit(OpCodes.Ldloca, _tempL);
            il.Emit(OpCodes.Ldfld, ValueTypeField);
            il.Emit(OpCodes.Ldc_I4, (int)FenBrowser.FenEngine.Core.Interfaces.ValueType.Number);
            il.Emit(OpCodes.Bne_Un, slowLabel);

            il.Emit(OpCodes.Ldloca, _tempR);
            il.Emit(OpCodes.Ldfld, ValueTypeField);
            il.Emit(OpCodes.Ldc_I4, (int)FenBrowser.FenEngine.Core.Interfaces.ValueType.Number);
            il.Emit(OpCodes.Bne_Un, slowLabel);

            // Fast path: Direct double comparison
            il.Emit(OpCodes.Ldloca, _tempL);
            il.Emit(OpCodes.Ldfld, ValueNumberField);
            il.Emit(OpCodes.Ldloca, _tempR);
            il.Emit(OpCodes.Ldfld, ValueNumberField);
            il.Emit(ilOp);
            il.Emit(OpCodes.Br, endLabel);

            // Slow path
            il.MarkLabel(slowLabel);
            il.Emit(OpCodes.Ldloca, _tempL);
            il.Emit(OpCodes.Call, ValueToNumberMethod);
            il.Emit(OpCodes.Ldloca, _tempR);
            il.Emit(OpCodes.Call, ValueToNumberMethod);
            il.Emit(ilOp);

            il.MarkLabel(endLabel);
        }

        private void EmitComparisonOp(ILGenerator il, System.Reflection.Emit.OpCode ilOp)
        {
            EmitComparisonRaw(il, ilOp);
            il.Emit(OpCodes.Call, FromBooleanMethod);
        }

        private void EmitCall(ILGenerator il, int argCount)
        {
            il.Emit(OpCodes.Stloc, _tempVal); // fn
            il.Emit(OpCodes.Stloc, _tempL);   // thisCtx
            
            var argArray = il.DeclareLocal(typeof(FenValue[]));
            il.Emit(OpCodes.Ldc_I4, argCount);
            il.Emit(OpCodes.Newarr, typeof(FenValue));
            il.Emit(OpCodes.Stloc, argArray);

            for (int i = argCount - 1; i >= 0; i--)
            {
                il.Emit(OpCodes.Stloc, _tempR); // pops argI
                il.Emit(OpCodes.Ldloc, argArray);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, _tempR);
                il.Emit(OpCodes.Stelem, typeof(FenValue));
            }

            il.Emit(OpCodes.Ldloc, _tempVal);
            il.Emit(OpCodes.Ldloc, _tempL);
            il.Emit(OpCodes.Ldloc, argArray);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, JitCallMethod);
        }

        private void EmitCreateArray(ILGenerator il, int count)
        {
            var argArray = il.DeclareLocal(typeof(FenValue[]));
            il.Emit(OpCodes.Ldc_I4, count);
            il.Emit(OpCodes.Newarr, typeof(FenValue));
            il.Emit(OpCodes.Stloc, argArray);

            for (int i = count - 1; i >= 0; i--)
            {
                il.Emit(OpCodes.Stloc, _tempVal);
                il.Emit(OpCodes.Ldloc, argArray);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, _tempVal);
                il.Emit(OpCodes.Stelem, typeof(FenValue));
            }

            il.Emit(OpCodes.Ldloc, argArray);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, JitCreateArrayMethod);
        }

        private void EmitCreateObject(ILGenerator il, int pairCount)
        {
            var keys = il.DeclareLocal(typeof(string[]));
            var values = il.DeclareLocal(typeof(FenValue[]));
            
            il.Emit(OpCodes.Ldc_I4, pairCount);
            il.Emit(OpCodes.Newarr, typeof(string));
            il.Emit(OpCodes.Stloc, keys);
            
            il.Emit(OpCodes.Ldc_I4, pairCount);
            il.Emit(OpCodes.Newarr, typeof(FenValue));
            il.Emit(OpCodes.Stloc, values);

            for (int i = pairCount - 1; i >= 0; i--)
            {
                il.Emit(OpCodes.Stloc, _tempVal); // Value
                il.Emit(OpCodes.Stloc, _tempR);   // Key (PushConst pushed it as FenValue)
                
                il.Emit(OpCodes.Ldloc, values);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, _tempVal);
                il.Emit(OpCodes.Stelem, typeof(FenValue));
                
                il.Emit(OpCodes.Ldloc, keys);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloca, _tempR);
                il.Emit(OpCodes.Call, ValueToStringMethod);
                il.Emit(OpCodes.Stelem, typeof(string));
            }

            il.Emit(OpCodes.Ldloc, keys);
            il.Emit(OpCodes.Ldloc, values);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, JitCreateObjectMethod);
        }
    }
}

