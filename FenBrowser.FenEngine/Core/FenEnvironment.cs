using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    public class FenEnvironment
    {
        private readonly Dictionary<string, IValue> _store;
        private readonly HashSet<string> _constants; // Track const declarations
        private readonly HashSet<string> _tdz; // Temporal Dead Zone - uninitialized let/const
        public FenEnvironment Outer { get; set; }

        public FenEnvironment(FenEnvironment outer = null)
        {
            _store = new Dictionary<string, IValue>();
            _constants = new HashSet<string>();
            _tdz = new HashSet<string>();
            Outer = outer;
        }

        public IValue Get(string name)
        {
            // Check for TDZ access
            if (_tdz.Contains(name))
            {
                return new ErrorValue($"Cannot access '{name}' before initialization");
            }
            
            if (_store.TryGetValue(name, out var value))
            {
                return value;
            }

            if (Outer != null)
            {
                return Outer.Get(name);
            }

            return null;
        }

        public IValue Set(string name, IValue value)
        {
            _store[name] = value;
            _tdz.Remove(name); // Remove from TDZ once initialized
            return value;
        }
        
        /// <summary>
        /// Declare a let/const variable in TDZ (uninitialized)
        /// </summary>
        public void DeclareTDZ(string name)
        {
            _tdz.Add(name);
        }
        
        /// <summary>
        /// Set a constant variable (cannot be reassigned)
        /// </summary>
        public IValue SetConst(string name, IValue value)
        {
            _store[name] = value;
            _constants.Add(name);
            _tdz.Remove(name); // Remove from TDZ once initialized
            return value;
        }
        
        /// <summary>
        /// Check if a variable is a constant
        /// </summary>
        public bool IsConstant(string name)
        {
            if (_constants.Contains(name))
                return true;
            if (Outer != null)
                return Outer.IsConstant(name);
            return false;
        }

        public IValue Update(string name, IValue value)
        {
            // Check if this is a constant
            if (_constants.Contains(name))
            {
                // Return an error for const reassignment
                return new ErrorValue($"Assignment to constant variable '{name}'");
            }
            
            // If variable exists in current scope, update it
            if (_store.ContainsKey(name))
            {
                _store[name] = value;
                return value;
            }

            // Otherwise check parent scopes
            if (Outer != null)
            {
                // Check if parent has it as constant
                if (Outer.IsConstant(name))
                {
                    return new ErrorValue($"Assignment to constant variable '{name}'");
                }
                return Outer.Update(name, value);
            }

            // If not found anywhere, create it in current scope (like JS non-strict mode)
            _store[name] = value;
            return value;
        }

        /// <summary>
        /// Inspect variables in this scope (for DevTools)
        /// </summary>
        public IDictionary<string, IValue> InspectVariables()
        {
            return new Dictionary<string, IValue>(_store);
        }
    }
}
