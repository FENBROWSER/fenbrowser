// =============================================================================
// Test262Config.cs
// Configuration for Test262 conformance benchmark runner
//
// PURPOSE: Centralized configuration for test discovery, execution limits,
//          memory safety thresholds, and output format selection.
// =============================================================================

namespace FenBrowser.Test262;

/// <summary>
/// Output format for test results.
/// </summary>
public enum OutputFormat
{
    Markdown,
    Json,
    Tap
}

/// <summary>
/// Configuration for the Test262 conformance runner.
/// All paths are auto-discovered relative to the repository root.
/// </summary>
public sealed class Test262Config
{
    /// <summary>
    /// Root path to the test262 repository (contains harness/, test/, etc.).
    /// Auto-discovered as {RepoRoot}/test262.
    /// </summary>
    public string Test262RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Number of tests per chunk for chunked execution.
    /// </summary>
    public int ChunkSize { get; set; } = 1000;

    /// <summary>
    /// Per-test timeout in milliseconds.
    /// </summary>
    public int PerTestTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Maximum managed heap memory in MB before skipping tests.
    /// </summary>
    public int MaxMemoryMB { get; set; } = 10000;

    /// <summary>
    /// Output format for results.
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Markdown;

    /// <summary>
    /// Path to write results file. If empty, prints to stdout.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to print verbose per-test output during execution.
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Auto-discover the test262 root path relative to the current executable.
    /// Walks up directory tree looking for the test262/ folder.
    /// </summary>
    public static Test262Config AutoDiscover()
    {
        var config = new Test262Config();

        // Walk up from the executable directory to find the repo root
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "test262");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "harness")))
            {
                config.Test262RootPath = candidate;
                break;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        if (string.IsNullOrEmpty(config.Test262RootPath))
        {
            Console.Error.WriteLine("[Test262Config] WARNING: Could not auto-discover test262 root path.");
            Console.Error.WriteLine("                Set TEST262_ROOT environment variable or pass --root <path>.");

            // Try environment variable fallback
            var envRoot = Environment.GetEnvironmentVariable("TEST262_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
            {
                config.Test262RootPath = envRoot;
            }
        }

        return config;
    }
}
