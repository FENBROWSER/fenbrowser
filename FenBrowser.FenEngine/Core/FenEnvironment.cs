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
        public FenValue[] FastStore; // NEW: Exposed for JIT direct access
        public FenEnvironment Outer { get; set; }

        public FenEnvironment(FenEnvironment outer = null)
        {
            _store = new Dictionary<string, FenValue>();
            _constants = new HashSet<string>();
            _tdz = new HashSet<string>();
            Outer = outer;
        }

        public FenValue Get(string name)
        {
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

            return FenValue.Undefined;
        }

        public FenValue Set(string name, FenValue value)
        {
            _store[name] = value;
            _tdz.Remove(name);
            return value;
        }

        // --- NEW: Fast Indexed Access ---
        
        public void InitializeFastStore(int size)
        {
            if (FastStore == null || FastStore.Length < size)
                FastStore = new FenValue[size];
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

        public FenValue Update(string name, FenValue value)
        {
            if (_constants.Contains(name))
            {
                return FenValue.FromError($"Assignment to constant variable '{name}'");
            }
            
            if (_store.ContainsKey(name))
            {
                _store[name] = value;
                return value;
            }

            if (Outer != null)
            {
                if (Outer.IsConstant(name))
                {
                    return FenValue.FromError($"Assignment to constant variable '{name}'");
                }
                return Outer.Update(name, value);
            }

            _store[name] = value;
            return value;
        }

        public IDictionary<string, FenValue> InspectVariables()
        {
            return new Dictionary<string, FenValue>(_store);
        }
    }
}
