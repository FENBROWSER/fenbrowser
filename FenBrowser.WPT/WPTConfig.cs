// =============================================================================
// WPTConfig.cs
// Configuration for Web Platform Tests conformance runner
//
// PURPOSE: Centralized configuration for WPT test discovery, execution,
//          timeouts, and output format selection.
// =============================================================================

namespace FenBrowser.WPT;

/// <summary>
/// Output format for WPT test results.
/// </summary>
public enum OutputFormat
{
    Markdown,
    Json,
    Tap
}

/// <summary>
/// Configuration for the WPT conformance runner.
/// </summary>
public sealed class WPTConfig
{
    /// <summary>
    /// Root path to the WPT repository (contains sub-directories like dom/, css/, html/, etc.).
    /// </summary>
    public string WptRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Per-test timeout in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 1_000;

    /// <summary>
    /// Maximum tests to run per category (0 = unlimited).
    /// </summary>
    public int MaxTestsPerCategory { get; set; } = 0;

    /// <summary>
    /// Output format for results.
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Markdown;

    /// <summary>
    /// Path to write results file. If empty, prints to stdout.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to print verbose per-test output.
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Number of tests per chunk (for run_chunk command).
    /// </summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>
    /// Auto-discover the WPT root path relative to the current executable.
    /// Walks up directory tree looking for the wpt/ folder.
    /// </summary>
    public static WPTConfig AutoDiscover()
    {
        var config = new WPTConfig();

        // Walk up from the executable directory to find the repo root
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            // Look for wpt/ directory
            var candidate = Path.Combine(dir, "wpt");
            if (Directory.Exists(candidate))
            {
                // Verify it looks like a WPT checkout (has resources/ or dom/ or css/)
                if (Directory.Exists(Path.Combine(candidate, "dom")) ||
                    Directory.Exists(Path.Combine(candidate, "css")) ||
                    Directory.Exists(Path.Combine(candidate, "resources")))
                {
                    config.WptRootPath = candidate;
                    break;
                }
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            // Try environment variable fallback
            var envRoot = Environment.GetEnvironmentVariable("WPT_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
            {
                config.WptRootPath = envRoot;
            }
            else
            {
                Console.Error.WriteLine("[WPTConfig] WARNING: Could not auto-discover WPT root path.");
                Console.Error.WriteLine("             Set WPT_ROOT environment variable or pass --root <path>.");
            }
        }

        return config;
    }
}
