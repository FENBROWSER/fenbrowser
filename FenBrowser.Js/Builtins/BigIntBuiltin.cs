using System.Globalization;
using System.Numerics;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("21.2", AbstractOperation = "BigInt", Url = "https://tc39.es/ecma262/#sec-bigint-objects")]
public sealed class BigIntBuiltin : IBuiltinModule
{
    private const double MaxSafeInteger = 9007199254740991d; // 2^53 - 1

    public string Name => "BigInt";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;
        var captured = context;
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "BigInt",
            (_, args) => ToBigInt(captured, args.Count > 0 ? args[0] : JsValue.Undefined, allowNumber: true),
            length: 1,
            constructWithNewTarget: (_, _) => throw new JsThrownException(captured.CreateTypeError("BigInt is not a constructor.")));
        constructor.SetPrototype(GetFunctionPrototypeHandle(context));
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        context.DefineIntrinsicFunction(constructorHandle, constructor, "asIntN", (_, args) =>
        {
            var bits = ToIndex(captured, args.Count > 0 ? args[0] : JsValue.Undefined);
            var value = ToBigInt(captured, args.Count > 1 ? args[1] : JsValue.Undefined, allowNumber: false).AsBigInt();
            return JsValue.FromBigInt(AsIntN(bits, value));
        }, length: 2);

        context.DefineIntrinsicFunction(constructorHandle, constructor, "asUintN", (_, args) =>
        {
            var bits = ToIndex(captured, args.Count > 0 ? args[0] : JsValue.Undefined);
            var value = ToBigInt(captured, args.Count > 1 ? args[1] : JsValue.Undefined, allowNumber: false).AsBigInt();
            return JsValue.FromBigInt(AsUintN(bits, value));
        }, length: 2);

        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        // ECMA-262 21.2.3.5 BigInt.prototype [ @@toStringTag ] = "BigInt"
        // { [[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: true }.
        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        _ = prototype.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("BigInt"), Writable: false, Enumerable: false, Configurable: true));

        context.DefineIntrinsicFunction(prototypeHandle, prototype, "valueOf", (thisValue, _) =>
            JsValue.FromBigInt(ThisBigIntValue(captured, thisValue)), length: 0);
        context.DefineIntrinsicFunction(prototypeHandle, prototype, "toString", (thisValue, args) =>
            JsValue.FromString(BigIntPrototypeToString(captured, thisValue, args)), length: 0);

        return new[] { BuiltinBinding.NonEnumerable("BigInt", JsValue.FromObject(constructorHandle)) };
    }

    private static ObjectHandle GetFunctionPrototypeHandle(IBuiltinContext ctx)
    {
        var functionConstructorHandle = ctx.MaterializeFunctionConstructor();
        var functionConstructor = ctx.Heap.GetObject(functionConstructorHandle);
        if (ctx.TryGetPropertyValue(functionConstructor, JsValue.FromObject(functionConstructorHandle), "prototype", out var prototypeValue) &&
            prototypeValue.Tag == JsValueTag.Object)
        {
            return prototypeValue.AsObjectHandle();
        }

        throw new InvalidOperationException("Function.prototype is unavailable.");
    }

    private static BigInteger ThisBigIntValue(IBuiltinContext context, JsValue value)
    {
        if (value.Tag == JsValueTag.BigInt)
        {
            return value.AsBigInt();
        }

        if (value.Tag == JsValueTag.Object &&
            context.Heap.GetObject(value.AsObjectHandle()) is BigIntObject bigIntObject)
        {
            return bigIntObject.Value;
        }

        throw new JsThrownException(context.CreateTypeError("BigInt.prototype method called on incompatible receiver."));
    }

    private static string BigIntPrototypeToString(IBuiltinContext context, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var value = ThisBigIntValue(context, thisValue);
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var radix = (int)context.ToNumber(args[0]);
        if (radix < 2 || radix > 36)
        {
            throw new JsThrownException(context.CreateRangeError("BigInt.prototype.toString radix must be between 2 and 36."));
        }

        return BigIntegerToRadixString(value, radix);
    }

    private static string BigIntegerToRadixString(BigInteger value, int radix)
    {
        if (value.IsZero)
        {
            return "0";
        }

        var negative = value.Sign < 0;
        var remaining = BigInteger.Abs(value);
        var digits = new List<char>();
        while (remaining > BigInteger.Zero)
        {
            remaining = BigInteger.DivRem(remaining, radix, out var remainder);
            var digit = (int)remainder;
            digits.Add((char)(digit < 10 ? '0' + digit : 'a' + digit - 10));
        }

        digits.Reverse();
        var result = new string(digits.ToArray());
        return negative ? "-" + result : result;
    }

    private static JsValue ToBigInt(IBuiltinContext context, JsValue input, bool allowNumber)
    {
        var primitive = ToPrimitive(context, input);
        switch (primitive.Tag)
        {
            case JsValueTag.BigInt:
                return primitive;
            case JsValueTag.Boolean:
                return JsValue.FromBigInt(primitive.AsBoolean() ? BigInteger.One : BigInteger.Zero);
            case JsValueTag.String:
            {
                if (!TryParseStringToBigInt(primitive.AsString(), out var parsed))
                {
                    throw new JsThrownException(context.CreateSyntaxError("Cannot convert string to BigInt."));
                }

                return JsValue.FromBigInt(parsed);
            }
            case JsValueTag.Int32:
            {
                if (!allowNumber)
                {
                    throw new JsThrownException(context.CreateTypeError("Cannot convert number to BigInt."));
                }

                return JsValue.FromBigInt(new BigInteger(primitive.AsInt32()));
            }
            case JsValueTag.Number:
            {
                if (!allowNumber)
                {
                    throw new JsThrownException(context.CreateTypeError("Cannot convert number to BigInt."));
                }

                var number = primitive.AsNumber();
                if (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number)
                {
                    throw new JsThrownException(context.CreateRangeError("The number cannot be converted to a BigInt because it is not an integer."));
                }

                return JsValue.FromBigInt(new BigInteger(number));
            }
            default:
                throw new JsThrownException(context.CreateTypeError("Cannot convert value to BigInt."));
        }
    }

    private static JsValue ToPrimitive(IBuiltinContext context, JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        var obj = context.Heap.GetObject(value.AsObjectHandle());
        var toPrimitiveSymbol = context.CreateWellKnownSymbol("toPrimitive");
        if (toPrimitiveSymbol.Tag == JsValueTag.Symbol &&
            obj.TryGetSymbolProperty(toPrimitiveSymbol.AsSymbolId(), h => context.Heap.GetObject(h), out var toPrimitiveDescriptor))
        {
            var exoticToPrimitive = toPrimitiveDescriptor.IsAccessor
                ? JsValue.Undefined
                : toPrimitiveDescriptor.Value;

            if (exoticToPrimitive.Tag != JsValueTag.Undefined && exoticToPrimitive.Tag != JsValueTag.Null)
            {
                if (!IsCallable(context, exoticToPrimitive))
                {
                    throw new JsThrownException(context.CreateTypeError("@@toPrimitive must be callable."));
                }

                var result = context.CallFunction(
                    exoticToPrimitive,
                    new[] { JsValue.FromString("number") },
                    value);
                if (result.Tag != JsValueTag.Object)
                {
                    return result;
                }

                throw new JsThrownException(context.CreateTypeError("@@toPrimitive must return a primitive value."));
            }
        }

        if (TryCallPrimitiveMethod(context, obj, value, "valueOf", out var primitive))
        {
            return primitive;
        }

        if (TryCallPrimitiveMethod(context, obj, value, "toString", out primitive))
        {
            return primitive;
        }

        throw new JsThrownException(context.CreateTypeError("Cannot convert object to primitive value."));
    }

    private static bool TryCallPrimitiveMethod(
        IBuiltinContext context,
        JsObject obj,
        JsValue thisValue,
        string methodName,
        out JsValue primitive)
    {
        if (context.TryGetPropertyValue(obj, thisValue, methodName, out var method) &&
            IsCallable(context, method))
        {
            var result = context.CallFunction(method, Array.Empty<JsValue>(), thisValue);
            if (result.Tag != JsValueTag.Object)
            {
                primitive = result;
                return true;
            }
        }

        primitive = JsValue.Undefined;
        return false;
    }

    private static bool IsCallable(IBuiltinContext context, JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               context.Heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject or BoundFunctionObject;
    }

    private static int ToIndex(IBuiltinContext context, JsValue value)
    {
        var primitive = ToPrimitive(context, value);
        if (primitive.Tag == JsValueTag.Undefined)
        {
            return 0;
        }

        if (primitive.Tag == JsValueTag.BigInt || primitive.Tag == JsValueTag.Symbol)
        {
            throw new JsThrownException(context.CreateTypeError("Cannot convert value to index."));
        }

        var number = context.ToNumber(primitive);
        if (double.IsNaN(number))
        {
            return 0;
        }

        if (double.IsInfinity(number))
        {
            throw new JsThrownException(context.CreateRangeError("BigInt bits must be a finite integer."));
        }

        var integer = number < 0 ? Math.Ceiling(number) : Math.Floor(number);
        if (integer == 0)
        {
            return 0;
        }

        if (integer < 0)
        {
            throw new JsThrownException(context.CreateRangeError("BigInt bits must be >= 0."));
        }

        if (integer > MaxSafeInteger)
        {
            throw new JsThrownException(context.CreateRangeError("BigInt bits must be <= 2^53 - 1."));
        }

        if (integer > int.MaxValue)
        {
            throw new JsThrownException(context.CreateRangeError("BigInt bits are too large for this runtime."));
        }

        return (int)integer;
    }

    private static BigInteger AsUintN(int bits, BigInteger value)
    {
        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        var modulo = BigInteger.One << bits;
        var result = value % modulo;
        if (result.Sign < 0)
        {
            result += modulo;
        }

        return result;
    }

    private static BigInteger AsIntN(int bits, BigInteger value)
    {
        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        var uintN = AsUintN(bits, value);
        var signedThreshold = BigInteger.One << (bits - 1);
        var modulo = BigInteger.One << bits;
        return uintN >= signedThreshold ? uintN - modulo : uintN;
    }

    // Exposed to the interpreter for ToBigInt of typed-array element writes
    // (BigInt64Array/BigUint64Array accept strings via the StringToBigInt grammar).
    internal static bool TryParseStringToBigInt(string text, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var sign = 1;
        var hadExplicitSign = false;
        if (trimmed[0] == '+' || trimmed[0] == '-')
        {
            hadExplicitSign = true;
            sign = trimmed[0] == '-' ? -1 : 1;
            trimmed = trimmed[1..];
            if (trimmed.Length == 0)
            {
                return false;
            }
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (hadExplicitSign)
            {
                return false;
            }
            return TryParseRadix(trimmed[2..], 16, sign, out value);
        }

        if (trimmed.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            if (hadExplicitSign)
            {
                return false;
            }
            return TryParseRadix(trimmed[2..], 8, sign, out value);
        }

        if (trimmed.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            if (hadExplicitSign)
            {
                return false;
            }
            return TryParseRadix(trimmed[2..], 2, sign, out value);
        }

        if (!BigInteger.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (sign < 0)
        {
            value = BigInteger.Negate(value);
        }

        return true;
    }

    private static bool TryParseRadix(string digits, int radix, int sign, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var ch in digits)
        {
            var digit = DigitValue(ch);
            if (digit < 0 || digit >= radix)
            {
                return false;
            }

            value = value * radix + digit;
        }

        if (sign < 0)
        {
            value = BigInteger.Negate(value);
        }

        return true;
    }

    private static int DigitValue(char ch)
    {
        if (ch is >= '0' and <= '9') return ch - '0';
        if (ch is >= 'a' and <= 'f') return 10 + (ch - 'a');
        if (ch is >= 'A' and <= 'F') return 10 + (ch - 'A');
        return -1;
    }
}
