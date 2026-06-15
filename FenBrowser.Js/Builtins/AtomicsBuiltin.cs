using System.Numerics;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 §25.4 — The Atomics Object.
//
// Atomics is a singleton ordinary object (not a constructor) exposing static
// read-modify-write operations over integer TypedArrays. FenJS executes on a
// single agent, so each operation is performed as an ordinary (non-interleaved)
// read-modify-write — which is observably identical to a real atomic step in the
// absence of concurrent agents. wait/notify degrade accordingly (no agent can
// notify, so wait never blocks indefinitely and notify wakes zero agents).
//
// All members are [[Writable]]: true, [[Enumerable]]: false, [[Configurable]]:
// true per the standard builtin shape; Atomics[ @@toStringTag ] = "Atomics".
public sealed class AtomicsBuiltin : IBuiltinModule
{
    public string Name => "Atomics";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var atomics = new JsObject();
        atomics.SetPrototype(context.GetObjectPrototype());
        var handle = heap.AllocateObject(atomics, AllocationSite.Current());
        heap.PushRoot(handle);

        // 25.4.* read-modify-write operations.
        Define(context, atomics, "add", 3, args => Rmw(context, args, AtomicOp.Add));
        Define(context, atomics, "and", 3, args => Rmw(context, args, AtomicOp.And));
        Define(context, atomics, "or", 3, args => Rmw(context, args, AtomicOp.Or));
        Define(context, atomics, "sub", 3, args => Rmw(context, args, AtomicOp.Sub));
        Define(context, atomics, "xor", 3, args => Rmw(context, args, AtomicOp.Xor));
        Define(context, atomics, "exchange", 3, args => Rmw(context, args, AtomicOp.Exchange));
        Define(context, atomics, "compareExchange", 4, args => CompareExchange(context, args));
        Define(context, atomics, "load", 2, args => Load(context, args));
        Define(context, atomics, "store", 3, args => Store(context, args));
        Define(context, atomics, "isLockFree", 1, args => IsLockFree(context, args));
        Define(context, atomics, "wait", 4, args => Wait(context, args));
        Define(context, atomics, "waitAsync", 4, args => WaitAsync(context, args));
        Define(context, atomics, "notify", 3, args => Notify(context, args));
        Define(context, atomics, "pause", 0, args => Pause(context, args));

        // 25.4.15 Atomics [ @@toStringTag ] = "Atomics"
        var toStringTagSymbol = context.CreateWellKnownSymbol("toStringTag");
        atomics.DefineOwnSymbolProperty(toStringTagSymbol.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString("Atomics"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("Atomics", JsValue.FromObject(handle)) };
    }

    private enum AtomicOp { Add, And, Or, Sub, Xor, Exchange }

