using System;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// ES5.1 Property Descriptor - stores metadata for object properties.
    /// Supports both data descriptors and accessor descriptors.
    /// </summary>
    public struct PropertyDescriptor
    {
        // Data descriptor fields
        public FenValue? Value;
        
        // Common descriptor fields
        public bool? Enumerable;
        public bool? Configurable;
        
        // Data descriptor specific
        public bool? Writable;
        
        // Accessor descriptor fields
        public FenFunction Getter;
        public FenFunction Setter;
        
        /// <summary>
        /// True if this is an accessor descriptor (has getter or setter).
        /// </summary>
        public bool IsAccessor => Getter != null || Setter != null;
        
        /// <summary>
        /// True if this is a data descriptor (has value or writable).
        /// </summary>
        public bool IsData => Value.HasValue || Writable.HasValue;

        /// <summary>
        /// True if this is a generic descriptor (neither data nor accessor).
        /// </summary>
        public bool IsGenericDescriptor() => !IsData && !IsAccessor;
        
        /// <summary>
        /// Creates a default data descriptor with writable, enumerable, configurable = true.
        /// This is the default for normal property assignments like obj.x = 5.
        /// </summary>
        public static PropertyDescriptor DataDefault(FenValue value)
        {
            return new PropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = true,
                Configurable = true,
                Getter = null,
                Setter = null
            };
        }
        
        /// <summary>
        /// Creates a non-enumerable data descriptor (for built-in methods).
        /// </summary>
        public static PropertyDescriptor DataNonEnumerable(FenValue value)
        {
            return new PropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = false,
                Configurable = true,
                Getter = null,
                Setter = null
            };
        }
        
        /// <summary>
        /// Creates an accessor descriptor.
        /// </summary>
        public static PropertyDescriptor Accessor(FenFunction getter, FenFunction setter, bool enumerable = true, bool configurable = true)
        {
            return new PropertyDescriptor
            {
                Value = null,      // Must be null so IsData returns false
                Writable = null,   // Must be null so IsData returns false
                Enumerable = enumerable,
                Configurable = configurable,
                Getter = getter,
                Setter = setter
            };
        }
    }
}
