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
            // ECMA-262 §24.1.1.2: Set key comparison uses SameValueZero.
            // SameValueZero(x, y) differs from StrictEquals only for NaN: NaN ===SameValueZero NaN.
            public bool Equals(FenValue x, FenValue y)
            {
                if (x.Type == Interfaces.ValueType.Number && double.IsNaN(x._numberValue) &&
                    y.Type == Interfaces.ValueType.Number && double.IsNaN(y._numberValue))
                    return true;
                return x.StrictEquals(y);
            }

            public int GetHashCode(FenValue obj)
            {
                // Normalise NaN so all NaN values land in the same bucket.
                if (obj.Type == Interfaces.ValueType.Number && double.IsNaN(obj._numberValue))
                    return HashCode.Combine(Interfaces.ValueType.Number, double.NaN.GetHashCode());
                return obj.GetHashCode();
            }
        }

        private readonly HashSet<FenValue> _storage = new HashSet<FenValue>(new JsValueEqualityComparer());
        private readonly IExecutionContext _context;

        /// <summary>
        /// Internal read-only accessor for StructuredClone (HTML §2.7.4) to iterate Set entries
        /// without exposing mutable storage. ECMA-262 §24.2.3.1 [[SetData]] internal slot.
        /// </summary>
        internal IReadOnlyCollection<FenValue> InternalStorage => _storage;

        public JsSet(IExecutionContext context)
        {
            _context = context;
            Set("size", FenValue.FromNumber(0));

            // ECMA-262 §24.2.3.12: Set.prototype[@@toStringTag] = "Set"
            if (JsSymbol.ToStringTag != null)
                Set(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Set"));

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
                 var entries = _storage.Select(v => FenValue.FromObject(CreateArray(new FenValue[] { v, v })));
                 return FenValue.FromObject(CreateIteratorResult(entries));
            })));

            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                foreach (var val in _storage)
                {
                    callback.Invoke(new FenValue[] { val, val, FenValue.FromObject(this) }, _context);
                }
                return FenValue.Undefined;
            })));

            // [Symbol.iterator] — default iterator is values()
            Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateIteratorResult(_storage));
            })));

            // ES2025: Set methods
            Set("union", FenValue.FromFunction(new FenFunction("union", Union)));
            Set("intersection", FenValue.FromFunction(new FenFunction("intersection", Intersection)));
            Set("difference", FenValue.FromFunction(new FenFunction("difference", Difference)));
            Set("symmetricDifference", FenValue.FromFunction(new FenFunction("symmetricDifference", SymmetricDifference)));
            Set("isSubsetOf", FenValue.FromFunction(new FenFunction("isSubsetOf", IsSubsetOf)));
            Set("isSupersetOf", FenValue.FromFunction(new FenFunction("isSupersetOf", IsSupersetOf)));
            Set("isDisjointFrom", FenValue.FromFunction(new FenFunction("isDisjointFrom", IsDisjointFrom)));
        }

        // Extracts iterable values from a FenValue (JsSet or array-like)
        private IEnumerable<FenValue> ExtractValues(FenValue other)
        {
            if (!other.IsObject) yield break;
            var obj = other.AsObject();
            if (obj == null) yield break;
            if (obj is JsSet otherSet)
            {
                foreach (var v in otherSet._storage) yield return v;
                yield break;
            }
            var lenVal = obj.Get("length", null);
            if (lenVal.IsNumber)
            {
                int len = (int)lenVal.ToNumber();
                for (int i = 0; i < len; i++) yield return obj.Get(i.ToString(), null);
            }
        }

        private FenValue Union(FenValue[] args, FenValue thisVal)
        {
            var result = new JsSet(_context);
            foreach (var v in _storage) result._storage.Add(v);
            foreach (var v in ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined)) result._storage.Add(v);
            result.Set("size", FenValue.FromNumber(result._storage.Count));
            return FenValue.FromObject(result);
        }

        private FenValue Intersection(FenValue[] args, FenValue thisVal)
        {
            var result = new JsSet(_context);
            var otherVals = new HashSet<FenValue>(ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined), new JsValueEqualityComparer());
            foreach (var v in _storage) if (otherVals.Contains(v)) result._storage.Add(v);
            result.Set("size", FenValue.FromNumber(result._storage.Count));
            return FenValue.FromObject(result);
        }

        private FenValue Difference(FenValue[] args, FenValue thisVal)
        {
            var result = new JsSet(_context);
            var otherVals = new HashSet<FenValue>(ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined), new JsValueEqualityComparer());
            foreach (var v in _storage) if (!otherVals.Contains(v)) result._storage.Add(v);
            result.Set("size", FenValue.FromNumber(result._storage.Count));
            return FenValue.FromObject(result);
        }

        private FenValue SymmetricDifference(FenValue[] args, FenValue thisVal)
        {
            var result = new JsSet(_context);
            var otherVals = new HashSet<FenValue>(ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined), new JsValueEqualityComparer());
            foreach (var v in _storage) if (!otherVals.Contains(v)) result._storage.Add(v);
            foreach (var v in otherVals) if (!_storage.Contains(v)) result._storage.Add(v);
            result.Set("size", FenValue.FromNumber(result._storage.Count));
            return FenValue.FromObject(result);
        }

        private FenValue IsSubsetOf(FenValue[] args, FenValue thisVal)
        {
            var otherVals = new HashSet<FenValue>(ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined), new JsValueEqualityComparer());
            return FenValue.FromBoolean(_storage.All(v => otherVals.Contains(v)));
        }

        private FenValue IsSupersetOf(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromBoolean(ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined).All(v => _storage.Contains(v)));
        }

        private FenValue IsDisjointFrom(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromBoolean(!ExtractValues(args.Length > 0 ? args[0] : FenValue.Undefined).Any(v => _storage.Contains(v)));
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
                iterator.Set(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));

            return iterator;
        }

        private FenObject CreateArray(FenValue[] items)
        {
            var obj = new FenObject();
            for (int i = 0; i < items.Length; i++) obj.Set(i.ToString(), items[i]);
            obj.Set("length", FenValue.FromNumber(items.Length));
            return obj;
        }
    }
}
