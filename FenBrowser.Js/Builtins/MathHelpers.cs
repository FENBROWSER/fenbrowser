using System.Globalization;

namespace FenBrowser.Js.Builtins;

// Static helper functions backing Math and Number builtin methods.
// Extracted from BytecodeInterpreter.cs as part of the Builtins/
// modularisation mandated by plan §20. These are pure static functions with
// zero interpreter dependencies, making them independently testable.

internal static class MathHelpers
{
    internal static bool IsNegativeZero(double value)
    {
        return value == 0d && BitConverter.DoubleToInt64Bits(value) < 0;
    }

    internal static double MathSign(double value)
    {
        if (double.IsNaN(value)) return double.NaN;
        if (value == 0d) return value;
        return value < 0 ? -1d : 1d;
    }

    internal static double MathTrunc(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d) return value;
        return value < 0 ? Math.Ceiling(value) : Math.Floor(value);
    }

    internal static double MathExpm1(double value)
    {
        if (double.IsNaN(value)) return double.NaN;
        if (double.IsNegativeInfinity(value)) return -1d;
        if (double.IsPositiveInfinity(value)) return double.PositiveInfinity;
        if (value == 0d) return value;
        return Math.Exp(value) - 1d;
    }

    internal static double MathLog1p(double value)
    {
        if (double.IsNaN(value) || value < -1d) return double.NaN;
        if (value == -1d) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(value)) return double.PositiveInfinity;
        if (value == 0d) return value;
        return Math.Log(1d + value);
    }

    internal static uint ToUint32(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0u;
        var truncated = value >= 0 ? Math.Floor(value) : Math.Ceiling(value);
        var modulo = truncated - Math.Floor(truncated / 4294967296d) * 4294967296d;
        return (uint)modulo;
    }

    internal static int ToInt32(double value)
    {
        var unsigned = ToUint32(value);
        return unchecked((int)unsigned);
    }

    // ECMA-262 7.1.17.1 / 21.1.3.6 — Number → String with radix argument support
    // and ECMA-262 25.5.2 JSON.stringify number formatting.

    internal static string FormatNumberForString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";

        var absolute = Math.Abs(value);
        var text = value.ToString("R", CultureInfo.InvariantCulture);

        if (text.Contains('E', StringComparison.Ordinal) || text.Contains('e', StringComparison.Ordinal))
        {
            if (absolute >= 1e-6 && absolute < 1e21)
                return ExpandExponentialNumber(text);
            return NormalizeExponentialNumber(text);
        }

        return TrimDecimalZeros(text);
    }

    // ECMA-262 21.1.3.6: radix conversion for toString(radix). Handles the
    // integer portion only; the caller handles the fractional portion.
    internal static string LongToRadixString(long n, int radix)
    {
        if (n == 0) return "0";
        var sb = new System.Text.StringBuilder();
        while (n > 0) { sb.Insert(0, DigitToChar((int)(n % radix))); n /= radix; }
        return sb.ToString();
    }

    // ECMA-262 21.1.3.2 / 21.1.3.5: normalise exponent to canonical form
    // "1.23e+5" (lowercase e, explicit sign, no leading zeros on exponent).
    internal static string NormaliseExponential(string text)
    {
        var eIdx = text.IndexOfAny(['e', 'E']);
        if (eIdx < 0) return text;

        var mantissa = text[..eIdx];
        var expPart = text[(eIdx + 1)..];
        var sign = "+";
        if (expPart.Length > 0 && (expPart[0] == '+' || expPart[0] == '-'))
        {
            sign = expPart[0] == '-' ? "-" : "+";
            expPart = expPart[1..];
        }
        expPart = expPart.TrimStart('0');
        if (expPart.Length == 0) expPart = "0";
        return mantissa + "e" + sign + expPart;
    }

    private static string ExpandExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0) return TrimDecimalZeros(normalized);

        var coefficient = normalized[..exponentMarker];
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
        var negative = coefficient.StartsWith("-", StringComparison.Ordinal);
        if (negative || coefficient.StartsWith("+", StringComparison.Ordinal))
            coefficient = coefficient[1..];

        var decimalIndex = coefficient.IndexOf('.', StringComparison.Ordinal);
        if (decimalIndex < 0) decimalIndex = coefficient.Length;

        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        var newDecimalIndex = decimalIndex + exponent;
        string expanded;
        if (newDecimalIndex <= 0)
            expanded = "0." + new string('0', -newDecimalIndex) + digits;
        else if (newDecimalIndex >= digits.Length)
            expanded = digits + new string('0', newDecimalIndex - digits.Length);
        else
            expanded = digits[..newDecimalIndex] + "." + digits[newDecimalIndex..];

        expanded = TrimDecimalZeros(expanded);
        return negative ? "-" + expanded : expanded;
    }

    private static string NormalizeExponentialNumber(string text)
    {
        var normalized = text.Replace('E', 'e');
        var exponentMarker = normalized.IndexOf('e', StringComparison.Ordinal);
        if (exponentMarker < 0) return TrimDecimalZeros(normalized);

        var coefficient = TrimDecimalZeros(normalized[..exponentMarker]);
        var exponent = int.Parse(normalized[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
        var sign = exponent >= 0 ? "+" : string.Empty;
        return $"{coefficient}e{sign}{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static string TrimDecimalZeros(string text)
    {
        if (!text.Contains(".", StringComparison.Ordinal)) return text;
        return text.TrimEnd('0').TrimEnd('.');
    }

    private static char DigitToChar(int digit)
    {
        return digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10);
    }
}
