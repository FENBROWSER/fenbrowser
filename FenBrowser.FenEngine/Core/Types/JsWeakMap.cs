using System;
using System.Runtime.CompilerServices;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsWeakMap : FenObject
    {
        private readonly ConditionalWeakTable<object, IValue> _storage = new ConditionalWeakTable<object, IValue>();

        public JsWeakMap()
        {
            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                var val = args.Length > 1 ? args[1] : FenValue.Undefined;

                if (!key.IsObject) throw new Exception("TypeError: WeakMap key must be an object");
                
                var keyObj = key.AsObject();
                if (keyObj  == null) throw new Exception("TypeError: WeakMap key cannot be null");

                try 
                {
                    _storage.Remove(keyObj); 
                    _storage.Add(keyObj, val);
                }
                catch { }
                
                return FenValue.FromObject(this);
            })));

            Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!key.IsObject) return FenValue.Undefined;
                
                var keyObj = key.AsObject();
                if (keyObj != null && _storage.TryGetValue(keyObj, out var val))
                    return (FenValue)val;
                
                return FenValue.Undefined;
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!key.IsObject) return FenValue.FromBoolean(false);
                
                var keyObj = key.AsObject();
                return FenValue.FromBoolean(keyObj != null && _storage.TryGetValue(keyObj, out _));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!key.IsObject) return FenValue.FromBoolean(false);
                
                var keyObj = key.AsObject();
                return FenValue.FromBoolean(keyObj != null && _storage.Remove(keyObj));
            })));
        }
    }
}