    private static void Define(IBuiltinContext context, JsObject owner, string name, int length,
        Func<IReadOnlyList<JsValue>, JsValue> fn)
    {
        var native = new NativeFunctionObject(name, (_, args) => fn(args), length: length);
        var fnHandle = context.Heap.AllocateObject(native, AllocationSite.Current());
        owner.DefineOwnProperty(name, new JsPropertyDescriptor(
            JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
    }

    // 25.4.3.1 ValidateIntegerTypedArray ( typedArray [ , waitable ] )
    private static TypedArrayObject ValidateIntegerTypedArray(IBuiltinContext context, JsValue value, bool waitable)
    {
        if (value.Tag != JsValueTag.Object || context.Heap.GetObject(value.AsObjectHandle()) is not TypedArrayObject ta)
            throw new JsThrownException(context.CreateTypeError("Atomics operation called on a non-TypedArray."));

        if (ta.Buffer.IsDetached)
            throw new JsThrownException(context.CreateTypeError("Atomics operation called on a detached ArrayBuffer."));

        var type = ta.ElementType;
        if (waitable)
        {
            if (type is not (TypedArrayElementType.Int32 or TypedArrayElementType.BigInt64))
                throw new JsThrownException(context.CreateTypeError(
                    "Atomics.wait/notify requires an Int32Array or BigInt64Array."));
        }
        else
        {
            // Allowed: Int8, Uint8, Int16, Uint16, Int32, Uint32, BigInt64, BigUint64.
            // Disallowed: Uint8Clamped, Float32, Float64.
            if (type is TypedArrayElementType.Uint8Clamped
                or TypedArrayElementType.Float32
                or TypedArrayElementType.Float64)
                throw new JsThrownException(context.CreateTypeError(
                    "Atomics operation requires an integer TypedArray."));
        }

        return ta;
    }

    // 25.4.3.2 ValidateAtomicAccess ( typedArray, requestIndex )
    private static int ValidateAtomicAccess(IBuiltinContext context, TypedArrayObject ta, JsValue requestIndex)
    {
        var integer = ToInteger(context, requestIndex);
        if (integer < 0 || double.IsInfinity(integer) || integer > int.MaxValue || integer >= ta.Length)
            throw new JsThrownException(context.CreateRangeError("Atomics access index out of bounds."));
        return (int)integer;
    }

    private static bool IsBig(TypedArrayObject ta)
        => ta.ElementType is TypedArrayElementType.BigInt64 or TypedArrayElementType.BigUint64;

    // ECMA-262 7.1.5 ToIntegerOrInfinity (NaN -> 0).
    private static double ToInteger(IBuiltinContext context, JsValue value)
    {
        var n = context.ToNumber(value);
        if (double.IsNaN(n)) return 0;
        if (double.IsInfinity(n)) return n;
        return Math.Truncate(n);
    }

    private static JsValue RequireBigInt(IBuiltinContext context, JsValue value)
    {
        if (value.Tag == JsValueTag.BigInt) return value;
        throw new JsThrownException(context.CreateTypeError("Cannot convert value to a BigInt."));
    }

    private static JsValue Rmw(IBuiltinContext context, IReadOnlyList<JsValue> args, AtomicOp op)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: false);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        var valueArg = Arg(args, 2);
        var old = ta.GetElement(index);

        if (IsBig(ta))
        {
            var v = RequireBigInt(context, valueArg).AsBigInt();
            var oldB = old.AsBigInt();
            var result = op switch
            {
                AtomicOp.Add => oldB + v,
                AtomicOp.And => oldB & v,
                AtomicOp.Or => oldB | v,
                AtomicOp.Sub => oldB - v,
                AtomicOp.Xor => oldB ^ v,
                AtomicOp.Exchange => v,
                _ => v
            };
            ta.SetElement(index, JsValue.FromBigInt(result));
        }
        else
        {
            var v = ToInteger(context, valueArg);
            var oldD = old.AsNumber();
            double result;
            switch (op)
            {
                case AtomicOp.Add: result = oldD + v; break;
                case AtomicOp.Sub: result = oldD - v; break;
                case AtomicOp.Exchange: result = v; break;
                case AtomicOp.And: result = unchecked((long)oldD & (long)v); break;
                case AtomicOp.Or: result = unchecked((long)oldD | (long)v); break;
                case AtomicOp.Xor: result = unchecked((long)oldD ^ (long)v); break;
                default: result = v; break;
            }
            ta.SetElement(index, JsValue.FromNumber(result));
        }

        return old;
    }

    // 25.4.6 Atomics.compareExchange ( typedArray, index, expectedValue, replacementValue )
    private static JsValue CompareExchange(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: false);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        var old = ta.GetElement(index);
        var scratch = MakeScratch(ta);

        if (IsBig(ta))
        {
            var expected = RequireBigInt(context, Arg(args, 2));
            var replacement = RequireBigInt(context, Arg(args, 3));
            scratch.SetElement(0, expected);
            var wrappedExpected = scratch.GetElement(0).AsBigInt();
            if (old.AsBigInt() == wrappedExpected)
                ta.SetElement(index, replacement);
        }
        else
        {
            var expected = ToInteger(context, Arg(args, 2));
            var replacement = ToInteger(context, Arg(args, 3));
            scratch.SetElement(0, JsValue.FromNumber(expected));
            var wrappedExpected = scratch.GetElement(0).AsNumber();
            if (old.AsNumber() == wrappedExpected)
                ta.SetElement(index, JsValue.FromNumber(replacement));
        }

