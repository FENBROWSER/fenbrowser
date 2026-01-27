using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Jit
{
    public static class JitRuntime
    {
        private static Interpreter _interpreter;

        public static void Initialize(Interpreter interpreter)
        {
            _interpreter = interpreter;
        }

        public static FenValue Call(FenValue fn, FenValue thisCtx, FenValue[] args, IExecutionContext context)
        {
            if (_interpreter == null) return FenValue.FromString("Error: JIT Runtime not initialized");
            return _interpreter.ApplyFunction(fn, new List<FenValue>(args), context, thisCtx);
        }

        public static FenValue GetProp(FenValue obj, string prop, IExecutionContext context)
        {
            if (obj.IsObject)
            {
                return obj.AsObject()?.Get(prop, context) ?? FenValue.Undefined;
            }
            if (obj.IsString && prop == "length")
            {
                return FenValue.FromNumber(obj.AsString().Length);
            }
            return FenValue.Undefined;
        }

        public static FenValue SetProp(FenValue obj, string prop, FenValue value, IExecutionContext context)
        {
            if (obj.IsObject)
            {
                obj.AsObject()?.Set(prop, value, context);
            }
            return value;
        }

        public static FenValue CreateArray(FenValue[] elements, IExecutionContext context)
        {
            var arr = new FenObject();
            for (int i = 0; i < elements.Length; i++)
            {
                arr.Set(i.ToString(), elements[i], context);
            }
            arr.Set("length", FenValue.FromNumber(elements.Length), context);
            return FenValue.FromObject(arr);
        }

        public static FenValue CreateObject(string[] keys, FenValue[] values, IExecutionContext context)
        {
            var obj = new FenObject();
            for (int i = 0; i < keys.Length; i++)
            {
                obj.Set(keys[i], values[i], context);
            }
            return FenValue.FromObject(obj);
        }
    }
}
