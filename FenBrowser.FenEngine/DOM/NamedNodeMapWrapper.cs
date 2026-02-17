using System;
using System.Collections.Generic;
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
             //   return FenValue.FromObject(new AttrWrapper(attrByName, _context));
                // Actually, standard NamedNodeMap doesn't usually expose attributes as properties directly on the object 
                // in the same way 'style' does. It's mostly item()/getNamedItem().
                // However, for debugging/ease, let's keep it strictly method-based unless requested.
                // Wait, some browsers do expose them. Let's stick to methods + index for now to be safe.
                {}

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
            // Read-only collection structure, though items can be modified via methods
        }

        public bool Has(string key, IExecutionContext context = null)
        {
             if (int.TryParse(key, out int index)) return index >= 0 && index < _map.Length;
             return !Get(key, context).IsUndefined;
        }

        public bool Delete(string key, IExecutionContext context = null) => false;
        
        public IEnumerable<string> Keys(IExecutionContext context = null) 
        {
            var keys = new List<string> { "length", "item", "getNamedItem", "setNamedItem", "removeNamedItem" };
            for(int i=0; i<_map.Length; i++) keys.Add(i.ToString());
            return keys;
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;

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
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "NamedNodeMap.setNamedItem"))
                 throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
            
            var wrapper = args[0].AsObject() as AttrWrapper;
            if (wrapper  == null) throw new FenSecurityError("Argument 1 of setNamedItem is not an Attr.");

            // Setting an attribute on map triggers owner element update internally in Core.NamedNodeMap
            // Exception: InUseAttributeError is handled in Core
            try
            {
                var old = _map.SetNamedItem(wrapper.Attr);
                return old != null ? FenValue.FromObject(new AttrWrapper(old, _context)) : FenValue.Null;
            }
            catch (Exception ex)
            {
                throw new FenSecurityError(ex.Message); // Or DOMException mapping
            }
        }

        private FenValue RemoveNamedItem(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "NamedNodeMap.removeNamedItem"))
                 throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0) return FenValue.Null;
            var name = args[0].ToString();

            try
            {
                var removed = _map.RemoveNamedItem(name);
                return removed != null ? FenValue.FromObject(new AttrWrapper(removed, _context)) : FenValue.Null;
            }
            catch (Exception ex)
            {
                throw new FenSecurityError(ex.Message); // Should match DOMException really
            }
        }
    }
}