        return old;
    }

    // 25.4.8 Atomics.load ( typedArray, index )
    private static JsValue Load(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: false);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        return ta.GetElement(index);
    }

    // 25.4.11 Atomics.store ( typedArray, index, value )
    private static JsValue Store(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: false);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        var valueArg = Arg(args, 2);

        if (IsBig(ta))
        {
            var v = RequireBigInt(context, valueArg);
            ta.SetElement(index, v);
            return v; // store returns the integer/bigint value, not the wrapped element.
        }

        var n = ToInteger(context, valueArg);
        ta.SetElement(index, JsValue.FromNumber(n));
        return JsValue.FromNumber(n);
    }

    // 25.4.7 Atomics.isLockFree ( size )
    private static JsValue IsLockFree(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var n = ToInteger(context, Arg(args, 0));
        return JsValue.FromBoolean(n is 1 or 2 or 4 or 8);
    }

    // 25.4.12 Atomics.wait ( typedArray, index, value, timeout )
    // Single-agent: a matching value never gets notified, so we return "timed-out"
    // immediately rather than blocking; a mismatch returns "not-equal".
    private static JsValue Wait(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: true);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        var current = ta.GetElement(index);

        bool equal;
        if (IsBig(ta))
        {
            var v = RequireBigInt(context, Arg(args, 2));
            equal = current.AsBigInt() == v.AsBigInt();
        }
        else
        {
            var v = ToInteger(context, Arg(args, 2));
            equal = current.AsNumber() == v;
        }

        // Coerce timeout for spec-observable side effects even though we never block.
        _ = context.ToNumber(Arg(args, 3));

        return JsValue.FromString(equal ? "timed-out" : "not-equal");
    }

    // 25.4.13 Atomics.waitAsync ( typedArray, index, value, timeout )
    // Returns a Record exposed as { async, value }. In a single-agent realm the
    // wait can never be satisfied by a notify, so a matching value reports an
    // immediate synchronous "timed-out" rather than yielding a pending promise.
    private static JsValue WaitAsync(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: true);
        var index = ValidateAtomicAccess(context, ta, Arg(args, 1));
        var current = ta.GetElement(index);

        bool equal;
        if (IsBig(ta))
        {
            var v = RequireBigInt(context, Arg(args, 2));
            equal = current.AsBigInt() == v.AsBigInt();
        }
        else
        {
            var v = ToInteger(context, Arg(args, 2));
            equal = current.AsNumber() == v;
        }

        _ = context.ToNumber(Arg(args, 3)); // coerce timeout for side effects.

        var result = new JsObject();
        var handle = context.Heap.AllocateObject(result, AllocationSite.Current());
        result.DefineOwnProperty("async", new JsPropertyDescriptor(
            JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
        result.DefineOwnProperty("value", new JsPropertyDescriptor(
            JsValue.FromString(equal ? "timed-out" : "not-equal"), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(handle);
    }

    // 25.4.10 Atomics.notify ( typedArray, index, count )
    private static JsValue Notify(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var ta = ValidateIntegerTypedArray(context, Arg(args, 0), waitable: true);
        _ = ValidateAtomicAccess(context, ta, Arg(args, 1));
        if (args.Count > 2 && !(Arg(args, 2).Tag == JsValueTag.Undefined))
            _ = ToInteger(context, Arg(args, 2)); // coerce count for side effects.
        // No other agent can be waiting on this single-agent realm.
        return JsValue.FromNumber(0);
    }

    // 25.4.14 Atomics.pause ( [ iterationNumber ] ) — ES2024+.
    // Pauses the calling agent for an implementation-defined duration.
    // Spec requires iterationNumber to be a Number (not Boolean, String, etc.).
    private static JsValue Pause(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        var duration = Arg(args, 0);
        if (duration.Tag != JsValueTag.Undefined)
        {
            // Step 1a: Type(iterationNumber) must be Number (not Boolean, String, Symbol, Object).
            if (duration.Tag != JsValueTag.Number && duration.Tag != JsValueTag.Int32)
                throw new JsThrownException(context.CreateTypeError("Atomics.pause: argument must be a Number."));
            var n = duration.AsNumber();
            // Step 1c: must be an integral Number (not NaN, not Infinity, not fractional).
            if (double.IsNaN(n) || double.IsInfinity(n) || Math.Truncate(n) != n)
                throw new JsThrownException(context.CreateTypeError("Atomics.pause: iterationNumber must be an integral Number."));
            // Step 1d: if negative, throw RangeError.
            if (n < 0)
                throw new JsThrownException(context.CreateRangeError("Atomics.pause: iterationNumber must be non-negative."));
            // Yield the thread for an implementation-defined fraction of n.
            System.Threading.Thread.Yield();
        }
        else
        {
            // No duration specified: yield is the sanest default for a single-agent engine.
            System.Threading.Thread.Yield();
        }

        return JsValue.Undefined;
    }

    // A 1-element TypedArray of the same element type, used to reproduce the exact
    // engine wrapping semantics when comparing expected values in compareExchange.
    private static TypedArrayObject MakeScratch(TypedArrayObject like)
    {
        var buf = new ArrayBufferObject(like.ElementSize);
        var len = buf.ByteLength;
        return like.ElementType switch
        {
            TypedArrayElementType.Int8 => new Int8Array(buf, 0, len),
            TypedArrayElementType.Uint8 => new Uint8Array(buf, 0, len),
            TypedArrayElementType.Uint8Clamped => new Uint8ClampedArray(buf, 0, len),
            TypedArrayElementType.Int16 => new Int16Array(buf, 0, len),
            TypedArrayElementType.Uint16 => new Uint16Array(buf, 0, len),
            TypedArrayElementType.Int32 => new Int32Array(buf, 0, len),
            TypedArrayElementType.Uint32 => new Uint32Array(buf, 0, len),
            TypedArrayElementType.BigInt64 => new BigInt64Array(buf, 0, len),
            TypedArrayElementType.BigUint64 => new BigUint64Array(buf, 0, len),
            _ => new Int32Array(buf, 0, len)
        };
    }

    private static JsValue Arg(IReadOnlyList<JsValue> args, int i) => i < args.Count ? args[i] : JsValue.Undefined;
}
