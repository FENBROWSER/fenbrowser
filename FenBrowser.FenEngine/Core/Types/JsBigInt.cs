using System;
using System.Numerics;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// JavaScript BigInt type for arbitrary-precision integers.
    /// Per ECMAScript 2020 specification.
    /// </summary>
    public class JsBigInt : IValue
    {
        private readonly BigInteger _value;

        public static readonly JsBigInt Zero = new JsBigInt(BigInteger.Zero);
        public static readonly JsBigInt One = new JsBigInt(BigInteger.One);

        public JsBigInt(BigInteger value)
        {
            _value = value;
        }

        public JsBigInt(long value)
        {
            _value = new BigInteger(value);
        }

        public JsBigInt(string value)
        {
            // Remove 'n' suffix if present
            var str = value?.TrimEnd('n', 'N') ?? "0";
            if (!BigInteger.TryParse(str, out _value))
            {
                _value = BigInteger.Zero;
            }
        }

        public BigInteger Value => _value;

        public JsValueType Type => JsValueType.BigInt;

        public bool ToBoolean() => _value != BigInteger.Zero;

        public double ToNumber()
        {
            // ECMA-262 §7.2.2: BigInt cannot be converted to Number — must throw TypeError
            throw new FenTypeError("TypeError: Cannot convert a BigInt value to a number");
        }

        public override string ToString() => _value.ToString() + "n";

        public string ToStringWithoutSuffix() => _value.ToString();

        public IObject ToObject()
        {
            // BigInt cannot be wrapped in Object() in JS
            throw new InvalidOperationException("Cannot convert BigInt to Object");
        }

        public bool LooseEquals(IValue other)
        {
            if (other is JsBigInt bigInt)
                return _value == bigInt._value;
            if (other.Type == JsValueType.Number)
                return (double)_value == other.ToNumber();
            if (other.Type == JsValueType.String)
            {
                if (BigInteger.TryParse(other.ToString().TrimEnd('n', 'N'), out var parsed))
                    return _value == parsed;
            }
            return false;
        }

        public bool StrictEquals(IValue other)
        {
            if (other is JsBigInt bigInt)
                return _value == bigInt._value;
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
        public bool IsBigInt => true;
        public bool IsSymbol => false;

        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;

        public FenValue ToPrimitive(IExecutionContext context, string preferredType = "number")
        {
            // BigInt is already a primitive
            return FenValue.FromBigInt(this);
        }

        // BigInt arithmetic operations
        public static JsBigInt Add(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value + right._value);

        public static JsBigInt Subtract(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value - right._value);

        public static JsBigInt Multiply(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value * right._value);

        public static JsBigInt Divide(JsBigInt left, JsBigInt right)
        {
            if (right._value == BigInteger.Zero)
                // ECMA-262 §21.2.3: BigInt division by zero must throw RangeError
                throw new FenRangeError("RangeError: Division by zero");
            return new JsBigInt(left._value / right._value);
        }

        public static JsBigInt Remainder(JsBigInt left, JsBigInt right)
        {
            if (right._value == BigInteger.Zero)
                // ECMA-262 §21.2.3: BigInt division by zero must throw RangeError
                throw new FenRangeError("RangeError: Division by zero");
            return new JsBigInt(left._value % right._value);
        }

        public static JsBigInt Power(JsBigInt baseVal, int exponent)
        {
            if (exponent < 0)
                throw new ArgumentException("Exponent must be non-negative for BigInt");
            return new JsBigInt(BigInteger.Pow(baseVal._value, exponent));
        }

        public static JsBigInt Negate(JsBigInt value)
            => new JsBigInt(-value._value);

        public static JsBigInt BitwiseAnd(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value & right._value);

        public static JsBigInt BitwiseOr(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value | right._value);

        public static JsBigInt BitwiseXor(JsBigInt left, JsBigInt right)
            => new JsBigInt(left._value ^ right._value);

        public static JsBigInt BitwiseNot(JsBigInt value)
            => new JsBigInt(~value._value);

        public static JsBigInt LeftShift(JsBigInt value, int shift)
            => new JsBigInt(value._value << shift);

        public static JsBigInt RightShift(JsBigInt value, int shift)
            => new JsBigInt(value._value >> shift);

        // Comparison
        public static bool LessThan(JsBigInt left, JsBigInt right)
            => left._value < right._value;

        public static bool LessThanOrEqual(JsBigInt left, JsBigInt right)
            => left._value <= right._value;

        public static bool GreaterThan(JsBigInt left, JsBigInt right)
            => left._value > right._value;

        public static bool GreaterThanOrEqual(JsBigInt left, JsBigInt right)
            => left._value >= right._value;

        public static bool Equals(JsBigInt left, JsBigInt right)
            => left._value == right._value;

        // Parse from string/number
        public static JsBigInt Parse(string value)
        {
            return new JsBigInt(value);
        }

        public static JsBigInt FromNumber(double value)
        {
            // ECMA-262 §21.2.1.1: BigInt() from a non-integer double must throw RangeError
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Floor(value) != value)
                throw new FenRangeError($"RangeError: The number {value} cannot be converted to a BigInt because it is not an integer");
            return new JsBigInt(new BigInteger(value));
        }

        public static bool TryParse(string value, out JsBigInt result)
        {
            var str = value?.TrimEnd('n', 'N') ?? "";
            if (BigInteger.TryParse(str, out var bigInt))
            {
                result = new JsBigInt(bigInt);
                return true;
            }
            result = null;
            return false;
        }

        public override bool Equals(object obj)
        {
            if (obj is JsBigInt other)
                return _value == other._value;
            return false;
        }

        public override int GetHashCode() => _value.GetHashCode();

        // Implicit conversions
        public static implicit operator JsBigInt(long value) => new JsBigInt(value);
        public static implicit operator JsBigInt(BigInteger value) => new JsBigInt(value);
    }
}
