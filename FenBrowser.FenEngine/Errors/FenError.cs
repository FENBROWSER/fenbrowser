using System;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Errors
{
    /// <summary>
    /// Base class for all FenEngine errors.
    /// Prevents information leakage and provides type-safe error handling.
    /// </summary>
    public abstract class FenError : Exception
    {
        private FenValue? _thrownValue;

        protected FenError(string message) : base(message) { }
        protected FenError(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Error type for classification
        /// </summary>
        public abstract ErrorType Type { get; }

        /// <summary>
        /// Safe error message (no internal implementation details exposed)
        /// </summary>
        public virtual string SafeMessage => Message;

        /// <summary>
        /// Materialized JS error object for VM/native throw propagation.
        /// </summary>
        public FenValue ThrownValue
        {
            get
            {
                if (_thrownValue.HasValue)
                {
                    return _thrownValue.Value;
                }

                var runtime = FenRuntime.GetActiveRuntime();
                _thrownValue = runtime != null
                    ? runtime.CreateThrownErrorValue(Type, Message)
                    : FenValue.FromError(Message ?? "Error");
                return _thrownValue.Value;
            }
        }
    }

    public enum ErrorType
    {
        Type,
        Range,
        Reference,
        Syntax,
        Security,
        Resource,
        Timeout,
        Internal
    }

    /// <summary>
    /// Type errors (e.g., calling non-function)
    /// </summary>
    public class FenTypeError : FenError
    {
        public FenTypeError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Type;
    }

    /// <summary>
    /// Range errors (e.g., array index out of bounds)
    /// </summary>
    public class FenRangeError : FenError
    {
        public FenRangeError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Range;
    }

    /// <summary>
    /// Reference errors (e.g., undefined variable)
    /// </summary>
    public class FenReferenceError : FenError
    {
        public FenReferenceError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Reference;
    }

    /// <summary>
    /// Syntax errors (e.g., invalid JavaScript)
    /// </summary>
    public class FenSyntaxError : FenError
    {
        public FenSyntaxError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Syntax;
    }

    /// <summary>
    /// Security violations (e.g., permission denied)
    /// </summary>
    public class FenSecurityError : FenError
    {
        public FenSecurityError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Security;
        
        // Never expose implementation details in security errors
        public override string SafeMessage => "Permission denied";
    }

    /// <summary>
    /// Resource limit violations (e.g., stack overflow)
    /// </summary>
    public class FenResourceError : FenError
    {
        public FenResourceError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Resource;
    }

    /// <summary>
    /// Execution timeout
    /// </summary>
    public class FenTimeoutError : FenError
    {
        public FenTimeoutError(string message) : base(message) { }
        public override ErrorType Type => ErrorType.Timeout;
    }

    /// <summary>
    /// Internal engine errors (should never be exposed to JavaScript)
    /// </summary>
    public class FenInternalError : FenError
    {
        public FenInternalError(string message, Exception innerException = null) 
            : base(message, innerException) { }
        public override ErrorType Type => ErrorType.Internal;
        public override string SafeMessage => "Internal error";
    }
}
