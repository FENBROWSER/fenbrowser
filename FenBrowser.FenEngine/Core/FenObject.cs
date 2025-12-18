using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript object in FenEngine
    /// </summary>
    public class FenObject : IObject
    {
        private readonly Dictionary<string, IValue> _properties = new Dictionary<string, IValue>();
        private IObject _prototype;
        public object NativeObject { get; set; } // Holds underlying .NET object (Regex, Date, etc.)

        public IValue Get(string key)
        {
            // PROXY TRAP: Get
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyGet__", out var proxyGet) && proxyGet is FenValue fnVal && fnVal.IsFunction)
                {
                    // Invoke proxy getter: (target, prop, receiver)
                    // Note: Receiver is 'this' (the proxy itself)
                    var fn = fnVal.AsFunction();
                    return fn.Invoke(new IValue[] { FenValue.FromString(key), FenValue.FromObject(this) }, null);
                }
            }

            if (_properties.TryGetValue(key, out var value))
                return value;
            
            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.Get(key);

            return FenValue.Undefined;
        }

        public void Set(string key, IValue value)
        {
            // PROXY TRAP: Set
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxySet__", out var proxySet) && proxySet is FenValue fnVal && fnVal.IsFunction)
                {
                    // Invoke proxy setter: (target, prop, value, receiver)
                    // Note: We don't have easy access to original target here unless stored, 
                    // but the closure in FenRuntime likely handles the target binding.
                    // We just pass the key and value and let the runtime-bound function handle it.
                    var fn = fnVal.AsFunction();
                    fn.Invoke(new IValue[] { FenValue.FromString(key), value, FenValue.FromObject(this) }, null);
                    return;
                }
            }

            _properties[key] = value;
        }

        public bool Has(string key)
        {
            // PROXY TRAP: Has
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyHas__", out var proxyHas) && proxyHas is FenValue fnVal && fnVal.IsFunction)
                {
                    var fn = fnVal.AsFunction();
                    var res = fn.Invoke(new IValue[] { FenValue.FromString(key) }, null);
                    return res.ToBoolean();
                }
            }

            if (_properties.TryGetValue(key, out var value)) return true;
            if (_prototype != null) return _prototype.Has(key);
            return false;
        }

        public bool Delete(string key)
        {
             // PROXY TRAP: DeleteProperty
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Note: DeleteProperty trap not yet fully wired in runtime, but placeholder checks won't hurt
            }
            return _properties.Remove(key);
        }

        public IEnumerable<string> Keys()
        {
             // PROXY TRAP: OwnKeys
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyOwnKeys__", out var proxyKeys) && proxyKeys is FenValue fnVal && fnVal.IsFunction)
                {
                    var fn = fnVal.AsFunction();
                    var res = fn.Invoke(new IValue[0], null);
                    // Convert result to enumerable string
                    // Assuming result is Array-like
                    var list = new List<string>();
                    // Basic handling: check if it's an array and iterate (simplified)
                     if (res.IsObject) {
                        var arr = res.AsObject();
                        // TODO: robust iteration
                     }
                     // Fallback for now or partial implementation
                }
            }
            
            // Filter out internal properties (starting with __) and Symbols (starting with @@)
            foreach (var k in _properties.Keys)
            {
                if (!k.StartsWith("__") && !k.StartsWith("@@"))
                    yield return k;
            }
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
