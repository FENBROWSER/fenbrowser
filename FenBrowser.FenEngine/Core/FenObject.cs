using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum

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

        public virtual IValue Get(string key, IExecutionContext context = null)
        {
            // PROXY TRAP: Get
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Phase D spec 2.3: Proxy traps MUST NOT execute during Measure, Layout, or Paint
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxyGet__", out var proxyGet) && proxyGet is FenValue fnVal && fnVal.IsFunction)
                {
                    // Invoke proxy getter: (target, prop, receiver)
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = fnVal.AsFunction();
                    return fn.Invoke(new IValue[] { target ?? FenValue.Undefined, FenValue.FromString(key), FenValue.FromObject(this) }, context);
                }
            }

            if (_properties.TryGetValue(key, out var value))
                return value;
            
            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.Get(key, context);

            return FenValue.Undefined;
        }

        public virtual void Set(string key, IValue value, IExecutionContext context = null)
        {
            // PROXY TRAP: Set
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Phase D spec 2.3: Proxy traps MUST NOT execute during Measure, Layout, or Paint
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxySet__", out var proxySet) && proxySet is FenValue fnVal && fnVal.IsFunction)
                {
                    // Invoke proxy setter: (target, prop, value, receiver)
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = fnVal.AsFunction();
                    fn.Invoke(new IValue[] { target ?? FenValue.Undefined, FenValue.FromString(key), value, FenValue.FromObject(this) }, context);
                    return;
                }
            }

            _properties[key] = value;
        }

        public virtual bool Has(string key, IExecutionContext context = null)
        {
            // PROXY TRAP: Has
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyHas__", out var proxyHas) && proxyHas is FenValue fnVal && fnVal.IsFunction)
                {
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = fnVal.AsFunction();
                    var res = fn.Invoke(new IValue[] { target ?? FenValue.Undefined, FenValue.FromString(key) }, context);
                    return res.ToBoolean();
                }
            }

            if (_properties.TryGetValue(key, out var value)) return true;
            if (_prototype != null) return _prototype.Has(key, context);
            return false;
        }

        public virtual bool Delete(string key, IExecutionContext context = null)
        {
             // PROXY TRAP: DeleteProperty
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Note: DeleteProperty trap not yet fully wired in runtime, but placeholder checks won't hurt
            }
            return _properties.Remove(key);
        }

        public virtual IEnumerable<string> Keys(IExecutionContext context = null)
        {
             // PROXY TRAP: OwnKeys
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyOwnKeys__", out var proxyKeys) && proxyKeys is FenValue fnVal && fnVal.IsFunction)
                {
                    var fn = fnVal.AsFunction();
                    var res = fn.Invoke(new IValue[0], context);
                    // Convert result to enumerable string
                    // Assuming result is Array-like
                    var list = new List<string>();
                    // Basic handling: check if it's an array and iterate (simplified)
                    // ...
                }
            }
            
            // Filter out internal properties (starting with __) and Symbols (starting with @@)
            foreach (var k in _properties.Keys)
            {
                if (!k.StartsWith("__") && !k.StartsWith("@@"))
                    yield return k;
            }
        }

        public virtual IObject GetPrototype() => _prototype;
        public virtual void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
