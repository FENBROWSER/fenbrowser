// SpecRef: DOM Living Standard, NodeList and list iteration behavior
// CapabilityId: DOM-NODELIST-LIVE-01
// Determinism: strict
// FallbackPolicy: spec-defined
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    public class NodeListWrapper : IObject
    {
        private readonly IEnumerable<Node> _source;
        private readonly IExecutionContext _context;
        private readonly Dictionary<string, FenValue> _expando = new Dictionary<string, FenValue>(StringComparer.Ordinal);
        
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
            if (TryParseArrayIndex(key, out var index))
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
                        if (args.Length > 0)
                            return Item(ToCollectionIndex(args[0]));
                        return FenValue.Null;
                    }));

                case "keys":
                    return FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
                    {
                        return FenValue.FromObject(CreateIteratorObject((index, node) => FenValue.FromNumber(index)));
                    }));

                case "values":
                    return FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
                    {
                        return FenValue.FromObject(CreateIteratorObject((index, node) => WrapNode(node)));
                    }));

                case "entries":
                    return FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
                    {
                        return FenValue.FromObject(CreateIteratorObject((index, node) =>
                        {
                            var pair = FenObject.CreateArray();
                            pair.Set("0", FenValue.FromNumber(index));
                            pair.Set("1", WrapNode(node));
                            pair.Set("length", FenValue.FromNumber(2));
                            return FenValue.FromObject(pair);
                        }));
                    }));

                case "[Symbol.iterator]":
                case "Symbol.iterator":
                case "Symbol(Symbol.iterator)":
                    return FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
                    {
                        return FenValue.FromObject(CreateIteratorObject((index, node) => WrapNode(node)));
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

                case "indexOf":
                    return FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
                    {
                        if (args.Length > 0)
                        {
                            var target = args[0];
                            int index = 0;
                            foreach (var node in _source)
                            {
                                if (WrapNode(node).StrictEquals(target)) return FenValue.FromNumber(index);
                                index++;
                            }
                        }
                        return FenValue.FromNumber(-1);
                    }));
            }

            if (_expando.TryGetValue(key, out var expando))
            {
                return expando;
            }
            
            return FenValue.Undefined;
        }

        private FenValue Item(uint index)
        {
            if (index > int.MaxValue)
            {
                return FenValue.Null;
            }

            var node = _source.ElementAtOrDefault((int)index);
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
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (key == "length" || key == "item" || key == "forEach" || key == "keys" || key == "values" || key == "entries" ||
                key == "[Symbol.iterator]" || key == "Symbol.iterator" || key == "Symbol(Symbol.iterator)")
            {
                return false;
            }

            if (TryParseArrayIndex(key, out var index))
            {
                return index >= (uint)_source.Count();
            }

            if (desc.IsAccessor)
            {
                return false;
            }

            _expando[key] = desc.Value ?? FenValue.Undefined;
            return true;
        }

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_expando.TryGetValue(key, out var val))
            {
                return new PropertyDescriptor { Value = val, Writable = true, Enumerable = true, Configurable = true, Getter = null, Setter = null };
            }

            if (TryParseArrayIndex(key, out var index) && index < (uint)_source.Count())
            {
                return new PropertyDescriptor { Value = Get(key), Writable = false, Enumerable = true, Configurable = true, Getter = null, Setter = null };
            }

            return null;
        }

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null)
        {
            return Keys(context);
        }
        public bool Delete(string key, IExecutionContext context = null)
        {
            if (TryParseArrayIndex(key, out var index))
            {
                return index >= (uint)_source.Count();
            }

            return _expando.Remove(key) || !_expando.ContainsKey(key);
        }
        public System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var keys = Enumerable.Range(0, _source.Count()).Select(i => i.ToString());
            return keys.Concat(_expando.Keys);
        }
        public bool Has(string key, IExecutionContext context = null)
        {
            if (TryParseArrayIndex(key, out var index))
            {
                return index < (uint)_source.Count();
            }

            return !Get(key, context).IsUndefined;
        }
        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (TryParseArrayIndex(key, out var index) && index < (uint)_source.Count())
            {
                return;
            }

            _expando[key] = value;
        }

        private FenObject CreateIteratorObject(Func<int, Node, FenValue> projection)
        {
            var snapshot = _source.ToList();
            var iterator = new FenObject();
            var index = 0;
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (args, thisVal) =>
            {
                var step = new FenObject();
                if (index >= snapshot.Count)
                {
                    step.Set("value", FenValue.Undefined);
                    step.Set("done", FenValue.FromBoolean(true));
                    return FenValue.FromObject(step);
                }

                step.Set("value", projection(index, snapshot[index]));
                step.Set("done", FenValue.FromBoolean(false));
                index++;
                return FenValue.FromObject(step);
            })));
            iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) => FenValue.FromObject(iterator))));
            iterator.Set("Symbol.iterator", iterator.Get("[Symbol.iterator]"));
            iterator.Set("Symbol(Symbol.iterator)", iterator.Get("[Symbol.iterator]"));
            return iterator;
        }

        private static bool TryParseArrayIndex(string key, out uint index)
        {
            return uint.TryParse(key, out index);
        }

        private static uint ToCollectionIndex(FenValue value)
        {
            var number = value.ToNumber();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            {
                return uint.MaxValue;
            }

            return unchecked((uint)number);
        }
    }
}
