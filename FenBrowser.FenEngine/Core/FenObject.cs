using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript object in FenEngine.
    /// Updated to use PropertyDescriptor for ES5.1 compliance.
    /// </summary>
    public class FenObject : IObject
    {
        /// <summary>
        /// Global default prototype for all FenObject instances.
        /// Set by FenRuntime during initialization to Object.prototype.
        /// </summary>
        public static IObject DefaultPrototype { get; set; }

        /// <summary>
        /// Default prototype for array instances. Set by Interpreter when GetArrayPrototype() is first called.
        /// Used by FenRuntime to create properly-prototyped arrays.
        /// </summary>
        public static IObject DefaultArrayPrototype { get; set; }

        private readonly Dictionary<string, PropertyDescriptor> _properties = new Dictionary<string, PropertyDescriptor>();
        private IObject _prototype;
        private bool _extensible = true;

        // Recursion guard for accessor getter/setter invocations (prevents stack overflow)
        [ThreadStatic]
        private static int _accessorDepth;
        private const int MAX_ACCESSOR_DEPTH = 64;

        public FenObject()
        {
            // Inherit from Object.prototype by default.
            // Guard: don't self-reference (objectPrototype itself is created before DefaultPrototype is set).
            if (DefaultPrototype != null && !ReferenceEquals(DefaultPrototype, this))
                _prototype = DefaultPrototype;
        }

        /// <summary>
        /// Factory method for creating array objects with the correct Array.prototype and [[Class]].
        /// Use this instead of new FenObject() whenever creating a JavaScript array.
        /// </summary>
        public static FenObject CreateArray()
        {
            var arr = new FenObject();
            arr.InternalClass = "Array";
            if (DefaultArrayPrototype != null && !ReferenceEquals(DefaultArrayPrototype, arr))
                arr.SetPrototype(DefaultArrayPrototype);
            return arr;
        }

        public object NativeObject { get; set; } // Holds underlying .NET object (Regex, Date, etc.)
        public string InternalClass { get; set; } = "Object"; // [[Class]] internal property
        public bool IsExtensible => _extensible;

        public virtual FenValue Get(string key, IExecutionContext context = null)
        {
            // PROXY TRAP: Get
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && (isProxy.Value.HasValue && isProxy.Value.Value.ToBoolean()))
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxyGet__", out var proxyGet) && (proxyGet.Value.HasValue && proxyGet.Value.Value.IsFunction))
                {
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxyGet.Value.Value.AsFunction();
                    return fn.Invoke(new FenValue[] { target.Value ?? FenValue.Undefined, FenValue.FromString(key), FenValue.FromObject(this) }, context);
                }
            }

            if (_properties.TryGetValue(key, out var desc))
            {
                // Accessor descriptor: invoke getter
                if (desc.IsAccessor && desc.Getter != null)
                {
                    if (++_accessorDepth > MAX_ACCESSOR_DEPTH)
                    {
                        _accessorDepth--;
                        return FenValue.Undefined;
                    }
                    try
                    {
                        return desc.Getter.Invoke(new FenValue[] { FenValue.FromObject(this) }, context);
                    }
                    finally
                    {
                        _accessorDepth--;
                    }
                }
                return desc.Value ?? FenValue.Undefined;
            }
            
            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.Get(key, context);

            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            // PROXY TRAP: Set
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && (isProxy.Value.HasValue && isProxy.Value.Value.ToBoolean()))
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_properties.TryGetValue("__proxySet__", out var proxySet) && (proxySet.Value.HasValue && proxySet.Value.Value.IsFunction))
                {
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxySet.Value.Value.AsFunction();
                    fn.Invoke(new FenValue[] { target.Value ?? FenValue.Undefined, FenValue.FromString(key), value, FenValue.FromObject(this) }, context);
                    return;
                }
            }

            // Check if property exists
            if (_properties.TryGetValue(key, out var existing))
            {
                // Accessor descriptor: invoke setter
                if (existing.IsAccessor)
                {
                    if (existing.Setter != null)
                    {
                        if (++_accessorDepth > MAX_ACCESSOR_DEPTH)
                        {
                            _accessorDepth--;
                            return;
                        }
                        try
                        {
                            existing.Setter.Invoke(new FenValue[] { FenValue.FromObject(this), value }, context);
                        }
                        finally
                        {
                            _accessorDepth--;
                        }
                    }
                    // No setter: silently fail in non-strict mode
                    return;
                }
                
                // Data descriptor: check writable
                if (existing.Writable == false)
                    return; // Silently fail in non-strict mode
                    
                existing.Value = value;
                _properties[key] = existing;
                return;
            }
            
            // New property: check extensibility
            if (!_extensible)
                return; // Silently fail in non-strict mode
            
            // Check prototype chain for inherited accessors
            IObject proto = _prototype;
            while (proto != null)
            {
                if (proto is FenObject fenProto && fenProto._properties.TryGetValue(key, out var inherited))
                {
                    if (inherited.IsAccessor && inherited.Setter != null)
                    {
                        if (++_accessorDepth > MAX_ACCESSOR_DEPTH)
                        {
                            _accessorDepth--;
                            return;
                        }
                        try
                        {
                            inherited.Setter.Invoke(new FenValue[] { FenValue.FromObject(this), value }, context);
                        }
                        finally
                        {
                            _accessorDepth--;
                        }
                        return;
                    }
                }
                proto = proto.GetPrototype();
            }

            // if (key == "throws" || key == "assert")
            // {
            //     Console.WriteLine($"[DEBUG] FenObject.Set: {key} = {value.Type} on {this.InternalClass}");
            // }
            _properties[key] = PropertyDescriptor.DataDefault(value);
        }

        public virtual bool Has(string key, IExecutionContext context = null)
        {
            // PROXY TRAP: Has
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && (isProxy.Value.HasValue && isProxy.Value.Value.ToBoolean()))
            {
                if (_properties.TryGetValue("__proxyHas__", out var proxyHas) && (proxyHas.Value.HasValue && proxyHas.Value.Value.IsFunction))
                {
                    _properties.TryGetValue("__proxyTarget__", out var target);
                    var fn = proxyHas.Value.Value.AsFunction();
                    var res = fn.Invoke(new FenValue[] { target.Value ?? FenValue.Undefined, FenValue.FromString(key) }, context);
                    return res.ToBoolean();
                }
            }

            if (_properties.ContainsKey(key)) return true;
            if (_prototype != null) return _prototype.Has(key, context);
            return false;
        }

        public virtual bool Delete(string key, IExecutionContext context = null)
        {
            if (_properties.TryGetValue(key, out var desc))
            {
                if (desc.Configurable == false)
                    return false; // Cannot delete non-configurable property
                return _properties.Remove(key);
            }
            return true; // Property doesn't exist, deletion "succeeds"
        }

        public virtual IEnumerable<string> Keys(IExecutionContext context = null)
        {
            // PROXY TRAP: OwnKeys
            if (_properties.TryGetValue("__isProxy__", out var isProxy) && (isProxy.Value.HasValue && isProxy.Value.Value.ToBoolean()))
            {
                if (_properties.TryGetValue("__proxyOwnKeys__", out var proxyKeys) && (proxyKeys.Value.HasValue && proxyKeys.Value.Value.IsFunction))
                {
                    var fn = proxyKeys.Value.Value.AsFunction();
                    fn.Invoke(new FenValue[0], context);
                }
            }
            
            foreach (var kvp in _properties)
            {
                // Only yield enumerable properties, skip internal ones
                if ((kvp.Value.Enumerable ?? false) && !kvp.Key.StartsWith("__") && !kvp.Key.StartsWith("@@"))
                    yield return kvp.Key;
            }
        }
        
        /// <summary>
        /// Get all own property names (including non-enumerable).
        /// </summary>
        public virtual IEnumerable<string> GetOwnPropertyNames()
        {
            foreach (var key in _properties.Keys)
            {
                if (!key.StartsWith("__") && !key.StartsWith("@@"))
                    yield return key;
            }
        }
        
        /// <summary>
        /// Define a property with specific descriptor.
        /// </summary>
        public virtual bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            PropertyDescriptor current;
            bool exists = _properties.TryGetValue(key, out current);

            if (!exists)
            {
                if (!_extensible) return false;
                
                // create new property with defaults if missing
                var newDesc = desc;
                if (newDesc.IsData)
                {
                    if (!newDesc.Writable.HasValue) newDesc.Writable = false;
                    if (!newDesc.Value.HasValue) newDesc.Value = FenValue.Undefined; // Actually Value is struct FenValue, verify default
                }
                if (!newDesc.Enumerable.HasValue) newDesc.Enumerable = false;
                if (!newDesc.Configurable.HasValue) newDesc.Configurable = false;
                
                _properties[key] = newDesc;
                return true;
            }

            // Existing property
            
            // If descriptor is empty, return true
            if (!desc.Value.HasValue && !desc.Writable.HasValue && desc.Getter == null && desc.Setter == null && !desc.Enumerable.HasValue && !desc.Configurable.HasValue)
                return true;

            // If current is not configurable
            if (current.Configurable == false)
            {
                if (desc.Configurable == true) return false;
                if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable) return false;
            }

            if (desc.IsGenericDescriptor())
            {
                // updates to enumerable/configurable handled above
            }
            else if (current.IsData != desc.IsData)
            {
                // Functionally different (Accessor <-> Data)
                if (current.Configurable == false) return false;
                
                // Preservation of attributes logic handled by caller or just overwrite?
                // Test262 usually requires "preserve what is not specified". 
                // Since `desc` has nullables, we can merge.
            }
            else if (current.IsData && desc.IsData)
            {
                if (current.Configurable == false && current.Writable == false)
                {
                    if (desc.Writable == true) return false;
                    if (desc.Value.HasValue && !desc.Value.Value.StrictEquals(current.Value)) return false;
                }
            }
            else if (current.IsAccessor && desc.IsAccessor)
            {
                if (current.Configurable == false)
                {
                    if (desc.Getter != null && desc.Getter != current.Getter) return false;
                    if (desc.Setter != null && desc.Setter != current.Setter) return false;
                }
            }

            // Merge
            var merged = current;
            if (desc.Value.HasValue) merged.Value = desc.Value;
            if (desc.Writable.HasValue) merged.Writable = desc.Writable;
            if (desc.Getter != null) merged.Getter = desc.Getter;
            if (desc.Setter != null) merged.Setter = desc.Setter;
            if (desc.Enumerable.HasValue) merged.Enumerable = desc.Enumerable;
            if (desc.Configurable.HasValue) merged.Configurable = desc.Configurable;
            
            _properties[key] = merged;
            return true;
        }
        
        /// <summary>
        /// Get the property descriptor for an own property.
        /// </summary>
        public virtual PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_properties.TryGetValue(key, out var desc))
                return desc;
            return null;
        }
        
        /// <summary>
        /// Set raw value without descriptor checks (for internal use).
        /// </summary>
        public void SetDirect(string key, FenValue value)
        {
            _properties[key] = PropertyDescriptor.DataDefault(value);
        }
        
        /// <summary>
        /// Prevent future property additions.
        /// </summary>
        public bool PreventExtensions() { _extensible = false; return true; }
        
        /// <summary>
        /// Seal the object: prevent extensions and make all properties non-configurable.
        /// </summary>
        public bool Seal()
        {
            _extensible = false;
            var keys = new List<string>(_properties.Keys);
            foreach (var key in keys)
            {
                var desc = _properties[key];
                desc.Configurable = false;
                _properties[key] = desc;
            }
            return true;
        }
        
        /// <summary>
        /// Freeze the object: seal + make all data properties non-writable.
        /// </summary>
        public bool Freeze()
        {
            _extensible = false;
            var keys = new List<string>(_properties.Keys);
            foreach (var key in keys)
            {
                var desc = _properties[key];
                desc.Configurable = false;
                if (desc.IsData) desc.Writable = false;
                _properties[key] = desc;
            }
            return true;
        }
        
        /// <summary>
        /// Check if object is sealed.
        /// </summary>
        public bool IsSealed()
        {
            if (_extensible) return false;
            foreach (var desc in _properties.Values)
                if (desc.Configurable == true) return false;
            return true;
        }
        
        /// <summary>
        /// Check if object is frozen.
        /// </summary>
        public bool IsFrozen()
        {
            if (_extensible) return false;
            foreach (var desc in _properties.Values)
            {
                if (desc.Configurable == true) return false;
                if (desc.IsData && desc.Writable == true) return false;
            }
            return true;
        }

        public virtual IObject GetPrototype() => _prototype;
        public virtual void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
