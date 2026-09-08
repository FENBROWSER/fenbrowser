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
// Name→slot lookup uses a chain map shared by shapes along one live linear
// transition chain. The immortal root shape never lends out its own chain map:
// doing so would make page-controlled property names added to the first chain
// process-lifetime roots even after all of the corresponding shapes died.
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
        public readonly object ExtensionGate = new();
        public int Tail;
    }

    private readonly ChainMap _chain;
    private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions = new(StringComparer.Ordinal);
    private int _transitionOperations;

    // The transition this shape was asked for last. An object literal, a
    // constructor and a class body all add the same properties in the same
    // order every time they run, so the answer is almost always the previous
    // one -- and giving it costs a reference compare instead of a dictionary
    // lookup and a weak-reference dereference, which is the whole cost of
    // adding a property to a fresh object.
    //
    // The pair is one object so a reader cannot see a key from one transition
    // beside the shape of another. Holding the target strongly keeps at most
    // one chain per shape alive: the one being used.
    private sealed record HotTransition(string Key, Shape Target);

    private HotTransition? _hotTransition;

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

        // Extending a shared chain is a compound operation: Tail must still point
        // exactly at the parent depth, the property must claim that one slot, and
        // Tail must advance as one atomic decision. The process-lifetime root is
        // intentionally excluded: otherwise the first property of every linear
        // chain would be inserted into an immortal dictionary and could never be
        // reclaimed even though root transitions themselves are weak.
        if (!ReferenceEquals(parent, _root))
        {
            lock (parentChain.ExtensionGate)
            {
                if (parentChain.Tail == parent.PropertyCount &&
                    parentChain.Map.TryAdd(property, _addedSlot))
                {
                    parentChain.Tail++;
                    _chain = parentChain;
                    return;
                }
            }
        }

        // Branch point, first child of the immortal root, or a stale entry from a
        // dead sibling chain: build a private map for this new live chain by
        // walking the lineage once. Once the chain's shapes die, this map and all
        // of its page-controlled strings can die with them.
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

        // Property names come from a function's own table, so the same site
        // offers the same instance every time and reference equality is the
        // test that matters. Anything else falls through to the map.
        if (_hotTransition is { } hot && ReferenceEquals(hot.Key, property))
        {
            return hot.Target;
        }

        if (_transitions.TryGetValue(property, out var weak))
        {
            if (weak.TryGetTarget(out var existing))
            {
                _hotTransition = new HotTransition(property, existing);
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
                    _hotTransition = new HotTransition(property, existing);
                    return existing;
                }

                _transitions.TryRemove(property, out _);
            }

            var next = new Shape(this, property);
            _transitions[property] = new WeakReference<Shape>(next);
            _hotTransition = new HotTransition(property, next);

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
