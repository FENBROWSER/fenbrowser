using System;
using System.Collections.Concurrent;
using System.Threading;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// JavaScript Symbol type for unique identifiers.
    /// Per ECMAScript 2015 (ES6) specification.
    /// </summary>
    public class JsSymbol : IValue
    {
        private static long _globalCounter;
        private static readonly ConcurrentDictionary<string, JsSymbol> _globalRegistry = new();

        private readonly long _id;
        private readonly string _description;
        private readonly bool _isWellKnown;

        // Well-known symbols (per spec)
        public static readonly JsSymbol Iterator = CreateWellKnown("Symbol.iterator");
        public static readonly JsSymbol ToStringTag = CreateWellKnown("Symbol.toStringTag");
        public static readonly JsSymbol ToPrimitive = CreateWellKnown("Symbol.toPrimitive");
        public static readonly JsSymbol HasInstance = CreateWellKnown("Symbol.hasInstance");
        public static readonly JsSymbol IsConcatSpreadable = CreateWellKnown("Symbol.isConcatSpreadable");
        public static readonly JsSymbol Species = CreateWellKnown("Symbol.species");
        public static readonly JsSymbol Match = CreateWellKnown("Symbol.match");
        public static readonly JsSymbol Replace = CreateWellKnown("Symbol.replace");
        public static readonly JsSymbol Search = CreateWellKnown("Symbol.search");
        public static readonly JsSymbol Split = CreateWellKnown("Symbol.split");
        public static readonly JsSymbol Unscopables = CreateWellKnown("Symbol.unscopables");
        public static readonly JsSymbol AsyncIterator = CreateWellKnown("Symbol.asyncIterator");
        public static readonly JsSymbol Dispose = CreateWellKnown("Symbol.dispose");
        public static readonly JsSymbol AsyncDispose = CreateWellKnown("Symbol.asyncDispose");

        /// <summary>
        /// Create a new unique symbol
        /// </summary>
        public JsSymbol(string description = null)
        {
            _id = Interlocked.Increment(ref _globalCounter);
            _description = description;
            _isWellKnown = false;
        }

        private JsSymbol(string description, bool isWellKnown)
        {
            _id = Interlocked.Increment(ref _globalCounter);
            _description = description;
            _isWellKnown = isWellKnown;
        }

        /// <summary>
        /// Get the symbol's description
        /// </summary>
        public string Description => _description;

        /// <summary>
        /// Get the unique internal ID
        /// </summary>
        public long Id => _id;

        /// <summary>
        /// Check if this is a well-known symbol
        /// </summary>
        public bool IsWellKnownSymbol => _isWellKnown;

        public JsValueType Type => JsValueType.Symbol;

        public bool ToBoolean() => true; // Symbols are always truthy

        public double ToNumber()
        {
            // ECMA-262 §7.1.3.1: Converting a Symbol to a number must throw TypeError
            throw new FenTypeError("TypeError: Cannot convert a Symbol value to a number");
        }

        public override string ToString()
        {
            return _description != null ? $"Symbol({_description})" : "Symbol()";
        }

        public IObject ToObject()
        {
            // Would return a Symbol wrapper object in full implementation
            throw new InvalidOperationException("Cannot convert Symbol to Object directly");
        }

        public bool LooseEquals(IValue other)
        {
            // Symbols are only equal to themselves
            if (other is JsSymbol symbol)
                return _id == symbol._id;
            return false;
        }

        public bool StrictEquals(IValue other)
        {
            // Symbols are only equal to themselves
            if (other is JsSymbol symbol)
                return _id == symbol._id;
            return false;
        }

        // Type checking
        public bool IsUndefined => false;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => false;
        public bool IsFunction => false;
        public bool IsBigInt => false;
        public bool IsSymbol => true;

        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;

        FenValue IValue.ToPrimitive(IExecutionContext context, string preferredType)
        {
            // Symbol is already a primitive
            return FenValue.FromSymbol(this);
        }

        /// <summary>
        /// Get or create a symbol in the global symbol registry.
        /// Symbol.for(key)
        /// </summary>
        public static JsSymbol For(string key)
        {
            if (string.IsNullOrEmpty(key))
                key = "undefined";

            return _globalRegistry.GetOrAdd(key, k => new JsSymbol(k));
        }

        /// <summary>
        /// Get the key for a registered symbol.
        /// Symbol.keyFor(sym)
        /// </summary>
        public static string KeyFor(JsSymbol symbol)
        {
            if (symbol  == null || symbol._isWellKnown)
                return null;

            foreach (var kvp in _globalRegistry)
            {
                if (kvp.Value._id == symbol._id)
                    return kvp.Key;
            }
            return null;
        }

        /// <summary>
        /// Create a well-known symbol
        /// </summary>
        private static JsSymbol CreateWellKnown(string name)
        {
            return new JsSymbol(name, true);
        }

        public override bool Equals(object obj)
        {
            if (obj is JsSymbol other)
                return _id == other._id;
            return false;
        }

        public override int GetHashCode() => _id.GetHashCode();

        /// <summary>
        /// Create a new unique symbol (Symbol())
        /// </summary>
        public static JsSymbol Create(string description = null)
        {
            return new JsSymbol(description);
        }

        /// <summary>
        /// Get the string key for object property access
        /// </summary>
        public string ToPropertyKey()
        {
            if (_isWellKnown && !string.IsNullOrEmpty(_description))
            {
                return $"[{_description}]";
            }
            return $"@@{_id}";
        }
    }
}
