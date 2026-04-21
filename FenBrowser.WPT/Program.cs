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
using System.Globalization;
using FenBrowser.Core.Logging;
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
                case "--chunk-size" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int cs))
                        config.ChunkSize = cs;
                    break;
                case "--workers" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int workers) && workers > 0)
                        config.WorkerCount = workers;
                    break;
                case "--isolate-process":
                    config.IsolateProcess = true;
                    break;
                case "--no-failure-bundles":
                    config.ExportFailureBundlesOnFailure = false;
                    break;
                case "--max-failure-bundles" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int maxBundles) && maxBundles > 0)
                        config.MaxFailureBundlesPerRun = maxBundles;
                    break;
                case "--log-preset" when i + 1 < args.Length:
                    config.LoggingPreset = args[++i];
                    break;
                case "--verbose" or "-v":
                    config.Verbose = true;
                    break;
            }
        }

        EngineLog.InitializeFromSettings();
        EngineLog.ApplyPreset(config.LoggingPreset);

        var command = args[0].ToLowerInvariant();

        // Auto-default: save JSON results to Results/wpt_results.json
        if (string.IsNullOrEmpty(config.OutputPath) &&
            command is not "run_pack" and not "extract_pack" and not "discover" and not "get_chunk_count")
        {
            var repoRoot = Path.GetDirectoryName(config.WptRootPath ?? Directory.GetCurrentDirectory())
                           ?? Directory.GetCurrentDirectory();
            config.OutputPath = Path.Combine(repoRoot, "Results", "wpt_results.json");
            config.Format = OutputFormat.Json;
        }

        try
        {
            return command switch
            {
                "get_chunk_count" => RunGetChunkCount(config),
                "run_chunk" => await RunChunkAsync(config, args),
                "run_category" => await RunCategoryAsync(config, args),
                "run_pack" => await RunPackAsync(config, args),
                "extract_pack" => RunExtractPack(config, args),
                "list_packs" => RunListPacks(config),
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
    /// Print total test count and number of chunks.
    /// </summary>
    private static int RunGetChunkCount(WPTConfig config)
    {
        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            Console.Error.WriteLine("[FATAL] WPT root path not found. Use --root <path> or set WPT_ROOT.");
            return 1;
        }

        var navigator = new HeadlessNavigator(config.WptRootPath, config.TimeoutMs);
        var runner = new WPTTestRunner(config.WptRootPath, navigator.GetNavigatorDelegate(), config.TimeoutMs);
        var tests = runner.DiscoverAllTests();
        int chunkCount = (int)Math.Ceiling((double)tests.Count / config.ChunkSize);

        Console.WriteLine($"Total tests: {tests.Count}");
        Console.WriteLine($"Chunk size:  {config.ChunkSize}");
        Console.WriteLine($"Chunks:      {chunkCount}");

        return 0;
    }

    /// <summary>
    /// Run a specific chunk of WPT tests.
    /// </summary>
    private static async Task<int> RunChunkAsync(WPTConfig config, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int chunkNumber) || chunkNumber < 1)
        {
            Console.Error.WriteLine("Usage: run_chunk <N> where N >= 1");
            return 1;
        }

        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            Console.Error.WriteLine("[FATAL] WPT root path not found. Use --root <path> or set WPT_ROOT.");
            return 1;
        }

        var navigator = new HeadlessNavigator(config.WptRootPath, config.TimeoutMs);
        var runner = new WPTTestRunner(config.WptRootPath, navigator.GetNavigatorDelegate(), config.TimeoutMs);

        var allTests = runner.DiscoverAllTests();
        int skip = (chunkNumber - 1) * config.ChunkSize;
        int take = config.ChunkSize;

        if (skip >= allTests.Count)
        {
            int maxChunk = (int)Math.Ceiling((double)allTests.Count / config.ChunkSize);
            Console.Error.WriteLine($"[ERROR] Chunk {chunkNumber} exceeds total tests ({allTests.Count}). Max chunk: {maxChunk}");
            return 1;
        }

        var chunkTests = allTests.Skip(skip).Take(take).ToList();
        Console.WriteLine($"[WPT] Chunk {chunkNumber}: tests {skip + 1}-{skip + chunkTests.Count} of {allTests.Count}");

        // Memory check
        long freeMemKB = GetFreeMemoryKB();
        if (freeMemKB > 0 && freeMemKB < 10_000_000)
            Console.Error.WriteLine($"[WARNING] Low system memory: {freeMemKB / 1_048_576.0:F1}GB free.");

        var sw = Stopwatch.StartNew();

        var results = ShouldUseIsolatedWorkers(config)
            ? await RunSpecificTestsIsolatedAsync(config, chunkTests, (name, count) =>
            {
                if (config.Verbose || count % 10 == 0)
                    Console.Write($"\r  [{count}/{chunkTests.Count}] {name} ({config.WorkerCount}w isolated)    ");
            })
            : await runner.RunSpecificTestsAsync(chunkTests, (name, count) =>
            {
                if (config.Verbose || count % 10 == 0)
                {
                    long mem = GC.GetTotalMemory(false) / 1_000_000;
                    Console.Write($"\r  [{count}/{chunkTests.Count}] {name} ({mem}MB)    ");
                }
            });

        sw.Stop();
        Console.WriteLine();

        int passed = results.Count(r => r.Success);
        int failed = results.Count(r => !r.Success);
        int timedOut = results.Count(r => r.TimedOut);
        double passRate = results.Count > 0 ? (double)passed / results.Count * 100 : 0;
        long avgMs = results.Count > 0 ? (long)(sw.Elapsed.TotalMilliseconds / results.Count) : 0;

        Console.WriteLine($"Chunk {chunkNumber} | {(long)sw.Elapsed.TotalMilliseconds}ms | Tests: {results.Count} | Pass: {passed} | Fail: {failed} | Timeout: {timedOut} | {passRate:F1}% | {avgMs}ms/test");

        // Append to wpt_results.md
        AppendChunkResultsToMd(config.WptRootPath, chunkNumber, skip + 1, skip + chunkTests.Count, sw.Elapsed, results);

        // Export JSON
        var output = ResultsExporter.Export(results, config.Format, null, sw.Elapsed, chunkNumber);
        WriteOutput(output, config);
        ExportFailureBundles(config, results, "wpt-run_chunk");

        return failed > 0 ? 1 : 0;
    }

    private static void AppendChunkResultsToMd(string wptRootPath, int chunk, int from, int to, TimeSpan elapsed,
        IReadOnlyList<WPTTestRunner.TestExecutionResult> results)
    {
        var repoRoot = Path.GetDirectoryName(wptRootPath) ?? Directory.GetCurrentDirectory();
        var mdPath = Path.Combine(repoRoot, "wpt_results.md");

        int passed = results.Count(r => r.Success);
        int failed = results.Count(r => !r.Success);
        int timedOut = results.Count(r => r.TimedOut);
        double passRate = results.Count > 0 ? (double)passed / results.Count * 100 : 0;
        long avgMs = results.Count > 0 ? (long)(elapsed.TotalMilliseconds / results.Count) : 0;

        string row = $"| {chunk} | {from}-{to} | {(long)elapsed.TotalMilliseconds} | {results.Count} | {passed} | {failed} | {timedOut} | {passRate:F1}% | {avgMs} |";

        if (!File.Exists(mdPath))
        {
            File.WriteAllText(mdPath,
                "# WPT Results\n\n" +
                "| Chunk | Range | Time (ms) | Tests | Passed | Failed | Timeout | Pass % | Avg/Test (ms) |\n" +
                "|-------|-------|-----------|-------|--------|--------|---------|--------|---------------|\n");
        }

        File.AppendAllText(mdPath, row + "\n");
    }

    private static long GetFreeMemoryKB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    if (long.TryParse(output.Trim(), out long kb)) return kb;
                }
            }
        }
        catch { }
        return 0;
    }

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
        ExportFailureBundles(config, results, $"wpt-run_category:{category}");

        return results.Any(r => !r.Success) ? 1 : 0;
    }

    private static async Task<int> RunPackAsync(WPTConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_pack <pack-name|path>");
            return 1;
        }

        if (string.IsNullOrEmpty(config.WptRootPath))
        {
            Console.Error.WriteLine("[FATAL] WPT root path not found. Use --root <path> or set WPT_ROOT.");
            return 1;
        }

        var repoRoot = DiscoverRepoRoot(config.WptRootPath);
        var pack = WPTRegressionPack.Load(repoRoot, args[1]);
        var tests = pack.ResolveTests(config.WptRootPath);

        var navigator = new HeadlessNavigator(config.WptRootPath, config.TimeoutMs);
        var runner = new WPTTestRunner(config.WptRootPath, navigator.GetNavigatorDelegate(), config.TimeoutMs);

        Console.WriteLine($"[WPT] Running regression pack: {pack.Name} ({tests.Count} tests)");
        var sw = Stopwatch.StartNew();
        var results = ShouldUseIsolatedWorkers(config)
            ? await RunSpecificTestsIsolatedAsync(config, tests.ToList(), (name, count) =>
            {
                if (config.Verbose || count % 10 == 0)
                    Console.Write($"\r  [{count}/{tests.Count}] {name} ({config.WorkerCount}w isolated)    ");
            })
            : await runner.RunSpecificTestsAsync(tests.ToList(), (name, count) =>
            {
                if (config.Verbose || count % 10 == 0)
                    Console.Write($"\r  [{count}/{tests.Count}] {name}    ");
            });
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine(GenerateBatchSummary(results));

        var output = ResultsExporter.Export(results, OutputFormat.Json, pack.Category, sw.Elapsed, 0, pack.Name, pack.Description);
        WriteRegressionPackOutput(output, repoRoot, pack, HasExplicitOutput(args) ? config.OutputPath : null);
        ExportFailureBundles(config, results, $"wpt-run_pack:{pack.Name}");

        return results.Any(r => !r.Success) ? 1 : 0;
    }

    private static int RunExtractPack(WPTConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: extract_pack <pack-name|path> [source-artifact]");
            return 1;
        }

        var repoRoot = DiscoverRepoRoot(config.WptRootPath);
        var pack = WPTRegressionPack.Load(repoRoot, args[1]);
        var sourceArtifact = args.Length >= 3 && !args[2].StartsWith("-", StringComparison.Ordinal)
            ? args[2]
            : Path.Combine(repoRoot, "Results", "wpt_results_latest.json");
        if (!Path.IsPathRooted(sourceArtifact))
        {
            sourceArtifact = Path.Combine(repoRoot, sourceArtifact);
        }

        if (!File.Exists(sourceArtifact))
        {
            Console.Error.WriteLine($"[ERROR] Source artifact not found: {sourceArtifact}");
            return 1;
        }

        var output = WPTRegressionArtifactBuilder.BuildFilteredArtifact(sourceArtifact, pack);
        WriteRegressionPackOutput(output, repoRoot, pack, HasExplicitOutput(args) ? config.OutputPath : null);
        return 0;
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

        if (!result.Success)
        {
            ExportFailureBundle(
                testId: GetRelativeWptTestId(config, result.TestFile),
                url: ToFileUrl(result.TestFile),
                summary: $"wpt-run_single failure: {result.Error}");
        }

        return result.Success ? 0 : 1;
    }

    private static int RunListPacks(WPTConfig config)
    {
        var repoRoot = DiscoverRepoRoot(config.WptRootPath);
        var packPaths = WPTRegressionPack.ListBuiltInPackPaths(repoRoot);
        Console.WriteLine("Built-in WPT regression packs:");
        foreach (var packPath in packPaths)
        {
            var pack = WPTRegressionPack.Load(repoRoot, packPath);
            Console.WriteLine($"  {pack.Name,-24} {pack.Category,-8} {pack.Selectors.Count,3} tests  {pack.Description}");
        }

        return 0;
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

    private static bool ShouldUseIsolatedWorkers(WPTConfig config)
    {
        return config.IsolateProcess || config.WorkerCount > 1;
    }

    private static async Task<IReadOnlyList<WPTTestRunner.TestExecutionResult>> RunSpecificTestsIsolatedAsync(
        WPTConfig config,
        IReadOnlyList<string> tests,
        Action<string, int>? onProgress)
    {
        string runnerDll = typeof(Program).Assembly.Location;
        var results = new WPTTestRunner.TestExecutionResult[tests.Count];
        int completed = 0;
        int workerCount = Math.Max(1, config.WorkerCount);
        var indexedTests = tests.Select((testFile, index) => (testFile, index));
        var options = new ParallelOptions { MaxDegreeOfParallelism = workerCount };

        await Parallel.ForEachAsync(indexedTests, options, async (entry, token) =>
        {
            results[entry.index] = await RunSingleIsolatedAsync(config, runnerDll, entry.testFile);
            int current = Interlocked.Increment(ref completed);
            onProgress?.Invoke(Path.GetFileName(entry.testFile), current);
        });

        return Array.AsReadOnly(results);
    }

    private static async Task<WPTTestRunner.TestExecutionResult> RunSingleIsolatedAsync(
        WPTConfig config,
        string runnerDll,
        string testFile)
    {
        var result = new WPTTestRunner.TestExecutionResult
        {
            TestFile = testFile,
            Success = false,
            HarnessCompleted = false,
            TimedOut = false,
            CompletionSignal = "isolated-child-missing",
            Error = "Isolated child process did not return a result"
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{runnerDll}\" run_single \"{testFile}\" --root \"{config.WptRootPath}\" --timeout {config.TimeoutMs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => proc.WaitForExit(config.TimeoutMs + 5_000));
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                result.TimedOut = true;
                result.CompletionSignal = "isolated-child-timeout";
                result.Error = $"Isolated child timeout after {config.TimeoutMs + 5_000}ms";
                return result;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            result.Output = stdout;

            if (TryParseIsolatedRunOutput(stdout, result))
            {
                if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                {
                    result.Error = !string.IsNullOrWhiteSpace(stderr)
                        ? stderr.Trim()
                        : "Unknown failure in isolated child process";
                }

                if (!result.Success && proc.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Error))
                {
                    result.Error = $"Child process exited with code {proc.ExitCode}";
                }

                return result;
            }

            result.Error = !string.IsNullOrWhiteSpace(stderr)
                ? $"Child process exited with code {proc.ExitCode}: {stderr.Trim()}"
                : $"Child process exited with code {proc.ExitCode} without WPT result output";
            result.CompletionSignal = "isolated-child-no-result";
        }
        catch (Exception ex)
        {
            result.CompletionSignal = "isolated-child-exception";
            result.Error = $"Isolated child process exception: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    private static bool TryParseIsolatedRunOutput(string stdout, WPTTestRunner.TestExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        string? status = null;
        int errorLineIndex = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
            {
                status = GetFieldValue(line);
            }
            else if (line.StartsWith("Signal:", StringComparison.OrdinalIgnoreCase))
            {
                result.CompletionSignal = GetFieldValue(line);
            }
            else if (line.StartsWith("Asserts:", StringComparison.OrdinalIgnoreCase))
            {
                ParseAssertionSummary(line, result);
            }
            else if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                errorLineIndex = i;
            }
            else if (line.StartsWith("Duration:", StringComparison.OrdinalIgnoreCase))
            {
                TryParseDuration(line, result);
            }
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            result.Success = string.Equals(status, "PASS", StringComparison.OrdinalIgnoreCase);
            result.HarnessCompleted = !string.IsNullOrWhiteSpace(result.CompletionSignal) &&
                                      !string.Equals(result.CompletionSignal, "timeout", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(result.CompletionSignal, "none", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(result.CompletionSignal, "isolated-child-missing", StringComparison.OrdinalIgnoreCase);
            result.TimedOut = string.Equals(result.CompletionSignal, "timeout", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(result.CompletionSignal, "isolated-child-timeout", StringComparison.OrdinalIgnoreCase);
            result.Error = string.Empty;

            if (errorLineIndex >= 0)
            {
                result.Error = CollectError(lines, errorLineIndex);
            }

            return true;
        }

        return false;
    }

    private static string GetFieldValue(string line)
    {
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : string.Empty;
    }

    private static void ParseAssertionSummary(string line, WPTTestRunner.TestExecutionResult result)
    {
        var value = GetFieldValue(line);
        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out int total))
        {
            result.TotalCount = total;
        }

        var openParen = value.IndexOf('(');
        var closeParen = value.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
        {
            return;
        }

        var details = value.Substring(openParen + 1, closeParen - openParen - 1)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var detail in details)
        {
            if (detail.StartsWith("Pass:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(GetFieldValue(detail), out int passCount))
            {
                result.PassCount = passCount;
            }
            else if (detail.StartsWith("Fail:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(GetFieldValue(detail), out int failCount))
            {
                result.FailCount = failCount;
            }
        }
    }

    private static string CollectError(string[] lines, int errorLineIndex)
    {
        var builder = new List<string> { GetFieldValue(lines[errorLineIndex].Trim()) };
        for (int i = errorLineIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("Test:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Result:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Signal:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Duration:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Asserts:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("=== Console Output ===", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            builder.Add(trimmed.Trim());
        }

        return string.Join(Environment.NewLine, builder.Where(static s => !string.IsNullOrWhiteSpace(s)));
    }

    private static void TryParseDuration(string line, WPTTestRunner.TestExecutionResult result)
    {
        var raw = GetFieldValue(line);
        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^2].Trim();
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double durationMs))
        {
            result.Duration = TimeSpan.FromMilliseconds(durationMs);
        }
    }

    private static string GenerateBatchSummary(IReadOnlyList<WPTTestRunner.TestExecutionResult> results)
    {
        var lines = new List<string>
        {
            "=== WPT Test Summary ===",
            string.Empty
        };

        int passed = results.Count(r => r.Success);
        int failed = results.Count - passed;
        int assertions = results.Sum(r => r.TotalCount);
        int passedAssertions = results.Sum(r => r.PassCount);
        int failedAssertions = results.Sum(r => r.FailCount);

        lines.Add($"Tests run: {results.Count}");
        lines.Add($"Assertions: {assertions} ({passedAssertions} passed, {failedAssertions} failed)");
        lines.Add($"Passed: {passed}");
        lines.Add($"Failed: {failed}");

        var failures = results.Where(r => !r.Success).Take(20).ToList();
        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Failed tests:");
            foreach (var failure in failures)
            {
                lines.Add($"  - {failure.TestFile}");
                if (!string.IsNullOrWhiteSpace(failure.Error))
                {
                    lines.Add($"    Error: {failure.Error}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteRegressionPackOutput(string content, string repoRoot, WPTRegressionPack pack, string? explicitOutputPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitOutputPath))
        {
            var explicitDir = Path.GetDirectoryName(explicitOutputPath);
            if (!string.IsNullOrEmpty(explicitDir) && !Directory.Exists(explicitDir))
                Directory.CreateDirectory(explicitDir);

            File.WriteAllText(explicitOutputPath, content);
            Console.WriteLine($"[WPT] Regression-pack artifact saved to {explicitOutputPath}");
            return;
        }

        var resultsRoot = Path.Combine(repoRoot, "Results");
        Directory.CreateDirectory(resultsRoot);

        var versionedPath = Path.Combine(resultsRoot, pack.CreateVersionedArtifactFileName(DateTime.UtcNow));
        var latestPath = Path.Combine(resultsRoot, pack.CreateLatestArtifactFileName());
        File.WriteAllText(versionedPath, content);
        File.WriteAllText(latestPath, content);

        Console.WriteLine($"[WPT] Regression-pack artifact saved to {versionedPath}");
        Console.WriteLine($"[WPT] Regression-pack latest alias updated at {latestPath}");
    }

    private static bool HasExplicitOutput(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase));
    }

    private static string DiscoverRepoRoot(string? wptRootPath)
    {
        if (!string.IsNullOrWhiteSpace(wptRootPath))
        {
            var startDir = new DirectoryInfo(Path.GetFullPath(wptRootPath));
            for (var current = startDir; current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "FenBrowser.sln")) ||
                    Directory.Exists(Path.Combine(current.FullName, "FenBrowser.WPT")))
                {
                    return current.FullName;
                }
            }
        }

        var searchDir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(searchDir, "FenBrowser.sln")))
                return searchDir;

            var parent = Directory.GetParent(searchDir);
            if (parent == null) break;
            searchDir = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
