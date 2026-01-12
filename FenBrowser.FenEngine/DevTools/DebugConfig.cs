using System;

namespace FenBrowser.FenEngine.DevTools
{
    /// <summary>
    /// Centralized debug configuration.
    /// Single source of truth for all debug/visualization settings.
    /// 
    /// Design: Zero-overhead when disabled - callers check flags before doing work.
    /// </summary>
    public sealed class DebugConfig
    {
        private static readonly Lazy<DebugConfig> _instance = new Lazy<DebugConfig>(() => new DebugConfig());
        
        /// <summary>
        /// Global singleton instance.
        /// </summary>
        public static DebugConfig Instance => _instance.Value;
        
        private DebugConfig() { }

        // --- Layout Visualization ---
        
        /// <summary>
        /// Show colored borders around layout boxes (margin, border, padding, content).
        /// </summary>
        public bool ShowLayoutBoxes { get; set; } = false;
        
        /// <summary>
        /// Show margin box outline (outer).
        /// </summary>
        public bool ShowMarginBox { get; set; } = false;
        
        /// <summary>
        /// Show padding box outline.
        /// </summary>
        public bool ShowPaddingBox { get; set; } = false;
        
        /// <summary>
        /// Show content box outline (inner).
        /// </summary>
        public bool ShowContentBox { get; set; } = false;

        // --- Dirty Region Visualization ---
        
        /// <summary>
        /// Highlight nodes marked as dirty.
        /// Red = Style, Yellow = Layout, Blue = Paint.
        /// </summary>
        public bool ShowDirtyRegions { get; set; } = false;

        // --- Overflow Visualization ---
        
        /// <summary>
        /// Show clipping boundaries with dashed lines.
        /// </summary>
        public bool ShowOverflow { get; set; } = false;

        // --- HitTest Visualization ---
        
        /// <summary>
        /// Highlight the element currently under the cursor.
        /// </summary>
        public bool ShowHitTestTarget { get; set; } = false;
        
        /// <summary>
        /// Current HitTest target (set by input handler).
        /// </summary>
        public object CurrentHitTarget { get; set; } = null;

        // --- Logging ---
        
        /// <summary>
        /// Master switch for debug logging. When false, all logging is skipped.
        /// </summary>
        public bool DebugLoggingEnabled { get; set; } = 
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>
        /// Minimum log level to emit.
        /// </summary>
        public LogLevel MinimumLogLevel { get; set; } = LogLevel.Info;

        // --- Convenience ---
        
        /// <summary>
        /// Returns true if ANY visual debug overlay is enabled.
        /// </summary>
        public bool AnyOverlayEnabled =>
            ShowLayoutBoxes || ShowMarginBox || ShowPaddingBox || ShowContentBox ||
            ShowDirtyRegions || ShowOverflow || ShowHitTestTarget;

        /// <summary>
        /// Enable all box visualization.
        /// </summary>
        public void EnableAllBoxes()
        {
            ShowLayoutBoxes = true;
            ShowMarginBox = true;
            ShowPaddingBox = true;
            ShowContentBox = true;
        }

        /// <summary>
        /// Disable all visualizations.
        /// </summary>
        public void DisableAll()
        {
            ShowLayoutBoxes = false;
            ShowMarginBox = false;
            ShowPaddingBox = false;
            ShowContentBox = false;
            ShowDirtyRegions = false;
            ShowOverflow = false;
            ShowHitTestTarget = false;
        }
    }

    /// <summary>
    /// Log levels for debug output.
    /// </summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5
    }
}
