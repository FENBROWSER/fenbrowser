using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 §21.3 — The Math Object.
//
// Math is a singleton ordinary object (not a constructor) whose properties are
// all non-enumerable. Constants are read-only; methods are writable+configurable
// per the spec's standard builtin shape.
[EcmaSpecReference(
    "21.3",
    AbstractOperation = "Math",
    Url = "https://tc39.es/ecma262/#sec-math-object")]
public sealed class MathBuiltin : IBuiltinModule
{
    public string Name => "Math";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var math = CreateOrdinaryObject(context);
        var handle = heap.AllocateObject(math, AllocationSite.Current());
        heap.PushRoot(handle);

        DefineMathConstant(math, "E", Math.E);
        DefineMathConstant(math, "LN10", Math.Log(10d));
        DefineMathConstant(math, "LN2", Math.Log(2d));
        DefineMathConstant(math, "LOG10E", 1d / Math.Log(10d));
        DefineMathConstant(math, "LOG2E", 1d / Math.Log(2d));
        DefineMathConstant(math, "PI", Math.PI);
        DefineMathConstant(math, "SQRT1_2", Math.Sqrt(0.5d));
        DefineMathConstant(math, "SQRT2", Math.Sqrt(2d));

        DefineMathFunction(context, handle, math, "abs", args => MathUnary(context, args, Math.Abs), length: 1);
        DefineMathFunction(context, handle, math, "acos", args => MathUnary(context, args, Math.Acos), length: 1);
        DefineMathFunction(context, handle, math, "asin", args => MathUnary(context, args, Math.Asin), length: 1);
        DefineMathFunction(context, handle, math, "atan", args => MathUnary(context, args, Math.Atan), length: 1);
        DefineMathFunction(context, handle, math, "atan2", args => MathAtan2(context, args), length: 2);
        DefineMathFunction(context, handle, math, "ceil", args => MathUnary(context, args, Math.Ceiling), length: 1);
        DefineMathFunction(context, handle, math, "cos", args => MathUnary(context, args, Math.Cos), length: 1);
        DefineMathFunction(context, handle, math, "exp", args => MathUnary(context, args, Math.Exp), length: 1);
        DefineMathFunction(context, handle, math, "floor", args => MathUnary(context, args, Math.Floor), length: 1);
        DefineMathFunction(context, handle, math, "log", args => MathUnary(context, args, Math.Log), length: 1);
        DefineMathFunction(context, handle, math, "max", args => MathMax(context, args), length: 2);
        DefineMathFunction(context, handle, math, "min", args => MathMin(context, args), length: 2);
        DefineMathFunction(context, handle, math, "pow", args => MathPow(context, args), length: 2);
        DefineMathFunction(context, handle, math, "round", args => MathRound(context, args), length: 1);
        DefineMathFunction(context, handle, math, "sin", args => MathUnary(context, args, Math.Sin), length: 1);
        DefineMathFunction(context, handle, math, "sqrt", args => MathUnary(context, args, Math.Sqrt), length: 1);
        DefineMathFunction(context, handle, math, "tan", args => MathUnary(context, args, Math.Tan), length: 1);
        // 21.3.2.28 Math.sign
        DefineMathFunction(context, handle, math, "sign", args => MathUnary(context, args, MathHelpers.MathSign), length: 1);
        // 21.3.2.35 Math.trunc
        DefineMathFunction(context, handle, math, "trunc", args => MathUnary(context, args, MathHelpers.MathTrunc), length: 1);
        // 21.3.2.9 Math.cbrt
        DefineMathFunction(context, handle, math, "cbrt", args => MathUnary(context, args, Math.Cbrt), length: 1);
        // 21.3.2.22 Math.log2
        DefineMathFunction(context, handle, math, "log2", args => MathUnary(context, args, Math.Log2), length: 1);
        // 21.3.2.21 Math.log10
        DefineMathFunction(context, handle, math, "log10", args => MathUnary(context, args, Math.Log10), length: 1);
        // 21.3.2.18 Math.hypot
        DefineMathFunction(context, handle, math, "hypot", args => MathHypot(context, args), length: 2);
        // 21.3.2.11 Math.clz32
        DefineMathFunction(context, handle, math, "clz32", args => JsValue.FromNumber(MathClz32(context, args)), length: 1);
        // 21.3.2.19 Math.imul
        DefineMathFunction(context, handle, math, "imul", args => JsValue.FromNumber(MathImul(context, args)), length: 2);
        // 21.3.2.16 Math.fround
        DefineMathFunction(context, handle, math, "fround", args => MathUnary(context, args, v => (double)(float)v), length: 1);
        // Math.f16round (Float16 proposal): round to the nearest half-precision value.
        // NaN stays NaN; everything else round-trips through IEEE-754 binary16.
        DefineMathFunction(context, handle, math, "f16round", args => MathUnary(context, args, v => double.IsNaN(v) ? double.NaN : (double)(Half)v), length: 1);
        // 21.3.2.31/.12/.33 sinh/cosh/tanh
        DefineMathFunction(context, handle, math, "sinh", args => MathUnary(context, args, Math.Sinh), length: 1);
        DefineMathFunction(context, handle, math, "cosh", args => MathUnary(context, args, Math.Cosh), length: 1);
        DefineMathFunction(context, handle, math, "tanh", args => MathUnary(context, args, Math.Tanh), length: 1);
        // 21.3.2.7/.2/.8 asinh/acosh/atanh
        DefineMathFunction(context, handle, math, "asinh", args => MathUnary(context, args, Math.Asinh), length: 1);
        DefineMathFunction(context, handle, math, "acosh", args => MathUnary(context, args, Math.Acosh), length: 1);
        DefineMathFunction(context, handle, math, "atanh", args => MathUnary(context, args, Math.Atanh), length: 1);
        // 21.3.2.14 expm1
        DefineMathFunction(context, handle, math, "expm1", args => MathUnary(context, args, MathHelpers.MathExpm1), length: 1);
        // 21.3.2.20 log1p
        DefineMathFunction(context, handle, math, "log1p", args => MathUnary(context, args, MathHelpers.MathLog1p), length: 1);
        // 21.3.2.27 Math.random — one Random per realm per spec requirement.
        var random = new Random();
        DefineMathFunction(context, handle, math, "random", _ => JsValue.FromNumber(random.NextDouble()), length: 0);

