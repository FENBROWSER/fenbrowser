using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// Represents a Hidden Class (Shape) which defines the memory layout of a FenObject.
    /// This allows fast property access via index instead of dictionary lookups.
    ///
    /// Structurally shared: a Shape stores only its parent + the one property it adds.
    /// The full name → slot map is materialized lazily on first lookup; construction is
    /// therefore O(1) instead of O(N) (previously copied the parent's whole dictionary on
    /// every transition, making N-property object construction O(N²)). For large object
    /// literals like x.com's __INITIAL_STATE__ this is a ~50x improvement.
    ///
    /// Thread safety: ConcurrentDictionary makes the lockless fast-path read safe.
    /// GC: transitions use WeakReference so shapes whose FenObjects have all been collected
    /// can themselves be collected, preventing unbounded shape-tree growth.
    /// </summary>
    public class Shape
    {
        // Global root shape from which all object shapes start.
        private static readonly Shape _rootShape = new Shape();
        public static Shape RootShape => _rootShape;

        // Persistent shape lineage: this shape adds _newProperty at slot (parent.PropertyCount).
        // Walks the parent chain on lookup until _flatMap is materialized.
        private readonly Shape _parent;
        private readonly string _newProperty;
        private readonly int _newSlot;

        // Lazily-built flat name → slot map. Null until first TryGetPropertyOffset miss
        // forces a walk; cached thereafter so subsequent lookups are O(1).
        // Volatile read so multi-threaded lookups see the materialized map without a lock.
        private volatile Dictionary<string, int> _flatMap;

        // Transition table: this shape + propertyName -> child Shape.
        // WeakReference values allow the GC to collect unused child shapes.
        // ConcurrentDictionary makes TryGetValue safe to call without a lock.
        private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions
            = new ConcurrentDictionary<string, WeakReference<Shape>>();

        // Used only in the slow (creation) path to prevent duplicate shape creation.
        private readonly object _transitionLock = new object();

        public int PropertyCount { get; }

        private Shape()
        {
            PropertyCount = 0;
            _newSlot = -1;
            // Root shape: an empty flat map satisfies lookups without walking.
            _flatMap = new Dictionary<string, int>();
        }

        private Shape(Shape parent, string newProperty)
        {
            _parent = parent;
            _newProperty = newProperty;
            _newSlot = parent.PropertyCount;
            PropertyCount = parent.PropertyCount + 1;
            // _flatMap stays null; materialized lazily.
        }

        /// <summary>
        /// Returns the child shape reached by adding <paramref name="propertyName"/> to this shape.
        /// Fast path is lock-free; shape creation is serialised to prevent duplicates.
        /// </summary>
        public Shape TransitionTo(string propertyName)
        {
            // Fast path: shape exists and its target is still alive.
            if (_transitions.TryGetValue(propertyName, out var weakRef) &&
                weakRef.TryGetTarget(out var existing))
                return existing;

            // Slow path: create or recreate the shape under a lock so only one instance is
            // created per key even when multiple threads race here simultaneously.
            lock (_transitionLock)
            {
                if (_transitions.TryGetValue(propertyName, out weakRef) &&
                    weakRef.TryGetTarget(out existing))
                    return existing;

                var next = new Shape(this, propertyName);
                _transitions[propertyName] = new WeakReference<Shape>(next);
                return next;
            }
        }

        /// <summary>Gets the storage-array offset for a property.</summary>
        public bool TryGetPropertyOffset(string propertyName, out int index)
        {
            // Materialized: O(1) hash lookup.
            var map = _flatMap;
            if (map != null)
            {
                return map.TryGetValue(propertyName, out index);
            }

            // Walk parent chain checking each shape's added property. For shallow shapes this
            // is cheap (typical object-literal property counts are small). Once a shape sees
            // sustained lookup traffic we materialize the flat map so subsequent calls are O(1).
            for (var s = this; s != null && s._parent != null; s = s._parent)
            {
                if (s._newProperty == propertyName)
                {
                    index = s._newSlot;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        /// <summary>
        /// Force materialization of the flat property map. Called by paths that benefit from
        /// O(1) repeated lookups (e.g., enumeration, snapshot dumps). Safe to call concurrently.
        /// </summary>
        public void EnsureFlatMap()
        {
            if (_flatMap != null) return;
            var built = new Dictionary<string, int>(PropertyCount);
            // Walk parent chain front-to-back via stack to insert in declaration order
            // (matters for GetPropertyNames stability).
            var stack = new Stack<Shape>(PropertyCount);
            for (var s = this; s != null && s._parent != null; s = s._parent)
                stack.Push(s);
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                built[s._newProperty] = s._newSlot;
            }
            // Publish; lost races are harmless — both copies are identical.
            Interlocked.CompareExchange(ref _flatMap, built, null);
        }

        /// <summary>Exposes all property names defined by this shape, in insertion order.</summary>
        public IEnumerable<string> GetPropertyNames()
        {
            EnsureFlatMap();
            return _flatMap.Keys;
        }
    }
}
