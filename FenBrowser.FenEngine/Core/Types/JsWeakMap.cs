using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsWeakMap : FenObject
    {
        private readonly ConditionalWeakTable<object, IValue> _storage = new ConditionalWeakTable<object, IValue>();

        // ES2023: Symbols are valid WeakMap keys. Extract the underlying reference type for both objects and symbols.
        private static object ExtractKey(FenValue key)
        {
            if (key.IsObject) return key.AsObject();
            if (key.IsSymbol) return key.AsSymbol();
            return null;
        }

        public JsWeakMap()
        {
            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                var key = args.Length > 0 ? args[0] : FenValue.Undefined;
                var val = args.Length > 1 ? args[1] : FenValue.Undefined;

                var keyRef = ExtractKey(key);
                if (keyRef == null) throw new InvalidOperationException("TypeError: WeakMap key must be an object or symbol");

                try
                {
                    _storage.Remove(keyRef);
                    _storage.Add(keyRef, val);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[JsWeakMap] Failed to add weak key/value: {ex.Message}", LogCategory.JavaScript);
                }

                return FenValue.FromObject(this);
            })));

            Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                var keyRef = ExtractKey(args.Length > 0 ? args[0] : FenValue.Undefined);
                if (keyRef != null && _storage.TryGetValue(keyRef, out var val))
                    return (FenValue)val;
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
    }
}


