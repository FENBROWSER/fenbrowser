using System;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Log categories for granular control over what gets logged.
    /// Uses Flags attribute to allow bitwise combinations.
    /// </summary>
    [Flags]
    public enum LogCategory
    {
        None         = 0,
        Navigation   = 1 << 0,   // URL loading, redirects, navigation flow
        Rendering    = 1 << 1,   // DOM building, layout, visual tree creation
        CSS          = 1 << 2,   // Style parsing, computation, application
        JavaScript   = 1 << 3,   // JS engine, script execution, DOM manipulation
        Network      = 1 << 4,   // HTTP requests, responses, caching
        Images       = 1 << 5,   // Image loading, decoding, rendering
        Layout       = 1 << 6,   // Flexbox, positioning, measurement
        Events       = 1 << 7,   // User input, browser events
        Storage      = 1 << 8,   // Cookies, local storage, cache
        Performance  = 1 << 9,   // Timing, memory, profiling
        Errors       = 1 << 10,  // Exceptions, failures, warnings
        DOM          = 1 << 11,  // DOM mutations, elements
        General      = 1 << 12,  // Application lifecycle, generic info
        All          = int.MaxValue
    }

    /// <summary>
    /// Log severity levels.
    /// </summary>
    public enum LogLevel
    {
        Error = 0,  // Critical failures
        Warn = 1,   // Non-critical issues
        Info = 2,   // General information
        Debug = 3,  // Detailed debugging
        Trace = 4   // Very verbose (method entry/exit)
    }
}
