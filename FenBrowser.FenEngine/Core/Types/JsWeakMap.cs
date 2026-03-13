using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

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
                // ECMA-262 §24.3.1.1: If key is not an Object or Symbol, throw a TypeError.
                throw new FenTypeError("Invalid value used as weak map key");
            }

            return keyRef;
        }

        public JsWeakMap()
        {
            // ECMA-262 §24.3.3.14: WeakMap.prototype[@@toStringTag] = "WeakMap"
            if (JsSymbol.ToStringTag != null)
                Set(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("WeakMap"));

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

        /// <summary>
        /// ECMA-262 §24.3.2.1: Populate from an iterable of [key, value] pairs.
        /// Accepts null/undefined (no-op), array-like objects, or Symbol.iterator iterables.
        /// </summary>
        internal void PopulateFromIterable(FenValue iterable, IExecutionContext context)
        {
            if (iterable.IsUndefined || iterable.IsNull)
                return;

            if (!iterable.IsObject)
                throw new FenTypeError("WeakMap constructor argument must be iterable");

            var source = iterable.AsObject();
            var iteratorKey = JsSymbol.Iterator?.ToPropertyKey();
            var iteratorMethod = !string.IsNullOrEmpty(iteratorKey) ? source.Get(iteratorKey, context) : FenValue.Undefined;

            if (iteratorMethod.IsFunction)
            {
                // Iterator protocol path
                var iteratorValue = iteratorMethod.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(source));
                if (!iteratorValue.IsObject)
                    throw new FenTypeError("WeakMap iterator is not an object");

                var iterator = iteratorValue.AsObject();
                while (true)
                {
                    var nextMethod = iterator.Get("next", context);
                    if (!nextMethod.IsFunction)
                        throw new FenTypeError("WeakMap iterator does not provide next()");

                    var nextResultValue = nextMethod.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(iterator));
                    if (!nextResultValue.IsObject)
                        throw new FenTypeError("WeakMap iterator result is not an object");

                    var nextResult = nextResultValue.AsObject();
                    if (nextResult.Get("done", context).ToBoolean())
                        break;

                    var pairValue = nextResult.Get("value", context);
                    if (!pairValue.IsObject)
                        throw new FenTypeError("WeakMap iterable entries must be objects");

                    var pair = pairValue.AsObject();
                    SetEntry(pair.Get("0", context), pair.Get("1", context));
                }
            }
            else
            {
                // Array-like path
                var lengthValue = source.Get("length", context);
                if (!lengthValue.IsNumber)
                    throw new FenTypeError("WeakMap constructor argument must be iterable");

                var length = Math.Max(0, (int)lengthValue.ToNumber());
                for (var i = 0; i < length; i++)
                {
                    var pairValue = source.Get(i.ToString(), context);
                    if (!pairValue.IsObject)
                        throw new FenTypeError("WeakMap iterable entries must be objects");

                    var pair = pairValue.AsObject();
                    SetEntry(pair.Get("0", context), pair.Get("1", context));
                }
            }
        }
    }
}
