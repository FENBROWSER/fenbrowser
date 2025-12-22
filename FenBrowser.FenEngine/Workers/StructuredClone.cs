using System;
using System.Collections.Generic;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Implements structured clone algorithm for Worker message passing.
    /// Clones data without transferring references between thread boundaries.
    /// 
    /// Supported types:
    /// - Primitives (null, bool, numbers, strings)
    /// - Plain objects (serializable)
    /// - Arrays
    /// - ArrayBuffer (as byte[])
    /// - Date (as DateTime)
    /// 
    /// NOT supported (throws):
    /// - Functions
    /// - DOM nodes
    /// - Symbols
    /// - WeakMap/WeakSet
    /// </summary>
    public static class StructuredClone
    {
        /// <summary>
        /// Clone a value using structured clone algorithm
        /// </summary>
        /// <param name="value">Value to clone</param>
        /// <returns>Deep copy of the value</returns>
        /// <exception cref="StructuredCloneException">Thrown for non-cloneable types</exception>
        public static object Clone(object value)
        {
            if (value == null)
                return null;

            var type = value.GetType();

            // Primitives - return as-is (immutable)
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return value;

            // DateTime
            if (value is DateTime dt)
                return new DateTime(dt.Ticks, dt.Kind);

            // DateTimeOffset
            if (value is DateTimeOffset dto)
                return new DateTimeOffset(dto.Ticks, dto.Offset);

            // Byte array (ArrayBuffer equivalent)
            if (value is byte[] bytes)
            {
                var clone = new byte[bytes.Length];
                Array.Copy(bytes, clone, bytes.Length);
                return clone;
            }

            // Arrays
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = (Array)value;
                var clone = Array.CreateInstance(elementType, array.Length);
                for (int i = 0; i < array.Length; i++)
                {
                    clone.SetValue(Clone(array.GetValue(i)), i);
                }
                return clone;
            }

            // Lists
            if (value is IList<object> list)
            {
                var clone = new List<object>(list.Count);
                foreach (var item in list)
                {
                    clone.Add(Clone(item));
                }
                return clone;
            }

            // Dictionaries (plain objects)
            if (value is IDictionary<string, object> dict)
            {
                var clone = new Dictionary<string, object>(dict.Count);
                foreach (var kvp in dict)
                {
                    clone[kvp.Key] = Clone(kvp.Value);
                }
                return clone;
            }

            // Check for non-cloneable types
            if (IsFunction(type))
                throw new StructuredCloneException("Functions cannot be cloned");

            if (type.FullName?.Contains("DOM") == true || type.FullName?.Contains("Element") == true)
                throw new StructuredCloneException("DOM nodes cannot be cloned");

            // Try JSON serialization as fallback for complex objects
            try
            {
                var json = JsonSerializer.Serialize(value);
                return JsonSerializer.Deserialize<object>(json);
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[StructuredClone] Fallback serialization failed: {ex.Message}", LogCategory.Errors);
                throw new StructuredCloneException($"Cannot clone value of type {type.Name}", ex);
            }
        }

        /// <summary>
        /// Check if a value can be cloned
        /// </summary>
        public static bool CanClone(object value)
        {
            if (value == null)
                return true;

            var type = value.GetType();

            // Primitives
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return true;

            // DateTime
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return true;

            // Byte arrays
            if (type == typeof(byte[]))
                return true;

            // Arrays of cloneable types
            if (type.IsArray)
                return true;

            // Collections
            if (value is IList<object> || value is IDictionary<string, object>)
                return true;

            // Functions are not cloneable
            if (IsFunction(type))
                return false;

            // DOM nodes are not cloneable
            if (type.FullName?.Contains("DOM") == true || type.FullName?.Contains("Element") == true)
                return false;

            // Try to serialize (expensive check)
            try
            {
                JsonSerializer.Serialize(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFunction(Type type)
        {
            return typeof(Delegate).IsAssignableFrom(type) ||
                   type.FullName?.Contains("Function") == true ||
                   type.FullName?.Contains("Func") == true ||
                   type.FullName?.Contains("Action") == true;
        }
    }

    /// <summary>
    /// Exception thrown when structured clone algorithm fails
    /// </summary>
    public class StructuredCloneException : Exception
    {
        public StructuredCloneException(string message) : base(message) { }
        public StructuredCloneException(string message, Exception inner) : base(message, inner) { }
    }
}
