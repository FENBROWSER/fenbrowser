using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Bytecode;

// Plan §31: polymorphic inline cache for property access sites.
// Up to 4 cached (Shape, Key, Slot) entries per instruction offset.
// Beyond 4 distinct shape/key pairs the site becomes megamorphic and falls
// back to the generic [[Get]]/[[Set]] path.

public sealed class PropertyInlineCacheEntry
{
    public Shape Shape { get; }
    public string Key { get; }
    public int Slot { get; }

    public PropertyInlineCacheEntry(Shape shape, string key, int slot)
    {
        Shape = shape;
        Key = key;
        Slot = slot;
    }
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
        slot = -1;
        return false;
    }

    public void Add(Shape shape, string key, int slot)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(key);
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        if (IsMegamorphic) return;

        // Cache identity is (shape,key), not shape alone. Dynamic element access such
        // as obj[k] commonly reads several stable keys from objects with the same
        // shape. Replacing an entry merely because the shape matched caused the IC to
        // thrash between keys and miss almost every alternating access.
        if (Matches(_e0, shape, key)) { _e0 = new(shape, key, slot); return; }
        if (Matches(_e1, shape, key)) { _e1 = new(shape, key, slot); return; }
        if (Matches(_e2, shape, key)) { _e2 = new(shape, key, slot); return; }
        if (Matches(_e3, shape, key)) { _e3 = new(shape, key, slot); return; }

        // Fill actual holes rather than choosing a slot from _count. Invalidation can
        // leave sparse occupancy (for example e0/e2/e3).
        var entry = new PropertyInlineCacheEntry(shape, key, slot);
        if (_e0 is null) { _e0 = entry; _count++; return; }
        if (_e1 is null) { _e1 = entry; _count++; return; }
        if (_e2 is null) { _e2 = entry; _count++; return; }
        if (_e3 is null) { _e3 = entry; _count++; return; }

        IsMegamorphic = true;
        _count = 4;
    }

    public void InvalidateShape(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (_e0?.Shape == shape) _e0 = null;
        if (_e1?.Shape == shape) _e1 = null;
        if (_e2?.Shape == shape) _e2 = null;
        if (_e3?.Shape == shape) _e3 = null;

        _count =
            (_e0 is null ? 0 : 1) +
            (_e1 is null ? 0 : 1) +
            (_e2 is null ? 0 : 1) +
            (_e3 is null ? 0 : 1);

        // A previously-megamorphic site may become cacheable again after the
        // invalidated shape disappears. Repopulation is bounded by the same four
        // shape/key slots.
        IsMegamorphic = false;
    }

    private static bool Matches(PropertyInlineCacheEntry? entry, Shape shape, string key) =>
        entry is not null && entry.Shape == shape && string.Equals(entry.Key, key, StringComparison.Ordinal);
}
