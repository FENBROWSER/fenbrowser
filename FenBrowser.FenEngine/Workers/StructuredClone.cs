using System;
using System.Collections.Generic;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Implements the HTML structured clone algorithm for Worker message passing.
    /// WHATWG HTML §2.7.5: StructuredSerializeInternal / StructuredDeserialize
    ///
    /// Supported types:
    /// - Primitives (null, undefined, bool, numbers, strings, symbols → throw)
    /// - FenObject / FenValue (JS engine values with cycle detection)
    /// - Plain .NET objects (serializable)
    /// - Arrays, ArrayBuffer, typed arrays
    /// - Date, RegExp, Map, Set
    ///
    /// NOT supported (throws DataCloneError):
    /// - Functions, DOM nodes, WeakMap/WeakSet, Symbols
    /// </summary>
    public static class StructuredClone
    {
        /// <summary>
        /// Clone a FenValue using the structured clone algorithm with cycle detection.
        /// This is the primary entry point for worker message passing.
        /// </summary>
        public static FenValue CloneFenValue(FenValue value)
        {
            var memory = new Dictionary<FenObject, FenValue>();
            return CloneFenValueInternal(value, memory);
        }

        /// <summary>
        /// Clone a FenValue with a transfer list per WHATWG HTML §2.7.3.
        /// Transferred objects are neutered/detached in the source and moved to the clone.
        /// Currently supports ArrayBuffer transfer (detaches source buffer).
        /// </summary>
        public static FenValue CloneFenValueWithTransfer(FenValue value, FenValue[] transferList)
        {
            var transferSet = new HashSet<FenObject>();
            if (transferList != null)
            {
                foreach (var item in transferList)
                {
                    if (!item.IsObject) continue;
                    var obj = item.AsObject() as FenObject;
                    if (obj == null) continue;

                    // Only ArrayBuffer is transferable in this implementation
                    if (obj is not JsArrayBuffer)
                        throw new StructuredCloneException($"DataCloneError: Object of type {obj.GetType().Name} is not transferable");

                    if (!transferSet.Add(obj))
                        throw new StructuredCloneException("DataCloneError: Transfer list contains duplicate objects");
                }
            }

            var memory = new Dictionary<FenObject, FenValue>();
            var result = CloneFenValueWithTransferInternal(value, memory, transferSet);

            // Detach transferred ArrayBuffers in the source
            foreach (var transferred in transferSet)
            {
                if (transferred is JsArrayBuffer srcBuf)
                {
                    srcBuf.Detach();
                }
            }

            return result;
        }

        private static FenValue CloneFenValueWithTransferInternal(FenValue v, Dictionary<FenObject, FenValue> memory, HashSet<FenObject> transferSet)
        {
            if (v.IsFunction)
                throw new StructuredCloneException("DataCloneError: Functions cannot be cloned");
            if (v.IsSymbol)
                throw new StructuredCloneException("DataCloneError: Symbols cannot be cloned");
            if (!v.IsObject) return v;

            var src = v.AsObject() as FenObject;
            if (src == null) return v;

            if (memory.TryGetValue(src, out var existing))
                return existing;

            // Transfer path: if this object is in the transfer set, move it directly
            if (transferSet.Contains(src) && src is JsArrayBuffer srcBuffer)
            {
                var transferred = new JsArrayBuffer(srcBuffer.Data.Length);
                Array.Copy(srcBuffer.Data, transferred.Data, srcBuffer.Data.Length);
                var result = FenValue.FromObject(transferred);
                memory[src] = result;
                return result;
            }

            // Fall through to normal clone for non-transferred objects
            return CloneFenValueInternal(v, memory);
        }

        private static FenValue CloneFenValueInternal(FenValue v, Dictionary<FenObject, FenValue> memory)
        {
            // Non-cloneable types
            if (v.IsFunction)
                throw new StructuredCloneException("DataCloneError: Functions cannot be cloned");
            if (v.IsSymbol)
                throw new StructuredCloneException("DataCloneError: Symbols cannot be cloned");

            // Primitives — returned by value
            if (!v.IsObject) return v;

            var src = v.AsObject() as FenObject;
            if (src == null) return v;

            // Cycle detection
            if (memory.TryGetValue(src, out var existing))
                return existing;

            // ArrayBuffer
            if (src is JsArrayBuffer srcBuffer)
            {
                var bufferClone = new JsArrayBuffer(srcBuffer.Data.Length);
                Array.Copy(srcBuffer.Data, bufferClone.Data, srcBuffer.Data.Length);
                var cloned = FenValue.FromObject(bufferClone);
                memory[src] = cloned;
                return cloned;
            }

            // Uint8Array
            if (src is JsUint8Array srcU8)
            {
                var clonedBuf = CloneFenValueInternal(FenValue.FromObject(srcU8.Buffer), memory);
                var clone = new JsUint8Array(clonedBuf,
                    FenValue.FromNumber(srcU8.ByteOffset),
                    FenValue.FromNumber(srcU8.Length));
                var cloned = FenValue.FromObject(clone);
                memory[src] = cloned;
                return cloned;
            }

            // Date objects (InternalClass == "Date")
            if (src.InternalClass == "Date")
            {
                var clone = new FenObject();
                clone.InternalClass = "Date";
                clone.SetPrototype(src.GetPrototype());
                var cloned = FenValue.FromObject(clone);
                memory[src] = cloned;
                // Copy primitive value and all properties
                foreach (var key in src.Keys())
                    clone.Set(key, CloneFenValueInternal(src.Get(key), memory));
                return cloned;
            }

            // RegExp objects (InternalClass == "RegExp")
            if (src.InternalClass == "RegExp")
            {
                var clone = new FenObject();
                clone.InternalClass = "RegExp";
                clone.SetPrototype(src.GetPrototype());
                var cloned = FenValue.FromObject(clone);
                memory[src] = cloned;
                foreach (var key in src.Keys())
                    clone.Set(key, CloneFenValueInternal(src.Get(key), memory));
                return cloned;
            }

            // Map objects (HTML §2.7.4 step 14): clone internal [[MapData]] entries
            if (src is JsMap srcMap)
            {
                var cloneMap = new JsMap(null);
                var cloned = FenValue.FromObject(cloneMap);
                memory[src] = cloned;
                foreach (var kvp in srcMap.InternalStorage)
                {
                    var clonedKey = CloneFenValueInternal(kvp.Key is FenValue fk ? fk : FenValue.FromObject((FenObject)kvp.Key), memory);
                    var clonedVal = CloneFenValueInternal(kvp.Value is FenValue fv ? fv : FenValue.FromObject((FenObject)kvp.Value), memory);
                    // Use the set method on the cloned map to properly maintain internal storage
                    var setFn = cloneMap.Get("set");
                    if (setFn.IsFunction)
                        setFn.AsFunction().NativeImplementation?.Invoke(new FenValue[] { clonedKey, clonedVal }, cloned);
                }
                return cloned;
            }

            // Set objects (HTML §2.7.4 step 15): clone internal [[SetData]] entries
            if (src is JsSet srcSet)
            {
                var cloneSet = new JsSet(null);
                var cloned = FenValue.FromObject(cloneSet);
                memory[src] = cloned;
                foreach (var entry in srcSet.InternalStorage)
                {
                    var clonedEntry = CloneFenValueInternal(entry, memory);
                    var addFn = cloneSet.Get("add");
                    if (addFn.IsFunction)
                        addFn.AsFunction().NativeImplementation?.Invoke(new FenValue[] { clonedEntry }, cloned);
                }
                return cloned;
            }

            // Error objects (HTML §2.7.4 step 21): clone name, message, stack
            if (src.InternalClass == "Error")
            {
                var clone = new FenObject();
                clone.InternalClass = "Error";
                clone.SetPrototype(src.GetPrototype());
                var cloned = FenValue.FromObject(clone);
                memory[src] = cloned;
                // Preserve standard Error properties per spec
                foreach (var prop in new[] { "name", "message", "stack" })
                {
                    var val = src.Get(prop);
                    if (val.Type != FenBrowser.FenEngine.Core.Interfaces.ValueType.Undefined)
                        clone.Set(prop, CloneFenValueInternal(val, memory));
                }
                // Also clone any user-added enumerable properties
                foreach (var key in src.Keys())
                {
                    if (key != "name" && key != "message" && key != "stack")
                        clone.Set(key, CloneFenValueInternal(src.Get(key), memory));
                }
                return cloned;
            }

            // Generic object / array
            bool isArray = src.InternalClass == "Array";
            var objClone = isArray ? FenObject.CreateArray() : new FenObject();
            objClone.InternalClass = src.InternalClass;
            objClone.SetPrototype(src.GetPrototype());
            var objCloned = FenValue.FromObject(objClone);
            memory[src] = objCloned;

            foreach (var key in src.Keys())
                objClone.Set(key, CloneFenValueInternal(src.Get(key), memory));

            return objCloned;
        }

        /// <summary>
        /// Clone a raw .NET value using structured clone algorithm.
        /// Legacy path for non-FenValue objects.
        /// </summary>
        public static object Clone(object value)
        {
            if (value  == null)
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
                throw new StructuredCloneException("DataCloneError: Functions cannot be cloned");

            if (type.FullName?.Contains("DOM") == true || type.FullName?.Contains("Element") == true)
                throw new StructuredCloneException("DataCloneError: DOM nodes cannot be cloned");

            // Last resort — reject rather than silently degrading to JSON
            throw new StructuredCloneException($"DataCloneError: Cannot clone value of type {type.Name}");
        }

        /// <summary>
        /// Check if a value can be cloned
        /// </summary>
        public static bool CanClone(object value)
        {
            if (value  == null)
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
