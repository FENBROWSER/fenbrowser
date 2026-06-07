using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-262 Annex B B.2.2 — legacy Object.prototype additional properties:
// __proto__ accessor, __defineGetter__, __defineSetter__, __lookupGetter__,
// __lookupSetter__. Kept in their own partial to avoid growing the interpreter
// monolith further (audit §2).
public sealed partial class BytecodeInterpreter
{
    private void InstallAnnexBObjectPrototype(ObjectHandle prototypeHandle, JsObject prototype)
    {
        // B.2.2.1 Object.prototype.__proto__ — an accessor property
        // { [[Enumerable]]: false, [[Configurable]]: true }.
        var getProto = new NativeFunctionObject("get __proto__",
            (thisValue, _) => AnnexBGetProto(thisValue), length: 0);
        var setProto = new NativeFunctionObject("set __proto__",
            (thisValue, args) => AnnexBSetProto(thisValue, args), length: 1);
        var getProtoHandle = _heap.AllocateObject(getProto, AllocationSite.Current());
        var setProtoHandle = _heap.AllocateObject(setProto, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "__proto__",
            JsPropertyDescriptor.Accessor(
                JsValue.FromObject(getProtoHandle),
                JsValue.FromObject(setProtoHandle),
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(prototypeHandle, getProtoHandle);
        _heap.WriteBarrier(prototypeHandle, setProtoHandle);

        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "__defineGetter__",
            (thisValue, args) => AnnexBDefineAccessor(thisValue, args, asGetter: true), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "__defineSetter__",
            (thisValue, args) => AnnexBDefineAccessor(thisValue, args, asGetter: false), length: 2);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "__lookupGetter__",
            (thisValue, args) => AnnexBLookupAccessor(thisValue, args, wantGetter: true), length: 1);
        _ = DefineNativePrototypeMethod(prototypeHandle, prototype, "__lookupSetter__",
            (thisValue, args) => AnnexBLookupAccessor(thisValue, args, wantGetter: false), length: 1);
    }

    // B.2.2.1.1 get Object.prototype.__proto__: return ? O.[[GetPrototypeOf]]().
    private JsValue AnnexBGetProto(JsValue thisValue)
    {
        var handle = RequireToObjectHandle(thisValue, "get __proto__");
        var obj = _heap.GetObject(handle);
        if (obj is ProxyObject proxy)
        {
            return ProxyGetPrototypeOf(proxy);
        }

        return obj.PrototypeHandle is { } proto ? JsValue.FromObject(proto) : JsValue.Null;
    }

