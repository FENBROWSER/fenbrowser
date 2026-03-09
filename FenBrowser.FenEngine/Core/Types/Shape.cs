using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// Represents a Hidden Class (Shape) which defines the memory layout of a FenObject.
    /// This allows fast property access via index instead of dictionary lookups.
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

        // Maps a property name to its offset in the storage array.
        private readonly Dictionary<string, int> _propertyMap = new Dictionary<string, int>();

        // Transition table: Shape + propertyName → child Shape.
        // WeakReference values allow the GC to collect unused child shapes.
        // ConcurrentDictionary makes TryGetValue safe to call without a lock.
        private readonly ConcurrentDictionary<string, WeakReference<Shape>> _transitions
            = new ConcurrentDictionary<string, WeakReference<Shape>>();

        // Used only in the slow (creation) path to prevent duplicate shape creation.
        private readonly object _transitionLock = new object();

        public int PropertyCount { get; private set; }

        private Shape()
        {
            PropertyCount = 0;
        }

        private Shape(Shape parent, string newProperty)
        {
            // Inherit the layout from the parent...
            foreach (var kvp in parent._propertyMap)
                _propertyMap[kvp.Key] = kvp.Value;

            // ...and append the new property at the end.
            _propertyMap[newProperty] = parent.PropertyCount;
            PropertyCount = parent.PropertyCount + 1;
        }

        /// <summary>
        /// Returns the child shape reached by adding <paramref name="propertyName"/> to this shape.
        /// Fast path is lock-free; shape creation is serialised to prevent duplicates.
        /// </summary>
        public Shape TransitionTo(string propertyName)
        {
            // Fast path: shape exists and its target is still alive (ConcurrentDictionary read — safe).
            if (_transitions.TryGetValue(propertyName, out var weakRef) &&
                weakRef.TryGetTarget(out var existing))
                return existing;

            // Slow path: create or recreate the shape under a lock so only one instance is
            // created per key even when multiple threads race here simultaneously.
            lock (_transitionLock)
            {
                // Re-check inside the lock (another thread may have just created it).
                if (_transitions.TryGetValue(propertyName, out weakRef) &&
                    weakRef.TryGetTarget(out existing))
                    return existing;

                var next = new Shape(this, propertyName);
                // Overwrite any stale (dead) WeakReference that may already be in the map.
                _transitions[propertyName] = new WeakReference<Shape>(next);
                return next;
            }
        }

        /// <summary>Gets the storage-array offset for a property.</summary>
        public bool TryGetPropertyOffset(string propertyName, out int index)
            => _propertyMap.TryGetValue(propertyName, out index);

        /// <summary>Exposes all property names defined by this shape.</summary>
        public IEnumerable<string> GetPropertyNames()
            => _propertyMap.Keys;
    }
}
