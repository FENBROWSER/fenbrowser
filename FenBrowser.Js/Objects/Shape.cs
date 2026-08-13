using System.Collections.Concurrent;

namespace FenBrowser.Js.Objects;

// Hidden Class / Shape system for property-access inline caches (plan §31).
//
// Each JsObject owns a Shape that describes its property layout. Shapes form a
// parent-linked tree: a shape stores only the one property it adds beyond its
// parent. The root shape (PropertyCount=0) is shared by all empty objects.
//
// Transitions are stored in a ConcurrentDictionary<key, WeakReference<Shape>>
// so shapes whose objects have all been GC'd are themselves collectible. Dead
// weak entries are removed opportunistically so their string keys do not turn
// the process-global root transition table into an unbounded name registry.
//
// Name→slot lookup uses a chain map shared by every shape along a linear
// transition chain. Appending a property extends the shared map in place
// (O(1)); only branch points copy. A shape sharing a map of N entries owns
// exactly the entries with slot < PropertyCount — entries past that belong to
// deeper shapes on the chain — so lookups validate the slot against
// PropertyCount.
public class Shape
{
    private const int TransitionPruneInterval = 64;

    private static readonly Shape _root = new();
    public static Shape Root => _root;

    private readonly Shape? _parent;
    private readonly string? _addedProperty;
    private readonly int _addedSlot;

    public Shape? Parent => _parent;
    public string AddedProperty => _addedProperty!;
    public int AddedSlot => _addedSlot;

    private sealed class ChainMap
    {
        public readonly ConcurrentDictionary<string, int> Map = new(StringComparer.Ordinal);
        public int Tail;
    }

    private readonly ChainMap _chain;
    private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions = new(StringComparer.Ordinal);
    private int _transitionOperations;

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
    public Shape TransitionTo(string property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (_transitions.TryGetValue(property, out var weak))
        {
            if (weak.TryGetTarget(out var existing))
            {
                return existing;
            }

            // Weak value is dead; remove the corresponding strong key immediately.
            _transitions.TryRemove(property, out _);
        }

        lock (_transitions)
        {
            if (_transitions.TryGetValue(property, out weak))
            {
                if (weak.TryGetTarget(out var existing))
                {
                    return existing;
                }

                _transitions.TryRemove(property, out _);
            }

            var next = new Shape(this, property);
            _transitions[property] = new WeakReference<Shape>(next);

            if (Interlocked.Increment(ref _transitionOperations) % TransitionPruneInterval == 0)
            {
                PruneDeadTransitions();
            }

            return next;
        }
    }

    public bool TryGetSlot(string property, out int slot)
    {
        if (_chain.Map.TryGetValue(property, out slot) && slot < PropertyCount)
        {
            return true;
        }

        slot = 0;
        return false;
    }

    private void PruneDeadTransitions()
    {
        foreach (var pair in _transitions)
        {
            if (!pair.Value.TryGetTarget(out _))
            {
                _transitions.TryRemove(pair.Key, out _);
            }
        }
    }
}
