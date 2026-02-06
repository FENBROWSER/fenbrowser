// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: DOMException.
    /// https://webidl.spec.whatwg.org/#idl-DOMException
    ///
    /// Represents an abnormal event during DOM processing.
    /// </summary>
    public class DomException : Exception
    {
        /// <summary>
        /// The exception name (e.g., "HierarchyRequestError", "NotFoundError").
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The legacy error code (for backward compatibility).
        /// </summary>
        public int Code { get; }

        public DomException(string name, string message = null)
            : base(message ?? GetDefaultMessage(name))
        {
            Name = name;
            Code = GetErrorCode(name);
        }

        public DomException(string name, string message, Exception innerException)
            : base(message ?? GetDefaultMessage(name), innerException)
        {
            Name = name;
            Code = GetErrorCode(name);
        }

        private static string GetDefaultMessage(string name)
        {
            return name switch
            {
                "IndexSizeError" => "Index or size is negative or greater than the allowed amount",
                "HierarchyRequestError" => "The operation would yield an incorrect node tree",
                "WrongDocumentError" => "The object is in the wrong document",
                "InvalidCharacterError" => "The string contains invalid characters",
                "NoModificationAllowedError" => "The object cannot be modified",
                "NotFoundError" => "The object cannot be found here",
                "NotSupportedError" => "The operation is not supported",
                "InUseAttributeError" => "The attribute is in use by another element",
                "InvalidStateError" => "The object is in an invalid state",
                "SyntaxError" => "The string did not match the expected pattern",
                "InvalidModificationError" => "The object cannot be modified in this way",
                "NamespaceError" => "The operation is not allowed by namespaces in XML",
                "SecurityError" => "The operation is insecure",
                "NetworkError" => "A network error occurred",
                "AbortError" => "The operation was aborted",
                "URLMismatchError" => "The given URL does not match another URL",
                "QuotaExceededError" => "The quota has been exceeded",
                "TimeoutError" => "The operation timed out",
                "InvalidNodeTypeError" => "The supplied node is incorrect or has an incorrect ancestor for this operation",
                "DataCloneError" => "The object cannot be cloned",
                "EncodingError" => "The encoding operation failed",
                "NotReadableError" => "The I/O read operation failed",
                "UnknownError" => "The operation failed for an unknown transient reason",
                "ConstraintError" => "A mutation operation in a transaction failed because a constraint was not satisfied",
                "DataError" => "Provided data is inadequate",
                "TransactionInactiveError" => "A request was placed against a transaction which is either currently not active or is finished",
                "ReadOnlyError" => "The mutating operation was attempted in a read-only transaction",
                "VersionError" => "An attempt was made to open a database using a lower version than the existing version",
                "OperationError" => "The operation failed for an operation-specific reason",
                "NotAllowedError" => "The request is not allowed by the user agent or the platform in the current context",
                _ => $"DOM exception: {name}"
            };
        }

        private static int GetErrorCode(string name)
        {
            // Legacy error codes per DOM spec
            return name switch
            {
                "IndexSizeError" => 1,
                "HierarchyRequestError" => 3,
                "WrongDocumentError" => 4,
                "InvalidCharacterError" => 5,
                "NoModificationAllowedError" => 7,
                "NotFoundError" => 8,
                "NotSupportedError" => 9,
                "InUseAttributeError" => 10,
                "InvalidStateError" => 11,
                "SyntaxError" => 12,
                "InvalidModificationError" => 13,
                "NamespaceError" => 14,
                "SecurityError" => 18,
                "NetworkError" => 19,
                "AbortError" => 20,
                "URLMismatchError" => 21,
                "QuotaExceededError" => 22,
                "TimeoutError" => 23,
                "InvalidNodeTypeError" => 24,
                "DataCloneError" => 25,
                _ => 0 // No legacy code
            };
        }

        public override string ToString()
        {
            return $"DOMException [{Name}]: {Message}";
        }
    }

    /// <summary>
    /// Exception constants for common DOM errors.
    /// </summary>
    public static class DomExceptionNames
    {
        public const string IndexSizeError = "IndexSizeError";
        public const string HierarchyRequestError = "HierarchyRequestError";
        public const string WrongDocumentError = "WrongDocumentError";
        public const string InvalidCharacterError = "InvalidCharacterError";
        public const string NoModificationAllowedError = "NoModificationAllowedError";
        public const string NotFoundError = "NotFoundError";
        public const string NotSupportedError = "NotSupportedError";
        public const string InUseAttributeError = "InUseAttributeError";
        public const string InvalidStateError = "InvalidStateError";
        public const string SyntaxError = "SyntaxError";
        public const string InvalidModificationError = "InvalidModificationError";
        public const string NamespaceError = "NamespaceError";
        public const string SecurityError = "SecurityError";
        public const string NetworkError = "NetworkError";
        public const string AbortError = "AbortError";
        public const string URLMismatchError = "URLMismatchError";
        public const string QuotaExceededError = "QuotaExceededError";
        public const string TimeoutError = "TimeoutError";
        public const string InvalidNodeTypeError = "InvalidNodeTypeError";
        public const string DataCloneError = "DataCloneError";
        public const string EncodingError = "EncodingError";
        public const string NotReadableError = "NotReadableError";
        public const string UnknownError = "UnknownError";
        public const string ConstraintError = "ConstraintError";
        public const string DataError = "DataError";
        public const string TransactionInactiveError = "TransactionInactiveError";
        public const string ReadOnlyError = "ReadOnlyError";
        public const string VersionError = "VersionError";
        public const string OperationError = "OperationError";
        public const string NotAllowedError = "NotAllowedError";
    }
}
