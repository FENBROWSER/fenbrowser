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
        if (left.Tag == JsValueTag.BigInt && right.Tag == JsValueTag.BigInt)
            return JsValue.FromBigInt(bigIntOp(left.AsBigInt(), right.AsBigInt()));
        if (left.Tag == JsValueTag.BigInt || right.Tag == JsValueTag.BigInt)
            throw new JsThrownException(CreateTypeError($"Cannot mix BigInt and other types in {opName}."));
        return JsValue.FromNumber(numOp(ToNumber(left), ToNumber(right)));
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
