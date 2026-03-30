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
        HtmlParsing  = 1 << 13,  // HTML element parsing, unsupported tags
        CssParsing   = 1 << 14,  // CSS property parsing, unsupported properties
        JsExecution  = 1 << 15,  // JS execution details, API calls, failures
        FeatureGaps  = 1 << 16,  // Unsupported features summary reporting
        ServiceWorker= 1 << 17,  // ServiceWorker lifecycle and fetch events
        WebDriver    = 1 << 18,  // WebDriver server and commands
        Cascade      = 1 << 19,  // CSS selector matching and cascade resolution
        ComputedStyle= 1 << 20,  // Final style calculation per element
        Text         = 1 << 21,  // Font selection, glyph shaping, measurement
        Paint        = 1 << 22,  // Paint commands, clipping, z-index
        Frame        = 1 << 23,  // Frame timing, orchestration, render loop
        Verification = 1 << 24,  // Verification system logs (Source vs Rendered vs Visual)
        Security     = 1 << 25,  // Policy decisions, denials, sandbox posture
        Accessibility= 1 << 26,  // Accessibility trees, platform bridge events
        ProcessIsolation = 1 << 27, // Child process contracts, sandbox lifecycle, IPC boundaries
        DevTools     = 1 << 28,  // Remote debugging, protocol traffic, instrumentation
        All          = int.MaxValue
    }

    public static class LogCategoryFacts
    {
        public const LogCategory DefaultOperationalCategories =
            LogCategory.Navigation |
            LogCategory.Rendering |
            LogCategory.Network |
            LogCategory.Errors |
            LogCategory.General |
            LogCategory.Security |
            LogCategory.ProcessIsolation |
            LogCategory.Verification;

        public static bool Includes(this LogCategory source, LogCategory category)
        {
            return category != LogCategory.None && (source & category) == category;
        }

        public static bool IsSingleCategory(this LogCategory category)
        {
            int value = (int)category;
            return value > 0 && (value & (value - 1)) == 0;
        }
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