        // ES2025 Math.sumPrecise: sum values precisely using the SumPrecise algorithm.
        DefineMathFunction(context, handle, math, "sumPrecise", args => MathSumPrecise(context, args), length: 1);

        // 21.3.1.9 Math [ @@toStringTag ] = "Math"
        var toStringTagSymbol = context.CreateWellKnownSymbol("toStringTag");
        math.DefineOwnSymbolProperty(toStringTagSymbol.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString("Math"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("Math", JsValue.FromObject(handle)) };
    }

    private static JsObject CreateOrdinaryObject(IBuiltinContext ctx = null!)
    {
        var obj = new JsObject();
        if (ctx != null) obj.SetPrototype(ctx.GetObjectPrototype());
        return obj;
    }

    private static void DefineMathConstant(JsObject math, string name, double value)
    {
        _ = math.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromNumber(value),
                Writable: false,
                Enumerable: false,
                Configurable: false));
    }

    private static void DefineMathFunction(
        IBuiltinContext context,
        ObjectHandle mathHandle,
        JsObject math,
        string name,
        Func<IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var function = new NativeFunctionObject(name, (_, args) => call(args), length: length);
        var functionHandle = context.Heap.AllocateObject(function, AllocationSite.Current());
        _ = math.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                JsValue.FromObject(functionHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        context.Heap.WriteBarrier(mathHandle, functionHandle);
    }

    private static JsValue MathUnary(IBuiltinContext context, IReadOnlyList<JsValue> args, Func<double, double> operation)
    {
        var value = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        return JsValue.FromNumber(operation(value));
    }

    private static JsValue MathAtan2(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var y = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        var x = args.Count > 1 ? context.ToNumber(args[1]) : double.NaN;
        return JsValue.FromNumber(Math.Atan2(y, x));
    }

    private static JsValue MathPow(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var x = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        var y = args.Count > 1 ? context.ToNumber(args[1]) : double.NaN;
        // ECMA-262 Number::exponentiate: if exponent is NaN, result is NaN.
        if (double.IsNaN(y))
            return JsValue.FromNumber(double.NaN);
        // ECMA-262 Number::exponentiate: a base of magnitude exactly 1 with an
        // infinite exponent is NaN (C's pow, which .NET follows, returns 1).
        if (double.IsInfinity(y) && Math.Abs(x) == 1d)
        {
            return JsValue.FromNumber(double.NaN);
        }

        return JsValue.FromNumber(Math.Pow(x, y));
    }

    private static JsValue MathRound(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var value = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d)
        {
            return JsValue.FromNumber(value);
        }

        // ECMA-262 21.3.2.28: if -0.5 ≤ x < 0, return -0.
        if (value >= -0.5d && value < 0d)
        {
            return JsValue.FromNumber(-0d);
        }

        // For values where |x| < 2^52, Math.Floor(x + 0.5) is exact and matches
        // the spec. For larger values, Math.Round with ToPositiveInfinity avoids
        // the precision loss of adding 0.5 to a large integer.
        if (Math.Abs(value) < 4503599627370496d) // 2^52
            return JsValue.FromNumber(Math.Floor(value + 0.5d));
        return JsValue.FromNumber(Math.Round(value, MidpointRounding.ToPositiveInfinity));
    }

    private static JsValue MathMax(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.FromNumber(double.NegativeInfinity);
        }

        // ECMA-262 21.3.2.24 step 2: ToNumber every argument first (side effects in
        // valueOf must run for all of them) before reducing; a NaN anywhere wins.
        var sawNaN = false;
        var result = double.NegativeInfinity;
        foreach (var arg in args)
        {
            var value = context.ToNumber(arg);
            if (double.IsNaN(value))
            {
                sawNaN = true;
                continue;
            }

            if (value > result || (value == 0d && result == 0d && !MathHelpers.IsNegativeZero(value)))
            {
                result = value;
            }
        }

        return JsValue.FromNumber(sawNaN ? double.NaN : result);
    }

    private static JsValue MathMin(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return JsValue.FromNumber(double.PositiveInfinity);
        }

        // ECMA-262 21.3.2.25 step 2: ToNumber every argument first before reducing;
        // a NaN anywhere wins.
        var sawNaN = false;
        var result = double.PositiveInfinity;
        foreach (var arg in args)
        {
            var value = context.ToNumber(arg);
            if (double.IsNaN(value))
            {
                sawNaN = true;
                continue;
            }

            if (value < result || (value == 0d && result == 0d && MathHelpers.IsNegativeZero(value)))
            {
                result = value;
            }
        }

        return JsValue.FromNumber(sawNaN ? double.NaN : result);
    }

    private static JsValue MathHypot(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        // ECMA-262 21.3.2.18: coerce ALL arguments to Number first (step 1-2),
        // THEN inspect them (step 3). This ensures side effects from later
        // arguments (valueOf/toString) run even if an earlier arg is Infinity/NaN,
        // and abrupt completions propagate correctly.
        var coerced = new double[args.Count];
        for (var i = 0; i < args.Count; i++)
            coerced[i] = context.ToNumber(args[i]);

        var sawNaN = false;
        var sum = 0d;
        for (var i = 0; i < coerced.Length; i++)
        {
            var v = coerced[i];
            if (double.IsInfinity(v))
                return JsValue.FromNumber(double.PositiveInfinity);

            if (double.IsNaN(v))
            {
                sawNaN = true;
                continue;
            }

            sum += v * v;
        }

        if (sawNaN)
            return JsValue.FromNumber(double.NaN);

        return JsValue.FromNumber(Math.Sqrt(sum));
    }

    private static double MathClz32(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var value = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 32d;
        }

        var u = MathHelpers.ToUint32(value);
        if (u == 0u)
        {
            return 32d;
        }

        var n = 0;
        while ((u & 0x80000000u) == 0u)
        {
            u <<= 1;
            n++;
        }

        return n;
    }

    private static double MathImul(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var x = args.Count > 0 ? context.ToNumber(args[0]) : double.NaN;
        var y = args.Count > 1 ? context.ToNumber(args[1]) : double.NaN;
        return unchecked((int)((uint)MathHelpers.ToInt32(x) * (uint)MathHelpers.ToInt32(y)));
    }

    // ES2025 Math.sumPrecise ( iterable ): maximal-precision summation.
    private static JsValue MathSumPrecise(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined || args[0].Tag == JsValueTag.Null)
            throw new JsThrownException(context.CreateTypeError("Math.sumPrecise: argument is not iterable"));

        var iterable = args[0];
        if (iterable.Tag != JsValueTag.Object)
            throw new JsThrownException(context.CreateTypeError("Math.sumPrecise: argument is not iterable"));

        // GetIterator flattenable
        var iterator = context.GetIterator(iterable);

        try
        {
            // State machine: minus-zero(0), finite(1), nan(2), plus-infinity(3), minus-infinity(4)
            const int StMinusZero = 0;
            const int StFinite = 1;
            const int StNaN = 2;
            const int StPlusInf = 3;
            const int StMinusInf = 4;

            int state = StMinusZero;
            int count = 0;
            var finiteValues = new List<double>();

            while (context.IteratorStepValue(iterator, out var next))
            {
                // Must be a Number (not Boolean, String, BigInt, Object, etc.)
                if (next.Tag != JsValueTag.Number && next.Tag != JsValueTag.Int32)
                    throw new JsThrownException(context.CreateTypeError("Math.sumPrecise: value is not a Number"));

                var n = next.Tag == JsValueTag.Int32 ? (double)next.AsInt32() : next.AsNumber();
                if (double.IsNaN(n))
                {
                    state = StNaN;
                    continue;
                }
                if (double.IsPositiveInfinity(n))
                {
                    if (state == StMinusInf) state = StNaN;
                    else state = StPlusInf;
                    continue;
                }
                if (double.IsNegativeInfinity(n))
                {
                    if (state == StPlusInf) state = StNaN;
                    else state = StMinusInf;
                    continue;
                }
                // Finite value
                if (state != StFinite) state = StFinite;
                count++;
                finiteValues.Add(n);
            }

            if (state == StMinusZero) return JsValue.FromNumber(-0d);
            if (state == StNaN) return JsValue.FromNumber(double.NaN);
            if (state == StPlusInf) return JsValue.FromNumber(double.PositiveInfinity);
            if (state == StMinusInf) return JsValue.FromNumber(double.NegativeInfinity);

            // Sum finite values using Neumaier compensated summation
            // Sort ascending to reduce cancellation errors
            finiteValues.Sort();
            double sum = 0;
            double c = 0;
            foreach (var x in finiteValues)
            {
                double t = sum + x;
                if (Math.Abs(sum) >= Math.Abs(x))
                    c += (sum - t) + x;
                else
                    c += (x - t) + sum;
                sum = t;
            }
            return JsValue.FromNumber(sum + c);
        }
        catch
        {
            context.IteratorClose(iterator);
            throw;
        }
    }
}
