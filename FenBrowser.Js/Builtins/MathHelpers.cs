using System.Globalization;
using System.Numerics;

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

    // ECMA-262 21.1.3.5 Number.prototype.toPrecision — shared helper.
    // Returns the formatted string for the absolute value with the given precision
    // and sign. Handles step 7 (x=0) and steps 10-14 (format selection).
    internal static string FormatToPrecision(double absValue, int precision, bool negative)
    {
        if (absValue == 0d)
        {
            if (precision == 1)
                return "0";
            return "0." + new string('0', precision - 1);
        }

        var (mantissa, exponent) = ComputeSignificantDigits(absValue, precision);
        return FormatFromMantissaExponent(mantissa, exponent, precision, negative);
    }

    // ECMA-262 21.1.3.2 Number.prototype.toExponential — shared helper.
    // Always returns exponential notation with the given fraction digits.
    internal static string FormatToExponential(double absValue, int fractionDigits)
    {
        if (absValue == 0d)
        {
            var zeros = fractionDigits > 0 ? "." + new string('0', fractionDigits) : "";
            return "0" + zeros + "e+0";
        }

        var precision = fractionDigits + 1;
        var (mantissa, exponent) = ComputeSignificantDigits(absValue, precision);

        if (fractionDigits == 0)
            return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent;

        return mantissa[0] + "." + mantissa.Substring(1) + "e" + (exponent >= 0 ? "+" : "") + exponent;
    }

    // Core algorithm: given abs(x) and desired number of significant digits p,
    // returns (m, e) where m is a decimal string of exactly p digits (no leading
    // zeros, padded if necessary) and e is the decimal exponent such that the
    // value is approximately m × 10^(e-p+1). Rounding follows the spec: ties to
    // the larger value (half-up).
    private static (string mantissa, int exponent) ComputeSignificantDigits(double absValue, int precision)
    {
        var bits = BitConverter.DoubleToInt64Bits(absValue);
        var binExp = (int)((bits >> 52) & 0x7FF);
        long rawMantissa = bits & 0x000FFFFFFFFFFFFF;

        var isSubnormal = binExp == 0;
        if (isSubnormal)
            binExp = 1;
        else
            rawMantissa |= 1L << 52;

        binExp -= 1023 + 52;

        var mantissa = new BigInteger(rawMantissa);

        // Estimate decimal exponent e = floor(log10(absValue))
        var log10Approx = binExp * 0.3010299956639812 + BigInteger.Log10(mantissa);
        var e = (int)Math.Floor(log10Approx);

        var pow10min = BigInteger.Pow(10, precision - 1);
        var pow10max = BigInteger.Pow(10, precision);

        // Compute n at current scale; if e was wrong, recompute at corrected scale.
        BigInteger n;
        while (true)
        {
            var scale = precision - 1 - e;

            BigInteger num, den;
            if (scale >= 0)
            {
                if (binExp >= 0)
                {
                    num = mantissa * BigInteger.Pow(2, binExp) * BigInteger.Pow(10, scale);
                    den = BigInteger.One;
                }
                else
                {
                    num = mantissa * BigInteger.Pow(10, scale);
                    den = BigInteger.Pow(2, -binExp);
                }
            }
            else
            {
                var negScale = -scale;
                if (binExp >= 0)
                {
                    num = mantissa * BigInteger.Pow(2, binExp);
                    den = BigInteger.Pow(10, negScale);
                }
                else
                {
                    num = mantissa;
                    den = BigInteger.Pow(2, -binExp) * BigInteger.Pow(10, negScale);
                }
            }

            // Round half-up (spec: ties to larger)
            n = (num * 2 + den) / (den * 2);

            if (n >= pow10max)
            {
                e++;
                continue;
            }
            if (n < pow10min)
            {
                e--;
                continue;
            }
            break;
        }

        var m = n.ToString(CultureInfo.InvariantCulture);
        while (m.Length < precision)
            m = "0" + m;

        return (m, e);
    }

    // ECMA-262 21.1.3.5 steps 10-14: format mantissa m (p digits) and exponent e
    // into fixed or exponential notation depending on the range of e.
    private static string FormatFromMantissaExponent(string m, int e, int p, bool negative)
    {
        var sign = negative ? "-" : "";

        // Step 10.c: e < -6 or e ≥ p → exponential
        if (e < -6 || e >= p)
        {
            if (p == 1)
                return sign + m + "e" + (e >= 0 ? "+" : "") + e;
            return sign + m[0] + "." + m.Substring(1) + "e" + (e >= 0 ? "+" : "") + e;
        }

        // Step 11: e = p-1 → return m as-is
        if (e == p - 1)
            return sign + m;

        // Step 12: e ≥ 0 → insert decimal after e+1 digits
        if (e >= 0)
            return sign + m.Substring(0, e + 1) + "." + m.Substring(e + 1);

        // Step 13: e < 0 → "0." + -(e+1) zeros + m
        return sign + "0." + new string('0', -(e + 1)) + m;
    }
}
