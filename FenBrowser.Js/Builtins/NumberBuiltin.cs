using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 21.1 — The Number Constructor.
[EcmaSpecReference(
    "21.1",
    AbstractOperation = "Number",
    Url = "https://tc39.es/ecma262/#sec-number-objects")]
public sealed class NumberBuiltin : IBuiltinModule
{
    public string Name => "Number";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        // 21.1.3 Properties of the Number Prototype Object
        var prototype = new NumberObject(0d);
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedProto = prototypeHandle;
        var capturedCtx = context;

        var constructor = new NativeFunctionObject(
            "Number",
            (_, args) => JsValue.FromNumber(args.Count > 0 ? context.ToNumber(args[0]) : 0d),
            args =>
            {
                var obj = new NumberObject(args.Count > 0 ? context.ToNumber(args[0]) : 0d);
                obj.SetPrototype(capturedProto);
                return JsValue.FromObject(heap.AllocateObject(obj, AllocationSite.Current()));
            },
            length: 1);

        // 21.1.2 Properties of the Number Constructor
        _ = constructor.DefineOwnProperty("MAX_VALUE", new JsPropertyDescriptor(JsValue.FromNumber(double.MaxValue), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("MIN_VALUE", new JsPropertyDescriptor(JsValue.FromNumber(double.Epsilon), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("NaN", new JsPropertyDescriptor(JsValue.FromNumber(double.NaN), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("POSITIVE_INFINITY", new JsPropertyDescriptor(JsValue.FromNumber(double.PositiveInfinity), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("NEGATIVE_INFINITY", new JsPropertyDescriptor(JsValue.FromNumber(double.NegativeInfinity), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("EPSILON", new JsPropertyDescriptor(JsValue.FromNumber(Math.Pow(2d, -52)), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("MAX_SAFE_INTEGER", new JsPropertyDescriptor(JsValue.FromNumber(9007199254740991d), Writable: false, Enumerable: false, Configurable: false));
        _ = constructor.DefineOwnProperty("MIN_SAFE_INTEGER", new JsPropertyDescriptor(JsValue.FromNumber(-9007199254740991d), Writable: false, Enumerable: false, Configurable: false));

        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        // Number.isFinite / isNaN / isInteger / isSafeInteger (21.1.2.2-5)
        DefineStaticMethod(heap, constructorHandle, constructor, "isFinite", args =>
            JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && !double.IsNaN(args[0].AsNumber()) && !double.IsInfinity(args[0].AsNumber())));
        DefineStaticMethod(heap, constructorHandle, constructor, "isNaN", args =>
            JsValue.FromBoolean(args.Count > 0 && args[0].Tag == JsValueTag.Number && double.IsNaN(args[0].AsNumber())));
        DefineStaticMethod(heap, constructorHandle, constructor, "isInteger", args =>
            JsValue.FromBoolean(IsIntegerNumber(args)));
        DefineStaticMethod(heap, constructorHandle, constructor, "isSafeInteger", args =>
            JsValue.FromBoolean(IsIntegerNumber(args) && Math.Abs(args[0].AsNumber()) <= 9007199254740991d));

        // Number.parseInt / Number.parseFloat — same object as global (21.1.2.13-14)
        var parseIntHandle = context.GetParseIntFunction();
        _ = constructor.DefineOwnProperty("parseInt",
            new JsPropertyDescriptor(JsValue.FromObject(parseIntHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(constructorHandle, parseIntHandle);

        var parseFloatHandle = context.GetParseFloatFunction();
        _ = constructor.DefineOwnProperty("parseFloat",
            new JsPropertyDescriptor(JsValue.FromObject(parseFloatHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(constructorHandle, parseFloatHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        _ = protoObj.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 21.1.3 prototype methods
        DefineProtoMethod(heap, capturedCtx, prototypeHandle, protoObj, "toString", NumberPrototypeToString, length: 1);
        DefineProtoMethod(heap, capturedCtx, prototypeHandle, protoObj, "toFixed", NumberPrototypeToFixed, length: 1);
        DefineProtoMethod(heap, capturedCtx, prototypeHandle, protoObj, "toExponential", NumberPrototypeToExponential, length: 1);
        DefineProtoMethod(heap, capturedCtx, prototypeHandle, protoObj, "toPrecision", NumberPrototypeToPrecision, length: 1);
        DefineProtoMethod(heap, capturedCtx, prototypeHandle, protoObj, "valueOf", NumberPrototypeValueOf, length: 0);

        return new[] { BuiltinBinding.NonEnumerable("Number", JsValue.FromObject(constructorHandle)) };
    }

    private static bool IsIntegerNumber(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Number) return false;
        var value = args[0].AsNumber();
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        return Math.Floor(value) == value;
    }

    private static double NumberThisValue(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Int32 or JsValueTag.Number)
            return ctx.ToNumber(thisValue);
        if (thisValue.Tag == JsValueTag.Object && ctx.Heap.GetObject(thisValue.AsObjectHandle()) is NumberObject no)
            return no.Value;
        throw new JsThrownException(ctx.CreateTypeError("Number.prototype method called on incompatible receiver."));
    }

    // 21.1.3.6 Number.prototype.toString([radix])
    private static JsValue NumberPrototypeToString(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(ctx, thisValue);
        var radix = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? (int)ctx.ToNumber(args[0]) : 10;
        if (radix < 2 || radix > 36)
            throw new JsThrownException(ctx.CreateRangeError("toString() radix argument must be between 2 and 36."));
        if (double.IsNaN(value)) return JsValue.FromString("NaN");
        if (double.IsInfinity(value)) return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        if (value == 0d) return JsValue.FromString("0");
        if (radix == 10) return JsValue.FromString(MathHelpers.FormatNumberForString(value));

        var negative = value < 0;
        var absolute = negative ? -value : value;
        var integerPart = (long)absolute;
        var fractionPart = absolute - integerPart;

        var intText = MathHelpers.LongToRadixString(integerPart, radix);
        if (fractionPart == 0d)
            return JsValue.FromString(negative ? "-" + intText : intText);

        // Emit up to 52 fractional digits with rounding.
        var frac = new System.Text.StringBuilder();
        for (var i = 0; i < 52; i++)
        {
            fractionPart *= radix;
            var digit = (int)fractionPart;
            frac.Append(MathHelpers.LongToRadixString(digit, radix));
            fractionPart -= digit;
            if (fractionPart <= double.Epsilon) break;
        }

        var result = negative ? "-" + intText : intText;
        if (frac.Length > 0) result += "." + frac.ToString();
        return JsValue.FromString(result);
    }

    // 21.1.3.3 toFixed
    private static JsValue NumberPrototypeToFixed(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(ctx, thisValue);
        var digits = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (digits < 0 || digits > 100)
            throw new JsThrownException(ctx.CreateRangeError("toFixed() digits argument must be between 0 and 100."));
        if (double.IsNaN(value)) return JsValue.FromString("NaN");
        if (double.IsInfinity(value)) return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        if (Math.Abs(value) >= 1e21) return JsValue.FromString(MathHelpers.FormatNumberForString(value));
        return JsValue.FromString(value.ToString("F" + digits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    // 21.1.3.2 toExponential
    private static JsValue NumberPrototypeToExponential(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(ctx, thisValue);

        // ECMA-262 21.1.3.2 step 10.b: fractionDigits undefined → use the fewest
        // fraction digits whose exponential form still round-trips to x. The old "R"
        // path never produced exponential notation, so (123.456).toExponential()
        // wrongly returned "123.456" instead of "1.23456e+2".
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            if (double.IsNaN(value)) return JsValue.FromString("NaN");
            if (double.IsInfinity(value)) return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
            for (var f = 0; f < 17; f++)
            {
                var candidate = FormatExponentialFixed(value, f);
                if (double.TryParse(candidate, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var back) && back == value)
                {
                    return JsValue.FromString(candidate);
                }
            }

            return JsValue.FromString(FormatExponentialFixed(value, 17));
        }

        // Step 2: ToInteger(fractionDigits) runs (and observes valueOf side effects)
        // before the x-is-NaN/Infinity short-circuit at steps 5-6.
        var digits = (int)ctx.ToNumber(args[0]);
        if (double.IsNaN(value)) return JsValue.FromString("NaN");
        if (double.IsInfinity(value)) return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");
        if (digits < 0 || digits > 100)
            throw new JsThrownException(ctx.CreateRangeError("toExponential() digits argument must be between 0 and 100."));

        return JsValue.FromString(FormatExponentialFixed(value, digits));
    }

    // Format `value` in normalized ECMA-262 exponential notation with exactly
    // `digits` fraction digits (e.g. digits=2 → "1.23e+4", digits=0 → "1e+4").
    private static string FormatExponentialFixed(double value, int digits)
    {
        var format = "0." + new string('0', digits) + "e+0";
        var raw = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        if (digits == 0) raw = raw.Replace(".e", "e", StringComparison.Ordinal);
        return MathHelpers.NormaliseExponential(raw);
    }

    // 21.1.3.5 toPrecision
    private static JsValue NumberPrototypeToPrecision(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = NumberThisValue(ctx, thisValue);
        // Step 2: precision undefined → ToString(x) (before any NaN handling).
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
            return JsValue.FromString(MathHelpers.FormatNumberForString(value));

        // Step 3: ToInteger(precision) runs (observing valueOf side effects) before
        // the x-is-NaN check at step 4.
        var precision = (int)ctx.ToNumber(args[0]);
        if (double.IsNaN(value)) return JsValue.FromString("NaN");
        if (double.IsInfinity(value)) return JsValue.FromString(value > 0 ? "Infinity" : "-Infinity");

        if (precision < 1 || precision > 100)
            throw new JsThrownException(ctx.CreateRangeError("toPrecision() precision argument must be between 1 and 100."));

        if (value == 0d)
            return JsValue.FromString(precision == 1 ? "0" : "0." + new string('0', precision - 1));

        var formatted = value.ToString("G" + precision, System.Globalization.CultureInfo.InvariantCulture);
        return JsValue.FromString(MathHelpers.NormaliseExponential(formatted));
    }

    // 21.1.3.7 valueOf
    private static JsValue NumberPrototypeValueOf(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        return JsValue.FromNumber(NumberThisValue(ctx, thisValue));
    }

    private static void DefineStaticMethod(JsHeap heap, ObjectHandle ownerHandle, JsObject owner, string name, Func<IReadOnlyList<JsValue>, JsValue> call)
    {
        var fn = new NativeFunctionObject(name, (_, args) => call(args), length: 1);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        _ = owner.DefineOwnProperty(name,
            new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(ownerHandle, fnHandle);
    }

    private delegate JsValue ProtoMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static void DefineProtoMethod(JsHeap heap, IBuiltinContext ctx, ObjectHandle protoHandle, JsObject proto, string name, ProtoMethod method, int length)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(captured, thisValue, args), length: length);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(fnHandle, callHandle);
        proto.DefineOwnProperty(name,
            new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, fnHandle);
    }
}