FenBrowser WPT (Web Platform Tests) Conformance CLI
====================================================

USAGE:
  FenBrowser.WPT <command> [options]

COMMANDS:
  get_chunk_count              Show total test count and chunk count
  run_chunk <N>                Run chunk N (1-indexed, 100 tests per chunk)
  run_category <name>          Run tests in a category (e.g., dom, css, html)
  run_pack <pack>              Run a named/path-based regression pack and write a versioned artifact
  extract_pack <pack> [json]   Extract a regression-pack artifact from an existing WPT JSON report
  list_packs                   List built-in regression packs
  run_single <path>            Run a single .html test file
  discover [category]          List categories or tests in a category
  help                         Show this help message

OPTIONS:
  --root <path>                Path to WPT root directory
  --format md|json|tap         Output format (default: json)
  --output|-o <path>           Write results to file
  --timeout <ms>               Per-test timeout (default: 30000)
  --chunk-size <N>             Tests per chunk (default: 100)
  --workers <N>                Number of isolated child workers for batch runs
  --isolate-process            Force isolated child-process execution
  --no-failure-bundles         Disable per-test failure bundle export
  --max-failure-bundles <N>    Max failure bundles per run (default: 20)
  --log-preset <name>          Engine log preset (developer|testrun|perf|ci)
  --max <N>                    Max tests per category
  --verbose|-v                 Verbose output

