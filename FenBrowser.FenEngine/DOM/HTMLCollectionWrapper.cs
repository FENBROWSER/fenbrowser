using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    public class HTMLCollectionWrapper : IObject
    {
        private readonly IEnumerable<Element> _source;
        private readonly IExecutionContext _context;

        public HTMLCollectionWrapper(IEnumerable<Element> source, IExecutionContext context)
        {
            _source = source ?? Array.Empty<Element>();
            _context = context;
        }

        public object NativeObject { get; set; }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            if (int.TryParse(key, out int index))
            {
                return Item(index);
            }

            // Named access support (id or name)
            var named = NamedItem(key);
            if (named != null && !named.IsNull) return named;

            switch (key)
            {
                case "length":
                    return FenValue.FromNumber(_source.Count());

                case "item":
                    return FenValue.FromFunction(new FenFunction("item", (args, thisVal) =>
                    {
                        if (args.Length > 0 && args[0].IsNumber)
                            return Item((int)args[0].ToNumber());
                        return FenValue.Null;
                    }));

                case "namedItem":
                    return FenValue.FromFunction(new FenFunction("namedItem", (args, thisVal) =>
                    {
                        if (args.Length > 0)
                            return NamedItem(args[0].ToString());
                        return FenValue.Null;
                    }));
            }

            return FenValue.Undefined;
        }

        private FenValue Item(int index)
        {
            var el = _source.ElementAtOrDefault(index);
            return WrapElement(el);
        }
        
        private FenValue NamedItem(string name)
        {
            // id then name
            var el = _source.FirstOrDefault(e => e.Attr != null && e.Attr.ContainsKey("id") && e.Attr["id"] == name);
            if (el  == null)
                el = _source.FirstOrDefault(e => e.Attr != null && e.Attr.ContainsKey("name") && e.Attr["name"] == name);
            
            return WrapElement(el);
        }

        private FenValue WrapElement(Element el)
        {
            if (el  == null) return FenValue.Null;
            return DomWrapperFactory.Wrap(el, _context);
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            // Read-only
        }

        public System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var count = _source.Count();
            for (int i = 0; i < count; i++) yield return i.ToString();
            yield return "length";
            yield return "item";
            yield return "namedItem";
        }

        public bool Has(string key, IExecutionContext context = null)
        {
             if (int.TryParse(key, out int index))
                return index >= 0 && index < _source.Count();
             
             if (key == "length" || key == "item" || key == "namedItem") return true;

             // Named check
             return NamedItem(key).IsObject;
        }

        public bool Delete(string key, IExecutionContext context = null) => false;
        
        public IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }
}
