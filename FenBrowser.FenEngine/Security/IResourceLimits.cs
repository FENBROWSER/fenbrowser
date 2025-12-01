using System;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// Resource limits to prevent DoS attacks and unbounded resource consumption.
    /// All limits enforced at runtime.
    /// </summary>
    public interface IResourceLimits
    {
        // Execution limits
        int MaxCallStackDepth { get; }
        TimeSpan MaxExecutionTime { get; }
        
        // Memory limits
        long MaxTotalMemory { get; }
        int MaxStringLength { get; }
        int MaxArrayLength { get; }
        int MaxObjectProperties { get; }
        
        // Property access limits
        int MaxPropertyChainDepth { get; }
        
        /// <summary>
        /// Check if call stack depth is within limits
        /// </summary>
        bool CheckCallStack(int currentDepth);

        /// <summary>
        /// Check if execution time is within limits
        /// </summary>
        bool CheckExecutionTime(TimeSpan elapsed);

        /// <summary>
        /// Check if memory allocation is within limits
        /// </summary>
        bool CheckMemory(long bytes);

        /// <summary>
        /// Check if string length is within limits
        /// </summary>
        bool CheckString(int length);

        /// <summary>
        /// Check if array length is within limits
        /// </summary>
        bool CheckArray(int length);

        /// <summary>
        /// Check if object property count is within limits
        /// </summary>
        bool CheckObjectProperties(int count);
    }

    /// <summary>
    /// Default resource limits for web scripts.
    /// Conservative limits to prevent abuse.
    /// </summary>
    public class DefaultResourceLimits : IResourceLimits
    {
        public int MaxCallStackDepth => 100;
        public TimeSpan MaxExecutionTime => TimeSpan.FromSeconds(5);
        public long MaxTotalMemory => 50 * 1024 * 1024; // 50MB
        public int MaxStringLength => 1_000_000; // 1MB strings
        public int MaxArrayLength => 100_000;
        public int MaxObjectProperties => 10_000;
        public int MaxPropertyChainDepth => 100;

        public bool CheckCallStack(int currentDepth) 
            => currentDepth < MaxCallStackDepth;

        public bool CheckExecutionTime(TimeSpan elapsed) 
            => elapsed < MaxExecutionTime;

        public bool CheckMemory(long bytes) 
            => bytes < MaxTotalMemory;

        public bool CheckString(int length) 
            => length < MaxStringLength;

        public bool CheckArray(int length) 
            => length < MaxArrayLength;

        public bool CheckObjectProperties(int count) 
            => count < MaxObjectProperties;
    }

    /// <summary>
    /// Stricter limits for untrusted/sandboxed scripts
    /// </summary>
    public class SandboxedResourceLimits : IResourceLimits
    {
        public int MaxCallStackDepth => 50;
        public TimeSpan MaxExecutionTime => TimeSpan.FromSeconds(1);
        public long MaxTotalMemory => 10 * 1024 * 1024; // 10MB
        public int MaxStringLength => 100_000;
        public int MaxArrayLength => 10_000;
        public int MaxObjectProperties => 1_000;
        public int MaxPropertyChainDepth => 20;

        public bool CheckCallStack(int currentDepth) 
            => currentDepth < MaxCallStackDepth;

        public bool CheckExecutionTime(TimeSpan elapsed) 
            => elapsed < MaxExecutionTime;

        public bool CheckMemory(long bytes) 
            => bytes < MaxTotalMemory;

        public bool CheckString(int length) 
            => length < MaxStringLength;

        public bool CheckArray(int length) 
            => length < MaxArrayLength;

        public bool CheckObjectProperties(int count) 
            => count < MaxObjectProperties;
    }
}
