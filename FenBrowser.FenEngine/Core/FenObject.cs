using System;
using System.Collections.Generic;
using System.Reflection;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;

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
        /// Default prototype for array instances. Set by runtime initialization.
        /// Used by FenRuntime to create properly-prototyped arrays.
        /// </summary>
        public static IObject DefaultArrayPrototype { get; set; }

        /// <summary>
        /// Default prototype for iterator instances (shared %IteratorPrototype%).
        /// Set by FenRuntime during initialization; used by runtime array/string iterators.
        /// </summary>
        public static FenObject DefaultIteratorPrototype { get; set; }

        private Shape _shape = Shape.RootShape;
        private PropertyDescriptor[] _properties = new PropertyDescriptor[4];
        private IObject _prototype;
        private bool _extensible = true;

        // Recursion guard for accessor getter/setter invocations (prevents stack overflow)
        [ThreadStatic]
        private static int _accessorDepth;
        private const int MAX_ACCESSOR_DEPTH = 64;
        // Guard named-window lookups from re-entering through DOM/property plumbing and recursing indefinitely.
        [ThreadStatic]
        private static int _windowNamedLookupDepth;
        private const int MAX_WINDOW_NAMED_LOOKUP_DEPTH = 32;
        [ThreadStatic]
        private static int _hasDepth;
        private const int MAX_HAS_DEPTH = 256;

        private static bool TryUnwrapJsThrownValue(Exception ex, out FenValue thrownValue)
        {
            thrownValue = FenValue.Undefined;
            if (ex == null)
            {
                return false;
            }

            var exType = ex.GetType();
            if (!string.Equals(exType.Name, "JsUncaughtException", StringComparison.Ordinal))
            {
                return false;
            }

            var thrownProp = exType.GetProperty("ThrownValue", BindingFlags.Public | BindingFlags.Instance);
            if (thrownProp == null || thrownProp.PropertyType != typeof(FenValue))
            {
                return false;
            }

            var raw = thrownProp.GetValue(ex);
            if (raw is FenValue fen)
            {
                thrownValue = fen;
                return true;
            }

            return false;
        }

        private static void RethrowUnwrappedJsValue(Exception ex)
        {
            if (!TryUnwrapJsThrownValue(ex, out var thrown))
            {
                throw ex;
            }

            throw new JsThrownValueException(thrown);
        }

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

            // JS arrays must always expose a numeric length; many runtime paths cast this directly.
            arr.Set("length", FenValue.FromNumber(0));
            return arr;
        }

        public object NativeObject { get; set; } // Holds underlying .NET object (Regex, Date, etc.)
        public string InternalClass { get; set; } = "Object"; // [[Class]] internal property
        public bool IsExtensible => _extensible;

        public virtual FenValue Get(string key, IExecutionContext context = null)
        {
            return GetWithReceiver(key, FenValue.FromObject(this), context);
        }

        public virtual FenValue GetWithReceiver(string key, FenValue receiver, IExecutionContext context = null)
        {
            // PROXY TRAP: Get
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) && 
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_shape.TryGetPropertyOffset("__proxyGet__", out var pgIdx) && 
                    (_properties[pgIdx].Value.HasValue && _properties[pgIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    
                    var fn = _properties[pgIdx].Value.Value.AsFunction();
                    return fn.Invoke(new FenValue[] { target ?? FenValue.Undefined, FenValue.FromString(key), receiver }, context);
                }
            }

            if (_shape.TryGetPropertyOffset(key, out var index))
            {
                var desc = _properties[index];
                // Treat tombstoned slots from Delete() as absent so prototype lookup still works.
                var isTombstoned = !desc.Value.HasValue && desc.Getter == null && desc.Setter == null && desc.Enumerable == false;
                if (!isTombstoned)
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
                            return desc.Getter.Invoke(Array.Empty<FenValue>(), context, receiver);
                        }
                        finally
                        {
                            _accessorDepth--;
                        }
                    }
                    return desc.Value ?? FenValue.Undefined;
                }
            }
            
            if (TryResolveWindowNamedProperty(key, receiver, context, out var namedWindowValue))
            {
                return namedWindowValue;
            }

            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.GetWithReceiver(key, receiver, context);

            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            SetWithReceiver(key, value, FenValue.FromObject(this), context);
        }

        public virtual void SetWithReceiver(string key, FenValue value, FenValue receiver, IExecutionContext context = null)
        {
            // Spec guard for __proto__ assignment on non-extensible ordinary objects.
            if (key == "__proto__" && !_extensible && (value.IsObject || value.IsFunction || value.IsNull))
            {
                var nextProto = value.IsNull ? null : value.AsObject();
                if (!ReferenceEquals(_prototype, nextProto))
                {
                    throw new FenTypeError("TypeError: Cannot set prototype");
                }

                return;
            }

            // PROXY TRAP: Set
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) && 
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                if (_shape.TryGetPropertyOffset("__proxySet__", out var psIdx) && 
                    (_properties[psIdx].Value.HasValue && _properties[psIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    
                    var fn = _properties[psIdx].Value.Value.AsFunction();
                    fn.Invoke(new FenValue[] { target ?? FenValue.Undefined, FenValue.FromString(key), value, receiver }, context);
                    return;
                }
            }

            // Check if property exists in fast storage
            if (_shape.TryGetPropertyOffset(key, out var existingIndex))
            {
                var existing = _properties[existingIndex];
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
                            existing.Setter.Invoke(new FenValue[] { value }, context, receiver);
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
                _properties[existingIndex] = existing;
                return;
            }
            
            // New property: check extensibility
            if (!_extensible)
                return; // Silently fail in non-strict mode
            
            // Check prototype chain for inherited accessors
            IObject proto = _prototype;
            while (proto != null)
            {
                if (proto is FenObject fenProto)
                {

                    // This checks proto without throwing away its caching structure if it's a FenObject
                    if (proto.Has(key) && fenProto._shape.TryGetPropertyOffset(key, out var protoIdx))
                    {
                        var inherited = fenProto._properties[protoIdx];
                        if (inherited.IsAccessor && inherited.Setter != null)
                        {
                            if (++_accessorDepth > MAX_ACCESSOR_DEPTH)
                            {
                                _accessorDepth--;
                                return;
                            }
                            try
                            {
                                inherited.Setter.Invoke(new FenValue[] { value }, context, receiver);
                            }
                            finally
                            {
                                _accessorDepth--;
                            }
                            return;
                        }
                    }
                }

                proto = proto.GetPrototype();
            }

            // Transition Shape and Append New Property
            _shape = _shape.TransitionTo(key);
            int newIndex = _shape.PropertyCount - 1;
            
            // Resize array if needed
            if (newIndex >= _properties.Length)
            {
                Array.Resize(ref _properties, _properties.Length * 2);
            }
            
            _properties[newIndex] = PropertyDescriptor.DataDefault(value);
        }

        public virtual bool Has(string key, IExecutionContext context = null)
        {
            if (++_hasDepth > MAX_HAS_DEPTH)
            {
                _hasDepth--;
                return false;
            }

            try
            {
                // PROXY TRAP: Has
                if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) && 
                    (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
                {
                    if (_shape.TryGetPropertyOffset("__proxyHas__", out var phIdx) && 
                        (_properties[phIdx].Value.HasValue && _properties[phIdx].Value.Value.IsFunction))
                    {
                        if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                        var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;

                        var fn = _properties[phIdx].Value.Value.AsFunction();
                        var res = fn.Invoke(new FenValue[] { target ?? FenValue.Undefined, FenValue.FromString(key) }, context);
                        return res.ToBoolean();
                    }
                }

                if (_shape.TryGetPropertyOffset(key, out _)) return true;
                if (TryResolveWindowNamedProperty(key, FenValue.FromObject(this), context, out _)) return true;
                if (_prototype != null) return _prototype.Has(key, context);
                return false;
            }
            finally
            {
                _hasDepth--;
            }
        }

        private bool TryResolveWindowNamedProperty(string key, FenValue receiver, IExecutionContext context, out FenValue value)
        {
            value = FenValue.Undefined;
            if (++_windowNamedLookupDepth > MAX_WINDOW_NAMED_LOOKUP_DEPTH)
            {
                _windowNamedLookupDepth--;
                return false;
            }
            try
            {
                if (string.IsNullOrEmpty(key) || string.Equals(key, "document", StringComparison.Ordinal) || string.Equals(key, "__fen_window_named_access__", StringComparison.Ordinal))
                {
                    return false;
                }
                if (!_shape.TryGetPropertyOffset("__fen_window_named_access__", out var markerIdx))
                {
                    return false;
                }
                var marker = _properties[markerIdx].Value;
                if (!marker.HasValue || !marker.Value.ToBoolean())
                {
                    return false;
                }
                FenValue doc;
                if (_shape.TryGetPropertyOffset("document", out var docIdx))
                {
                    doc = _properties[docIdx].Value ?? FenValue.Undefined;
                }
                else if (_prototype != null)
                {
                    if (_prototype is FenObject fenProto)
                    {
                        doc = fenProto.GetWithReceiver("document", receiver, context);
                    }
                    else
                    {
                        doc = _prototype.Get("document", context);
                    }
                }
                else
                {
                    return false;
                }
                if (!doc.IsObject)
                {
                    return false;
                }
                var docObj = doc.AsObject();
                if (docObj == null)
                {
                    return false;
                }
                var getById = docObj.Get("getElementById", context);
                if (!getById.IsFunction)
                {
                    return false;
                }
                var found = getById.AsFunction().Invoke(new[] { FenValue.FromString(key) }, context, doc);
                if (found.IsNull || found.IsUndefined)
                {
                    return false;
                }
                value = found;
                return true;
            }
            finally
            {
                _windowNamedLookupDepth--;
            }
        }
        public virtual bool Delete(string key, IExecutionContext context = null)
        {
            if (_shape.TryGetPropertyOffset(key, out var index))
            {
                var desc = _properties[index];
                if (desc.Configurable == false)
                    return false; // Cannot delete non-configurable property
                    
                // Deleting invalidates the fast path for this specific object, 
                // but we can't easily rebuild array index maps at runtime efficiently.
                // For now, we "tombstone" it by removing enumerability and setting value to undefined/empty.
                desc.Value = null;
                desc.Enumerable = false;
                desc.Configurable = true;
                desc.Writable = true;
                desc.Getter = null;
                desc.Setter = null;
                _properties[index] = desc;
                return true;
            }
            return true; // Property doesn't exist, deletion "succeeds"
        }

        public virtual IEnumerable<string> Keys(IExecutionContext context = null)
        {
            // PROXY TRAP: OwnKeys
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) && 
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                if (_shape.TryGetPropertyOffset("__proxyOwnKeys__", out var pkIdx) && 
                    (_properties[pkIdx].Value.HasValue && _properties[pkIdx].Value.Value.IsFunction))
                {
                    var fn = _properties[pkIdx].Value.Value.AsFunction();
                    fn.Invoke(new FenValue[0], context);
                }
            }
            
            foreach (var key in _shape.GetPropertyNames())
            {
                if (_shape.TryGetPropertyOffset(key, out var idx))
                {
                    var desc = _properties[idx];
                    // Skip deleted / tombstoned properties
                    if (!desc.Value.HasValue && desc.Getter == null && desc.Setter == null && desc.Enumerable == false) continue;
                    
                    if ((desc.Enumerable ?? false) && !key.StartsWith("__") && !key.StartsWith("@@"))
                        yield return key;
                }
            }
        }
        
        /// <summary>
        /// Get all own property names (including non-enumerable).
        /// </summary>
        public virtual IEnumerable<string> GetOwnPropertyNames()
        {
            foreach (var key in _shape.GetPropertyNames())
            {
                if (_shape.TryGetPropertyOffset(key, out var idx))
                {
                    var desc = _properties[idx];
                    if (!desc.Value.HasValue && desc.Getter == null && desc.Setter == null && desc.Enumerable == false) continue; // Skip bounds/deleted

                    if (!key.StartsWith("__") && !key.StartsWith("@@"))
                        yield return key;
                }
            }
        }
        
        /// <summary>
        /// Define a property with specific descriptor.
        /// </summary>
        public virtual bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            // PROXY TRAP: defineProperty
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) &&
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                if (_shape.TryGetPropertyOffset("__proxyDefineProperty__", out var pdIdx) &&
                    (_properties[pdIdx].Value.HasValue && _properties[pdIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    var fn = _properties[pdIdx].Value.Value.AsFunction();

                    var descObj = new FenObject();
                    if (desc.Value.HasValue) descObj.Set("value", desc.Value.Value);
                    if (desc.Writable.HasValue) descObj.Set("writable", FenValue.FromBoolean(desc.Writable.Value));
                    if (desc.Getter != null) descObj.Set("get", FenValue.FromFunction(desc.Getter));
                    if (desc.Setter != null) descObj.Set("set", FenValue.FromFunction(desc.Setter));
                    if (desc.Enumerable.HasValue) descObj.Set("enumerable", FenValue.FromBoolean(desc.Enumerable.Value));
                    if (desc.Configurable.HasValue) descObj.Set("configurable", FenValue.FromBoolean(desc.Configurable.Value));

                    FenValue trapResult;
                    try
                    {
                        trapResult = fn.Invoke(new FenValue[] { target ?? FenValue.Undefined, FenValue.FromString(key), FenValue.FromObject(descObj) }, null);
                    }
                    catch (Exception ex)
                    {
                        RethrowUnwrappedJsValue(ex);
                        throw;
                    }
                    return trapResult.ToBoolean();
                }
            }

            PropertyDescriptor current;
            bool exists = _shape.TryGetPropertyOffset(key, out var index);

            if (!exists)
            {
                if (!_extensible) return false;
                
                // create new property with defaults if missing
                var newDesc = desc;
                if (newDesc.IsData)
                {
                    if (!newDesc.Writable.HasValue) newDesc.Writable = false;
                    if (!newDesc.Value.HasValue) newDesc.Value = FenValue.Undefined; 
                }
                if (!newDesc.Enumerable.HasValue) newDesc.Enumerable = false;
                if (!newDesc.Configurable.HasValue) newDesc.Configurable = false;
                
                _shape = _shape.TransitionTo(key);
                index = _shape.PropertyCount - 1;
                
                if (index >= _properties.Length)
                {
                    Array.Resize(ref _properties, _properties.Length * 2);
                }
                
                _properties[index] = newDesc;
                return true;
            }

            // Existing property
            current = _properties[index];
            
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
            
            _properties[index] = merged;
            return true;
        }
        
        /// <summary>
        /// Get the property descriptor for an own property.
        /// </summary>
        public virtual PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            // PROXY TRAP: getOwnPropertyDescriptor
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) &&
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                if (_shape.TryGetPropertyOffset("__proxyGetOwnPropertyDescriptor__", out var gopdIdx) &&
                    (_properties[gopdIdx].Value.HasValue && _properties[gopdIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    var trapFn = _properties[gopdIdx].Value.Value.AsFunction();
                    FenValue trapResult;
                    try
                    {
                        trapResult = trapFn.Invoke(new FenValue[] { target ?? FenValue.Undefined, FenValue.FromString(key) }, null);
                    }
                    catch (Exception ex)
                    {
                        RethrowUnwrappedJsValue(ex);
                        throw;
                    }

                    if (trapResult.IsUndefined) return null;
                    if (!trapResult.IsObject)
                    {
                        throw new FenTypeError("TypeError: Proxy getOwnPropertyDescriptor trap must return an object or undefined");
                    }

                    var descObj = trapResult.AsObject();
                    var desc = new PropertyDescriptor
                    {
                        Enumerable = descObj.Get("enumerable", null).ToBoolean(),
                        Configurable = descObj.Get("configurable", null).ToBoolean()
                    };

                    var getVal = descObj.Get("get", null);
                    var setVal = descObj.Get("set", null);
                    bool hasGet = descObj.Has("get");
                    bool hasSet = descObj.Has("set");
                    if (hasGet || hasSet)
                    {
                        desc.Getter = getVal.IsFunction ? getVal.AsFunction() : null;
                        desc.Setter = setVal.IsFunction ? setVal.AsFunction() : null;
                    }
                    else
                    {
                        desc.Value = descObj.Get("value", null);
                        desc.Writable = descObj.Get("writable", null).ToBoolean();
                    }

                    return desc;
                }

                // No trap: forward to proxy target own descriptor.
                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var forwardedTargetIdx)) _shape.TryGetPropertyOffset("__target__", out forwardedTargetIdx);
                if (forwardedTargetIdx >= 0)
                {
                    var forwardedTarget = _properties[forwardedTargetIdx].Value;
                    if (forwardedTarget.HasValue && (forwardedTarget.Value.IsObject || forwardedTarget.Value.IsFunction))
                    {
                        return forwardedTarget.Value.AsObject()?.GetOwnPropertyDescriptor(key);
                    }
                }
            }

            if (_shape.TryGetPropertyOffset(key, out var index))
            {
                var desc = _properties[index];
                if (!desc.Value.HasValue && desc.Getter == null && desc.Setter == null && desc.Enumerable == false) return null; // Tombstoned
                return desc;
            }

            return null;
        }

        /// <summary>
        /// Set a built-in property: writable=true, enumerable=false, configurable=true.
        /// Use for prototype methods and built-in properties per the ES spec.
        /// </summary>
        public void SetBuiltin(string key, FenValue value) =>
            DefineOwnProperty(key, PropertyDescriptor.DataNonEnumerable(value));

        /// <summary>
        /// Set raw value without descriptor checks (for internal use).
        /// </summary>
        public void SetDirect(string key, FenValue value)
        {
            if (_shape.TryGetPropertyOffset(key, out var idx))
            {
                _properties[idx] = PropertyDescriptor.DataDefault(value);
                return;
            }
            
            _shape = _shape.TransitionTo(key);
            idx = _shape.PropertyCount - 1;
            
            if (idx >= _properties.Length)
            {
                Array.Resize(ref _properties, _properties.Length * 2);
            }
            
            _properties[idx] = PropertyDescriptor.DataDefault(value);
        }

        /// <summary>
        /// Read a direct own slot value without invoking accessors/proxy traps.
        /// Useful for runtime internal-slot style checks (e.g. Proxy internals).
        /// </summary>
        public bool TryGetDirect(string key, out FenValue value)
        {
            if (_shape.TryGetPropertyOffset(key, out var idx))
            {
                var desc = _properties[idx];
                if (desc.Value.HasValue)
                {
                    value = desc.Value.Value;
                    return true;
                }
            }

            value = FenValue.Undefined;
            return false;
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
            foreach (var key in _shape.GetPropertyNames())
            {
                if (_shape.TryGetPropertyOffset(key, out var idx))
                {
                    var desc = _properties[idx];
                    desc.Configurable = false;
                    _properties[idx] = desc;
                }
            }
            return true;
        }
        
        /// <summary>
        /// Freeze the object: seal + make all data properties non-writable.
        /// </summary>
        public bool Freeze()
        {
            _extensible = false;
            foreach (var key in _shape.GetPropertyNames())
            {
                if (_shape.TryGetPropertyOffset(key, out var idx))
                {
                    var desc = _properties[idx];
                    desc.Configurable = false;
                    if (desc.IsData) desc.Writable = false;
                    _properties[idx] = desc;
                }
            }
            return true;
        }
        
        /// <summary>
        /// Check if object is sealed.
        /// </summary>
        public bool IsSealed()
        {
            if (_extensible) return false;
            for (int i = 0; i < _shape.PropertyCount; i++)
                if (_properties[i].Configurable == true) return false;
            return true;
        }
        
        /// <summary>
        /// Check if object is frozen.
        /// </summary>
        public bool IsFrozen()
        {
            if (_extensible) return false;
            for (int i = 0; i < _shape.PropertyCount; i++)
            {
                if (_properties[i].Configurable == true) return false;
                if (_properties[i].IsData && _properties[i].Writable == true) return false;
            }
            return true;
        }

        public virtual IObject GetPrototype()
        {
            // PROXY TRAP: getPrototypeOf
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) &&
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                if (_shape.TryGetPropertyOffset("__proxyGetPrototypeOf__", out var gpoIdx) &&
                    (_properties[gpoIdx].Value.HasValue && _properties[gpoIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    var trapFn = _properties[gpoIdx].Value.Value.AsFunction();
                    FenValue trapResult;
                    try
                    {
                        trapResult = trapFn.Invoke(new FenValue[] { target ?? FenValue.Undefined }, null);
                    }
                    catch (Exception ex)
                    {
                        RethrowUnwrappedJsValue(ex);
                        throw;
                    }

                    if (trapResult.IsNull) return null;
                    if (trapResult.IsObject || trapResult.IsFunction) return trapResult.AsObject();
                    throw new FenTypeError("TypeError: Proxy getPrototypeOf trap must return an object or null");
                }

                // No trap: forward to proxy target prototype.
                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var forwardedTargetIdx)) _shape.TryGetPropertyOffset("__target__", out forwardedTargetIdx);
                if (forwardedTargetIdx >= 0)
                {
                    var forwardedTarget = _properties[forwardedTargetIdx].Value;
                    if (forwardedTarget.HasValue && (forwardedTarget.Value.IsObject || forwardedTarget.Value.IsFunction))
                    {
                        return forwardedTarget.Value.AsObject()?.GetPrototype();
                    }
                }
            }

            return _prototype;
        }
        public virtual bool TrySetPrototype(IObject prototype)
        {
            // PROXY TRAP: setPrototypeOf
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) &&
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                if (_shape.TryGetPropertyOffset("__proxySetPrototypeOf__", out var spoIdx) &&
                    (_properties[spoIdx].Value.HasValue && _properties[spoIdx].Value.Value.IsFunction))
                {
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx)) _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    var target = targetIdx >= 0 ? _properties[targetIdx].Value : FenValue.Undefined;
                    var trapFn = _properties[spoIdx].Value.Value.AsFunction();
                    FenValue trapResult;
                    try
                    {
                        trapResult = trapFn.Invoke(new FenValue[] { target ?? FenValue.Undefined, prototype != null ? FenValue.FromObject(prototype) : FenValue.Null }, null);
                    }
                    catch (Exception ex)
                    {
                        RethrowUnwrappedJsValue(ex);
                        throw;
                    }

                    return trapResult.ToBoolean();
                }

                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var forwardedTargetIdx)) _shape.TryGetPropertyOffset("__target__", out forwardedTargetIdx);
                if (forwardedTargetIdx >= 0)
                {
                    var forwardedTarget = _properties[forwardedTargetIdx].Value;
                    if (forwardedTarget.HasValue && (forwardedTarget.Value.IsObject || forwardedTarget.Value.IsFunction))
                    {
                        if (forwardedTarget.Value.AsObject() is FenObject fenTarget)
                        {
                            return fenTarget.TrySetPrototype(prototype);
                        }

                        forwardedTarget.Value.AsObject()?.SetPrototype(prototype);
                        return true;
                    }
                }
            }

            // Immutable-prototype exotic: Object.prototype may only have null prototype.
            if (ReferenceEquals(this, DefaultPrototype))
            {
                return prototype == null;
            }

            var current = _prototype;
            if (ReferenceEquals(current, prototype))
            {
                return true;
            }

            if (!_extensible)
            {
                return false;
            }

            // Ordinary cycle detection. If a Proxy is encountered in the candidate chain, stop checking.
            var p = prototype;
            while (p != null)
            {
                if (ReferenceEquals(p, this))
                {
                    return false;
                }

                if (p is FenObject fenP)
                {
                    if (fenP._shape.TryGetPropertyOffset("__isProxy__", out var proxyIdx) &&
                        (fenP._properties[proxyIdx].Value.HasValue && fenP._properties[proxyIdx].Value.Value.ToBoolean()))
                    {
                        break;
                    }

                    p = fenP._prototype;
                    continue;
                }

                p = p.GetPrototype();
            }

            _prototype = prototype;
            return true;
        }

        public virtual void SetPrototype(IObject prototype) => _prototype = prototype;

        /// <summary>
        /// Gets the current Shape (Hidden Class) of the object for Inline Caching
        /// </summary>
        public Shape GetShape() => _shape;

        /// <summary>
        /// Provides direct memory array access to the properties for Inline Caching
        /// </summary>
        public PropertyDescriptor[] GetPropertyStorage() => _properties;
    }
}
























