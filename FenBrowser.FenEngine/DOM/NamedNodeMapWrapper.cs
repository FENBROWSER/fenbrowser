using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Wraps a DOM NamedNodeMap for JavaScript access.
    /// SPEC: DOM Living Standard §4.10
    /// </summary>
    public class NamedNodeMapWrapper : IObject
    {
        private readonly NamedNodeMap _map;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        private readonly Dictionary<string, PropertyDescriptor> _expandos = new(StringComparer.Ordinal);
        private readonly List<string> _expandoOrder = new();
        public object NativeObject { get; set; }

        public NamedNodeMapWrapper(NamedNodeMap map, IExecutionContext context)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            NativeObject = map;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.IsAccessor)
                {
                    return expandoDesc.Getter != null
                        ? expandoDesc.Getter.Invoke(Array.Empty<FenValue>(), context ?? _context, FenValue.FromObject(this))
                        : FenValue.Undefined;
                }

                return expandoDesc.Value ?? FenValue.Undefined;
            }

            // 1. Indexed access
            if (int.TryParse(key, out int index))
            {
                 var attr = _map[index];
                 return attr != null ? FenValue.FromObject(new AttrWrapper(attr, _context)) : FenValue.Undefined;
            }

            // 2. Named access (if it matches an attribute name directly, e.g. attributes.id)
            // Note: NamedNodeMap acts like a collection, but older specs allowed direct named access.
            // Modern spec usually requires getNamedItem, but for convenience/compat we check.
            var attrByName = _map.GetNamedItem(key);
            if (attrByName != null)
            {
                return FenValue.FromObject(new AttrWrapper(attrByName, _context));
            }

            switch (key.ToLowerInvariant())
            {
                case "length":
                    return FenValue.FromNumber(_map.Length);
                
                case "item":
                    return FenValue.FromFunction(new FenFunction("item", Item));
                
                case "getnameditem":
                    return FenValue.FromFunction(new FenFunction("getNamedItem", GetNamedItem));
                
                case "setnameditem":
                    return FenValue.FromFunction(new FenFunction("setNamedItem", SetNamedItem));
                
                case "removenameditem":
                    return FenValue.FromFunction(new FenFunction("removeNamedItem", RemoveNamedItem));

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.IsAccessor)
                {
                    expandoDesc.Setter?.Invoke(new[] { value }, context ?? _context, FenValue.FromObject(this));
                    return;
                }

                if (expandoDesc.Writable == false)
                {
                    return;
                }

                expandoDesc.Value = value;
                _expandos[key] = expandoDesc;
                return;
            }

            // Read-only collection structure, though items can be modified via methods
        }

        public bool Has(string key, IExecutionContext context = null)
        {
             if (_expandos.ContainsKey(key)) return true;
             if (int.TryParse(key, out int index)) return index >= 0 && index < _map.Length;
             return !Get(key, context).IsUndefined;
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var desc))
            {
                if (desc.Configurable == false)
                {
                    return false;
                }

                _expandos.Remove(key);
                _expandoOrder.Remove(key);
                return true;
            }

            return false;
        }
        
        public IEnumerable<string> Keys(IExecutionContext context = null) 
        {
            var keys = new List<string>();
            for(int i=0; i<_map.Length; i++)
            {
                keys.Add(i.ToString());
            }

            for (int i = 0; i < _map.Length; i++)
            {
                var attr = _map[i];
                if (attr != null && !string.IsNullOrEmpty(attr.Name))
                {
                    keys.Add(attr.Name);
                }
            }
            foreach (var expando in _expandoOrder)
            {
                if (_expandos.TryGetValue(expando, out var desc) && (desc.Enumerable ?? false))
                {
                    keys.Add(expando);
                }
            }

            return keys.Distinct(StringComparer.Ordinal);
        }

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null)
        {
            var names = new List<string>();
            for (int i = 0; i < _map.Length; i++)
            {
                names.Add(i.ToString());
            }

            for (int i = 0; i < _map.Length; i++)
            {
                var attr = _map[i];
                if (attr != null && !string.IsNullOrEmpty(attr.Name))
                {
                    names.Add(attr.Name);
                }
            }

            foreach (var expando in _expandoOrder)
            {
                if (_expandos.ContainsKey(expando))
                {
                    names.Add(expando);
                }
            }

            return names.Distinct(StringComparer.Ordinal);
        }

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                return expandoDesc;
            }

            if (int.TryParse(key, out var index) && index >= 0 && index < _map.Length)
            {
                return new PropertyDescriptor { Value = Get(key), Writable = false, Enumerable = true, Configurable = true, Getter = null, Setter = null };
            }

            var attr = _map.GetNamedItem(key);
            if (attr != null)
            {
                return new PropertyDescriptor { Value = FenValue.FromObject(new AttrWrapper(attr, _context)), Writable = false, Enumerable = false, Configurable = true, Getter = null, Setter = null };
            }

            return null;
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if ((!_expandos.ContainsKey(key) && int.TryParse(key, out var index) && index >= 0 && index < _map.Length) || _map.GetNamedItem(key) != null)
            {
                if (desc.IsAccessor)
                {
                    return false;
                }

                if (desc.Configurable == false)
                {
                    return false;
                }

                return !desc.Value.HasValue || GetOwnPropertyDescriptor(key)?.Value.Value.StrictEquals(desc.Value.Value) == true;
            }

            if (_expandos.TryGetValue(key, out var current))
            {
                if (current.Configurable == false)
                {
                    if (desc.Configurable == true) return false;
                    if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable) return false;

                    if (current.IsData && current.Writable == false)
                    {
                        if (desc.Writable == true) return false;
                        if (desc.Value.HasValue && !desc.Value.Value.StrictEquals(current.Value)) return false;
                    }
                }

                if (desc.Value.HasValue) current.Value = desc.Value;
                if (desc.Writable.HasValue) current.Writable = desc.Writable;
                if (desc.Enumerable.HasValue) current.Enumerable = desc.Enumerable;
                if (desc.Configurable.HasValue) current.Configurable = desc.Configurable;
                if (desc.Getter != null || desc.Setter != null)
                {
                    current.Getter = desc.Getter;
                    current.Setter = desc.Setter;
                    if (!desc.Value.HasValue)
                    {
                        current.Value = null;
                        current.Writable = null;
                    }
                }

                _expandos[key] = current;
                return true;
            }

            var normalized = desc;
            if (normalized.IsData)
            {
                if (!normalized.Value.HasValue) normalized.Value = FenValue.Undefined;
                if (!normalized.Writable.HasValue) normalized.Writable = false;
            }
            if (!normalized.Enumerable.HasValue) normalized.Enumerable = false;
            if (!normalized.Configurable.HasValue) normalized.Configurable = false;

            _expandos[key] = normalized;
            _expandoOrder.Add(key);
            return true;
        }

        // --- Methods ---

        private FenValue Item(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var idx = (int)args[0].ToNumber();
            var attr = _map[idx];
            return attr != null ? FenValue.FromObject(new AttrWrapper(attr, _context)) : FenValue.Null;
        }

        private FenValue GetNamedItem(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var name = args[0].ToString();
            var attr = _map.GetNamedItem(name);
            return attr != null ? FenValue.FromObject(new AttrWrapper(attr, _context)) : FenValue.Null;
        }

        private FenValue SetNamedItem(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) throw new FenTypeError("1 argument required");
            if (!args[0].IsObject || args[0].IsNull) throw new FenTypeError("Argument 1 is not an object.");
            
            var wrapper = args[0].AsObject() as AttrWrapper;
            if (wrapper == null) throw new FenTypeError("Argument 1 of setNamedItem is not an Attr.");

            try
            {
                var old = _map.SetNamedItem(wrapper.Attr);
                return old != null ? FenValue.FromObject(new AttrWrapper(old, _context)) : FenValue.Null;
            }
            catch (Exception ex)
            {
                throw new DomException("InUseAttributeError", ex.Message);
            }
        }

        private FenValue RemoveNamedItem(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) throw new FenTypeError("1 argument required");
            if (args[0].IsUndefined || args[0].IsNull) throw new FenTypeError("Cannot convert null or undefined to primitive value");
            var name = args[0].ToString();

            try
            {
                var removed = _map.RemoveNamedItem(name);
                return removed != null ? FenValue.FromObject(new AttrWrapper(removed, _context)) : FenValue.Null;
            }
            catch (Exception ex)
            {
                throw new DomException("NotFoundError", ex.Message);
            }
        }
    }
}
