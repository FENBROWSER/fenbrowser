using System;
using System.Collections.Generic;
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
        


        public JsSymbol AsSymbol()
        {
             return Type == Interfaces.ValueType.Symbol ? _refValue as JsSymbol : null;
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
            if (!(other is FenValue otherFen)) return false;
            if (Type == otherFen.Type) return this == otherFen;
            
            // TODO: Basic loose equality rules (null == undefined, etc.)
            if ((IsNull || IsUndefined) && (otherFen.IsNull || otherFen.IsUndefined)) return true;
            
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
                    if (double.TryParse((string)_refValue, out double result))
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
                case Interfaces.ValueType.Number: return _numberValue.ToString();
                case Interfaces.ValueType.Boolean: return _numberValue != 0 ? "true" : "false";
                case Interfaces.ValueType.Null: return "null";
                case Interfaces.ValueType.Undefined: return "undefined";
                case Interfaces.ValueType.Object: return "[object Object]";
                case Interfaces.ValueType.Function: return "[function]";
                case Interfaces.ValueType.Error: return (string)_refValue ?? "Error";
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
