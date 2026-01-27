using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsSet : FenObject
    {
        private class JsValueEqualityComparer : IEqualityComparer<FenValue>
        {
            public bool Equals(FenValue x, FenValue y)
            {
                if (x.Type != y.Type) return false;
                return x.StrictEquals(y);
            }
            public int GetHashCode(FenValue obj)
            {
                return obj.GetHashCode();
            }
        }

        private readonly HashSet<FenValue> _storage = new HashSet<FenValue>(new JsValueEqualityComparer());
        private readonly IExecutionContext _context;

        public JsSet(IExecutionContext context)
        {
            _context = context;
            Set("size", FenValue.FromNumber(0));

            Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                if (_storage.Add(val))
                {
                    Set("size", FenValue.FromNumber(_storage.Count));
                }
                return FenValue.FromObject(this);
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                return FenValue.FromBoolean(_storage.Contains(val));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                bool removed = _storage.Remove(val);
                if (removed) Set("size", FenValue.FromNumber(_storage.Count));
                return FenValue.FromBoolean(removed);
            })));

            Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                _storage.Clear();
                Set("size", FenValue.FromNumber(0));
                return FenValue.Undefined;
            })));

            Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                 return FenValue.FromObject(CreateIteratorResult(_storage));
            })));
            
            Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                 return FenValue.FromObject(CreateIteratorResult(_storage));
            })));

            Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                 // Entries in Set are [value, value]
                 var entries = _storage.Select(v => FenValue.FromObject(CreateArray(new FenValue[] { v, v })));
                 return FenValue.FromObject(CreateIteratorResult(entries));
            })));

            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                
                foreach(var val in _storage)
                {
                    callback.Invoke(new FenValue[] { (FenValue)val, (FenValue)val, FenValue.FromObject(this) }, _context);
                }
                return FenValue.Undefined;
            })));
        }

        private FenObject CreateIteratorResult(IEnumerable<FenValue> items)
        {
            var iterator = new FenObject();
            var enumerator = items.GetEnumerator();
            
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (a, t) => {
                bool hasNext = enumerator.MoveNext();
                var res = new FenObject();
                res.Set("value", hasNext ? (FenValue)enumerator.Current : FenValue.Undefined);
                res.Set("done", FenValue.FromBoolean(!hasNext));
                return FenValue.FromObject(res);
            })));
            
             if (JsSymbol.Iterator != null)
                iterator.Set(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a,t) => FenValue.FromObject(iterator))));

            return iterator;
        }

        private FenObject CreateArray(FenValue[] items)
        {
            var obj = new FenObject();
            for(int i=0; i<items.Length; i++) obj.Set(i.ToString(), items[i]);
            obj.Set("length", FenValue.FromNumber(items.Length));
            return obj;
        }
    }
}
