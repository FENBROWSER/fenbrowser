using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;

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
        public static readonly FenValue True = new FenValue { Type = Interfaces.ValueType.Boolean, _numberValue = 1.0 };
        public static readonly FenValue False = new FenValue { Type = Interfaces.ValueType.Boolean, _numberValue = 0.0 };
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

        public static FenValue FromThrow(FenValue value)
        {
            return new FenValue { Type = Interfaces.ValueType.Throw, _refValue = value };
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

        public FenValue GetThrownValue()
        {
            return Type == Interfaces.ValueType.Throw ? (FenValue)_refValue : FenValue.Undefined;
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
        public bool IsError => Type == Interfaces.ValueType.Error;
        public bool IsHtmlDdaObject => (Type == Interfaces.ValueType.Object || Type == Interfaces.ValueType.Function) &&
                                       _refValue is Interfaces.IHtmlDdaObject;

        // IValue Implementation
        public bool ToBoolean() => AsBoolean();
        public double ToNumber() => AsNumber();
        public string ToString2() => AsString();
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

            // Annex B [[IsHTMLDDA]] special-case
            if (a.IsHtmlDdaObject && (b.IsNull || b.IsUndefined)) return true;
            if (b.IsHtmlDdaObject && (a.IsNull || a.IsUndefined)) return true;
            
            // Step 4-5: Number == String -> convert string to number
            if (a.IsNumber && b.IsString) return a._numberValue == b.AsNumber();
            if (a.IsString && b.IsNumber) return a.AsNumber() == b._numberValue;
            
            // Step 6-7: Boolean == anything -> convert boolean to number, recurse
            if (a.IsBoolean) return FromNumber(a._numberValue).LooseEquals(b);
            if (b.IsBoolean) return a.LooseEquals(FromNumber(b._numberValue));
            
            // Step 8-9: Object == String|Number -> ToPrimitive, recurse
            if ((a.IsObject || a.IsFunction) && (b.IsString || b.IsNumber || b.IsSymbol))
                return a.ToPrimitive(null).LooseEquals(b);
            if ((b.IsObject || b.IsFunction) && (a.IsString || a.IsNumber || a.IsSymbol))
                return a.LooseEquals(b.ToPrimitive(null));
            
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
                case Interfaces.ValueType.Object:
                case Interfaces.ValueType.Function: return !IsHtmlDdaObject;
                default: return true; 
            }
        }

        public double AsNumber(IExecutionContext context = null)
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
                case Interfaces.ValueType.Object:
                    // ES5.1 9.3: ToNumber for Objects via ToPrimitive(Number)
                    var prim = ToPrimitive(context, "number");
                    return prim.AsNumber(context);
                default: return double.NaN;
            }
        }

        public string AsString(IExecutionContext context = null)
        {
            switch (Type)
            {
                case Interfaces.ValueType.String: return (string)_refValue;
                case Interfaces.ValueType.Number:
                    if (double.IsNaN(_numberValue)) return "NaN";
                    if (double.IsPositiveInfinity(_numberValue)) return "Infinity";
                    if (double.IsNegativeInfinity(_numberValue)) return "-Infinity";
                    return NumberToJsString(_numberValue);
                case Interfaces.ValueType.Boolean: return _numberValue != 0 ? "true" : "false";
                case Interfaces.ValueType.Symbol:
                    throw new FenTypeError("TypeError: Cannot convert a Symbol value to a string");
                case Interfaces.ValueType.Null: return "null";
                case Interfaces.ValueType.Undefined: return "undefined";
                case Interfaces.ValueType.Object:
                {
                    // ES5.1 9.8: ToString for Objects via ToPrimitive(String) â€” calls toString()/valueOf()
                    var prim = ToPrimitive(context, "string");
                    if (prim.Type != Interfaces.ValueType.Object && prim.Type != Interfaces.ValueType.Function)
                        return prim.AsString(context);
                    // Fallback if no method returned a primitive
                    var objFallback = AsObject();
                    if (objFallback is FenObject fenObjFallback)
                        return $"[object {fenObjFallback.InternalClass ?? "Object"}]";
                    return "[object Object]";
                }
                case Interfaces.ValueType.Function: return "[function]";
                case Interfaces.ValueType.Error: return (string)_refValue ?? "Error";
                case Interfaces.ValueType.Throw: return ((FenValue)_refValue).AsString();
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
        public FenValue ToPrimitive(IExecutionContext context, string preferredType = "number")
        {
            // Primitives return themselves.
            if (Type != Interfaces.ValueType.Object && Type != Interfaces.ValueType.Function)
                return this;

            var obj = AsObject();
            if (obj == null) return this;

            // ES2015+: check @@toPrimitive first (multiple internal key encodings in this runtime).
            FenValue toPrimMethod = obj.Get("@@toPrimitive", context);
            if (toPrimMethod.IsUndefined) toPrimMethod = obj.Get(Types.JsSymbol.ToPrimitive.ToPropertyKey(), context);
            if (toPrimMethod.IsUndefined) toPrimMethod = obj.Get("[Symbol.toPrimitive]", context);
            if (toPrimMethod.IsUndefined) toPrimMethod = obj.Get("Symbol.toPrimitive", context);
            if (toPrimMethod.IsUndefined) toPrimMethod = obj.Get("@@Symbol.toPrimitive", context);
            if (toPrimMethod.IsUndefined) toPrimMethod = obj.Get("Symbol(Symbol.toPrimitive)", context);

            if (!toPrimMethod.IsUndefined)
            {
                if (!toPrimMethod.IsFunction)
                    throw new FenTypeError("TypeError: @@toPrimitive is not a function");

                var hint = FromString(preferredType == "string" ? "string" : preferredType == "number" ? "number" : "default");
                FenValue oldThis = Undefined;
                if (context != null)
                {
                    oldThis = context.ThisBinding;
                    context.ThisBinding = FromObject(obj);
                }

                var primResult = toPrimMethod.AsFunction().Invoke(new FenValue[] { hint }, context);

                if (context != null)
                    context.ThisBinding = oldThis;

                if (primResult.Type != Interfaces.ValueType.Object && primResult.Type != Interfaces.ValueType.Function)
                    return primResult;

                throw new FenTypeError("TypeError: Cannot convert object to primitive value");
            }

            // Date defaults to string hint for OrdinaryToPrimitive.
            if (obj is FenObject fenObj && fenObj.InternalClass == "Date")
                preferredType = "string";

            string[] tryOrder = preferredType == "string"
                ? new[] { "toString", "valueOf" }
                : new[] { "valueOf", "toString" };

            foreach (var methodName in tryOrder)
            {
                var method = obj.Get(methodName, context);
                if (!method.IsFunction) continue;

                FenValue oldThis = Undefined;
                IExecutionContext invokeCtx = context;
                if (context != null)
                {
                    oldThis = context.ThisBinding;
                    context.ThisBinding = FromObject(obj);
                }
                else
                {
                    invokeCtx = new FenBrowser.FenEngine.Core.ExecutionContext
                    {
                        ThisBinding = FromObject(obj)
                    };
                }

                var result = method.AsFunction().Invoke(Array.Empty<FenValue>(), invokeCtx);

                if (context != null)
                    context.ThisBinding = oldThis;

                if (result.Type != Interfaces.ValueType.Object && result.Type != Interfaces.ValueType.Function)
                    return result;
            }

            throw new FenTypeError("TypeError: Cannot convert object to primitive value");
        }

        /// <summary>
        /// ES5.1 Section 11.8.5: Abstract Relational Comparison
        /// Returns: true if x < y, false if x >= y, undefined if comparison is undefined
        /// </summary>
        public static FenValue AbstractRelationalComparison(FenValue x, FenValue y, IExecutionContext context, bool leftFirst = true)
        {
            // Step 1-2: Get primitives
            FenValue px, py;
            if (leftFirst)
            {
                px = x.ToPrimitive(context, "number");
                py = y.ToPrimitive(context, "number");
            }
            else
            {
                py = y.ToPrimitive(context, "number");
                px = x.ToPrimitive(context, "number");
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
                case Interfaces.ValueType.Throw:
                    return ((FenValue)_refValue).ToNativeObject();
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

        /// <summary>
        /// Converts a finite, non-NaN, non-Infinity double to a string following ECMAScript 9.8.1.
        /// Matches JavaScript's Number.prototype.toString() output precisely.
        /// </summary>
        private static string NumberToJsString(double value)
        {
            if (value == 0.0) return "0"; // handles both +0 and -0

            bool negative = value < 0;
            double abs = negative ? -value : value;

            // "R" (round-trip) format gives the shortest representation that parses back to the same double.
            string r = abs.ToString("R", CultureInfo.InvariantCulture);

            // Parse digits and exponent n such that value = digits * 10^(n - k), k = digits.Length
            ParseDigitsAndExponent(r, out string digits, out int n);
            int k = digits.Length;

            string result;
            if (k <= n && n <= 21)
            {
                // Integer form: e.g. 1e20 â†’ "100000000000000000000"
                result = digits + new string('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                // Fixed-point: e.g. 123.456 â†’ "123.456"
                result = digits.Substring(0, n) + "." + digits.Substring(n);
            }
            else if (-6 < n && n <= 0)
            {
                // Small decimal: e.g. 0.000001 â†’ "0.000001"
                result = "0." + new string('0', -n) + digits;
            }
            else
            {
                // Exponential: e.g. 1e+21, 1.5e+21, 1e-7
                int exp = n - 1;
                string expStr = exp >= 0 ? "e+" + exp.ToString(CultureInfo.InvariantCulture)
                                         : "e" + exp.ToString(CultureInfo.InvariantCulture);
                result = k == 1
                    ? digits + expStr
                    : digits[0] + "." + digits.Substring(1) + expStr;
            }

            return negative ? "-" + result : result;
        }

        private static void ParseDigitsAndExponent(string r, out string digits, out int n)
        {
            // r is in "R" format: could be "0.1", "123.456", "1.5E+20", "1E-07", etc.
            int eIdx = r.IndexOf('E');
            int baseExp = 0;
            string mantissa;

            if (eIdx >= 0)
            {
                baseExp = int.Parse(r.Substring(eIdx + 1), CultureInfo.InvariantCulture);
                mantissa = r.Substring(0, eIdx);
            }
            else
            {
                mantissa = r;
            }

            int dotIdx = mantissa.IndexOf('.');
            string intPart, fracPart;
            if (dotIdx >= 0)
            {
                intPart = mantissa.Substring(0, dotIdx);
                fracPart = mantissa.Substring(dotIdx + 1);
            }
            else
            {
                intPart = mantissa;
                fracPart = string.Empty;
            }

            // Raw digits = integer part + fractional part, trailing zeros stripped
            string raw = (intPart + fracPart).TrimEnd('0');
            if (raw.Length == 0) raw = "0";

            // n = position of most significant digit's power-of-10 + 1
            int rawN = baseExp + intPart.Length;

            // Strip leading zeros (from "0.001" the intPart is "0") and adjust n
            int leadingZeros = 0;
            while (leadingZeros < raw.Length && raw[leadingZeros] == '0')
                leadingZeros++;

            if (leadingZeros > 0)
            {
                raw = raw.Substring(leadingZeros);
                rawN -= leadingZeros;
            }

            digits = raw.Length > 0 ? raw : "0";
            n = rawN;
        }
    }
}




