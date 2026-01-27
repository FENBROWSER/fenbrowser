using System;
using System.Runtime.CompilerServices;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsWeakSet : FenObject
    {
        private readonly ConditionalWeakTable<object, object> _storage = new ConditionalWeakTable<object, object>();
        private static readonly object _present = new object();

        public JsWeakSet()
        {
            Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;

                if (!val.IsObject) throw new Exception("TypeError: WeakSet value must be an object");
                
                var valObj = val.AsObject();
                if (valObj  == null) throw new Exception("TypeError: WeakSet value cannot be null");

                try 
                {
                    _storage.Remove(valObj);
                    _storage.Add(valObj, _present);
                }
                catch { }
                
                return FenValue.FromObject(this);
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!val.IsObject) return FenValue.FromBoolean(false);
                
                var valObj = val.AsObject();
                return FenValue.FromBoolean(valObj != null && _storage.TryGetValue(valObj, out _));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (!val.IsObject) return FenValue.FromBoolean(false);
                
                var valObj = val.AsObject();
                return FenValue.FromBoolean(valObj != null && _storage.Remove(valObj));
            })));
        }
    }
}
