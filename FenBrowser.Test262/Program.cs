// =============================================================================
// Program.cs
// FenBrowser Test262 Conformance Benchmark CLI
//
// PURPOSE: Standalone CLI for running ECMAScript Test262 tests against
//          FenBrowser's JavaScript engine with chunked execution,
//          memory safety guards, and multi-format reporting.
//
// USAGE:
//   FenBrowser.Test262 get_chunk_count [--root <path>]
//   FenBrowser.Test262 run_chunk <N> [--root <path>] [--format md|json|tap] [--isolate-process]
//   FenBrowser.Test262 run_category <name> [--max <N>] [--root <path>]
//   FenBrowser.Test262 run_single <path> [--root <path>]
//   FenBrowser.Test262 summary [--root <path>]
// =============================================================================

using System.Diagnostics;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.Test262;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var config = Test262Config.AutoDiscover();

        // Parse global flags
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length:
                    config.Test262RootPath = args[++i];
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
                        config.PerTestTimeoutMs = timeout;
                    break;
                case "--chunk-size" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int cs))
                        config.ChunkSize = cs;
                    break;
                case "--max-memory-mb" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int maxMemoryMb) && maxMemoryMb > 0)
                        config.MaxMemoryMB = maxMemoryMb;
                    break;
                case "--verbose" or "-v":
                    config.Verbose = true;
                    break;
                case "--isolate-process":
                    config.IsolateProcess = true;
                    break;
            }
        }

        if (string.IsNullOrEmpty(config.Test262RootPath))
        {
            Console.Error.WriteLine("[FATAL] test262 root path not found. Use --root <path> or set TEST262_ROOT.");
            return 1;
        }

        // Auto-default: save JSON results to Results/test262_results.json
        if (string.IsNullOrEmpty(config.OutputPath))
        {
            var repoRoot = Path.GetDirectoryName(config.Test262RootPath)
                           ?? Directory.GetCurrentDirectory();
            config.OutputPath = Path.Combine(repoRoot, "Results", "test262_results.json");
            config.Format = OutputFormat.Json;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "get_chunk_count" => RunGetChunkCount(config),
                "run_chunk" => await RunChunkAsync(config, args),
                "run_category" => await RunCategoryAsync(config, args),
                "run_single" => await RunSingleAsync(config, args),
                "summary" => RunSummary(config),
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
    /// Discover total test count and print the number of chunks.
    /// </summary>
    private static int RunGetChunkCount(Test262Config config)
    {
        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        var tests = runner.DiscoverTests();
        int chunkCount = (int)Math.Ceiling((double)tests.Count / config.ChunkSize);

        Console.WriteLine($"Total tests: {tests.Count}");
        Console.WriteLine($"Chunk size:  {config.ChunkSize}");
        Console.WriteLine($"Chunks:      {chunkCount}");

        return 0;
    }

    /// <summary>
    /// Run a specific chunk of tests with memory safety guards.
    /// </summary>
    private static async Task<int> RunChunkAsync(Test262Config config, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int chunkNumber) || chunkNumber < 1)
        {
            Console.Error.WriteLine("Usage: run_chunk <N> where N >= 1");
            return 1;
        }

        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        runner.MemoryThresholdBytes = config.MaxMemoryMB * 1_000_000L;

        // Discover and slice
        var allTests = runner.DiscoverTests();
        int skip = (chunkNumber - 1) * config.ChunkSize;
        int take = config.ChunkSize;

        if (skip >= allTests.Count)
        {
            Console.Error.WriteLine(
                $"[ERROR] Chunk {chunkNumber} exceeds total tests ({allTests.Count}). Max chunk: {(int)Math.Ceiling((double)allTests.Count / config.ChunkSize)}");
            return 1;
        }

        var chunkTests = allTests.Skip(skip).Take(take).ToList();
        Console.WriteLine(
            $"[Test262] Chunk {chunkNumber}: tests {skip + 1}-{skip + chunkTests.Count} of {allTests.Count}");

        // Memory check before starting
        long freeMemKB = GetFreeMemoryKB();
        if (freeMemKB > 0 && freeMemKB < 10_000_000) // <10GB free
        {
            Console.Error.WriteLine(
                $"[WARNING] Low system memory: {freeMemKB / 1_048_576.0:F1}GB free. Consider waiting.");
        }

        var sw = Stopwatch.StartNew();
        int completed = 0;

        IReadOnlyList<Test262Runner.TestResult> results;
        if (config.IsolateProcess)
        {
            results = await RunChunkIsolatedAsync(config, chunkTests, (name, count) =>
            {
                completed = count;
                if (config.Verbose || count % 100 == 0)
                    Console.Write($"\r  [{count}/{chunkTests.Count}] {name}    ");
            });
        }
        else
        {
            results = await runner.RunSpecificTestsAsync(chunkTests, (name, count) =>
            {
                completed = count;
                if (config.Verbose || count % 100 == 0)
                {
                    long mem = GC.GetTotalMemory(false) / 1_000_000;
                    Console.Write($"\r  [{count}/{chunkTests.Count}] {name} ({mem}MB)    ");
                }
            });
        }

        sw.Stop();
        Console.WriteLine();

        // Output results
        int passed = results.Count(r => r.Passed);
        int failed = results.Count(r => !r.Passed);
        double passRate = results.Count > 0 ? (double)passed / results.Count * 100 : 0;

        Console.WriteLine($"{(long)sw.Elapsed.TotalMilliseconds}ms | Pass: {passed} | Fail: {failed} ({passRate:F1}%)");

        // Export
        var output = ResultsExporter.Export(results, config.Format, chunkNumber, sw.Elapsed);
        WriteOutput(output, config);

        return failed > 0 ? 1 : 0;
    }
    private static async Task<IReadOnlyList<Test262Runner.TestResult>> RunChunkIsolatedAsync(
        Test262Config config,
        IReadOnlyList<string> chunkTests,
        Action<string, int>? onProgress)
    {
        var results = new List<Test262Runner.TestResult>(chunkTests.Count);
        string runnerDll = typeof(Program).Assembly.Location;

        for (int i = 0; i < chunkTests.Count; i++)
        {
            string testFile = chunkTests[i];
            var result = await RunSingleIsolatedAsync(config, runnerDll, testFile);
            results.Add(result);
            onProgress?.Invoke(Path.GetFileName(testFile), i + 1);
        }

        return results;
    }

    private static async Task<Test262Runner.TestResult> RunSingleIsolatedAsync(
        Test262Config config,
        string runnerDll,
        string testFile)
    {
        var result = new Test262Runner.TestResult
        {
            TestFile = testFile,
            Passed = false,
            Expected = "Success",
            Actual = "Error",
            Error = "Isolated child process did not return a result"
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{runnerDll}\" run_single \"{testFile}\" --root \"{config.Test262RootPath}\" --timeout {config.PerTestTimeoutMs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => proc.WaitForExit(config.PerTestTimeoutMs + 5000));
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                result.Error = $"Isolated child timeout after {config.PerTestTimeoutMs + 5000}ms";
                return result;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (stdout.Contains("Result:   PASS", StringComparison.OrdinalIgnoreCase))
            {
                result.Passed = true;
                result.Expected = "Success";
                result.Actual = "Success";
                result.Error = string.Empty;
            }
            else if (stdout.Contains("Result:   FAIL", StringComparison.OrdinalIgnoreCase))
            {
                result.Passed = false;
                var lines = stdout.Split('\n');
                var expectedLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Expected:", StringComparison.OrdinalIgnoreCase));
                var actualLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Actual:", StringComparison.OrdinalIgnoreCase));
                var errorLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase));

                result.Expected = expectedLine is null ? "Success" : expectedLine.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "Success";
                result.Actual = actualLine is null ? "Error" : actualLine.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "Error";
                result.Error = errorLine is null ? (string.IsNullOrWhiteSpace(stderr) ? "Unknown failure in isolated child process" : stderr.Trim()) : errorLine.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "Unknown failure in isolated child process";
            }
            else
            {
                result.Passed = false;
                result.Error = !string.IsNullOrWhiteSpace(stderr)
                    ? $"Child process exited with code {proc.ExitCode}: {stderr.Trim()}"
                    : $"Child process exited with code {proc.ExitCode} without Test262 result output";
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Error = $"Isolated child process exception: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <summary>
    /// Run all tests in a specific category.
    /// </summary>
    private static async Task<int> RunCategoryAsync(Test262Config config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_category <name> [--max <N>]");
            return 1;
        }

        string category = args[1];
        int maxTests = int.MaxValue;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--max" && i + 1 < args.Length && int.TryParse(args[i + 1], out int m))
            {
                maxTests = m;
                i++;
            }
        }

        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        runner.MemoryThresholdBytes = config.MaxMemoryMB * 1_000_000L;

        Console.WriteLine($"[Test262] Running category: {category}");
        var sw = Stopwatch.StartNew();

        var results = await runner.RunCategoryAsync(category, (name, count) =>
        {
            if (config.Verbose || count % 50 == 0)
                Console.Write($"\r  [{count}] {name}    ");
        }, maxTests);

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine(runner.GenerateSummary());

        var output = ResultsExporter.Export(results, config.Format, totalDuration: sw.Elapsed);
        WriteOutput(output, config);

        return results.Any(r => !r.Passed) ? 1 : 0;
    }

    /// <summary>
    /// Run a single test file.
    /// </summary>
    private static async Task<int> RunSingleAsync(Test262Config config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_single <path>");
            return 1;
        }

        string testPath = args[1];
        if (!Path.IsPathRooted(testPath))
        {
            testPath = Path.Combine(config.Test262RootPath, "test", testPath);
        }

        if (!File.Exists(testPath))
        {
            Console.Error.WriteLine($"[ERROR] Test file not found: {testPath}");
            return 1;
        }

        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        runner.MemoryThresholdBytes = config.MaxMemoryMB * 1_000_000L;
        var sw = Stopwatch.StartNew();
        var result = await runner.RunSingleTestAsync(testPath);
        sw.Stop();

        Console.WriteLine($"Test:     {Path.GetFileName(testPath)}");
        Console.WriteLine($"Result:   {(result.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds:F1}ms");

        if (!string.IsNullOrEmpty(result.Expected))
            Console.WriteLine($"Expected: {result.Expected}");
        if (!string.IsNullOrEmpty(result.Actual))
            Console.WriteLine($"Actual:   {result.Actual}");
        if (!string.IsNullOrEmpty(result.Error))
            Console.WriteLine($"Error:    {result.Error}");
        if (result.Metadata?.Features.Count > 0)
            Console.WriteLine($"Features: {string.Join(", ", result.Metadata.Features)}");

        return result.Passed ? 0 : 1;
    }

    /// <summary>
    /// Print summary info about the test262 suite.
    /// </summary>
    private static int RunSummary(Test262Config config)
    {
        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        var tests = runner.DiscoverTests();

        // Categorize
        var categories = tests
            .GroupBy(t =>
            {
                var rel = Path.GetRelativePath(Path.Combine(config.Test262RootPath, "test"), t);
                var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts[0];
            })
            .OrderByDescending(g => g.Count())
            .ToList();

        Console.WriteLine($"Test262 Suite Summary");
        Console.WriteLine($"Root: {config.Test262RootPath}");
        Console.WriteLine($"Total tests: {tests.Count}");
        Console.WriteLine($"Categories: {categories.Count}");
        Console.WriteLine();
        Console.WriteLine("Top categories:");
        Console.WriteLine($"  {"Category",-45} {"Count",8}");
        Console.WriteLine($"  {new string('-', 45),-45} {new string('-', 8),8}");

        foreach (var cat in categories.Take(25))
        {
            Console.WriteLine($"  {cat.Key,-45} {cat.Count(),8}");
        }

        if (categories.Count > 25)
            Console.WriteLine($"  ... and {categories.Count - 25} more categories");

        return 0;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static void WriteOutput(string content, Test262Config config)
    {
        if (!string.IsNullOrEmpty(config.OutputPath))
        {
            var dir = Path.GetDirectoryName(config.OutputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Clear content first — each run overwrites the previous results
            File.WriteAllText(config.OutputPath, content);
            Console.WriteLine($"[Test262] Results saved to {config.OutputPath}");
        }
    }

    /// <summary>
    /// Get free physical memory in KB. Returns 0 if unavailable.
    /// </summary>
    private static long GetFreeMemoryKB()
    {
        try
        {
            // Windows-specific: PerformanceCounter or WMI
            if (OperatingSystem.IsWindows())
            {
                var output = RunProcess("powershell",
                    "-Command \"(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory\"");
                if (long.TryParse(output?.Trim(), out long kb))
                    return kb;
            }
        }
        catch
        {
            /* Ignore — not critical */
        }

        return 0;
    }

    private static string? RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var result = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
FenBrowser Test262 Conformance Benchmark
========================================

USAGE:
  FenBrowser.Test262 <command> [options]

COMMANDS:
  get_chunk_count              Show total test count and chunk count
  run_chunk <N>                Run chunk N (1-indexed, 1000 tests per chunk)
  run_category <name>          Run a category (e.g., language/expressions)
  run_single <path>            Run a single .js test file
  summary                      Show test suite summary with categories
  help                         Show this help message

OPTIONS:
  --root <path>                Path to test262 root directory
  --format md|json|tap         Output format (default: md)
  --output|-o <path>           Write results to file
  --timeout <ms>               Per-test timeout (default: 10000)
  --chunk-size <N>             Tests per chunk (default: 1000)
  --max-memory-mb <N>         Per-runner managed heap cap in MB (default: 10000)
  --verbose|-v                 Verbose output
  --isolate-process            Run each test in child process (crash-safe)

ENVIRONMENT:
  TEST262_ROOT                 Alternative to --root flag

EXAMPLES:
  FenBrowser.Test262 get_chunk_count
  FenBrowser.Test262 run_chunk 1 --format json -o results.json
  FenBrowser.Test262 run_category language/expressions --max 100
  FenBrowser.Test262 run_single language/expressions/addition/S11.6.1_A1.js
");
        return 0;
    }
}




