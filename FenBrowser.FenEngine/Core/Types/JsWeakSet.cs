using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

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
                // ECMA-262 §24.4.1.1: If value is not an Object or Symbol, throw a TypeError.
                throw new FenTypeError("Invalid value used in weak set");
            }

            return valueRef;
        }

        public JsWeakSet()
        {
            // ECMA-262 §24.4.3.5: WeakSet.prototype[@@toStringTag] = "WeakSet"
            if (JsSymbol.ToStringTag != null)
                Set(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("WeakSet"));

            Set("add", FenValue.FromFunction(new FenFunction("add", (args, thisVal) =>
            {
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                AddEntry(value);
                // ECMA-262 §24.4.3.1: add() returns this WeakSet.
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

        /// <summary>
        /// ECMA-262 §24.4.2.1: Populate from an iterable of values.
        /// Accepts null/undefined (no-op), array-like objects, or Symbol.iterator iterables.
        /// </summary>
        internal void PopulateFromIterable(FenValue iterable, IExecutionContext context)
        {
            if (iterable.IsUndefined || iterable.IsNull)
                return;

            if (!iterable.IsObject)
                throw new FenTypeError("WeakSet constructor argument must be iterable");

            var source = iterable.AsObject();
            var iteratorKey = JsSymbol.Iterator?.ToPropertyKey();
            var iteratorMethod = !string.IsNullOrEmpty(iteratorKey) ? source.Get(iteratorKey, context) : FenValue.Undefined;

            if (iteratorMethod.IsFunction)
            {
                // Iterator protocol path
                var iteratorValue = iteratorMethod.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(source));
                if (!iteratorValue.IsObject)
                    throw new FenTypeError("WeakSet iterator is not an object");

                var iterator = iteratorValue.AsObject();
                while (true)
                {
                    var nextMethod = iterator.Get("next", context);
                    if (!nextMethod.IsFunction)
                        throw new FenTypeError("WeakSet iterator does not provide next()");

                    var nextResultValue = nextMethod.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(iterator));
                    if (!nextResultValue.IsObject)
                        throw new FenTypeError("WeakSet iterator result is not an object");

                    var nextResult = nextResultValue.AsObject();
                    if (nextResult.Get("done", context).ToBoolean())
                        break;

                    AddEntry(nextResult.Get("value", context));
                }
            }
            else
            {
                // Array-like path
                var lengthValue = source.Get("length", context);
                if (!lengthValue.IsNumber)
                    throw new FenTypeError("WeakSet constructor argument must be iterable");

                var length = Math.Max(0, (int)lengthValue.ToNumber());
                for (var i = 0; i < length; i++)
                {
                    AddEntry(source.Get(i.ToString(), context));
                }
            }
        }
    }
}
