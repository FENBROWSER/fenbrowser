using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Plan §31: inline cache helpers. Data-property-only fast path for now.
public sealed partial class BytecodeInterpreter
{
    private bool TryGetLoadIC(BytecodeFunction fn, int offset, JsValue receiver, string key, out JsValue result)
    {
        if (receiver.Tag != JsValueTag.Object || fn.LoadICs is null || !fn.LoadICs.TryGetValue(offset, out var ic))
        { result = JsValue.Undefined; return false; }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (!ic.TryGet(obj, key, out var slot) || obj.PropertyArray[slot] is not { } desc)
        { result = JsValue.Undefined; return false; }

        if (desc.IsAccessor)
        {
            ic.InvalidateShape(obj.CurrentShape);
            result = JsValue.Undefined;
            return false;
        }

        result = desc.Value;
        return true;
    }

    private void PopulateLoadIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
    {
        if (receiver.Tag != JsValueTag.Object) return;
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (!obj.CurrentShape.TryGetSlot(key, out var slot) || obj.PropertyArray[slot] is not { } desc) return;
        if (desc.IsAccessor) return; // accessors not cached yet

        fn.LoadICs ??= new();
        if (!fn.LoadICs.TryGetValue(offset, out var ic))
        { ic = new PolymorphicInlineCache(); fn.LoadICs[offset] = ic; }
        ic.Add(obj.CurrentShape, key, slot);
    }

    // Store IC fast path: receiver is a plain object whose current shape carries
    // `key` as a writable, non-accessor own data property. Updates the slot in
    // place without walking the prototype chain. Returns false if the IC misses
    // (caller must take the slow [[Set]] path) or if the cached slot has been
    // invalidated (made non-writable, deleted, or turned into an accessor).
    private bool TryStoreIC(BytecodeFunction fn, int offset, ObjectHandle ownerHandle, JsValue receiver, string key, JsValue value)
    {
        if (receiver.Tag != JsValueTag.Object || fn.StoreICs is null || !fn.StoreICs.TryGetValue(offset, out var ic))
            return false;

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj is ProxyObject) return false;
        // ECMA-262 10.4.2.4: writing an Array's "length" is an exotic operation that
        // may delete out-of-range elements. Never short-circuit it through the IC.
        if (obj is ArrayObject && key == "length") return false;
        if (!ic.TryGet(obj, key, out var slot) || obj.PropertyArray[slot] is not { } desc)
            return false;
        if (desc.IsAccessor || !desc.Writable)
        {
            ic.InvalidateShape(obj.CurrentShape);
            return false;
        }

        var updated = desc with { Value = value };
        obj.PropertyArray[slot] = updated;
        WriteDescriptorBarrier(ownerHandle, updated);
        return true;
    }

    private void PopulateStoreIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
    {
        if (receiver.Tag != JsValueTag.Object) return;
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj is ProxyObject) return;
        if (obj is ArrayObject && key == "length") return;
        if (!obj.CurrentShape.TryGetSlot(key, out var slot) || obj.PropertyArray[slot] is not { } desc) return;
        if (desc.IsAccessor || !desc.Writable) return;

        fn.StoreICs ??= new();
        if (!fn.StoreICs.TryGetValue(offset, out var ic))
        { ic = new PolymorphicInlineCache(); fn.StoreICs[offset] = ic; }
        ic.Add(obj.CurrentShape, key, slot);
    }

    // Tier 4 #20 GetElem IC: same shape/key/slot lookup as the LoadIC, but
    // keyed off the GetElem instruction offset and only consulted when the
    // key value is a String at runtime (the common `obj["foo"]` case).
    // Integer-indexed array access remains on the slow path.
    private bool TryGetElemStringIC(BytecodeFunction fn, int offset, JsValue receiver, string key, out JsValue result)
    {
        if (receiver.Tag != JsValueTag.Object || fn.LoadICs is null || !fn.LoadICs.TryGetValue(offset, out var ic))
        { result = JsValue.Undefined; return false; }

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (obj is ProxyObject) { result = JsValue.Undefined; return false; }
        if (!ic.TryGet(obj, key, out var slot) || obj.PropertyArray[slot] is not { } desc)
        { result = JsValue.Undefined; return false; }
        if (desc.IsAccessor) { ic.InvalidateShape(obj.CurrentShape); result = JsValue.Undefined; return false; }

        result = desc.Value;
        return true;
    }

    private void PopulateGetElemStringIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
        => PopulateLoadIC(fn, offset, receiver, key);

    // Tier 4 #20 Call IC: monomorphic cache of the resolved callee handle at
    // each call site. On a hit the dispatch path is fixed (NativeFunction,
    // JsFunction, BoundFunction, Proxy) so the type-discrimination cascade in
    // CallFunction is skipped. Bound functions still need to merge args, so
    // they take the slow path; only NativeFunction and ordinary
    // JsFunction monomorphic call sites benefit.
    private bool TryDispatchCallIC(BytecodeFunction fn, int offset, JsValue callee, IReadOnlyList<JsValue> args, JsValue thisValue, out JsValue result)
    {
        result = JsValue.Undefined;
        if (callee.Tag != JsValueTag.Object) return false;
        fn.CallICs ??= new();
        if (!fn.CallICs.TryGetValue(offset, out var entry)) return false;
        if (entry.Megamorphic) return false;

        var handle = callee.AsObjectHandle().ToInt64();
        if (entry.CalleeHandle != handle) return false;

        var obj = _heap.GetObject(callee.AsObjectHandle());
        switch (entry.Kind)
        {
            case CallICKind.Native:
                if (obj is not NativeFunctionObject nfn) { entry.Megamorphic = true; return false; }
                entry.Hits++;
                result = nfn.Call(thisValue, args);
                return true;
            case CallICKind.OrdinaryFunction:
                if (obj is not JsFunctionObject jfn ||
                    jfn.Kind != Objects.FunctionKind.Ordinary)
                { entry.Megamorphic = true; return false; }
                // Fall back to CallFunction for the ordinary case: it owns
                // arity-binding, strict-this conversion, and frame setup. The
                // IC still saved the type-discrimination cascade.
                result = CallFunction(callee, args, thisValue);
                entry.Hits++;
                return true;
            default:
                return false;
        }
    }

    private void PopulateCallIC(BytecodeFunction fn, int offset, JsValue callee)
    {
        if (callee.Tag != JsValueTag.Object) return;
        var obj = _heap.GetObject(callee.AsObjectHandle());
        CallICKind kind;
        if (obj is NativeFunctionObject) kind = CallICKind.Native;
        else if (obj is JsFunctionObject f && f.Kind == Objects.FunctionKind.Ordinary) kind = CallICKind.OrdinaryFunction;
        else return; // Proxy, bound, async, generator — not cached.

        fn.CallICs ??= new();
        var handle = callee.AsObjectHandle().ToInt64();
        if (fn.CallICs.TryGetValue(offset, out var entry))
        {
            if (entry.CalleeHandle != handle) entry.Megamorphic = true;
            return;
        }
        fn.CallICs[offset] = new CallICEntry { CalleeHandle = handle, Kind = kind };
    }
}
