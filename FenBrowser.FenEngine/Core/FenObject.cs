using System;
using System.Collections.Generic;
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
        public FenRuntime OwningRuntime { get; internal set; }

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
        // ECMA-262 §9.1: Symbol-keyed own properties stored separately from string-keyed ones.
        // Key is the symbol's unique integer ID; value is the property descriptor.
        private Dictionary<long, PropertyDescriptor> _symbolProperties;
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
        // Guard prototype chain traversal from circular or excessively deep chains (prevents stack overflow)
        [ThreadStatic]
        private static int _protoChainDepth;
        private const int MAX_PROTO_CHAIN_DEPTH = 256;

        // Approximate allocation tracking for resource limits (per-thread, reset per script execution)
        [ThreadStatic]
        private static long _threadAllocatedBytes;
        private const int ESTIMATED_OBJECT_BYTES = 80; // Approximate per-object overhead (header + properties array + shape ref)

        private static bool TryUnwrapJsThrownValue(Exception ex, out FenValue thrownValue)
        {
            return JsThrownValueException.TryExtract(ex, out thrownValue);
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
            _threadAllocatedBytes += ESTIMATED_OBJECT_BYTES;
            OwningRuntime = FenRuntime.GetActiveRuntime();
            // Inherit from Object.prototype by default.
            // Guard: don't self-reference (objectPrototype itself is created before DefaultPrototype is set).
            var defaultPrototype = OwningRuntime?.ResolveObjectPrototypeForNewObject() ?? DefaultPrototype;
            if (defaultPrototype != null && !ReferenceEquals(defaultPrototype, this))
                _prototype = defaultPrototype;
        }

        /// <summary>Returns approximate bytes allocated by FenObject instances on this thread.</summary>
        public static long GetAllocatedBytes() => _threadAllocatedBytes;

        /// <summary>Resets the per-thread allocation counter (call at script execution start).</summary>
        public static void ResetAllocatedBytes() => _threadAllocatedBytes = 0;

        /// <summary>
        /// Factory method for creating array objects with the correct Array.prototype and [[Class]].
        /// Use this instead of new FenObject() whenever creating a JavaScript array.
        /// </summary>
        public static FenObject CreateArray()
        {
            var arr = new FenObject();
            arr.InternalClass = "Array";
            var defaultArrayPrototype = arr.OwningRuntime?.ResolveArrayPrototypeForNewArray() ?? DefaultArrayPrototype;
            if (defaultArrayPrototype != null && !ReferenceEquals(defaultArrayPrototype, arr))
                arr.SetPrototype(defaultArrayPrototype);

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

        public virtual FenValue Get(FenValue key, IExecutionContext context = null)
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

                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx))
                {
                    _shape.TryGetPropertyOffset("__target__", out targetIdx);
                }

                var target = targetIdx >= 0 && _properties[targetIdx].Value.HasValue
                    ? _properties[targetIdx].Value.Value
                    : FenValue.Undefined;
                if (_shape.TryGetPropertyOffset("__proxyGet__", out var pgIdx) && 
                    (_properties[pgIdx].Value.HasValue && _properties[pgIdx].Value.Value.IsFunction))
                {
                    FenValue handler = FenValue.Undefined;
                    if (!TryGetDirect("__proxyHandler__", out handler))
                    {
                        TryGetDirect("__handler__", out handler);
                    }

                    var fn = _properties[pgIdx].Value.Value.AsFunction();
                    return fn.Invoke(new FenValue[] { target, FenValue.FromString(key), receiver }, context, handler);
                }

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                {
                    return targetObject.GetWithReceiver(key, receiver, context);
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

            // Prototype chain lookup (with depth guard against circular/deep chains)
            if (_prototype != null)
            {
                if (++_protoChainDepth > MAX_PROTO_CHAIN_DEPTH)
                {
                    _protoChainDepth--;
                    return FenValue.Undefined;
                }
                try
                {
                    return _prototype.GetWithReceiver(key, receiver, context);
                }
                finally
                {
                    _protoChainDepth--;
                }
            }

            return FenValue.Undefined;
        }

        public virtual FenValue GetWithReceiver(FenValue key, FenValue receiver, IExecutionContext context = null)
        {
            if (!TryNormalizePropertyKey(key, context, out var stringKey, out var symbolKey))
            {
                return FenValue.Undefined;
            }

            if (symbolKey == null)
            {
                return GetWithReceiver(stringKey, receiver, context);
            }

            if (TryGetDirect("__isProxy__", out var proxyMarker) && proxyMarker.ToBoolean())
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

                TryGetDirect("__proxyTarget__", out var target);
                if (target.IsUndefined)
                {
                    TryGetDirect("__target__", out target);
                }

                if (TryGetDirect("__proxyGet__", out var proxyGet) && proxyGet.IsFunction)
                {
                    FenValue handler = FenValue.Undefined;
                    if (!TryGetDirect("__proxyHandler__", out handler))
                    {
                        TryGetDirect("__handler__", out handler);
                    }

                    return proxyGet.AsFunction().Invoke(
                        new[] { target, key, receiver },
                        context,
                        handler);
                }

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                {
                    return targetObject.GetWithReceiver(key, receiver, context);
                }
            }

            if (_symbolProperties != null && _symbolProperties.TryGetValue(symbolKey.Id, out var desc))
            {
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

            var legacyKey = symbolKey.ToPropertyKey();
            if (!string.IsNullOrEmpty(legacyKey))
            {
                var legacyValue = GetWithReceiver(legacyKey, receiver, context);
                if (!legacyValue.IsUndefined)
                {
                    return legacyValue;
                }
            }

            if (_prototype is FenObject fenProto)
            {
                if (++_protoChainDepth > MAX_PROTO_CHAIN_DEPTH)
                {
                    _protoChainDepth--;
                    return FenValue.Undefined;
                }
                try
                {
                    return fenProto.GetWithReceiver(key, receiver, context);
                }
                finally
                {
                    _protoChainDepth--;
                }
            }

            if (_prototype != null && !string.IsNullOrEmpty(legacyKey))
            {
                if (++_protoChainDepth > MAX_PROTO_CHAIN_DEPTH)
                {
                    _protoChainDepth--;
                    return FenValue.Undefined;
                }
                try
                {
                    return _prototype.GetWithReceiver(legacyKey, receiver, context);
                }
                finally
                {
                    _protoChainDepth--;
                }
            }

            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            SetWithReceiver(key, value, FenValue.FromObject(this), context);
        }

        public virtual void Set(FenValue key, FenValue value, IExecutionContext context = null)
        {
            SetWithReceiver(key, value, FenValue.FromObject(this), context);
        }

        /// <summary>
        /// ECMA-262 §9.1.9.1: Set with explicit strict-mode flag.
        /// In strict mode, throws TypeError for non-writable or no-setter-accessor properties.
        /// </summary>
        public void Set(string key, FenValue value, bool strict)
        {
            // Build a minimal context stub only when strict is true and we need it.
            // Simpler: forward to SetWithReceiver with a flag-bearing context shim.
            SetWithReceiverStrict(key, value, FenValue.FromObject(this), strict);
        }

        private void SetWithReceiverStrict(string key, FenValue value, FenValue receiver, bool strict)
        {
            // __proto__ guard
            if (key == "__proto__" && !_extensible && (value.IsObject || value.IsFunction || value.IsNull))
            {
                var nextProto = value.IsNull ? null : value.AsObject();
                if (!ReferenceEquals(_prototype, nextProto))
                    throw new FenTypeError("TypeError: Cannot set prototype");
                return;
            }

            // PROXY TRAP: Set
            if (_shape.TryGetPropertyOffset("__isProxy__", out var selfProxyIdx) &&
                (_properties[selfProxyIdx].Value.HasValue && _properties[selfProxyIdx].Value.Value.ToBoolean()))
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx))
                {
                    _shape.TryGetPropertyOffset("__target__", out targetIdx);
                }

                var target = targetIdx >= 0 && _properties[targetIdx].Value.HasValue
                    ? _properties[targetIdx].Value.Value
                    : FenValue.Undefined;
                if (_shape.TryGetPropertyOffset("__proxySet__", out var psIdx) &&
                    (_properties[psIdx].Value.HasValue && _properties[psIdx].Value.Value.IsFunction))
                {
                    FenValue handler = FenValue.Undefined;
                    if (!TryGetDirect("__proxyHandler__", out handler))
                    {
                        TryGetDirect("__handler__", out handler);
                    }

                    var fn = _properties[psIdx].Value.Value.AsFunction();
                    fn.Invoke(new FenValue[] { target, FenValue.FromString(key), value, receiver }, null, handler);
                    return;
                }

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                {
                    targetObject.SetWithReceiver(key, value, receiver, null);
                    return;
                }
            }

            if (_shape.TryGetPropertyOffset(key, out var existingIndex))
            {
                var existing = _properties[existingIndex];
                if (existing.IsAccessor)
                {
                    if (existing.Setter != null)
                    {
                        if (++_accessorDepth > MAX_ACCESSOR_DEPTH) { _accessorDepth--; return; }
                        try { existing.Setter.Invoke(new FenValue[] { value }, null, receiver); }
                        finally { _accessorDepth--; }
                        return;
                    }
                    // ECMA-262 §9.1.9.1 step 3.a – accessor with no setter
                    if (strict) throw new FenTypeError($"TypeError: Cannot set property '{key}' which has only a getter");
                    return;
                }
                if (existing.Writable == false)
                {
                    // ECMA-262 §9.1.9.1 step 4.a
                    if (strict) throw new FenTypeError($"TypeError: Cannot assign to read only property '{key}'");
                    return;
                }
                existing.Value = value;
                _properties[existingIndex] = existing;
                return;
            }

            if (!_extensible)
            {
                // ECMA-262 §9.1.9.1 step 5.a.i
                if (strict) throw new FenTypeError($"TypeError: Cannot add property '{key}', object is not extensible");
                return;
            }

            // Check prototype chain for inherited accessors
            IObject proto = _prototype;
            while (proto != null)
            {
                if (proto is FenObject fenProto && proto.Has(key) && fenProto._shape.TryGetPropertyOffset(key, out var protoIdx))
                {
                    var inherited = fenProto._properties[protoIdx];
                    if (inherited.IsAccessor && inherited.Setter != null)
                    {
                        if (++_accessorDepth > MAX_ACCESSOR_DEPTH) { _accessorDepth--; return; }
                        try { inherited.Setter.Invoke(new FenValue[] { value }, null, receiver); }
                        finally { _accessorDepth--; }
                        return;
                    }
                }
                proto = proto.GetPrototype();
            }

            _shape = _shape.TransitionTo(key);
            int newIndex = _shape.PropertyCount - 1;
            if (newIndex >= _properties.Length) Array.Resize(ref _properties, _properties.Length * 2);
            _properties[newIndex] = PropertyDescriptor.DataDefault(value);
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

                if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx))
                {
                    _shape.TryGetPropertyOffset("__target__", out targetIdx);
                }

                var target = targetIdx >= 0 && _properties[targetIdx].Value.HasValue
                    ? _properties[targetIdx].Value.Value
                    : FenValue.Undefined;
                if (_shape.TryGetPropertyOffset("__proxySet__", out var psIdx) && 
                    (_properties[psIdx].Value.HasValue && _properties[psIdx].Value.Value.IsFunction))
                {
                    FenValue handler = FenValue.Undefined;
                    if (!TryGetDirect("__proxyHandler__", out handler))
                    {
                        TryGetDirect("__handler__", out handler);
                    }

                    var fn = _properties[psIdx].Value.Value.AsFunction();
                    fn.Invoke(new FenValue[] { target, FenValue.FromString(key), value, receiver }, context, handler);
                    return;
                }

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                {
                    targetObject.SetWithReceiver(key, value, receiver, context);
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
                    // No setter: ECMA-262 §9.1.9.1 step 3.a – in strict mode throw TypeError
                    {
                        bool isStrict = context?.StrictMode == true || context?.Environment?.StrictMode == true;
                        if (isStrict)
                            throw new FenTypeError($"TypeError: Cannot set property '{key}' which has only a getter");
                    }
                    return;
                }
                
                // Data descriptor: check writable
                // ECMA-262 §9.1.9.1 step 4.a: in strict mode, throw TypeError for non-writable property.
                if (existing.Writable == false)
                {
                    bool isStrict = context?.StrictMode == true || context?.Environment?.StrictMode == true;
                    if (isStrict)
                        throw new FenTypeError($"TypeError: Cannot assign to read only property '{key}'");
                    return; // Silently fail in non-strict mode
                }

                existing.Value = value;
                _properties[existingIndex] = existing;
                return;
            }

            // New property: check extensibility
            // ECMA-262 §9.1.9.1 step 5.a.i: in strict mode, throw TypeError on non-extensible object.
            if (!_extensible)
            {
                bool isStrict = context?.StrictMode == true || context?.Environment?.StrictMode == true;
                if (isStrict)
                    throw new FenTypeError($"TypeError: Cannot add property '{key}', object is not extensible");
                return; // Silently fail in non-strict mode
            }
            
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

        public virtual void SetWithReceiver(FenValue key, FenValue value, FenValue receiver, IExecutionContext context = null)
        {
            if (!TryNormalizePropertyKey(key, context, out var stringKey, out var symbolKey))
            {
                return;
            }

            if (symbolKey == null)
            {
                SetWithReceiver(stringKey, value, receiver, context);
                return;
            }

            if (TryGetDirect("__isProxy__", out var proxyMarker) && proxyMarker.ToBoolean())
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

                TryGetDirect("__proxyTarget__", out var target);
                if (target.IsUndefined)
                {
                    TryGetDirect("__target__", out target);
                }

                if (TryGetDirect("__proxySet__", out var proxySet) && proxySet.IsFunction)
                {
                    FenValue handler = FenValue.Undefined;
                    if (!TryGetDirect("__proxyHandler__", out handler))
                    {
                        TryGetDirect("__handler__", out handler);
                    }

                    proxySet.AsFunction().Invoke(new[] { target, key, value, receiver }, context, handler);
                    return;
                }

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                {
                    targetObject.SetWithReceiver(key, value, receiver, context);
                    return;
                }
            }

            var legacyKey = symbolKey.ToPropertyKey();
            if (!string.IsNullOrEmpty(legacyKey) && _shape.TryGetPropertyOffset(legacyKey, out _))
            {
                SetWithReceiver(legacyKey, value, receiver, context);
                return;
            }

            if (_symbolProperties != null && _symbolProperties.TryGetValue(symbolKey.Id, out var existing))
            {
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
                            existing.Setter.Invoke(new[] { value }, context, receiver);
                        }
                        finally
                        {
                            _accessorDepth--;
                        }
                    }
                    else if (context?.StrictMode == true || context?.Environment?.StrictMode == true)
                    {
                        throw new FenTypeError($"TypeError: Cannot set property '{symbolKey}' which has only a getter");
                    }

                    return;
                }

                if (existing.Writable == false)
                {
                    if (context?.StrictMode == true || context?.Environment?.StrictMode == true)
                    {
                        throw new FenTypeError($"TypeError: Cannot assign to read only property '{symbolKey}'");
                    }

                    return;
                }

                existing.Value = value;
                _symbolProperties[symbolKey.Id] = existing;
                return;
            }

            IObject proto = _prototype;
            while (proto != null)
            {
                if (proto is FenObject fenProto &&
                    fenProto._symbolProperties != null &&
                    fenProto._symbolProperties.TryGetValue(symbolKey.Id, out var inherited) &&
                    inherited.IsAccessor &&
                    inherited.Setter != null)
                {
                    if (++_accessorDepth > MAX_ACCESSOR_DEPTH)
                    {
                        _accessorDepth--;
                        return;
                    }

                    try
                    {
                        inherited.Setter.Invoke(new[] { value }, context, receiver);
                    }
                    finally
                    {
                        _accessorDepth--;
                    }

                    return;
                }

                proto = proto.GetPrototype();
            }

            if (!_extensible)
            {
                if (context?.StrictMode == true || context?.Environment?.StrictMode == true)
                {
                    throw new FenTypeError($"TypeError: Cannot add property '{symbolKey}', object is not extensible");
                }

                return;
            }

            if (_symbolProperties == null)
            {
                _symbolProperties = new Dictionary<long, PropertyDescriptor>();
            }

            _symbolProperties[symbolKey.Id] = PropertyDescriptor.DataDefault(value);
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
                    if (!_shape.TryGetPropertyOffset("__proxyTarget__", out var targetIdx))
                    {
                        _shape.TryGetPropertyOffset("__target__", out targetIdx);
                    }

                    var target = targetIdx >= 0 && _properties[targetIdx].Value.HasValue
                        ? _properties[targetIdx].Value.Value
                        : FenValue.Undefined;
                    if (_shape.TryGetPropertyOffset("__proxyHas__", out var phIdx) && 
                        (_properties[phIdx].Value.HasValue && _properties[phIdx].Value.Value.IsFunction))
                    {
                        FenValue handler = FenValue.Undefined;
                        if (!TryGetDirect("__proxyHandler__", out handler))
                        {
                            TryGetDirect("__handler__", out handler);
                        }

                        var fn = _properties[phIdx].Value.Value.AsFunction();
                        var res = fn.Invoke(new FenValue[] { target, FenValue.FromString(key) }, context, handler);
                        return res.ToBoolean();
                    }

                    if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                    {
                        return targetObject.Has(key, context);
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

        public virtual bool Has(FenValue key, IExecutionContext context = null)
        {
            if (!TryNormalizePropertyKey(key, context, out var stringKey, out var symbolKey))
            {
                return false;
            }

            if (symbolKey == null)
            {
                return Has(stringKey, context);
            }

            if (++_hasDepth > MAX_HAS_DEPTH)
            {
                _hasDepth--;
                return false;
            }

            try
            {
                if (TryGetDirect("__isProxy__", out var proxyMarker) && proxyMarker.ToBoolean())
                {
                    TryGetDirect("__proxyTarget__", out var target);
                    if (target.IsUndefined)
                    {
                        TryGetDirect("__target__", out target);
                    }

                    if (TryGetDirect("__proxyHas__", out var proxyHas) && proxyHas.IsFunction)
                    {
                        FenValue handler = FenValue.Undefined;
                        if (!TryGetDirect("__proxyHandler__", out handler))
                        {
                            TryGetDirect("__handler__", out handler);
                        }

                        return proxyHas.AsFunction().Invoke(new[] { target, key }, context, handler).ToBoolean();
                    }

                    if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject targetObject)
                    {
                        return targetObject.Has(key, context);
                    }
                }

                if (_symbolProperties != null && _symbolProperties.ContainsKey(symbolKey.Id))
                {
                    return true;
                }

                var legacyKey = symbolKey.ToPropertyKey();
                if (!string.IsNullOrEmpty(legacyKey) && _shape.TryGetPropertyOffset(legacyKey, out _))
                {
                    return true;
                }

                if (_prototype is FenObject fenProto)
                {
                    return fenProto.Has(key, context);
                }

                return _prototype != null && !string.IsNullOrEmpty(legacyKey) && _prototype.Has(legacyKey, context);
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
                if (PrototypeChainDefinesStringProperty(key))
                {
                    return false;
                }
                FenValue doc = FenValue.Undefined;
                if (_shape.TryGetPropertyOffset("document", out var docIdx))
                {
                    doc = _properties[docIdx].Value ?? FenValue.Undefined;
                }
                else
                {
                    for (var env = context?.Environment; env != null; env = env.Outer)
                    {
                        if (env.TryGetLocal("document", out var directDoc))
                        {
                            doc = directDoc;
                            break;
                        }
                    }
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

        private bool PrototypeChainDefinesStringProperty(string key)
        {
            for (var current = _prototype; current != null; current = current.GetPrototype())
            {
                if (current.GetOwnPropertyDescriptor(key).HasValue)
                {
                    return true;
                }
            }

            return false;
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

        public virtual bool Delete(FenValue key, IExecutionContext context = null)
        {
            if (!TryNormalizePropertyKey(key, context, out var stringKey, out var symbolKey))
            {
                return true;
            }

            if (symbolKey == null)
            {
                return Delete(stringKey, context);
            }

            var legacyKey = symbolKey.ToPropertyKey();
            if (!string.IsNullOrEmpty(legacyKey) && _shape.TryGetPropertyOffset(legacyKey, out _))
            {
                return Delete(legacyKey, context);
            }

            return DeleteSymbol(symbolKey);
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
        /// Enumerate all enumerable string-keyed properties in the prototype chain.
        /// Used by for...in per ECMA-262 §14.7.5.9. Returns own enumerable keys first,
        /// then walks the prototype chain, skipping already-seen keys (shadowed properties).
        /// </summary>
        public virtual IEnumerable<string> EnumerableKeys(IExecutionContext context = null)
        {
            var seen = new HashSet<string>();
            IObject current = this;
            while (current != null)
            {
                if (current is FenObject fenObj)
                {
                    foreach (var key in fenObj.Keys(context))
                    {
                        if (seen.Add(key))
                            yield return key;
                    }
                    current = fenObj.GetPrototype();
                }
                else
                {
                    // For non-FenObject IObject implementations
                    var keys = current.Keys(context);
                    if (keys != null)
                    {
                        foreach (var key in keys)
                        {
                            if (seen.Add(key))
                                yield return key;
                        }
                    }
                    break;
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
                // ECMA-262 §9.1.6.3 step 3: if non-extensible, adding a new property must throw TypeError.
                if (!_extensible)
                    throw new FenTypeError("TypeError: Cannot add property " + key + ", object is not extensible");

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

            // ECMA-262 §9.1.6.3 ValidateAndApplyPropertyDescriptor:
            // If current is not configurable, various changes must throw TypeError.
            if (current.Configurable == false)
            {
                // Cannot change non-configurable to configurable.
                if (desc.Configurable == true)
                    throw new FenTypeError("TypeError: Cannot redefine property: " + key);
                // Cannot change Enumerable on a non-configurable property.
                if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable)
                    throw new FenTypeError("TypeError: Cannot redefine property: " + key);
            }

            if (desc.IsGenericDescriptor())
            {
                // updates to enumerable/configurable handled above
            }
            else if (current.IsData != desc.IsData)
            {
                // Cannot change from data to accessor or vice versa if non-configurable.
                if (current.Configurable == false)
                    throw new FenTypeError("TypeError: Cannot redefine property: " + key);
            }
            else if (current.IsData && desc.IsData)
            {
                if (current.Configurable == false && current.Writable == false)
                {
                    // Cannot change Writable from false to true.
                    if (desc.Writable == true)
                        throw new FenTypeError("TypeError: Cannot redefine property: " + key);
                    // Cannot change value of non-writable property (SameValue check, ECMA-262 §7.2.12).
                    if (desc.Value.HasValue && !desc.Value.Value.StrictEquals(current.Value ?? FenValue.Undefined))
                        throw new FenTypeError("TypeError: Cannot assign to read only property '" + key + "'");
                }
            }
            else if (current.IsAccessor && desc.IsAccessor)
            {
                if (current.Configurable == false)
                {
                    if (desc.Getter != null && !ReferenceEquals(desc.Getter, current.Getter))
                        throw new FenTypeError("TypeError: Cannot redefine property: " + key);
                    if (desc.Setter != null && !ReferenceEquals(desc.Setter, current.Setter))
                        throw new FenTypeError("TypeError: Cannot redefine property: " + key);
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

        public virtual bool DefineOwnProperty(FenValue key, PropertyDescriptor desc)
        {
            if (!TryNormalizePropertyKey(key, null, out var stringKey, out var symbolKey))
            {
                return false;
            }

            if (symbolKey == null)
            {
                return DefineOwnProperty(stringKey, desc);
            }

            if (TryGetDirect("__isProxy__", out var proxyMarker) && proxyMarker.ToBoolean())
            {
                if (TryGetDirect("__proxyDefineProperty__", out var proxyDefine) && proxyDefine.IsFunction)
                {
                    TryGetDirect("__proxyTarget__", out var target);
                    if (target.IsUndefined)
                    {
                        TryGetDirect("__target__", out target);
                    }

                    var descObj = new FenObject();
                    if (desc.Value.HasValue) descObj.Set("value", desc.Value.Value);
                    if (desc.Writable.HasValue) descObj.Set("writable", FenValue.FromBoolean(desc.Writable.Value));
                    if (desc.Getter != null) descObj.Set("get", FenValue.FromFunction(desc.Getter));
                    if (desc.Setter != null) descObj.Set("set", FenValue.FromFunction(desc.Setter));
                    if (desc.Enumerable.HasValue) descObj.Set("enumerable", FenValue.FromBoolean(desc.Enumerable.Value));
                    if (desc.Configurable.HasValue) descObj.Set("configurable", FenValue.FromBoolean(desc.Configurable.Value));

                    return proxyDefine.AsFunction().Invoke(
                        new[] { target, key, FenValue.FromObject(descObj) },
                        null).ToBoolean();
                }
            }

            return DefineSymbolProperty(symbolKey, desc);
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

        public virtual PropertyDescriptor? GetOwnPropertyDescriptor(FenValue key)
        {
            if (!TryNormalizePropertyKey(key, null, out var stringKey, out var symbolKey))
            {
                return null;
            }

            if (symbolKey == null)
            {
                return GetOwnPropertyDescriptor(stringKey);
            }

            if (TryGetDirect("__isProxy__", out var proxyMarker) && proxyMarker.ToBoolean())
            {
                if (TryGetDirect("__proxyGetOwnPropertyDescriptor__", out var proxyGopd) && proxyGopd.IsFunction)
                {
                    TryGetDirect("__proxyTarget__", out var target);
                    if (target.IsUndefined)
                    {
                        TryGetDirect("__target__", out target);
                    }

                    var trapResult = proxyGopd.AsFunction().Invoke(new[] { target, key }, null);
                    if (trapResult.IsUndefined)
                    {
                        return null;
                    }

                    if (!trapResult.IsObject)
                    {
                        throw new FenTypeError("TypeError: Proxy getOwnPropertyDescriptor trap must return an object or undefined");
                    }

                    var descObj = trapResult.AsObject();
                    var trapDesc = new PropertyDescriptor
                    {
                        Enumerable = descObj.Get("enumerable", null).ToBoolean(),
                        Configurable = descObj.Get("configurable", null).ToBoolean()
                    };

                    var getVal = descObj.Get("get", null);
                    var setVal = descObj.Get("set", null);
                    if (descObj.Has("get") || descObj.Has("set"))
                    {
                        trapDesc.Getter = getVal.IsFunction ? getVal.AsFunction() : null;
                        trapDesc.Setter = setVal.IsFunction ? setVal.AsFunction() : null;
                    }
                    else
                    {
                        trapDesc.Value = descObj.Get("value", null);
                        trapDesc.Writable = descObj.Get("writable", null).ToBoolean();
                    }

                    return trapDesc;
                }
            }

            if (_symbolProperties != null && _symbolProperties.TryGetValue(symbolKey.Id, out var desc))
            {
                return desc;
            }

            var legacyKey = symbolKey.ToPropertyKey();
            if (!string.IsNullOrEmpty(legacyKey))
            {
                return GetOwnPropertyDescriptor(legacyKey);
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

        public virtual void SetPrototype(IObject prototype)
        {
            // ECMA-262 §9.1.2.1: OrdinarySetPrototypeOf — if non-extensible and prototype changes, throw TypeError.
            if (!_extensible && !ReferenceEquals(_prototype, prototype))
                throw new FenTypeError("TypeError: #<Object> is not extensible");
            _prototype = prototype;
        }

        // -----------------------------------------------------------------------
        // ECMA-262 §9.1: Symbol-keyed property operations
        // -----------------------------------------------------------------------

        /// <summary>
        /// Get a Symbol-keyed own property value (no prototype chain lookup).
        /// Returns Undefined if absent.
        /// </summary>
        public FenValue GetSymbol(JsSymbol symbol)
        {
            if (symbol == null) return FenValue.Undefined;
            if (_symbolProperties != null && _symbolProperties.TryGetValue(symbol.Id, out var desc))
            {
                if (desc.IsAccessor && desc.Getter != null)
                    return desc.Getter.Invoke(Array.Empty<FenValue>(), null);
                return desc.Value ?? FenValue.Undefined;
            }
            // Prototype chain lookup
            if (_prototype is FenObject fenProto) return fenProto.GetSymbol(symbol);
            return FenValue.Undefined;
        }

        /// <summary>
        /// Set a Symbol-keyed own property.
        /// ECMA-262 §9.1.9.1: throws TypeError in strict mode when the property is non-writable.
        /// </summary>
        public void SetSymbol(JsSymbol symbol, FenValue value, bool strict = false)
        {
            if (symbol == null) return;
            if (_symbolProperties == null) _symbolProperties = new Dictionary<long, PropertyDescriptor>();
            if (_symbolProperties.TryGetValue(symbol.Id, out var existing))
            {
                if (existing.IsAccessor)
                {
                    if (existing.Setter != null)
                        existing.Setter.Invoke(new FenValue[] { value }, null);
                    // No setter: silently fail (strict TypeError deferred to spec §9.1.9.1 step 5.b)
                    return;
                }
                if (existing.Writable == false)
                {
                    // ECMA-262 §9.1.9.1 step 4.a – strict mode throws TypeError
                    if (strict)
                        throw new FenTypeError($"TypeError: Cannot assign to read only property '{symbol}'");
                    return;
                }
                existing.Value = value;
                _symbolProperties[symbol.Id] = existing;
                return;
            }
            if (!_extensible)
            {
                if (strict) throw new FenTypeError("TypeError: Cannot add property on a non-extensible object");
                return;
            }
            _symbolProperties[symbol.Id] = PropertyDescriptor.DataDefault(value);
        }

        /// <summary>
        /// Check whether this object has a Symbol-keyed own property (not prototype).
        /// </summary>
        public bool HasSymbol(JsSymbol symbol)
        {
            if (symbol == null) return false;
            if (_symbolProperties != null && _symbolProperties.ContainsKey(symbol.Id)) return true;
            if (_prototype is FenObject fenProto) return fenProto.HasSymbol(symbol);
            return false;
        }

        /// <summary>
        /// Delete a Symbol-keyed own property. Returns false if non-configurable.
        /// </summary>
        public bool DeleteSymbol(JsSymbol symbol)
        {
            if (symbol == null) return true;
            if (_symbolProperties == null) return true;
            if (_symbolProperties.TryGetValue(symbol.Id, out var desc))
            {
                if (desc.Configurable == false) return false;
                _symbolProperties.Remove(symbol.Id);
            }
            return true;
        }

        /// <summary>
        /// Define a Symbol-keyed property with a full descriptor.
        /// </summary>
        public bool DefineSymbolProperty(JsSymbol symbol, PropertyDescriptor desc)
        {
            if (symbol == null) return false;
            if (_symbolProperties == null) _symbolProperties = new Dictionary<long, PropertyDescriptor>();
            if (!_symbolProperties.ContainsKey(symbol.Id))
            {
                // ECMA-262 §9.1.6.3 step 3: non-extensible object cannot gain new properties.
                if (!_extensible)
                    throw new FenTypeError("TypeError: Cannot add property " + symbol + ", object is not extensible");
                _symbolProperties[symbol.Id] = desc;
                return true;
            }
            var current = _symbolProperties[symbol.Id];
            if (current.Configurable == false)
            {
                // ECMA-262 §9.1.6.3: non-configurable violations must throw TypeError.
                if (desc.Configurable == true)
                    throw new FenTypeError("TypeError: Cannot redefine property: " + symbol);
                if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable)
                    throw new FenTypeError("TypeError: Cannot redefine property: " + symbol);
            }
            // Merge
            if (desc.Value.HasValue) current.Value = desc.Value;
            if (desc.Writable.HasValue) current.Writable = desc.Writable;
            if (desc.Getter != null) current.Getter = desc.Getter;
            if (desc.Setter != null) current.Setter = desc.Setter;
            if (desc.Enumerable.HasValue) current.Enumerable = desc.Enumerable;
            if (desc.Configurable.HasValue) current.Configurable = desc.Configurable;
            _symbolProperties[symbol.Id] = current;
            return true;
        }

        /// <summary>
        /// Enumerate own Symbol-keyed property IDs (for Reflect.ownKeys / ownKeys trap).
        /// </summary>
        public IEnumerable<JsSymbol> GetOwnSymbolKeys()
        {
            // We need to reconstruct JsSymbol references from IDs. Since JsSymbol.Id is internal,
            // we store them as values in a parallel lookup seeded via SetSymbol.
            // Return nothing here if the symbol bag is empty.
            yield break; // Partial: caller tracks symbols via SetSymbol call sites.
        }

        private static bool TryNormalizePropertyKey(FenValue key, IExecutionContext context, out string stringKey, out JsSymbol symbolKey)
        {
            stringKey = null;
            symbolKey = null;

            if (key.IsSymbol)
            {
                symbolKey = key.AsSymbol();
                return symbolKey != null;
            }

            stringKey = key.AsString(context);
            return true;
        }

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
























