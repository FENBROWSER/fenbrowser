using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.DOM
{
    public class NodeListWrapper : IObject
    {
        private readonly IEnumerable<Node> _source;
        private readonly IExecutionContext _context;
        
        // Caching strategy might be needed for identity preservation, 
        // but for now we'll wrap on demand or rely on Engine's object cache if it exists.
        // Ideally, NodeWrapper should be cached by the Engine/Context.

        public NodeListWrapper(IEnumerable<Node> source, IExecutionContext context)
        {
            _source = source ?? Array.Empty<Node>();
            _context = context;
        }

        public object NativeObject { get; set; }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            // Array index support
            if (int.TryParse(key, out int index))
            {
                return Item(index);
            }

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
                
                case "forEach":
                    return FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
                    {
                        if (args.Length > 0 && args[0].IsFunction)
                        {
                            var callback = args[0].AsFunction();
                            int i = 0;
                            foreach (var node in _source)
                            {
                                var val = WrapNode(node);
                                callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), FenValue.FromObject(this) }, _context);
                                i++;
                            }
                        }
                        return FenValue.Undefined;
                    }));
            }
            
            return FenValue.Undefined;
        }

        private FenValue Item(int index)
        {
            var node = _source.ElementAtOrDefault(index);
            return WrapNode(node);
        }

        private FenValue WrapNode(Node node)
        {
            if (node  == null) return FenValue.Null;
            // Delegation to factory or localized wrapper logic
            // Ideally we call back into a shared wrapper factory
            return FenEngine.DOM.DomWrapperFactory.Wrap(node, _context); 
        }


        public IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null) => new string[0];
        public bool Has(string key, IExecutionContext context = null) => Get(key, context) != FenValue.Undefined;
        public void Set(string key, FenValue value, IExecutionContext context = null) { }
    }
}
