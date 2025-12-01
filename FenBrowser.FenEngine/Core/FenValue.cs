using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript value in FenEngine
    /// </summary>
    public class FenValue : IValue
    {
        public Interfaces.ValueType Type { get; private set; }
        private object _value;

        // Static singleton values for undefined and null
        public static readonly FenValue Undefined = new FenValue { Type = Interfaces.ValueType.Undefined };
        public static readonly FenValue Null = new FenValue { Type = Interfaces.ValueType.Null };

        // Constructors for different types
        private FenValue() { }

        public static FenValue FromBoolean(bool value)
        {
            return new FenValue { Type = Interfaces.ValueType.Boolean, _value = value };
        }

        public static FenValue FromNumber(double value)
        {
            return new FenValue { Type = Interfaces.ValueType.Number, _value = value };
        }

        public static FenValue FromString(string value)
        {
            return new FenValue { Type = Interfaces.ValueType.String, _value = value ?? string.Empty };
        }

        public static FenValue FromObject(IObject obj)
        {
            return new FenValue { Type = Interfaces.ValueType.Object, _value = obj };
        }

        public static FenValue FromFunction(FenFunction func)
        {
            return new FenValue { Type = Interfaces.ValueType.Function, _value = func };
        }

        // Type checking
        public bool IsUndefined => Type == Interfaces.ValueType.Undefined;
        public bool IsNull => Type == Interfaces.ValueType.Null;
        public bool IsBoolean => Type == Interfaces.ValueType.Boolean;
        public bool IsNumber => Type == Interfaces.ValueType.Number;
        public bool IsString => Type == Interfaces.ValueType.String;
        public bool IsObject => Type == Interfaces.ValueType.Object;
        public bool IsFunction => Type == Interfaces.ValueType.Function;

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
            return this == otherFen; // TODO: Implement loose equality rules
        }

        // Value extraction
        public bool AsBoolean()
        {
            switch (Type)
            {
                case Interfaces.ValueType.Boolean: return (bool)_value;
                case Interfaces.ValueType.Number: return (double)_value != 0 && !double.IsNaN((double)_value);
                case Interfaces.ValueType.String: return !string.IsNullOrEmpty((string)_value);
                case Interfaces.ValueType.Null:
                case Interfaces.ValueType.Undefined: return false;
                default: return true; // Objects and functions are truthy
            }
        }

        public double AsNumber()
        {
            switch (Type)
            {
                case Interfaces.ValueType.Number: return (double)_value;
                case Interfaces.ValueType.Boolean: return (bool)_value ? 1.0 : 0.0;
                case Interfaces.ValueType.String:
                    if (double.TryParse((string)_value, out double result))
                        return result;
                    return double.NaN;
                case Interfaces.ValueType.Null: return 0.0;
                case Interfaces.ValueType.Undefined: return double.NaN;
                default: return double.NaN;
            }
        }

        public string AsString()
        {
            switch (Type)
            {
                case Interfaces.ValueType.String: return (string)_value;
                case Interfaces.ValueType.Number: return ((double)_value).ToString();
                case Interfaces.ValueType.Boolean: return (bool)_value ? "true" : "false";
                case Interfaces.ValueType.Null: return "null";
                case Interfaces.ValueType.Undefined: return "undefined";
                case Interfaces.ValueType.Object: return "[object Object]";
                case Interfaces.ValueType.Function: return "[function]";
                default: return string.Empty;
            }
        }

        public IObject AsObject()
        {
            if (Type == Interfaces.ValueType.Object) return (IObject)_value;
            return null;
        }

        public FenFunction AsFunction()
        {
            if (Type == Interfaces.ValueType.Function) return (FenFunction)_value;
            return null;
        }

        // Operators
        public static FenValue operator +(FenValue a, FenValue b)
        {
            // String concatenation
            if (a.IsString || b.IsString)
                return FromString(a.AsString() + b.AsString());
            
            // Numeric addition
            return FromNumber(a.AsNumber() + b.AsNumber());
        }

        public static FenValue operator -(FenValue a, FenValue b)
        {
            return FromNumber(a.AsNumber() - b.AsNumber());
        }

        public static FenValue operator *(FenValue a, FenValue b)
        {
            return FromNumber(a.AsNumber() * b.AsNumber());
        }

        public static FenValue operator /(FenValue a, FenValue b)
        {
            return FromNumber(a.AsNumber() / b.AsNumber());
        }

        // Equality
        public static bool operator ==(FenValue a, FenValue b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            
            if (a.Type != b.Type) return false;
            
            switch (a.Type)
            {
                case Interfaces.ValueType.Undefined:
                case Interfaces.ValueType.Null:
                    return true;
                case Interfaces.ValueType.Boolean:
                case Interfaces.ValueType.Number:
                case Interfaces.ValueType.String:
                    return Equals(a._value, b._value);
                default:
                    return ReferenceEquals(a._value, b._value);
            }
        }

        public static bool operator !=(FenValue a, FenValue b) => !(a == b);

        public override bool Equals(object obj) => obj is FenValue other && this == other;
        public override int GetHashCode() => _value?.GetHashCode() ?? 0;
    }
}
