using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript object in FenEngine
    /// </summary>
    public class FenObject : IObject
    {
        private readonly Dictionary<string, IValue> _properties = new Dictionary<string, IValue>();
        private IObject _prototype;

        public IValue Get(string key)
        {
            if (_properties.TryGetValue(key, out var value))
                return value;
            
            // Prototype chain lookup
            if (_prototype != null)
                return _prototype.Get(key);

            return FenValue.Undefined;
        }

        public void Set(string key, IValue value)
        {
            _properties[key] = value;
        }

        public bool Has(string key)
        {
            if (_properties.ContainsKey(key)) return true;
            if (_prototype != null) return _prototype.Has(key);
            return false;
        }

        public bool Delete(string key)
        {
            return _properties.Remove(key);
        }

        public IEnumerable<string> Keys()
        {
            return _properties.Keys;
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
