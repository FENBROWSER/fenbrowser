using System.Collections.Generic;

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
        IValue Get(string key, IExecutionContext context = null);

        /// <summary>
        /// Set a property value
        /// </summary>
        void Set(string key, IValue value, IExecutionContext context = null);

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
    }
}
