using System.Collections.Generic;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Interfaces
{
    /// <summary>
    /// Represents a JavaScript object.
    /// Extensible for different object implementations (Array, Date, RegExp, etc.)
    /// </summary>
    public interface IObject
    {
        /// <summary>
        /// Get a property value
        /// </summary>
        FenValue Get(string key, IExecutionContext context = null);

        /// <summary>
        /// Set a property value
        /// </summary>
        void Set(string key, FenValue value, IExecutionContext context = null);

        /// <summary>
        /// Check if object has a property
        /// </summary>
        bool Has(string key, IExecutionContext context = null);

        /// <summary>
        /// Delete a property
        /// </summary>
        bool Delete(string key, IExecutionContext context = null);

        /// <summary>
        /// Get all enumerable property keys
        /// </summary>
        IEnumerable<string> Keys(IExecutionContext context = null);

        /// <summary>
        /// Get the prototype object (for inheritance)
        /// </summary>
        IObject GetPrototype();

        /// <summary>
        /// Set the prototype object
        /// </summary>
        void SetPrototype(IObject prototype);

        /// <summary>
        /// Define a property with descriptor.
        /// </summary>
        bool DefineOwnProperty(string key, PropertyDescriptor desc);

        // Default Interface Methods for ES5/ES6 Reflection
        public PropertyDescriptor? GetOwnPropertyDescriptor(string key) => null;
        public bool PreventExtensions() => false; 
        public bool IsExtensible => true; 
        public bool Seal() => false; 
        public bool IsSealed() => false; 
        public bool Freeze() => false; 
        public bool IsFrozen() => false;
    }
}
