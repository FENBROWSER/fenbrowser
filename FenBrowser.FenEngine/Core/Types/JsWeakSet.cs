using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsWeakSet : FenObject
    {
        private readonly ConditionalWeakTable<object, object> _storage = new ConditionalWeakTable<object, object>();
        private static readonly object Present = new object();

        private static object ExtractValue(FenValue value)
        {
            if (value.IsObject)
            {
                return value.AsObject();
            }

            if (value.IsSymbol)
            {
                return value.AsSymbol();
            }

            return null;
        }

        private static object RequireValue(FenValue value)
        {
            var valueRef = ExtractValue(value);
            if (valueRef == null)
            {
                throw new InvalidOperationException("TypeError: WeakSet value must be an object or symbol");
            }

            return valueRef;
        }

        public JsWeakSet()
        {
            Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                AddEntry(value);
                return FenValue.FromObject(this);
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var valueRef = ExtractValue(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.FromBoolean(valueRef != null && _storage.TryGetValue(valueRef, out _));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var valueRef = ExtractValue(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.FromBoolean(valueRef != null && _storage.Remove(valueRef));
            })));
        }

        internal void AddEntry(FenValue value)
        {
            var valueRef = RequireValue(value);
            _storage.Remove(valueRef);
            _storage.Add(valueRef, Present);
        }
    }
}