ENVIRONMENT:
  WPT_ROOT                     Alternative to --root flag

EXAMPLES:
  FenBrowser.WPT get_chunk_count
  FenBrowser.WPT run_chunk 1
  FenBrowser.WPT run_chunk 1 --workers 10
  FenBrowser.WPT run_chunk 5 --format json -o results/chunk5.json
  FenBrowser.WPT run_pack dom_event_api --workers 10
  FenBrowser.WPT list_packs
  FenBrowser.WPT run_pack dom_no_assertion
  FenBrowser.WPT extract_pack dom_event_api Results/wpt_results_latest.json
  FenBrowser.WPT discover
  FenBrowser.WPT discover dom
  FenBrowser.WPT run_category dom --max 50 --format json
  FenBrowser.WPT run_single dom/nodes/Document-createElement.html
");
        return 0;
    }

    private static void ExportFailureBundles(
        WPTConfig config,
        IReadOnlyList<WPTTestRunner.TestExecutionResult> results,
        string summaryPrefix)
    {
        if (!config.ExportFailureBundlesOnFailure || results == null || results.Count == 0)
        {
            return;
        }

        foreach (var failure in results.Where(r => !r.Success).Take(config.MaxFailureBundlesPerRun))
        {
            ExportFailureBundle(
                GetRelativeWptTestId(config, failure.TestFile),
                ToFileUrl(failure.TestFile),
                $"{summaryPrefix} | {failure.Error}");
        }
    }

    private static void ExportFailureBundle(string testId, string url, string summary)
    {
        try
        {
            var bundlePath = EngineLog.ExportFailureBundle(testId: testId, url: url, summary: summary, maxEntries: 4000);
            Console.WriteLine($"[WPT] Failure bundle exported: {bundlePath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WPT] Failure bundle export failed for {testId}: {ex.Message}");
        }
    }

    private static string GetRelativeWptTestId(WPTConfig config, string testFile)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(config.WptRootPath) && !string.IsNullOrWhiteSpace(testFile))
            {
                return Path.GetRelativePath(config.WptRootPath, testFile).Replace('\\', '/');
            }
        }
        catch
        {
        }

        return testFile ?? "wpt/unknown";
    }

    private static string ToFileUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch
        {
            return path;
        }
    }
}
