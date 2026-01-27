using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript Symbol primitive.
    /// Symbols are unique, immutable identifiers used as property keys.
    /// </summary>
    public class FenSymbol : IValue
    {
        private static int _globalCounter = 0;
        private static readonly Dictionary<string, FenSymbol> _globalRegistry = new Dictionary<string, FenSymbol>();
        private static readonly object _lock = new object();

        public string Description { get; }
        private readonly int _id;

        // Well-known symbols
        public static readonly FenSymbol Iterator = new FenSymbol("Symbol.iterator");
        public static readonly FenSymbol ToStringTag = new FenSymbol("Symbol.toStringTag");
        public static readonly FenSymbol HasInstance = new FenSymbol("Symbol.hasInstance");
        public static readonly FenSymbol ToPrimitive = new FenSymbol("Symbol.toPrimitive");
        public static readonly FenSymbol IsConcatSpreadable = new FenSymbol("Symbol.isConcatSpreadable");
        public static readonly FenSymbol Species = new FenSymbol("Symbol.species");
        public static readonly FenSymbol Match = new FenSymbol("Symbol.match");
        public static readonly FenSymbol Replace = new FenSymbol("Symbol.replace");
        public static readonly FenSymbol Search = new FenSymbol("Symbol.search");
        public static readonly FenSymbol Split = new FenSymbol("Symbol.split");
        public static readonly FenSymbol Unscopables = new FenSymbol("Symbol.unscopables");
        public static readonly FenSymbol AsyncIterator = new FenSymbol("Symbol.asyncIterator");

        public FenSymbol(string description = null)
        {
            Description = description;
            lock (_lock) { _id = ++_globalCounter; }
        }

        /// <summary>
        /// Symbol.for(key) - Returns a shared symbol from the global registry.
        /// </summary>
        public static FenSymbol For(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "";
            lock (_lock)
            {
                if (!_globalRegistry.TryGetValue(key, out var sym))
                {
                    sym = new FenSymbol(key);
                    _globalRegistry[key] = sym;
                }
                return sym;
            }
        }

        /// <summary>
        /// Symbol.keyFor(symbol) - Returns the key for a symbol in the global registry.
        /// </summary>
        public static string KeyFor(FenSymbol symbol)
        {
            if (symbol  == null) return null;
            lock (_lock)
            {
                foreach (var kv in _globalRegistry)
                {
                    if (ReferenceEquals(kv.Value, symbol))
                        return kv.Key;
                }
            }
            return null;
        }

        // IValue implementation
        public JsValueType Type => JsValueType.Object; // Symbols are treated as objects for now
        public bool IsUndefined => false;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => true; // Symbol appears as object in typeof for certain cases
        public bool IsFunction => false;

        public bool ToBoolean() => true; // Symbols are truthy
        public double ToNumber() => double.NaN;
        public IObject ToObject() => null; // Symbols can't be converted to object directly
        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;

        public bool StrictEquals(IValue other)
        {
            // Symbols are strictly equal only to themselves
            return ReferenceEquals(this, other);
        }

        public bool LooseEquals(IValue other)
        {
            // Same as strict for symbols
            return StrictEquals(other);
        }

        public override string ToString() => $"Symbol({Description ?? ""})";

        public override bool Equals(object obj)
        {
            // Symbols are compared by identity
            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode() => _id;

        /// <summary>
        /// Creates a FenObject that represents the Symbol constructor for use in JS.
        /// </summary>
        public static FenObject CreateSymbolConstructor()
        {
            var symbolCtor = new FenObject();

            // Symbol(description) - Create a new unique symbol
            symbolCtor.NativeObject = new Func<IValue[], IExecutionContext, IValue>((args, ctx) =>
            {
                var desc = args.Length > 0 ? args[0].ToString() : null;
                return FenValue.FromObject(new FenSymbolWrapper(new FenSymbol(desc)));
            });

            // Symbol.for(key)
            symbolCtor.Set("for", FenValue.FromFunction(new FenFunction("for", (args, ctx) =>
            {
                var key = args.Length > 0 ? args[0].ToString() : "";
                return FenValue.FromObject(new FenSymbolWrapper(FenSymbol.For(key)));
            })));

            // Symbol.keyFor(symbol)
            symbolCtor.Set("keyFor", FenValue.FromFunction(new FenFunction("keyFor", (args, ctx) =>
            {
                if (args.Length > 0 && args[0].AsObject() is FenSymbolWrapper sw)
                {
                    var key = FenSymbol.KeyFor(sw.Symbol);
                    return key != null ? FenValue.FromString(key) : FenValue.Undefined;
                }
                return FenValue.Undefined;
            })));

            // Well-known symbols
            symbolCtor.Set("iterator", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Iterator)));
            symbolCtor.Set("toStringTag", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.ToStringTag)));
            symbolCtor.Set("hasInstance", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.HasInstance)));
            symbolCtor.Set("toPrimitive", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.ToPrimitive)));
            symbolCtor.Set("isConcatSpreadable", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.IsConcatSpreadable)));
            symbolCtor.Set("species", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Species)));
            symbolCtor.Set("match", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Match)));
            symbolCtor.Set("replace", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Replace)));
            symbolCtor.Set("search", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Search)));
            symbolCtor.Set("split", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Split)));
            symbolCtor.Set("unscopables", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.Unscopables)));
            symbolCtor.Set("asyncIterator", FenValue.FromObject(new FenSymbolWrapper(FenSymbol.AsyncIterator)));

            return symbolCtor;
        }
    }

    /// <summary>
    /// Wrapper to expose FenSymbol as an IObject for JavaScript interop.
    /// </summary>
    public class FenSymbolWrapper : FenObject
    {
        public FenSymbol Symbol { get; }

        public FenSymbolWrapper(FenSymbol symbol)
        {
            Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            
            // Add toString method
            Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, ctx) =>
                FenValue.FromString(Symbol.ToString()))));
            
            // Add description property
            Set("description", FenValue.FromString(Symbol.Description ?? ""));
        }

        public override string ToString() => Symbol.ToString();
    }
}
