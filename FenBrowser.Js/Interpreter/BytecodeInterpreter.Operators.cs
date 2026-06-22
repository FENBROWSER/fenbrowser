using System.Numerics;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Operator + numeric coercion helpers extracted from BytecodeInterpreter.cs
// as audit §2 slice 2 (interpreter monolith breakup). Pure file move — no
// semantic change. The methods stay private to the partial class.
public sealed partial class BytecodeInterpreter
{
    private bool IsCallable(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        return IsCallableTarget(value.AsObjectHandle());
    }

    private JsValue BigIntArith(JsValue left, JsValue right, string opName,
        Func<System.Numerics.BigInteger, System.Numerics.BigInteger, System.Numerics.BigInteger> bigIntOp,
        Func<double, double, double> numOp)
    {
        // ECMA-262 13.15.3 ApplyStringOrNumericBinaryOperator (numeric path): coerce
        // each operand with ToNumeric (ToPrimitive then BigInt-or-ToNumber) BEFORE
        // deciding BigInt vs Number, in left-to-right order. Skipping this made
        // `{[Symbol.toPrimitive](){return 2n}} - 1n` and `Object(2n) - 1n` wrongly
        // throw "Cannot mix BigInt".
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
        {
            var r = rnum.AsBigInt();
            // ECMA-262 BigInt::divide / BigInt::remainder: "If y is 0ℤ, throw a
            // RangeError." Otherwise the BigInteger op throws DivideByZeroException
            // and crashes the host.
            if (r.IsZero && (opName == "division" || opName == "modulo"))
                throw new JsThrownException(CreateRangeError($"Division by zero in BigInt {opName}."));
            return JsValue.FromBigInt(bigIntOp(lnum.AsBigInt(), r));
        }
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError($"Cannot mix BigInt and other types in {opName}."));
        return JsValue.FromNumber(numOp(lnum.AsNumber(), rnum.AsNumber()));
    }

    // ECMA-262 7.1.4-style ToNumeric: a BigInt stays a BigInt; everything else
    // is coerced to Number (ToNumber, which runs ToPrimitive and may throw, e.g.
    // for Symbol). Used by the update operators so `x++` produces a numeric old
    // value rather than a `x + 1` string concatenation.
    private JsValue ToNumericValue(JsValue value)
    {
        // ECMA-262 7.1.4 ToNumeric: ToPrimitive(value, number) first, then if the
        // primitive is a BigInt keep it, otherwise ToNumber it. Coercing before the
        // BigInt check is what lets BigInt-wrapping objects (Object(2n)) and
        // Symbol.toPrimitive overrides participate in numeric operators.
        var primitive = value.Tag == JsValueTag.Object
            ? ToPrimitive(value, PrimitiveHint.Number)
            : value;
        if (primitive.Tag == JsValueTag.BigInt)
        {
            return primitive;
        }

        return JsValue.FromNumber(ToNumber(primitive));
    }

    // Adds delta (+1 / -1) to an already-ToNumeric value, preserving BigInt vs
    // Number. ECMA-262 13.4.x step "Let newValue be ... ::add/subtract".
    private static JsValue StepNumeric(JsValue numeric, int delta)
    {
        if (numeric.Tag == JsValueTag.BigInt)
        {
            return JsValue.FromBigInt(numeric.AsBigInt() + delta);
        }

        return JsValue.FromNumber(numeric.AsNumber() + delta);
    }

    private JsValue Add(JsValue left, JsValue right)
    {
        var leftPrimitive = ToPrimitive(left, PrimitiveHint.Default);
        var rightPrimitive = ToPrimitive(right, PrimitiveHint.Default);

        if (leftPrimitive.Tag == JsValueTag.String || rightPrimitive.Tag == JsValueTag.String)
        {
            return JsValue.FromString(ToStringForAddition(leftPrimitive) + ToStringForAddition(rightPrimitive));
        }

        if (leftPrimitive.Tag == JsValueTag.BigInt && rightPrimitive.Tag == JsValueTag.BigInt)
        {
            return JsValue.FromBigInt(leftPrimitive.AsBigInt() + rightPrimitive.AsBigInt());
        }

        if (leftPrimitive.Tag == JsValueTag.BigInt || rightPrimitive.Tag == JsValueTag.BigInt)
        {
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types in addition."));
        }

        return JsValue.FromNumber(ToNumber(leftPrimitive) + ToNumber(rightPrimitive));
    }

    private string ToStringForAddition(JsValue value)
    {
        if (value.Tag == JsValueTag.Symbol)
        {
            throw new JsThrownException(CreateTypeError("Cannot convert a Symbol value to a string."));
        }

        return ToStringValue(value);
    }

    private static string FormatNumberForString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            return "0";
        }

        var absolute = Math.Abs(value);
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        if (text.Contains('E', StringComparison.Ordinal) || text.Contains('e', StringComparison.Ordinal))
        {
            if (absolute >= 1e-6 && absolute < 1e21)
            {
                return ExpandExponentialNumber(text);
            }

            return NormalizeExponentialNumber(text);
        }

        return TrimDecimalZeros(text);
    }

    private static string ExpandExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = normalized[..exponentMarker];
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var negative = coefficient.StartsWith("-", StringComparison.Ordinal);
        if (negative || coefficient.StartsWith("+", StringComparison.Ordinal))
        {
            coefficient = coefficient[1..];
        }

        var decimalIndex = coefficient.IndexOf('.', StringComparison.Ordinal);
        if (decimalIndex < 0)
        {
            decimalIndex = coefficient.Length;
        }

        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        var newDecimalIndex = decimalIndex + exponent;
        string expanded;
        if (newDecimalIndex <= 0)
        {
            expanded = "0." + new string('0', -newDecimalIndex) + digits;
        }
        else if (newDecimalIndex >= digits.Length)
        {
            expanded = digits + new string('0', newDecimalIndex - digits.Length);
        }
        else
        {
            expanded = digits[..newDecimalIndex] + "." + digits[newDecimalIndex..];
        }

        expanded = TrimDecimalZeros(expanded);
        return negative ? "-" + expanded : expanded;
    }

    private static string NormalizeExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0)
        {
            return TrimDecimalZeros(normalized);
        }

        var coefficient = TrimDecimalZeros(normalized[..exponentMarker]);
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var sign = exponent >= 0 ? "+" : string.Empty;
        return $"{coefficient}e{sign}{exponent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string TrimDecimalZeros(string text)
    {
        if (!text.Contains(".", StringComparison.Ordinal))
        {
            return text;
        }

        return text.TrimEnd('0').TrimEnd('.');
    }

    // BigInt::leftShift(x, y): if y < 0, floor division by 2^(-y); else x * 2^y.
    private static BigInteger BigIntShiftLeft(BigInteger x, BigInteger y)
    {
        if (y < 0)
        {
            return BigIntShiftRightFloor(x, -y);
        }
        if (y > int.MaxValue)
            throw new JsThrownException(JsValue.FromString("BigInt shift amount too large."));
        return x << (int)y;
    }

    // BigInt::signedRightShift(x, y) = BigInt::leftShift(x, -y).
    private static BigInteger BigIntShiftRight(BigInteger x, BigInteger y)
    {
        return BigIntShiftLeft(x, -y);
    }

    // Floor division by 2^shift: rounds towards -inf for negative numbers.
    private static BigInteger BigIntShiftRightFloor(BigInteger x, BigInteger shift)
    {
        if (shift > int.MaxValue)
            return x >= 0 ? BigInteger.Zero : BigInteger.MinusOne;
        int s = (int)shift;
        var pow = BigInteger.One << s;
        if (x >= 0) return x / pow;
        var q = x / pow;
        if (x % pow != 0) q--;
        return q;
    }

    // Applies bitwise AND: ToNumeric both, BigInt native, else ToInt32.
    private JsValue BitwiseAndOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(lnum.AsBigInt() & rnum.AsBigInt());
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        return JsValue.FromNumber(MathHelpers.ToInt32(ToNumber(lnum)) & MathHelpers.ToInt32(ToNumber(rnum)));
    }

    // Applies bitwise OR.
    private JsValue BitwiseOrOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(lnum.AsBigInt() | rnum.AsBigInt());
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        return JsValue.FromNumber(MathHelpers.ToInt32(ToNumber(lnum)) | MathHelpers.ToInt32(ToNumber(rnum)));
    }

    // Applies bitwise XOR.
    private JsValue BitwiseXorOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(lnum.AsBigInt() ^ rnum.AsBigInt());
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        return JsValue.FromNumber(MathHelpers.ToInt32(ToNumber(lnum)) ^ MathHelpers.ToInt32(ToNumber(rnum)));
    }

    // Applies bitwise NOT: ToNumeric, BigInt ~, else ~ToInt32.
    private JsValue BitwiseNotOp(JsValue value)
    {
        var numeric = ToNumericValue(value);
        if (numeric.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(~numeric.AsBigInt());
        return JsValue.FromNumber(~MathHelpers.ToInt32(ToNumber(numeric)));
    }

    // Applies left shift.
    private JsValue LeftShiftOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(BigIntShiftLeft(lnum.AsBigInt(), rnum.AsBigInt()));
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        var sl = MathHelpers.ToInt32(ToNumber(lnum));
        var sc = (int)(MathHelpers.ToUint32(ToNumber(rnum)) & 0x1F);
        return JsValue.FromNumber(sl << sc);
    }

    // Applies signed right shift.
    private JsValue RightShiftOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(BigIntShiftRight(lnum.AsBigInt(), rnum.AsBigInt()));
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        var sr = MathHelpers.ToInt32(ToNumber(lnum));
        var sc = (int)(MathHelpers.ToUint32(ToNumber(rnum)) & 0x1F);
        return JsValue.FromNumber(sr >> sc);
    }

    // Applies unsigned right shift: BigInt throws TypeError.
    private JsValue UnsignedRightShiftOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("BigInt does not support unsigned right shift."));
        var u = MathHelpers.ToUint32(ToNumber(lnum));
        var sc = (int)(MathHelpers.ToUint32(ToNumber(rnum)) & 0x1F);
        return JsValue.FromNumber(u >> sc);
    }

    // Applies exponentiation with proper ToNumeric coercion.
    private JsValue ExponentiationOp(JsValue left, JsValue right)
    {
        var lnum = ToNumericValue(left);
        var rnum = ToNumericValue(right);
        if (lnum.Tag == JsValueTag.BigInt && rnum.Tag == JsValueTag.BigInt)
        {
            var baseVal = lnum.AsBigInt();
            var expVal = rnum.AsBigInt();
            if (expVal < BigInteger.Zero)
                throw new JsThrownException(CreateRangeError("BigInt exponent must be non-negative."));
            if (expVal > int.MaxValue)
                throw new JsThrownException(CreateRangeError("BigInt exponent is too large."));
            return JsValue.FromBigInt(BigInteger.Pow(baseVal, (int)expVal));
        }
        if (lnum.Tag == JsValueTag.BigInt || rnum.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError("Cannot mix BigInt and other types, use explicit conversions."));
        return JsValue.FromNumber(Math.Pow(ToNumber(lnum), ToNumber(rnum)));
    }
}
