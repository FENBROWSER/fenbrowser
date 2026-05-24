using FenBrowser.Js.Bytecode;
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
}
