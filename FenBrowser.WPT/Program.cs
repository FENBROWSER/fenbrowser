// =============================================================================
// Program.cs
// FenBrowser WPT Conformance CLI
//
// PURPOSE: Standalone CLI for running Web Platform Tests against
//          FenBrowser's rendering and JS engine with multi-format reporting.
//
// USAGE:
//   FenBrowser.WPT run_category <name> [--root <path>] [--format md|json|tap]
//   FenBrowser.WPT run_single <path> [--root <path>]
//   FenBrowser.WPT discover [category] [--root <path>]
// =============================================================================

using System.Diagnostics;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.WPT;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var config = WPTConfig.AutoDiscover();

        // Parse global flags
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length:
                    config.WptRootPath = args[++i];
                    break;
                case "--format" when i + 1 < args.Length:
                    config.Format = args[++i].ToLowerInvariant() switch
                    {
                        "json" => OutputFormat.Json,
                        "tap" => OutputFormat.Tap,
                        _ => OutputFormat.Markdown
                    };
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    config.OutputPath = args[++i];
                    break;
                case "--timeout" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int timeout))
                        config.TimeoutMs = timeout;
                    break;
                case "--max" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int max))
                        config.MaxTestsPerCategory = max;
                    break;
                case "--verbose" or "-v":
                    config.Verbose = true;
                    break;
            }
        }

        // Auto-default: save JSON results to Results/wpt_results.json
        if (string.IsNullOrEmpty(config.OutputPath))
        {
            var repoRoot = Path.GetDirectoryName(config.WptRootPath ?? Directory.GetCurrentDirectory())
                           ?? Directory.GetCurrentDirectory();
            config.OutputPath = Path.Combine(repoRoot, "Results", "wpt_results.json");
            config.Format = OutputFormat.Json;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "run_category" => await RunCategoryAsync(config, args),
                "run_single" => await RunSingleAsync(config, args),
                "discover" => RunDiscover(config, args),
                "--help" or "-h" or "help" => PrintUsage(),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            if (config.Verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // =========================================================================
    // Commands
    // =========================================================================

    /// <summary>
    /// Run all tests in a specific WPT category.
    /// </summary>
    private static async Task<int> RunCategoryAsync(WPTConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_category <name> [--max <N>]");
            return 1;
        }

        string category = args[1];

        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            Console.Error.WriteLine("[FATAL] WPT root path not found. Use --root <path> or set WPT_ROOT.");
            return 1;
        }

        var navigator = new HeadlessNavigator(config.WptRootPath, config.TimeoutMs);
        var runner = new WPTTestRunner(config.WptRootPath, navigator.GetNavigatorDelegate(), config.TimeoutMs);

        int maxTests = config.MaxTestsPerCategory > 0 ? config.MaxTestsPerCategory : int.MaxValue;

        Console.WriteLine($"[WPT] Running category: {category}");
        var sw = Stopwatch.StartNew();

        var results = await runner.RunCategoryAsync(category, (name, count) =>
        {
            if (config.Verbose || count % 10 == 0)
                Console.Write($"\r  [{count}] {name}    ");
        }, maxTests);

        sw.Stop();
        Console.WriteLine();

        // Print summary
        Console.WriteLine(runner.GenerateSummary());

        // Export
        var output = ResultsExporter.Export(results, config.Format, category, sw.Elapsed);
        WriteOutput(output, config);

        return results.Any(r => !r.Success) ? 1 : 0;
    }

    /// <summary>
    /// Run a single WPT test file.
    /// </summary>
    private static async Task<int> RunSingleAsync(WPTConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_single <path>");
            return 1;
        }

        string testPath = args[1];
        if (!Path.IsPathRooted(testPath) && !string.IsNullOrEmpty(config.WptRootPath))
        {
            testPath = Path.Combine(config.WptRootPath, testPath);
        }

        if (!File.Exists(testPath))
        {
            Console.Error.WriteLine($"[ERROR] Test file not found: {testPath}");
            return 1;
        }

        var navigator = new HeadlessNavigator(config.WptRootPath, config.TimeoutMs);
        var runner = new WPTTestRunner(
            config.WptRootPath ?? Path.GetDirectoryName(testPath) ?? ".",
            navigator.GetNavigatorDelegate(),
            config.TimeoutMs);

        Console.WriteLine($"[WPT] Running: {Path.GetFileName(testPath)}");
        var sw = Stopwatch.StartNew();
        var result = await runner.RunSingleTestAsync(testPath, config.Verbose);
        sw.Stop();

        Console.WriteLine($"Test:     {Path.GetFileName(testPath)}");
        Console.WriteLine($"Result:   {(result.Success ? "PASS" : "FAIL")}");
        Console.WriteLine($"Signal:   {result.CompletionSignal}");
        Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds:F1}ms");
        Console.WriteLine($"Asserts:  {result.TotalCount} (Pass: {result.PassCount}, Fail: {result.FailCount})");

        if (!string.IsNullOrEmpty(result.Error))
            Console.WriteLine($"Error:    {result.Error}");

        if (!string.IsNullOrEmpty(result.Output) && config.Verbose)
        {
            Console.WriteLine();
            Console.WriteLine("=== Console Output ===");
            Console.WriteLine(result.Output);
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>
    /// Discover available WPT test categories and test files.
    /// </summary>
    private static int RunDiscover(WPTConfig config, string[] args)
    {
        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            Console.Error.WriteLine("[FATAL] WPT root path not found. Use --root <path> or set WPT_ROOT.");
            return 1;
        }

        string? category = args.Length >= 2 ? args[1] : null;

        if (string.IsNullOrEmpty(category))
        {
            // List top-level categories
            Console.WriteLine($"WPT Root: {config.WptRootPath}");
            Console.WriteLine();

            var dirs = Directory.GetDirectories(config.WptRootPath)
                .Select(d => new DirectoryInfo(d))
                .Where(d => !d.Name.StartsWith(".") && !d.Name.StartsWith("_"))
                .OrderBy(d => d.Name)
                .ToList();

            Console.WriteLine($"Categories ({dirs.Count}):");
            Console.WriteLine($"  {"Category",-30} {"HTML Tests",12} {"HTM Tests",12}");
            Console.WriteLine($"  {new string('-', 30),-30} {new string('-', 12),12} {new string('-', 12),12}");

            foreach (var dir in dirs)
            {
                int htmlCount = 0, htmCount = 0;
                try
                {
                    htmlCount = Directory.GetFiles(dir.FullName, "*.html", SearchOption.AllDirectories)
                        .Count(f => !Path.GetFileName(f).StartsWith("_"));
                    htmCount = Directory.GetFiles(dir.FullName, "*.htm", SearchOption.AllDirectories)
                        .Count(f => !Path.GetFileName(f).StartsWith("_"));
                }
                catch
                {
                    /* Ignore access errors */
                }

                if (htmlCount + htmCount > 0)
                    Console.WriteLine($"  {dir.Name,-30} {htmlCount,12} {htmCount,12}");
            }
        }
        else
        {
            // List tests in a specific category
            var catPath = Path.Combine(config.WptRootPath, category);
            if (!Directory.Exists(catPath))
            {
                Console.Error.WriteLine($"Category not found: {category}");
                return 1;
            }

            var tests = new List<string>();
            tests.AddRange(Directory.GetFiles(catPath, "*.html", SearchOption.AllDirectories));
            tests.AddRange(Directory.GetFiles(catPath, "*.htm", SearchOption.AllDirectories));
            tests = tests.Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f).ToList();

            Console.WriteLine($"Category: {category}");
            Console.WriteLine($"Tests: {tests.Count}");
            Console.WriteLine();

            foreach (var test in tests.Take(100))
            {
                var rel = Path.GetRelativePath(config.WptRootPath, test);
                Console.WriteLine($"  {rel}");
            }

            if (tests.Count > 100)
                Console.WriteLine($"  ... and {tests.Count - 100} more");
        }

        return 0;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static void WriteOutput(string content, WPTConfig config)
    {
        if (!string.IsNullOrEmpty(config.OutputPath))
        {
            var dir = Path.GetDirectoryName(config.OutputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Clear content first — each run overwrites the previous results
            File.WriteAllText(config.OutputPath, content);
            Console.WriteLine($"[WPT] Results saved to {config.OutputPath}");
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
FenBrowser WPT (Web Platform Tests) Conformance CLI
====================================================

USAGE:
  FenBrowser.WPT <command> [options]

COMMANDS:
  run_category <name>          Run tests in a category (e.g., dom, css, html)
  run_single <path>            Run a single .html test file
  discover [category]          List categories or tests in a category
  help                         Show this help message

OPTIONS:
  --root <path>                Path to WPT root directory
  --format md|json|tap         Output format (default: md)
  --output|-o <path>           Write results to file
  --timeout <ms>               Per-test timeout (default: 30000)
  --max <N>                    Max tests per category
  --verbose|-v                 Verbose output

ENVIRONMENT:
  WPT_ROOT                     Alternative to --root flag

EXAMPLES:
  FenBrowser.WPT discover
  FenBrowser.WPT discover dom
  FenBrowser.WPT run_category dom --max 50 --format json
  FenBrowser.WPT run_single dom/nodes/Document-createElement.html
");
        return 0;
    }
}
