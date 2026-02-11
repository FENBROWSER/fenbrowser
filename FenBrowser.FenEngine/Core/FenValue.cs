using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript value in FenEngine.
    /// Refactored to a struct (Value Type) to minimize heap allocations and maximize JIT performance.
    /// </summary>
    public struct FenValue : IValue
    {
        public Interfaces.ValueType Type { get => _type; private set => _type = value; }
        public Interfaces.ValueType _type; // Manual backing field for JIT
        public double _numberValue;       // Public field for JIT
        private object _refValue;

        // Static singleton values for undefined and null
        public static readonly FenValue Undefined = new FenValue { Type = Interfaces.ValueType.Undefined };
        public static readonly FenValue Null = new FenValue { Type = Interfaces.ValueType.Null };
        public static readonly FenValue Break = new FenValue { Type = Interfaces.ValueType.Break };
        public static readonly FenValue Continue = new FenValue { Type = Interfaces.ValueType.Continue };

        public static FenValue BreakWithLabel(string label)
        {
            return new FenValue { Type = Interfaces.ValueType.Break, _refValue = label };
        }

        public static FenValue ContinueWithLabel(string label)
        {
            return new FenValue { Type = Interfaces.ValueType.Continue, _refValue = label };
        }

        /// <summary>
        /// Gets the label for a labeled break/continue, or null if unlabeled.
        /// </summary>
        public string BreakContinueLabel => (Type == Interfaces.ValueType.Break || Type == Interfaces.ValueType.Continue) ? _refValue as string : null;

        /// <summary>
        /// Gets the inner value for ReturnValue or Yield wrapper types.
        /// </summary>
        public FenValue InnerValue => (_refValue is FenValue fv) ? fv : Undefined;

        public static FenValue FromBoolean(bool value)
        {
            return new FenValue { Type = Interfaces.ValueType.Boolean, _numberValue = value ? 1.0 : 0.0 };
        }

        public static FenValue FromNumber(double value)
        {
            return new FenValue { Type = Interfaces.ValueType.Number, _numberValue = value };
        }

        public static FenValue FromString(string value)
        {
            return new FenValue { Type = Interfaces.ValueType.String, _refValue = value ?? string.Empty };
        }

        public static FenValue FromObject(IObject obj)
        {
            return new FenValue { Type = Interfaces.ValueType.Object, _refValue = obj };
        }

        public static FenValue FromFunction(FenFunction func)
        {
            return new FenValue { Type = Interfaces.ValueType.Function, _refValue = func };
        }

        public static FenValue FromSymbol(JsSymbol symbol)
        {
            return new FenValue { Type = Interfaces.ValueType.Symbol, _refValue = symbol };
        }

        public static FenValue FromBigInt(Types.JsBigInt bigInt)
        {
            return new FenValue { Type = Interfaces.ValueType.BigInt, _refValue = bigInt };
        }

        public static FenValue FromError(string message)
        {
            return new FenValue { Type = Interfaces.ValueType.Error, _refValue = message };
        }

        public static FenValue FromReturnValue(FenValue value)
        {
            return new FenValue { Type = Interfaces.ValueType.ReturnValue, _refValue = value };
        }

        public static FenValue FromYield(FenValue value)
        {
            return new FenValue { Type = Interfaces.ValueType.Yield, _refValue = value };
        }
        
        /// <summary>
        /// Creates a yield delegation marker for yield* (ES2015)
        /// The inner value is the iterable/generator to delegate to
        /// </summary>
        public static FenValue FromYieldDelegate(FenValue iterable)
        {
            return new FenValue { Type = Interfaces.ValueType.YieldDelegate, _refValue = iterable };
        }
        


        public JsSymbol AsSymbol()
        {
             return Type == Interfaces.ValueType.Symbol ? _refValue as JsSymbol : null;
        }

        public Types.JsBigInt AsBigInt()
        {
             return Type == Interfaces.ValueType.BigInt ? _refValue as Types.JsBigInt : null;
        }

        public FenValue GetReturnValue()
        {
            return Type == Interfaces.ValueType.ReturnValue ? (FenValue)_refValue : FenValue.Undefined;
        }

        // Type checking
        public bool IsUndefined => Type == Interfaces.ValueType.Undefined;
        public bool IsNull => Type == Interfaces.ValueType.Null;
        public bool IsBoolean => Type == Interfaces.ValueType.Boolean;
        public bool IsNumber => Type == Interfaces.ValueType.Number;
        public bool IsString => Type == Interfaces.ValueType.String;
        public bool IsObject => Type == Interfaces.ValueType.Object;
        public bool IsFunction => Type == Interfaces.ValueType.Function;
        public bool IsSymbol => Type == Interfaces.ValueType.Symbol;
        public bool IsBigInt => Type == Interfaces.ValueType.BigInt;

        // IValue Implementation
        public bool ToBoolean() => AsBoolean();
        public double ToNumber() => AsNumber();
        public override string ToString() => AsString();
        public IObject ToObject() => AsObject();
        
        public bool StrictEquals(IValue other)
        {
            if (!(other is FenValue otherFen)) return false;
            return this == otherFen;
        }

        public bool LooseEquals(IValue other)
        {
            if (!(other is FenValue b)) return false;
            var a = this;
            
            // ES5.1 Section 11.9.3: Abstract Equality Comparison
            // Step 1: Same type -> strict equals
            if (a.Type == b.Type) return a == b;
            
            // Step 2-3: null == undefined (bidirectional)
            if ((a.IsNull && b.IsUndefined) || (a.IsUndefined && b.IsNull)) return true;
            
            // Step 4-5: Number == String -> convert string to number
            if (a.IsNumber && b.IsString) return a._numberValue == b.AsNumber();
            if (a.IsString && b.IsNumber) return a.AsNumber() == b._numberValue;
            
            // Step 6-7: Boolean == anything -> convert boolean to number, recurse
            if (a.IsBoolean) return FromNumber(a._numberValue).LooseEquals(b);
            if (b.IsBoolean) return a.LooseEquals(FromNumber(b._numberValue));
            
            // Step 8-9: Object == String|Number -> ToPrimitive, recurse
            if ((a.IsObject || a.IsFunction) && (b.IsString || b.IsNumber || b.IsSymbol))
                return a.ToPrimitive().LooseEquals(b);
            if ((b.IsObject || b.IsFunction) && (a.IsString || a.IsNumber || a.IsSymbol))
                return a.LooseEquals(b.ToPrimitive());
            
            // Step 10: Otherwise false
            return false;
        }

        // Value extraction
        public bool AsBoolean()
        {
            switch (Type)
            {
                case Interfaces.ValueType.Boolean: return _numberValue != 0;
                case Interfaces.ValueType.Number: return _numberValue != 0 && !double.IsNaN(_numberValue);
                case Interfaces.ValueType.String: return !string.IsNullOrEmpty((string)_refValue);
                case Interfaces.ValueType.Null:
                case Interfaces.ValueType.Undefined: return false;
                case Interfaces.ValueType.Symbol: return true;
                default: return true; 
            }
        }

        public double AsNumber()
        {
            switch (Type)
            {
                case Interfaces.ValueType.Number: return _numberValue;
                case Interfaces.ValueType.Boolean: return _numberValue;
                case Interfaces.ValueType.String:
                    var s = ((string)_refValue).Trim();
                    if (string.IsNullOrEmpty(s)) return 0.0; // ES5.1: "" -> 0
                    // Handle hex: 0x or 0X prefix
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        try { return Convert.ToInt64(s, 16); }
                        catch { return double.NaN; }
                    }
                    // Handle Infinity
                    if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
                    if (s == "-Infinity") return double.NegativeInfinity;
                    // Standard number parsing
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                        return result;
                    return double.NaN;
                case Interfaces.ValueType.Null: return 0.0;
                default: return double.NaN;
            }
        }

        public string AsString()
        {
            switch (Type)
            {
                case Interfaces.ValueType.String: return (string)_refValue;
                case Interfaces.ValueType.Number:
                    if (double.IsNaN(_numberValue)) return "NaN";
                    if (double.IsPositiveInfinity(_numberValue)) return "Infinity";
                    if (double.IsNegativeInfinity(_numberValue)) return "-Infinity";
                    return _numberValue.ToString(CultureInfo.InvariantCulture);
                case Interfaces.ValueType.Boolean: return _numberValue != 0 ? "true" : "false";
                case Interfaces.ValueType.Null: return "null";
                case Interfaces.ValueType.Undefined: return "undefined";
                case Interfaces.ValueType.Object:
                {
                    // ES5.1 9.8: ToString for Objects via ToPrimitive(String) — calls toString()/valueOf()
                    var prim = ToPrimitive("string");
                    if (prim.Type != Interfaces.ValueType.Object && prim.Type != Interfaces.ValueType.Function)
                        return prim.AsString();
                    // Fallback if no method returned a primitive
                    var objFallback = AsObject();
                    if (objFallback is FenObject fenObjFallback)
                        return $"[object {fenObjFallback.InternalClass ?? "Object"}]";
                    return "[object Object]";
                }
                case Interfaces.ValueType.Function: return "[function]";
                case Interfaces.ValueType.Error: return (string)_refValue ?? "Error";
                case Interfaces.ValueType.BigInt:
                    var bigInt = _refValue as Types.JsBigInt;
                    return bigInt?.ToStringWithoutSuffix() ?? "0";
                default: return string.Empty;
            }
        }

        public IObject AsObject()
        {
            if (Type == Interfaces.ValueType.Object || Type == Interfaces.ValueType.Function)
                return (IObject)_refValue;
            return null;
        }

        public FenFunction AsFunction()
        {
            if (Type == Interfaces.ValueType.Function)
                return (FenFunction)_refValue;
            return null;
        }

        public string AsError()
        {
            if (Type == Interfaces.ValueType.Error)
                return (string)_refValue;
            return null;
        }

        /// <summary>
        /// ES2015+ Section 7.1.1: OrdinaryToPrimitive / @@toPrimitive
        /// Converts an object to a primitive value, respecting Symbol.toPrimitive.
        /// </summary>
        public FenValue ToPrimitive(string preferredType = "number")
        {
            // Primitives return themselves
            if (Type != Interfaces.ValueType.Object && Type != Interfaces.ValueType.Function)
                return this;

            var obj = AsObject();
            if (obj == null) return this;

            // ES2015: Check for [Symbol.toPrimitive] method first
            var toPrimMethod = obj.Get("@@toPrimitive", null);
            if (!toPrimMethod.IsUndefined && toPrimMethod.IsFunction)
            {
                var hint = FromString(preferredType == "string" ? "string" : preferredType == "number" ? "number" : "default");
                var result = toPrimMethod.AsFunction().Invoke(new FenValue[] { hint }, null);
                if (result.Type != Interfaces.ValueType.Object && result.Type != Interfaces.ValueType.Function)
                    return result;
                // If result is still an object, TypeError — but we fallthrough for safety
            }

            // For date objects, prefer string
            if (obj is FenObject fenObj && fenObj.InternalClass == "Date")
                preferredType = "string";

            string[] tryOrder = preferredType == "string"
                ? new[] { "toString", "valueOf" }
                : new[] { "valueOf", "toString" };

            foreach (var methodName in tryOrder)
            {
                var method = obj.Get(methodName, null);
                if (method.IsFunction)
                {
                    var result = method.AsFunction().Invoke(new FenValue[0], null);
                    // Check if result is primitive
                    if (result.Type != Interfaces.ValueType.Object && result.Type != Interfaces.ValueType.Function)
                        return result;
                }
            }

            // TypeError in spec, but we'll return NaN for safety
            return FromNumber(double.NaN);
        }

        /// <summary>
        /// ES5.1 Section 11.8.5: Abstract Relational Comparison
        /// Returns: true if x < y, false if x >= y, undefined if comparison is undefined
        /// </summary>
        public static FenValue AbstractRelationalComparison(FenValue x, FenValue y, bool leftFirst = true)
        {
            // Step 1-2: Get primitives
            FenValue px, py;
            if (leftFirst)
            {
                px = x.ToPrimitive("number");
                py = y.ToPrimitive("number");
            }
            else
            {
                py = y.ToPrimitive("number");
                px = x.ToPrimitive("number");
            }
            
            // Step 3: If both strings, compare as strings
            if (px.IsString && py.IsString)
            {
                var sx = (string)px._refValue;
                var sy = (string)py._refValue;
                return FromBoolean(string.CompareOrdinal(sx, sy) < 0);
            }
            
            // Step 4: ToNumber both
            var nx = px.AsNumber();
            var ny = py.AsNumber();
            
            // Step 5-6: NaN check
            if (double.IsNaN(nx) || double.IsNaN(ny))
                return Undefined; // Comparison is "undefined"
            
            // Step 7-11: Normal numeric comparison
            return FromBoolean(nx < ny);
        }

        public object ToNativeObject()
        {
            switch (Type)
            {
                case Interfaces.ValueType.Undefined:
                case Interfaces.ValueType.Null:
                    return null;
                case Interfaces.ValueType.Boolean:
                    return _numberValue != 0;
                case Interfaces.ValueType.Number:
                    return _numberValue;
                case Interfaces.ValueType.String:
                    return (string)_refValue;
                case Interfaces.ValueType.Object:
                    var obj = (IObject)_refValue;
                    if (obj is FenObject fenObj)
                    {
                        var lengthVal = fenObj.Get("length");
                        if (lengthVal.IsNumber)
                        {
                            var len = (int)lengthVal.AsNumber();
                            var list = new List<object>();
                            for (int i = 0; i < len; i++) list.Add(fenObj.Get(i.ToString()).ToNativeObject());
                            return list;
                        }
                        
                        var dict = new Dictionary<string, object>();
                        foreach (var key in fenObj.Keys()) dict[key] = fenObj.Get(key).ToNativeObject();
                        return dict;
                    }
                    return "[object Object]";
                case Interfaces.ValueType.ReturnValue:
                    return _refValue;
                default:
                    return null;
            }
        }

        // Operators
        public static FenValue operator +(FenValue a, FenValue b)
        {
            if (a.IsString || b.IsString) return FromString(a.AsString() + b.AsString());
            return FromNumber(a.AsNumber() + b.AsNumber());
        }

        public static FenValue operator -(FenValue a, FenValue b) => FromNumber(a.AsNumber() - b.AsNumber());
        public static FenValue operator *(FenValue a, FenValue b) => FromNumber(a.AsNumber() * b.AsNumber());
        public static FenValue operator /(FenValue a, FenValue b) => FromNumber(a.AsNumber() / b.AsNumber());
        
        public static bool operator true(FenValue a) => a.AsBoolean();
        public static bool operator false(FenValue a) => !a.AsBoolean();
        public static FenValue operator !(FenValue a) => FromBoolean(!a.AsBoolean());

        // Equality
        public static bool operator ==(FenValue a, FenValue b)
        {
            if (a.Type != b.Type) return false;
            
            switch (a.Type)
            {
                case Interfaces.ValueType.Undefined:
                case Interfaces.ValueType.Null:
                    return true;
                case Interfaces.ValueType.Boolean:
                case Interfaces.ValueType.Number:
                    return a._numberValue == b._numberValue;
                case Interfaces.ValueType.String:
                    // Use ordinal string comparison, not reference equality
                    return string.Equals((string)a._refValue, (string)b._refValue, StringComparison.Ordinal);
                case Interfaces.ValueType.Object:
                case Interfaces.ValueType.Function:
                case Interfaces.ValueType.Symbol:
                    return ReferenceEquals(a._refValue, b._refValue);
                default:
                    return false;
            }
        }

        public static bool operator !=(FenValue a, FenValue b) => !(a == b);

        public override bool Equals(object obj)
        {
            if (obj is FenValue other) return this == other;
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Type, _numberValue, _refValue);
        }
    }
}
