using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Property-access helpers extracted from BytecodeInterpreter.cs as part of
// audit §2 (interpreter monolith breakup). Pure file move — no semantic
// change. The methods stay private to BytecodeInterpreter via the partial
// class split.
public sealed partial class BytecodeInterpreter
{
    // Unified property read that handles every JsValueTag the spec considers a valid
    // receiver of [[Get]]. Object falls through to the existing TryGetPropertyValue
    // path; String / Number / Boolean primitives consult their respective prototype
    // (with special-cased "length" / integer-index for String); undefined / null
    // raise TypeError per ToObject (7.1.18).
    [MayExecuteJs]
    private JsValue GetReceiverProperty(JsValue receiver, string key)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                // ECMA-262 23.2.4.2 IntegerIndexedElementGet: TypedArray integer
                // indices route to the underlying buffer, not the property table.
                if (obj is TypedArrayObject ta && IsCanonicalIntegerIndex(key, out var taIdx))
                {
                    return ta.GetElement(taIdx);
                }
                if (obj is ProxyObject proxyGet)
                    return ProxyGet(proxyGet, receiver, key);
                if (TryGetPropertyValue(obj, receiver, key, out var value))
                {
                    return value;
                }
                // ECMA-262 10.3.3: callable native objects whose [[Prototype]] was
                // never wired up still need Function.prototype methods (`call`,
                // `apply`, `bind`, `toString`) to be reachable. Fall back through
                // Function.prototype only for functions that have no explicit chain.
                if (obj.PrototypeHandle is null
                    && (obj is NativeFunctionObject || obj is JsFunctionObject || obj is BoundFunctionObject)
                    && _functionPrototypeHandle is { } fnProto)
                {
                    var fpObj = _heap.GetObject(fnProto);
                    if (TryGetPropertyValue(fpObj, receiver, key, out var fpValue))
                    {
                        return fpValue;
                    }
                }
                return JsValue.Undefined;
            }
            case JsValueTag.HostObject:
                // F.5 - route through HostObjectTable validation, then IHostHooks.
                return GetHostObjectProperty(receiver, key);
            case JsValueTag.String:
            {
                var s = receiver.AsString();
                if (key == "length")
                {
                    return JsValue.FromNumber(s.Length);
                }

                // Integer index access ("abc"[1] == "b"). Out-of-range returns undefined
                // per 22.1.4.1; the spec uses an exotic-object [[GetOwnProperty]] but the
                // observable behaviour is exactly this.
                if (IsCanonicalIntegerIndex(key, out var idx))
                {
                    return idx >= 0 && idx < s.Length
                        ? JsValue.FromString(s[idx].ToString())
                        : JsValue.Undefined;
                }

                // Fall through to String.prototype - lookup returns the inherited method
                // value; the caller (CallMethodN opcode) keeps the receiver string as
                // `thisValue` so the native method receives the primitive directly.
                var stringProto = _heap.GetObject(GetGlobalPrototype("String"));
                return TryGetPropertyValue(stringProto, receiver, key, out var sv) ? sv : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(GetGlobalPrototype("Number"));
                return TryGetPropertyValue(numberProto, receiver, key, out var nv) ? nv : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(GetGlobalPrototype("Boolean"));
                return TryGetPropertyValue(boolProto, receiver, key, out var bv) ? bv : JsValue.Undefined;
            }
            case JsValueTag.Symbol:
            {
                // ECMA-262 7.1.18 ToObject(symbol) yields a Symbol wrapper whose
                // [[Prototype]] is %Symbol.prototype%; reads (toString, valueOf,
                // description) resolve there while the primitive symbol remains
                // the receiver/thisValue handed to the called method.
                var symbolProto = _heap.GetObject(GetGlobalPrototype("Symbol"));
                return TryGetPropertyValue(symbolProto, receiver, key, out var symv) ? symv : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading '" + key + "')."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading '" + key + "')."));
            default:
                return JsValue.Undefined;
        }
    }

    // Symbol-keyed [[Get]] mirroring GetReceiverProperty: Object falls through to
    // the symbol-table lookup; String/Number/Boolean primitives consult their
    // prototype's symbol table; undefined / null raise TypeError.
    [MayExecuteJs]
    private JsValue GetReceiverSymbolProperty(JsValue receiver, long symbolId)
    {
        switch (receiver.Tag)
        {
            case JsValueTag.Object:
            {
                var obj = ResolveObject(receiver);
                if (obj is ProxyObject proxyGet)
                {
                    return ProxyGetSymbol(proxyGet, receiver, symbolId);
                }

                return obj.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.String:
            {
                var stringProto = _heap.GetObject(GetGlobalPrototype("String"));
                return stringProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Number:
            case JsValueTag.Int32:
            {
                var numberProto = _heap.GetObject(GetGlobalPrototype("Number"));
                return numberProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Boolean:
            {
                var boolProto = _heap.GetObject(GetGlobalPrototype("Boolean"));
                return boolProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Symbol:
            {
                // ToObject(symbol) → %Symbol.prototype% for symbol-keyed reads
                // such as sym[Symbol.toPrimitive].
                var symbolProto = _heap.GetObject(GetGlobalPrototype("Symbol"));
                return symbolProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.BigInt:
            {
                // ToObject(bigint) → %BigInt.prototype% for symbol-keyed reads
                // such as the @@toStringTag used by Object.prototype.toString.
                var bigIntProto = _heap.GetObject(GetGlobalPrototype("BigInt"));
                return bigIntProto.TryGetSymbolProperty(symbolId, h => _heap.GetObject(h), out var desc)
                    ? GetDescriptorValue(desc, receiver)
                    : JsValue.Undefined;
            }
            case JsValueTag.Undefined:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of undefined (reading symbol key)."));
            case JsValueTag.Null:
                throw new JsThrownException(CreateTypeError(
                    "Cannot read properties of null (reading symbol key)."));
            default:
                return JsValue.Undefined;
        }
    }

    // True when `key` is a non-negative decimal integer with no leading zeros (except
    // the literal "0"). Matches the spec's "canonical numeric string" definition for
    // 7.1.21 CanonicalNumericIndexString restricted to non-negative integers, which is
    // what String exotic objects accept as index keys.
    private static bool IsCanonicalIntegerIndex(string key, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length > 1 && key[0] == '0')
        {
            return false;
        }

        var result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            // Guard against overflow on absurdly long keys.
            if (result > (int.MaxValue - (c - '0')) / 10)
            {
                return false;
            }

            result = result * 10 + (c - '0');
        }

        index = result;
        return true;
    }

    [MayExecuteJs]
    private bool TryGetPropertyValue(JsObject obj, JsValue receiver, string key, out JsValue value)
    {
        if (obj is ProxyObject proxyGet)
        {
            value = ProxyGet(proxyGet, receiver, key);
            return true;
        }

        if (obj.TryGetOwnProperty(key, out var descriptor))
        {
            value = GetDescriptorValue(descriptor, receiver);
            return true;
        }

        if (obj.PrototypeHandle is { } prototypeHandle)
        {
            return TryGetPropertyValue(_heap.GetObject(prototypeHandle), receiver, key, out value);
        }

        value = JsValue.Undefined;
        return false;
    }
}
