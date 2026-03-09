using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsMap : FenObject
    {
        private class JsValueEqualityComparer : IEqualityComparer<IValue>
        {
            // ECMA-262 §24.1.1.2: Map key comparison uses SameValueZero.
            // SameValueZero(x, y) differs from StrictEquals only for NaN: NaN ===SameValueZero NaN.
            public bool Equals(IValue x, IValue y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                if (x is FenValue xv && y is FenValue yv &&
                    xv.Type == Interfaces.ValueType.Number && double.IsNaN(xv._numberValue) &&
                    yv.Type == Interfaces.ValueType.Number && double.IsNaN(yv._numberValue))
                    return true;
                return x.StrictEquals(y);
            }

            public int GetHashCode(IValue obj)
            {
                // Normalise NaN so all NaN keys land in the same bucket.
                if (obj is FenValue v && v.Type == Interfaces.ValueType.Number && double.IsNaN(v._numberValue))
                    return HashCode.Combine(Interfaces.ValueType.Number, double.NaN.GetHashCode());
                return obj.GetHashCode();
            }
        }

        private readonly Dictionary<IValue, IValue> _storage = new Dictionary<IValue, IValue>(new JsValueEqualityComparer());
        private readonly IExecutionContext _context;

        public JsMap(IExecutionContext context)
        {
            _context = context;
            Set("size", FenValue.FromNumber(0));

            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                var val = args.Length > 1 ? args[1] : FenValue.Undefined;
                _storage[key] = val;
                Set("size", FenValue.FromNumber(_storage.Count));
                return FenValue.FromObject(this);
            })));

            Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                return _storage.TryGetValue(key, out var val) ? (FenValue)val : FenValue.Undefined;
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                return FenValue.FromBoolean(_storage.ContainsKey(key));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                bool removed = _storage.Remove(key);
                if (removed) Set("size", FenValue.FromNumber(_storage.Count));
                return FenValue.FromBoolean(removed);
            })));

            Set("clear", FenValue.FromFunction(new FenFunction("clear", (args, thisVal) =>
            {
                _storage.Clear();
                Set("size", FenValue.FromNumber(0));
                return FenValue.Undefined;
            })));

            Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                 return FenValue.FromObject(CreateIteratorResult(_storage.Keys.Select(k => (FenValue)k)));
            })));
            
            Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                 return FenValue.FromObject(CreateIteratorResult(_storage.Values.Select(v => (FenValue)v)));
            })));
            
             Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                 var entries = _storage.Select(kv => FenValue.FromObject(CreateArray(new FenValue[] { (FenValue)kv.Key, (FenValue)kv.Value })));
                 return FenValue.FromObject(CreateIteratorResult(entries));
            })));
            
            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                // Use _context stored
                // Also standard map.forEach passes (value, key, map)
                foreach(var kv in _storage)
                {
                    callback.Invoke(new FenValue[] { (FenValue)kv.Value, (FenValue)kv.Key, FenValue.FromObject((IObject)this) }, _context);
                }
                return FenValue.Undefined;
            })));

            // [Symbol.iterator] — default iterator returns entries (same as entries())
            Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
            {
                var entries = _storage.Select(kv => FenValue.FromObject(CreateArray(new FenValue[] { (FenValue)kv.Key, (FenValue)kv.Value })));
                return FenValue.FromObject(CreateIteratorResult(entries));
            })));
        }

        private FenObject CreateIteratorResult(IEnumerable<FenValue> items)
        {
            var iterator = new FenObject();
            var enumerator = items.GetEnumerator();
            
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (a, t) => {
                bool hasNext = enumerator.MoveNext();
                var res = new FenObject();
                res.Set("value", hasNext ? enumerator.Current : FenValue.Undefined);
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
