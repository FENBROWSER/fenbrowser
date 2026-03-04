using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

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

        public FenEnvironment(FenEnvironment outer = null)
        {
            _store = new Dictionary<string, FenValue>();
            _constants = new HashSet<string>();
            _tdz = new HashSet<string>();
            Outer = outer;
        }

        public FenEnvironment(FenEnvironment outer, IObject withObject)
            : this(outer)
        {
            _isWithEnvironment = true;
            _withObject = withObject;
        }

        public bool IsWithEnvironment => _isWithEnvironment;

        public FenValue Get(string name)
        {
            if (_isWithEnvironment && HasWithBinding(name))
            {
                return _withObject?.Get(name) ?? FenValue.Undefined;
            }

            if (_tdz.Contains(name))
            {
                return FenValue.FromError($"Cannot access '{name}' before initialization");
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
                value = FenValue.FromError($"Cannot access '{name}' before initialization");
                return true;
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
                return FenValue.FromError($"Assignment to constant variable '{name}'");
            }

            if (_store.ContainsKey(name))
            {
                _store[name] = value;
                SyncFastSlot(name, value);
                return value;
            }

            if (Outer != null)
            {
                if (Outer.IsConstant(name))
                {
                    return FenValue.FromError($"Assignment to constant variable '{name}'");
                }
                return Outer.Update(name, value, isStrict);
            }

            if (isStrict || StrictMode)
            {
                return FenValue.FromError($"ReferenceError: {name} is not defined");
            }

            _store[name] = value;
            SyncFastSlot(name, value);
            return value;
        }

        public bool HasBinding(string name)
        {
            if (_isWithEnvironment && HasWithBinding(name))
            {
                return true;
            }

            if (_store.ContainsKey(name) || _tdz.Contains(name))
            {
                return true;
            }

            if (Outer != null && Outer.HasBinding(name))
            {
                return true;
            }

            return TryResolveLegacyGlobalFromWindow(name, out _);
        }

        public IDictionary<string, FenValue> InspectVariables()
        {
            return new Dictionary<string, FenValue>(_store);
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

        private bool TryResolveLegacyGlobalFromWindow(string name, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            IObject windowObj = null;
            FenValue docVal = FenValue.Undefined;
            IObject docObj = null;

            if (TryGetBindingFromChain("window", out var windowVal) && windowVal.IsObject)
            {
                windowObj = windowVal.AsObject();
                if (windowObj != null)
                {
                    // First check explicit window own/prototype properties.
                    var windowProp = windowObj.Get(name);
                    if (!windowProp.IsUndefined)
                    {
                        value = windowProp;
                        return true;
                    }

                    docVal = windowObj.Get("document");
                    if (docVal.IsObject)
                    {
                        docObj = docVal.AsObject();
                    }
                }
            }

            // Fallback: direct global "document" binding when window is not exposed on this chain.
            if (docObj == null && TryGetBindingFromChain("document", out var directDocVal) && directDocVal.IsObject)
            {
                docVal = directDocVal;
                docObj = directDocVal.AsObject();
            }

            // Final fallback: resolve document through normal identifier lookup (needed when globals are bridged, not stored directly).
            if (docObj == null && !string.Equals(name, "document", StringComparison.Ordinal))
            {
                var resolvedDoc = Get("document");
                if (resolvedDoc.IsObject)
                {
                    docVal = resolvedDoc;
                    docObj = resolvedDoc.AsObject();
                }
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

        private void SyncFastSlot(string name, FenValue value)
        {
            if (_fastSlotByName != null && _fastSlotByName.TryGetValue(name, out int slotIndex))
            {
                SetFast(slotIndex, value);
            }
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
            // Spec-observable behavior: with-environment performs a single Get on %Symbol.unscopables%.
            var unscopables = _withObject.Get("Symbol(Symbol.unscopables)");

            if (!unscopables.IsObject)
            {
                return false;
            }

            var unscopablesObj = unscopables.AsObject();
            if (unscopablesObj == null || !unscopablesObj.Has(name))
            {
                return false;
            }

            return unscopablesObj.Get(name).ToBoolean();
        }
    }
}
