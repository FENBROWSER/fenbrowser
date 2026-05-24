using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Bytecode;

// Plan §31: polymorphic inline cache for property access sites.
// Up to 4 cached (Shape, Key, Slot) entries per instruction offset.
// Beyond 4 distinct shapes the site becomes megamorphic and falls
// back to the generic [[Get]]/[[Set]] path.

public sealed class PropertyInlineCacheEntry
{
    public Shape Shape { get; }
    public string Key { get; }
    public int Slot { get; }
    public PropertyInlineCacheEntry(Shape shape, string key, int slot)
    { Shape = shape; Key = key; Slot = slot; }
}

public sealed class PolymorphicInlineCache
{
    private PropertyInlineCacheEntry? _e0, _e1, _e2, _e3;
    private int _count;
    public bool IsMegamorphic { get; private set; }

    public bool TryGet(JsObject obj, string key, out int slot)
    {
        var shape = obj.CurrentShape;
        if (_e0 is { } e0 && e0.Shape == shape && e0.Key == key) { slot = e0.Slot; return true; }
        if (_e1 is { } e1 && e1.Shape == shape && e1.Key == key) { slot = e1.Slot; return true; }
        if (_e2 is { } e2 && e2.Shape == shape && e2.Key == key) { slot = e2.Slot; return true; }
        if (_e3 is { } e3 && e3.Shape == shape && e3.Key == key) { slot = e3.Slot; return true; }
        slot = -1; return false;
    }

    public void Add(Shape shape, string key, int slot)
    {
        if (IsMegamorphic) return;
        // Replace existing entry for same shape
        if (_e0?.Shape == shape) { _e0 = new(shape, key, slot); return; }
        if (_e1?.Shape == shape) { _e1 = new(shape, key, slot); return; }
        if (_e2?.Shape == shape) { _e2 = new(shape, key, slot); return; }
        if (_e3?.Shape == shape) { _e3 = new(shape, key, slot); return; }

        switch (_count)
        {
            case 0: _e0 = new(shape, key, slot); _count = 1; break;
            case 1: _e1 = new(shape, key, slot); _count = 2; break;
            case 2: _e2 = new(shape, key, slot); _count = 3; break;
            case 3: _e3 = new(shape, key, slot); _count = 4; break;
            default: IsMegamorphic = true; break;
        }
    }

    public void InvalidateShape(Shape shape)
    {
        if (_e0?.Shape == shape) { _e0 = null; _count--; }
        if (_e1?.Shape == shape) { _e1 = null; _count--; }
        if (_e2?.Shape == shape) { _e2 = null; _count--; }
        if (_e3?.Shape == shape) { _e3 = null; _count--; }
        IsMegamorphic = false;
    }
}
