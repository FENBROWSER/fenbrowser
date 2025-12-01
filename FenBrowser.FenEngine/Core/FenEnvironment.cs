using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    public class FenEnvironment
    {
        private readonly Dictionary<string, IValue> _store;
        public FenEnvironment Outer { get; set; }

        public FenEnvironment(FenEnvironment outer = null)
        {
            _store = new Dictionary<string, IValue>();
            Outer = outer;
        }

        public IValue Get(string name)
        {
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
            return value;
        }

        public IValue Update(string name, IValue value)
        {
            // If variable exists in current scope, update it
            if (_store.ContainsKey(name))
            {
                _store[name] = value;
                return value;
            }

            // Otherwise check parent scopes
            if (Outer != null)
            {
                return Outer.Update(name, value);
            }

            // If not found anywhere, create it in current scope (like JS non-strict mode)
            _store[name] = value;
            return value;
        }
    }
}
