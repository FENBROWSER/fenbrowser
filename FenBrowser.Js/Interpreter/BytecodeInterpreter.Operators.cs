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
}
