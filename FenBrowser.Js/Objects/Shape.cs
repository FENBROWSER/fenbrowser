using System.Collections.Concurrent;

namespace FenBrowser.Js.Objects;

// Hidden Class / Shape system for property-access inline caches (plan §31).
//
// Each JsObject owns a Shape that describes its property layout. Shapes form a
// parent-linked tree: a shape stores only the one property it adds beyond its
// parent. The root shape (PropertyCount=0) is shared by all empty objects.
//
// Transitions are stored in a ConcurrentDictionary<key, WeakReference<Shape>>
// so shapes whose objects have all been GC'd are themselves collectible.
//
// Name→slot lookup uses a chain map shared by every shape along a linear
// transition chain. Appending a property extends the shared map in place
// (O(1)); only branch points copy. A shape sharing a map of N entries owns
// exactly the entries with slot < PropertyCount — entries past that belong to
// deeper shapes on the chain — so lookups validate the slot against
// PropertyCount. The previous design materialized a fresh flat map per shape
// on first lookup, which made growing an object by N properties O(N²) (the
// "first big array in the process takes seconds" pathology).
public class Shape
{
    private static readonly Shape _root = new();
    public static Shape Root => _root;

    private readonly Shape? _parent;
    private readonly string? _addedProperty;
    private readonly int _addedSlot;

    // Exposed for enumeration and IC validation. Parent is null for the root shape.
    public Shape? Parent => _parent;
    public string AddedProperty => _addedProperty!;
    public int AddedSlot => _addedSlot;

    // Shared along a linear transition chain. Tail tracks how many entries
    // represent the chain so far; a shape may append only when its parent is
    // the current tail (Tail == parent.PropertyCount), so every entry with
    // slot < PropertyCount is guaranteed to belong to this shape's lineage.
    private sealed class ChainMap
    {
        public readonly ConcurrentDictionary<string, int> Map = new(StringComparer.Ordinal);
        public int Tail;
    }

    private readonly ChainMap _chain;
    private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions = new();

    public int PropertyCount { get; }

    private Shape()
    {
        PropertyCount = 0;
        _addedSlot = -1;
        _chain = new ChainMap();
    }

    private Shape(Shape parent, string property)
    {
        _parent = parent;
        _addedProperty = property;
        _addedSlot = parent.PropertyCount;
        PropertyCount = parent.PropertyCount + 1;

        var parentChain = parent._chain;
        if (Volatile.Read(ref parentChain.Tail) == parent.PropertyCount &&
            parentChain.Map.TryAdd(property, _addedSlot))
        {
            Interlocked.Increment(ref parentChain.Tail);
            _chain = parentChain;
            return;
        }

        // Branch point (or a stale entry from a dead sibling chain): build a
        // private map for this new chain by walking the lineage once.
        var chain = new ChainMap { Tail = PropertyCount };
        chain.Map[property] = _addedSlot;
        for (Shape? s = parent; s != null && s != _root; s = s._parent)
        {
            chain.Map.TryAdd(s._addedProperty!, s._addedSlot);
        }

        _chain = chain;
    }

    // Returns the child shape reached by adding `property` to this shape.
    // Fast path is lock-free via ConcurrentDictionary; slow path creates a new
    // child shape under a simple lock.
    public Shape TransitionTo(string property)
    {
        if (_transitions.TryGetValue(property, out var wr) && wr.TryGetTarget(out var existing))
            return existing;

        lock (_transitions)
        {
            if (_transitions.TryGetValue(property, out wr) && wr.TryGetTarget(out existing))
                return existing;

            var next = new Shape(this, property);
            _transitions[property] = new WeakReference<Shape>(next);
            return next;
        }
    }

    // Gets the storage-array slot index for a property. Returns false if the
    // property is absent from this shape's lineage.
    public bool TryGetSlot(string property, out int slot)
    {
        if (_chain.Map.TryGetValue(property, out slot) && slot < PropertyCount)
        {
            return true;
        }

        slot = 0;
        return false;
    }
}
