using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsWeakMap : FenObject
    {
        private readonly ConditionalWeakTable<object, IValue> _storage = new ConditionalWeakTable<object, IValue>();

        private static object ExtractKey(FenValue key)
        {
            if (key.IsObject)
            {
                return key.AsObject();
            }

            if (key.IsSymbol)
            {
                return key.AsSymbol();
            }

            return null;
        }

        private static object RequireKey(FenValue key)
        {
            var keyRef = ExtractKey(key);
            if (keyRef == null)
            {
                throw new InvalidOperationException("TypeError: WeakMap key must be an object or symbol");
            }

            return keyRef;
        }

        public JsWeakMap()
        {
            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                var value = args.Length > 1 ? args[1] : FenValue.Undefined;
                SetEntry(key, value);
                return FenValue.FromObject(this);
            })));

            Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var keyRef = ExtractKey(args.Length > 0 ? args[0] : FenValue.Undefined);
                if (keyRef != null && _storage.TryGetValue(keyRef, out var value))
                {
                    return (FenValue)value;
                }

                return FenValue.Undefined;
            })));

            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                var keyRef = ExtractKey(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.FromBoolean(keyRef != null && _storage.TryGetValue(keyRef, out _));
            })));

            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                var keyRef = ExtractKey(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.FromBoolean(keyRef != null && _storage.Remove(keyRef));
            })));
        }

        internal void SetEntry(FenValue key, FenValue value)
        {
            var keyRef = RequireKey(key);
            _storage.Remove(keyRef);
            _storage.Add(keyRef, value);
        }
    }
}
