using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript environment (scope).
    /// Updated to use FenValue structs to eliminate heap allocations for variable storage.
    /// </summary>
    public class FenEnvironment
    {
        private readonly Dictionary<string, FenValue> _store;
        private readonly HashSet<string> _constants;
        private readonly HashSet<string> _tdz;
        private readonly IObject _withObject;
        private readonly bool _isWithEnvironment;
        private readonly bool _isLexicalScope;
        private FenObject _liveModuleExports;
        private IDictionary<string, int> _fastSlotByName;
        public FenValue[] FastStore; // NEW: Exposed for JIT direct access
        public FenEnvironment Outer { get; set; }

        /// <summary>
        /// True when this scope (or any enclosing scope) is in strict mode.
        /// Set to true by the runtime on function-level environments that contain
        /// a "use strict" directive; child scopes inherit via the Outer chain.
        /// </summary>
        public bool StrictMode
        {
            get => _strictMode || (Outer?.StrictMode ?? false);
            set => _strictMode = value;
        }
        private bool _strictMode;
        [ThreadStatic]
        private static int _legacyGlobalLookupDepth;
        private const int MaxLegacyGlobalLookupDepth = 16;

        public FenEnvironment(FenEnvironment outer = null, bool isLexicalScope = false)
        {
            _store = new Dictionary<string, FenValue>();
            _constants = new HashSet<string>();
            _tdz = new HashSet<string>();
            Outer = outer;
            _isLexicalScope = isLexicalScope;
        }

        public FenEnvironment(FenEnvironment outer, IObject withObject)
            : this(outer)
        {
            _isWithEnvironment = true;
            _withObject = withObject;
        }

        public bool IsWithEnvironment => _isWithEnvironment;
        public bool IsLexicalScope => _isLexicalScope;
        public bool IsGlobalEnvironment => Outer == null && !_isWithEnvironment && !_isLexicalScope;

        public FenValue Get(string name)
        {
            if (_isWithEnvironment && HasWithBinding(name))
            {
                return _withObject?.Get(name) ?? FenValue.Undefined;
            }

            if (_tdz.Contains(name))
            {
                // ECMA-262 §9.1.1.1: Accessing a TDZ binding must throw ReferenceError
                throw new FenReferenceError($"ReferenceError: Cannot access '{name}' before initialization");
            }

            if (_store.TryGetValue(name, out var value))
            {
                return value;
            }

            if (Outer != null)
            {
                return Outer.Get(name);
            }

            if (TryResolveLegacyGlobalFromWindow(name, out var legacyGlobal))
            {
                return legacyGlobal;
            }

            return FenValue.Undefined;
        }

        public bool HasLocalBinding(string name)
        {
            if (_isWithEnvironment && HasWithBinding(name))
            {
                return true;
            }

            return _store.ContainsKey(name) || _tdz.Contains(name);
        }

        public IEnumerable<string> GetOwnBindingNames()
        {
            foreach (var name in _store.Keys)
            {
                yield return name;
            }
        }

        public bool TryGetLocal(string name, out FenValue value)
        {
            if (_isWithEnvironment)
            {
                // ResolveBindingEnvironment already performed HasBinding + unscopables filtering.
                // Avoid re-running unscopables here to preserve proxy-observable access order.
                if (_withObject?.Has(name) == true)
                {
                    value = _withObject.Get(name);
                    return true;
                }
            }

            if (_tdz.Contains(name))
            {
                // ECMA-262 §9.1.1.1: Accessing a TDZ binding must throw ReferenceError
                throw new FenReferenceError($"ReferenceError: Cannot access '{name}' before initialization");
            }

            return _store.TryGetValue(name, out value);
        }

        public FenEnvironment ResolveBindingEnvironment(string name)
        {
            for (var env = this; env != null; env = env.Outer)
            {
                if ((env._isWithEnvironment && env.HasWithBinding(name)) ||
                    env._store.ContainsKey(name) || env._tdz.Contains(name))
                {
                    return env;
                }
            }

            return null;
        }

        public FenValue Set(string name, FenValue value)
        {
            _store[name] = value;
            _tdz.Remove(name);
            SyncFastSlot(name, value);
            SyncLiveModuleExport(name, value);
            return value;
        }

        // --- NEW: Fast Indexed Access ---

        public void InitializeFastStore(int size)
        {
            if (FastStore == null || FastStore.Length < size)
                FastStore = new FenValue[size];
        }

        public void ConfigureFastSlots(IDictionary<string, int> fastSlotByName)
        {
            _fastSlotByName = fastSlotByName;
        }

        public void InheritFastSlots(FenEnvironment outer)
        {
            if (outer == null)
            {
                return;
            }

            _fastSlotByName = outer._fastSlotByName;
            FastStore = outer.FastStore;
        }

        public FenValue GetFast(int index)
        {
            if (FastStore != null && index >= 0 && index < FastStore.Length)
                return FastStore[index];
            return FenValue.Undefined;
        }

        public void SetFast(int index, FenValue value)
        {
            if (FastStore == null) InitializeFastStore(index + 1);
            else if (index >= FastStore.Length)
            {
                var newStore = new FenValue[index + 1];
                System.Array.Copy(FastStore, newStore, FastStore.Length);
                FastStore = newStore;
            }
            FastStore[index] = value;
        }

        public void DeclareTDZ(string name)
        {
            _tdz.Add(name);
        }

        public FenValue SetConst(string name, FenValue value)
        {
            _store[name] = value;
            _constants.Add(name);
            _tdz.Remove(name);
            SyncFastSlot(name, value);
            SyncLiveModuleExport(name, value);
            return value;
        }

        public bool IsConstant(string name)
        {
            if (_constants.Contains(name))
                return true;
            if (Outer != null)
                return Outer.IsConstant(name);
            return false;
            }

        public FenValue Update(string name, FenValue value, bool isStrict = false)
        {
            if (_isWithEnvironment && HasWithBinding(name))
            {
                _withObject?.Set(name, value);
                return value;
            }

            if (_constants.Contains(name))
            {
                // ECMA-262 §14.3.1: Assignment to a const binding must throw TypeError
                throw new FenTypeError($"TypeError: Assignment to constant variable '{name}'");
            }

            if (_store.ContainsKey(name))
            {
                _store[name] = value;
                SyncFastSlot(name, value);
                SyncLiveModuleExport(name, value);
                return value;
            }

            if (Outer != null)
            {
                if (Outer.IsConstant(name))
                {
                    // ECMA-262 §14.3.1: Assignment to a const binding must throw TypeError
                    throw new FenTypeError($"TypeError: Assignment to constant variable '{name}'");
                }
                return Outer.Update(name, value, isStrict);
            }

            if (isStrict || StrictMode)
            {
                throw new FenReferenceError($"ReferenceError: {name} is not defined");
            }


            _store[name] = value;
            SyncFastSlot(name, value);
            return value;
        }

        public bool HasBinding(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var visited = new HashSet<FenEnvironment>();
            for (var env = this; env != null; env = env.Outer)
            {
                if (!visited.Add(env))
                {
                    break;
                }

                if (env._isWithEnvironment && env.HasWithBinding(name))
                {
                    return true;
                }

                if (env._store.ContainsKey(name) || env._tdz.Contains(name))
                {
                    return true;
                }
            }

            return TryResolveLegacyGlobalFromWindow(name, out _);
        }

        public IDictionary<string, FenValue> InspectVariables()
        {
            return new Dictionary<string, FenValue>(_store);
        }

        public void AttachLiveModuleExports(FenObject exportObject)
        {
            _liveModuleExports = exportObject;
        }

        public FenEnvironment GetDeclarationEnvironment()
        {
            var env = this;
            while (env != null && env._isWithEnvironment)
            {
                env = env.Outer;
            }

            return env ?? this;
        }

        public FenEnvironment GetVarDeclarationEnvironment()
        {
            var env = this;
            while (env != null && (env._isWithEnvironment || env._isLexicalScope))
            {
                env = env.Outer;
            }

            return env ?? this;
        }

        private bool TryResolveLegacyGlobalFromWindow(string name, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (++_legacyGlobalLookupDepth > MaxLegacyGlobalLookupDepth)
            {
                _legacyGlobalLookupDepth--;
                return false;
            }

            try
            {
                FenValue docVal = FenValue.Undefined;
                IObject docObj = null;

                // Resolve only direct global document binding here; avoid re-entering window named-property plumbing.
                if (TryGetBindingFromChain("document", out var directDocVal) && directDocVal.IsObject)
                {
                    docVal = directDocVal;
                    docObj = directDocVal.AsObject();
                }

                if (docObj == null)
                {
                    return false;
                }

                // Legacy named access: id-backed global variables (e.g., <iframe id="iframe"> => window.iframe).
                var getById = docObj.Get("getElementById");
                if (!getById.IsFunction)
                {
                    return false;
                }

                var found = getById.AsFunction().Invoke(new[] { FenValue.FromString(name) }, null, docVal);
                if (!found.IsNull && !found.IsUndefined)
                {
                    value = found;
                    return true;
                }

                return false;
            }
            finally
            {
                _legacyGlobalLookupDepth--;
            }
        }
        private bool TryGetBindingFromChain(string name, out FenValue value)
        {
            for (var env = this; env != null; env = env.Outer)
            {
                if (env._isWithEnvironment && env.HasWithBinding(name))
                {
                    value = env._withObject?.Get(name) ?? FenValue.Undefined;
                    return true;
                }

                if (env._store.TryGetValue(name, out value))
                {
                    return true;
                }
            }

            value = FenValue.Undefined;
            return false;
        }

        private bool TryAssignToGlobalObject(string name, FenValue value)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            FenObject globalRoot = null;
            if (TryGetBindingFromChain("globalThis", out var globalThisVal) && globalThisVal.IsObject)
            {
                globalRoot = globalThisVal.AsObject() as FenObject;
            }
            else if (TryGetBindingFromChain("window", out var windowVal) && windowVal.IsObject)
            {
                globalRoot = windowVal.AsObject() as FenObject;
            }
            else if (TryGetBindingFromChain("self", out var selfVal) && selfVal.IsObject)
            {
                globalRoot = selfVal.AsObject() as FenObject;
            }

            if (globalRoot == null)
            {
                return false;
            }

            if (globalRoot.GetOwnPropertyDescriptor(name).HasValue)
            {
                globalRoot.Set(name, value);
                return true;
            }

            var proto = globalRoot.GetPrototype() as FenObject;
            if (proto != null)
            {
                var isProxyDesc = proto.GetOwnPropertyDescriptor("__isProxy__");
                if (isProxyDesc.HasValue && isProxyDesc.Value.Value.HasValue && isProxyDesc.Value.Value.Value.ToBoolean())
                {
                    var setTrapDesc = proto.GetOwnPropertyDescriptor("__proxySet__");
                    if (setTrapDesc.HasValue && setTrapDesc.Value.Value.HasValue && setTrapDesc.Value.Value.Value.IsFunction)
                    {
                        var targetDesc = proto.GetOwnPropertyDescriptor("__proxyTarget__") ?? proto.GetOwnPropertyDescriptor("__target__");
                        var proxyTarget = targetDesc.HasValue && targetDesc.Value.Value.HasValue
                            ? targetDesc.Value.Value.Value
                            : FenValue.Undefined;
                        var setTrap = setTrapDesc.Value.Value.Value.AsFunction();
                        setTrap.Invoke(new[] { proxyTarget, FenValue.FromString(name), value, FenValue.FromObject(globalRoot) }, null, FenValue.Undefined);
                        return true;
                    }
                }
            }

            globalRoot.Set(name, value);
            return true;
        }

        public void SyncGlobalDeclarationBinding(string name, FenValue value)
        {
            if (!IsGlobalEnvironment || string.IsNullOrEmpty(name) || !TryGetGlobalObject(out var globalRoot))
            {
                return;
            }

            var descriptor = new PropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = true,
                Configurable = false
            };

            var existingDescriptor = globalRoot.GetOwnPropertyDescriptor(name);
            if (!existingDescriptor.HasValue)
            {
                if (!globalRoot.IsExtensible)
                {
                    throw new FenTypeError($"TypeError: Cannot create global property '{name}' on a non-extensible global object");
                }

                if (!globalRoot.DefineOwnProperty(name, descriptor))
                {
                    throw new FenTypeError($"TypeError: Cannot define global property '{name}'");
                }

                return;
            }

            if (!globalRoot.DefineOwnProperty(name, descriptor))
            {
                throw new FenTypeError($"TypeError: Cannot redefine global property '{name}'");
            }
        }

        private bool TryGetGlobalObject(out FenObject globalRoot)
        {
            globalRoot = null;
            if (TryGetBindingFromChain("globalThis", out var globalThisVal) && globalThisVal.IsObject)
            {
                globalRoot = globalThisVal.AsObject() as FenObject;
            }
            else if (TryGetBindingFromChain("window", out var windowVal) && windowVal.IsObject)
            {
                globalRoot = windowVal.AsObject() as FenObject;
            }
            else if (TryGetBindingFromChain("self", out var selfVal) && selfVal.IsObject)
            {
                globalRoot = selfVal.AsObject() as FenObject;
            }

            return globalRoot != null;
        }
        private void SyncFastSlot(string name, FenValue value)
        {
            if (_fastSlotByName != null && _fastSlotByName.TryGetValue(name, out int slotIndex))
            {
                SetFast(slotIndex, value);
            }
        }

        private void SyncLiveModuleExport(string name, FenValue value)
        {
            if (_liveModuleExports == null || string.IsNullOrEmpty(name) || !name.StartsWith("__fen_export_", StringComparison.Ordinal))
            {
                return;
            }

            _liveModuleExports.SetDirect(name.Substring("__fen_export_".Length), value);
        }

        private bool HasWithBinding(string name)
        {
            if (!_isWithEnvironment || _withObject == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (!_withObject.Has(name))
            {
                return false;
            }

            return !IsUnscopable(name);
        }
        private bool IsUnscopable(string name)
        {
            if (_withObject == null)
            {
                return false;
            }

            var unscopablesObj = TryGetUnscopablesObject();
            if (unscopablesObj == null || !unscopablesObj.Has(name))
            {
                return false;
            }

            return unscopablesObj.Get(name).ToBoolean();
        }

        private IObject TryGetUnscopablesObject()
        {
            if (_withObject == null)
            {
                return null;
            }

            // Preferred lookup: %Symbol.unscopables%.
            var symbolUnscopables = _withObject.Get("Symbol(Symbol.unscopables)");
            if (symbolUnscopables.IsObject)
            {
                return symbolUnscopables.AsObject();
            }

            // Compatibility fallback for legacy/string-keyed setups.
            var legacyUnscopables = _withObject.Get("Symbol.unscopables");
            if (legacyUnscopables.IsObject)
            {
                return legacyUnscopables.AsObject();
            }

            return null;
        }
    }
}











