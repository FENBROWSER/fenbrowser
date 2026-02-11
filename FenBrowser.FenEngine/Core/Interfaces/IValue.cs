using System;

namespace FenBrowser.FenEngine.Core.Interfaces
{
    /// <summary>
    /// Represents a JavaScript value in FenEngine.
    /// All value types implement this interface for extensibility.
    /// </summary>
    public interface IValue
    {
        /// <summary>
        /// The type of this value
        /// </summary>
        ValueType Type { get; }

        /// <summary>
        /// Convert to boolean (following JavaScript truthiness rules)
        /// </summary>
        bool ToBoolean();

        /// <summary>
        /// Convert to number (following JavaScript coercion rules)
        /// </summary>
        double ToNumber();

        /// <summary>
        /// Convert to string
        /// </summary>
        string ToString();

        /// <summary>
        /// Convert to object (boxing primitives)
        /// </summary>
        IObject ToObject();

        /// <summary>
        /// Check if this value is strictly equal to another
        /// </summary>
        bool StrictEquals(IValue other);

        /// <summary>
        /// Check if this value is loosely equal to another (type coercion allowed)
        /// </summary>
        bool LooseEquals(IValue other);

        // Type checking properties
        bool IsUndefined { get; }
        bool IsNull { get; }
        bool IsBoolean { get; }
        bool IsNumber { get; }
        bool IsString { get; }
        bool IsObject { get; }
        bool IsFunction { get; }

        /// <summary>
        /// Get the function value if this is a function, otherwise null
        /// </summary>
        Core.FenFunction AsFunction();

        /// <summary>
        /// Get the object value if this is an object, otherwise null
        /// </summary>
        IObject AsObject();
    }

    /// <summary>
    /// JavaScript value types
    /// </summary>
    public enum ValueType
    {
        Undefined,
        Null,
        Boolean,
        Number,
        String,
        Object,
        Function,
        Symbol,    // Future
        BigInt,    // Future
        ReturnValue, // Internal
        Error,       // Internal
        Break,       // Internal
        Continue,    // Internal
        Throw,       // Internal (throw value)
        Yield,        // Internal
        YieldDelegate // Internal (yield* delegation)
    }
}
