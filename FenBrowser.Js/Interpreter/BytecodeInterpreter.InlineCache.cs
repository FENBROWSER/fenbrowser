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
        if (!obj.CurrentShape.TryGetSlot(key, out var slot) || obj.PropertyArray[slot] is not { } desc) return;
        if (desc.IsAccessor || !desc.Writable) return;

        fn.StoreICs ??= new();
        if (!fn.StoreICs.TryGetValue(offset, out var ic))
        { ic = new PolymorphicInlineCache(); fn.StoreICs[offset] = ic; }
        ic.Add(obj.CurrentShape, key, slot);
    }
}
