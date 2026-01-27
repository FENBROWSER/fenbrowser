using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript object in FenEngine.
    /// Updated to use FenValue structs for properties to eliminate boxing.
    /// </summary>
    public class FenObject : IObject
    {
        private readonly Dictionary<string, FenValue> _properties = new Dictionary<string, FenValue>();
        private IObject _prototype;
        public object NativeObject { get; set; } // Holds underlying .NET object (Regex, Date, etc.)

        public virtual FenValue Get(string key, IExecutionContext context = null)
        {
            // PROXY TRAP: Get
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Phase D spec 2.3: Proxy traps MUST NOT execute during Measure, Layout, or Paint
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxyGet__", out var proxyGet) && proxyGet.IsFunction)
                {
                    // Invoke proxy getter: (target, prop, receiver)
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxyGet.AsFunction();
                    return fn.Invoke(new FenValue[] { target, FenValue.FromString(key), FenValue.FromObject(this) }, context);
                }
            }

            if (_properties.TryGetValue(key, out var value))
                return value;
            
            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.Get(key, context);

            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            // PROXY TRAP: Set
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                // Phase D spec 2.3: Proxy traps MUST NOT execute during Measure, Layout, or Paint
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxySet__", out var proxySet) && proxySet.IsFunction)
                {
                    // Invoke proxy setter: (target, prop, value, receiver)
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxySet.AsFunction();
                    fn.Invoke(new FenValue[] { target, FenValue.FromString(key), value, FenValue.FromObject(this) }, context);
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
                if (_properties.TryGetValue("__proxyHas__", out var proxyHas) && proxyHas.IsFunction)
                {
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxyHas.AsFunction();
                    var res = fn.Invoke(new FenValue[] { target, FenValue.FromString(key) }, context);
                    return res.ToBoolean();
                }
            }

            if (_properties.TryGetValue(key, out var value)) return true;
            if (_prototype != null) return _prototype.Has(key, context);
            return false;
        }

        public virtual bool Delete(string key, IExecutionContext context = null)
        {
            return _properties.Remove(key);
        }

        public virtual IEnumerable<string> Keys(IExecutionContext context = null)
        {
             // PROXY TRAP: OwnKeys
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && isProxy.ToBoolean())
            {
                if (_properties.TryGetValue("__proxyOwnKeys__", out var proxyKeys) && proxyKeys.IsFunction)
                {
                    var fn = proxyKeys.AsFunction();
                    var res = fn.Invoke(new FenValue[0], context);
                    // Simplified: in a real implementation we would convert the returned array to an enumerable
                }
            }
            
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
