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
// A lazy flat map (name→slot) is built on first lookup and cached, making
// subsequent TryGetSlot calls O(1) after the first miss per shape.
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

    private volatile Dictionary<string, int>? _flatMap;
    private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions = new();

    public int PropertyCount { get; }

    private Shape()
    {
        PropertyCount = 0;
        _addedSlot = -1;
        _flatMap = new Dictionary<string, int>();
    }

    private Shape(Shape parent, string property)
    {
        _parent = parent;
        _addedProperty = property;
        _addedSlot = parent.PropertyCount;
        PropertyCount = parent.PropertyCount + 1;
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
        var map = _flatMap;
        if (map != null)
            return map.TryGetValue(property, out slot);

        // Materialize the flat map on first miss.
        map = BuildFlatMap();
        _flatMap = map;
        return map.TryGetValue(property, out slot);
    }

    // Builds a full name→slot dictionary by walking the parent chain once.
    private Dictionary<string, int> BuildFlatMap()
    {
        var chain = new List<Shape>();
        for (Shape? s = this; s != null && s != _root; s = s._parent)
            chain.Add(s);
        chain.Reverse();

        var map = new Dictionary<string, int>(chain.Count, StringComparer.Ordinal);
        foreach (var shape in chain)
            map[shape._addedProperty!] = shape._addedSlot;
        return map;
    }
}
