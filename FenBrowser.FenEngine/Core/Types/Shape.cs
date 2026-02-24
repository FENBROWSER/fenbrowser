using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// Represents a Hidden Class (Shape) which defines the memory layout of a FenObject.
    /// This allows fast property access via index instead of dictionary lookups.
    /// </summary>
    public class Shape
    {
        // Global root shape from which all object shapes start
        private static readonly Shape _rootShape = new Shape();
        public static Shape RootShape => _rootShape;

        // Maps a property name to its offset in the storage array
        private readonly Dictionary<string, int> _propertyMap = new Dictionary<string, int>();

        // Tracks how this shape transitions to a new shape when a specific property is added
        // e.g. ThisShape + "name" -> NewShape
        private readonly Dictionary<string, Shape> _transitions = new Dictionary<string, Shape>();

        // Total number of properties defined by this shape
        public int PropertyCount { get; private set; }

        private Shape()
        {
            PropertyCount = 0;
        }

        private Shape(Shape parent, string newProperty)
        {
            // Inherit the layout from the parent...
            foreach (var kvp in parent._propertyMap)
            {
                _propertyMap[kvp.Key] = kvp.Value;
            }

            // ...and append the new property at the end.
            _propertyMap[newProperty] = parent.PropertyCount;
            PropertyCount = parent.PropertyCount + 1;
        }

        // Lock for thread safety during multi-threaded test runner execution
        private readonly object _transitionLock = new object();

        /// <summary>
        /// Returns the new shape resulting from adding the specified property to this shape.
        /// </summary>
        public Shape TransitionTo(string propertyName)
        {
            // First do a lock-free read check
            if (_transitions.TryGetValue(propertyName, out var nextShape))
            {
                return nextShape;
            }

            lock (_transitionLock)
            {
                // Double-checked locking
                if (_transitions.TryGetValue(propertyName, out nextShape))
                {
                    return nextShape;
                }

                // Otherwise, branch into a new shape
                nextShape = new Shape(this, propertyName);
                _transitions[propertyName] = nextShape;
                return nextShape;
            }
        }

        /// <summary>
        /// Gets the raw index (storage offset) for a property.
        /// </summary>
        public bool TryGetPropertyOffset(string propertyName, out int index)
        {
            return _propertyMap.TryGetValue(propertyName, out index);
        }
        
        /// <summary>
        /// Exposes all known property names stored in this shape layout.
        /// </summary>
        public IEnumerable<string> GetPropertyNames()
        {
            return _propertyMap.Keys;
        }
    }
}
