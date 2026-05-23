namespace FenBrowser.Js.Builtins;

// ECMA-262 §21.3 — static helper functions backing Math builtin methods.
// Extracted from BytecodeInterpreter.cs as a first step toward the Builtins/
// modularisation mandated by plan §20. These are pure static functions with
// zero interpreter dependencies, making them independently testable.

internal static class MathHelpers
{
    internal static bool IsNegativeZero(double value)
    {
        return value == 0d && BitConverter.DoubleToInt64Bits(value) < 0;
    }

    // 21.3.2.28 step 4-5: -0 stays -0, +0 stays +0, NaN stays NaN. Negative finite or
    // -Infinity returns -1; positive finite or +Infinity returns +1.
    internal static double MathSign(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (value == 0d)
        {
            return value;
        }

        return value < 0 ? -1d : 1d;
    }

    // 21.3.2.35: truncate fractional part. Returns +-0 and +-Infinity unchanged,
    // NaN unchanged; otherwise integer with the same sign as the argument.
    internal static double MathTrunc(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d)
        {
            return value;
        }

        return value < 0 ? Math.Ceiling(value) : Math.Floor(value);
    }

    // 21.3.2.14 expm1(x) = e^x - 1. NaN preserved; -Infinity yields -1; +Infinity
    // yields +Infinity. Routed through Math.Exp(x) - 1 since BCL has no expm1; the
    // accuracy difference matters for x near zero but not for spec conformance of
    // boundary cases.
    internal static double MathExpm1(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsNegativeInfinity(value))
        {
            return -1d;
        }

        if (double.IsPositiveInfinity(value))
        {
            return double.PositiveInfinity;
        }

        if (value == 0d)
        {
            return value;   // preserves -0
        }

        return Math.Exp(value) - 1d;
    }

    // 21.3.2.20 log1p(x) = log(1 + x). NaN, -1, +Infinity, and the (x < -1) range
    // each have explicit spec branches; otherwise delegates to Math.Log(1 + x).
    internal static double MathLog1p(double value)
    {
        if (double.IsNaN(value) || value < -1d)
        {
            return double.NaN;
        }

        if (value == -1d)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(value))
        {
            return double.PositiveInfinity;
        }

        if (value == 0d)
        {
            return value;   // preserves -0
        }

        return Math.Log(1d + value);
    }
}