    // B.2.2.1.2 set Object.prototype.__proto__.
    private JsValue AnnexBSetProto(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        // 1. Let O be ? RequireObjectCoercible(this value).
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(
                "Object.prototype.__proto__ called on null or undefined."));
        }

        var proto = args.Count > 0 ? args[0] : JsValue.Undefined;
        // 2. If Type(proto) is neither Object nor Null, return undefined.
        if (proto.Tag != JsValueTag.Object && proto.Tag != JsValueTag.Null)
        {
            return JsValue.Undefined;
        }

        // 3. If Type(O) is not Object, return undefined.
        if (thisValue.Tag != JsValueTag.Object)
        {
            return JsValue.Undefined;
        }

        // Steps 4-5: ? O.[[SetPrototypeOf]](proto); throw TypeError if it returns false.
        if (!OrdinarySetPrototypeOf(thisValue.AsObjectHandle(), proto))
        {
            throw new JsThrownException(CreateTypeError(
                "Object.prototype.__proto__: cannot set prototype (cycle, non-extensible, or immutable)."));
        }

        return JsValue.Undefined;
    }

    // B.2.2.2 __defineGetter__ / B.2.2.3 __defineSetter__.
    private JsValue AnnexBDefineAccessor(JsValue thisValue, IReadOnlyList<JsValue> args, bool asGetter)
    {
        var label = asGetter ? "__defineGetter__" : "__defineSetter__";
        var handle = RequireToObjectHandle(thisValue, label);
        var key = args.Count > 0 ? args[0] : JsValue.Undefined;
        var accessor = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (!IsCallableValue(accessor))
        {
            throw new JsThrownException(CreateTypeError(
                $"Object.prototype.{label}: the second argument must be callable."));
        }

        var target = _heap.GetObject(handle);
        if (key.Tag == JsValueTag.Symbol)
        {
            var symId = key.AsSymbolId();
            var (g, s) = ExistingAccessorHalves(target.TryGetOwnSymbolProperty(symId, out var ex), ex);
            if (asGetter) g = accessor; else s = accessor;
            _ = target.DefineOwnSymbolProperty(symId,
                JsPropertyDescriptor.Accessor(g, s, Enumerable: true, Configurable: true));
        }
        else
        {
            var name = ToPropertyKey(key);
            var (g, s) = ExistingAccessorHalves(target.TryGetOwnProperty(name, out var ex), ex);
            if (asGetter) g = accessor; else s = accessor;
            _ = target.DefineOwnProperty(name,
                JsPropertyDescriptor.Accessor(g, s, Enumerable: true, Configurable: true));
        }

        _heap.WriteBarrier(handle, accessor.AsObjectHandle());
        return JsValue.Undefined;
    }

    // B.2.2.4 __lookupGetter__ / B.2.2.5 __lookupSetter__: walk the prototype chain;
    // the first own property found decides the result (accessor → its get/set or
    // undefined; data property → undefined).
    private JsValue AnnexBLookupAccessor(JsValue thisValue, IReadOnlyList<JsValue> args, bool wantGetter)
    {
        var label = wantGetter ? "__lookupGetter__" : "__lookupSetter__";
        var handle = RequireToObjectHandle(thisValue, label);
        var key = args.Count > 0 ? args[0] : JsValue.Undefined;
        var isSymbol = key.Tag == JsValueTag.Symbol;
        var symId = isSymbol ? key.AsSymbolId() : 0;
        var name = isSymbol ? null : ToPropertyKey(key);

        ObjectHandle? current = handle;
        while (current is { } cur)
        {
            var obj = _heap.GetObject(cur);
            // B.2.2.4 step a: desc be ? O.[[GetOwnProperty]](key). For a Proxy this
            // MUST invoke the getOwnPropertyDescriptor trap and propagate its abrupt
            // completion — walking the raw target/prototype skipped the trap, so a
            // throwing trap was silently swallowed.
            bool found;
            JsPropertyDescriptor desc;
            if (obj is ProxyObject proxy && !isSymbol)
            {
                found = ProxyTryGetOwnPropertyDescriptor(proxy, name!, out desc);
            }
            else
            {
                found = isSymbol
                    ? obj.TryGetOwnSymbolProperty(symId, out desc)
                    : obj.TryGetOwnProperty(name!, out desc);
            }

            if (found)
            {
                if (!desc.IsAccessor)
                {
                    return JsValue.Undefined;
                }

                var half = wantGetter ? desc.Get : desc.Set;
                return half.Tag == JsValueTag.Undefined ? JsValue.Undefined : half;
            }

            // B.2.2.4 step c: set O to ? O.[[GetPrototypeOf]]() (trap-aware for Proxy).
            if (obj is ProxyObject protoProxy)
            {
                var proto = ProxyGetPrototypeOf(protoProxy);
                current = proto.Tag == JsValueTag.Object ? proto.AsObjectHandle() : (ObjectHandle?)null;
            }
            else
            {
                current = obj.PrototypeHandle;
            }
        }

        return JsValue.Undefined;
    }

    private static (JsValue Get, JsValue Set) ExistingAccessorHalves(bool found, JsPropertyDescriptor existing)
        => found && existing.IsAccessor ? (existing.Get, existing.Set) : (JsValue.Undefined, JsValue.Undefined);

    // ECMA-262 7.1.18 ToObject restricted to the receiver of an Object.prototype
    // method: null/undefined throw, primitives box to their wrapper handle.
    private ObjectHandle RequireToObjectHandle(JsValue value, string methodLabel)
    {
        if (value.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError(
                $"Object.prototype.{methodLabel} called on null or undefined."));
        }

        if (value.Tag == JsValueTag.Object)
        {
            return value.AsObjectHandle();
        }

        return CreateObjectFromValue(value).AsObjectHandle();
    }

    private bool IsCallableValue(JsValue value)
        => value.Tag == JsValueTag.Object && IsCallableTarget(value.AsObjectHandle());

    // ECMA-262 10.1.2 [[SetPrototypeOf]] for ordinary objects (10.1.2.1) plus the
    // immutable-prototype (10.4.7.1) and Proxy (10.5.2) variants. Returns whether the
    // change succeeded WITHOUT throwing for the disallowed-but-not-abrupt cases
    // (non-extensible with a different prototype, cycle, immutable mismatch); the
    // caller decides whether a false result becomes a TypeError. Proxy trap aborts
    // propagate as thrown exceptions.
    private bool OrdinarySetPrototypeOf(ObjectHandle handle, JsValue protoValue)
    {
        var obj = _heap.GetObject(handle);
        if (obj is ProxyObject proxy)
        {
            return ProxySetPrototypeOf(proxy, protoValue);
        }

        var current = obj.PrototypeHandle;
        // Step 2: SameValue(V, current) → no-op success.
        var sameValue = protoValue.Tag == JsValueTag.Null
            ? current is null
            : protoValue.Tag == JsValueTag.Object && current is { } c && c.Equals(protoValue.AsObjectHandle());
        if (sameValue)
        {
            return true;
        }

        // 10.4.7.1: immutable prototype exotic objects reject any real change.
        if (obj.ImmutablePrototype)
        {
            return false;
        }

        // Steps 3-4: a non-extensible object cannot change its prototype.
        if (!obj.Extensible)
        {
            return false;
        }

        // Steps 5-7: walk the proposed chain; reject a cycle. Stop at a non-ordinary
        // [[GetPrototypeOf]] (e.g. a Proxy) — its chain is not statically analysable.
        if (protoValue.Tag == JsValueTag.Object)
        {
            var p = protoValue.AsObjectHandle();
            while (true)
            {
                if (p.Equals(handle))
                {
                    return false;
                }

                var pObj = _heap.GetObject(p);
                if (pObj is ProxyObject)
                {
                    break;
                }

                if (pObj.PrototypeHandle is { } next)
                {
                    p = next;
                }
                else
                {
                    break;
                }
            }
        }

        if (protoValue.Tag == JsValueTag.Object)
        {
            obj.SetPrototype(protoValue.AsObjectHandle());
            _heap.WriteBarrier(handle, protoValue.AsObjectHandle());
        }
        else
        {
            obj.SetPrototype(null);
        }

        return true;
    }
}
