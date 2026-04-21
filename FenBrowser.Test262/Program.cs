// SpecRef: Test262 harness execution truthfulness contract
// CapabilityId: VERIFY-TEST262-TRUTH-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
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
//   FenBrowser.Test262 run_chunk <N> [--root <path>] [--format md|json|tap] [--isolate-process] [--workers <N>]
//   FenBrowser.Test262 run_category <name> [--max <N>] [--root <path>]
//   FenBrowser.Test262 run_single <path> [--root <path>]
//   FenBrowser.Test262 run_manifest <path> [--root <path>] [--output <path>]
//   FenBrowser.Test262 summary [--root <path>]
// =============================================================================

using System.Diagnostics;
using System.Text.Json;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.Test262;

public static class Program
{
    private static readonly JsonSerializerOptions WorkerJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
                case "--workers" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int workers) && workers > 0)
                        config.WorkerCount = workers;
                    break;
                case "--verbose" or "-v":
                    config.Verbose = true;
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
            }
        }

        EngineLog.InitializeFromSettings();
        EngineLog.ApplyPreset(config.LoggingPreset);

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
                "run_manifest" => await RunManifestAsync(config, args),
                "run_worker" => await PersistentIsolatedWorkerHost.RunAsync(config),
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

        bool useIsolatedWorkers = ShouldUseIsolatedWorkers(config, chunkTests.Count);
        if (useIsolatedWorkers && !config.IsolateProcess)
        {
            Console.WriteLine(
                $"[Test262] Auto-enabled isolated workers for chunk {chunkNumber} ({chunkTests.Count} tests >= threshold {config.AutoParallelThreshold}).");
        }

        IReadOnlyList<Test262Runner.TestResult> results;
        if (useIsolatedWorkers)
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
        ExportFailureBundles(config, results, "test262-run_chunk");

        return failed > 0 ? 1 : 0;
    }

    private static bool ShouldUseIsolatedWorkers(Test262Config config, int testCount)
    {
        if (config.IsolateProcess)
            return true;

        if (config.AutoParallelThreshold <= 0)
            return false;

        return testCount >= config.AutoParallelThreshold;
    }

    private static async Task<IReadOnlyList<Test262Runner.TestResult>> RunChunkIsolatedAsync(
        Test262Config config,
        IReadOnlyList<string> chunkTests,
        Action<string, int>? onProgress)
    {
        string runnerDll = typeof(Program).Assembly.Location;
        int workerCount = GetEffectiveIsolatedWorkerCount(config, chunkTests.Count);
        string tempRoot = Path.Combine(Path.GetTempPath(), "FenBrowser.Test262", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var workerClients = Enumerable.Range(0, workerCount)
            .Select(index => new PersistentIsolatedWorkerClient(config, runnerDll, index))
            .ToArray();

        try
        {
            var batches = PartitionTests(chunkTests, workerCount);
            var testOrder = chunkTests
                .Select((testFile, index) => new { testFile, index })
                .ToDictionary(x => x.testFile, x => x.index, StringComparer.OrdinalIgnoreCase);

            int completed = 0;
            int nextBatchIndex = -1;
            var workerTasks = Enumerable.Range(0, workerCount).Select(async _ =>
            {
                var localResults = new List<Test262Runner.TestResult>();

                while (true)
                {
                    int batchIndex = Interlocked.Increment(ref nextBatchIndex);
                    if (batchIndex >= batches.Count)
                        break;

                    var client = workerClients[_];
                    IReadOnlyList<Test262Runner.TestResult> batchResults;
                    try
                    {
                        var workerResult = await client.RunBatchAsync(
                            batches[batchIndex],
                            GetPersistentWorkerBatchTimeout(config, batches[batchIndex].Count));
                        batchResults = workerResult.Results;

                        if (workerResult.RecycleSuggested || client.ShouldRecycle())
                        {
                            await client.RecycleAsync();
                        }
                    }
                    catch
                    {
                        batchResults = await RunManifestBatchIsolatedAsync(
                            config,
                            runnerDll,
                            batches[batchIndex],
                            batchIndex,
                            tempRoot,
                            null);
                        await client.RecycleAsync();
                    }

                    foreach (var result in batchResults)
                    {
                        int current = Interlocked.Increment(ref completed);
                        onProgress?.Invoke(Path.GetFileName(result.TestFile), current);
                    }

                    localResults.AddRange(batchResults);
                }

                return (IReadOnlyList<Test262Runner.TestResult>)localResults;
            }).ToArray();

            var workerResults = await Task.WhenAll(workerTasks);
            return workerResults
                .SelectMany(resultSet => resultSet)
                .OrderBy(result => testOrder.TryGetValue(result.TestFile, out int index) ? index : int.MaxValue)
                .ToList();
        }
        finally
        {
            foreach (var workerClient in workerClients)
            {
                await workerClient.DisposeAsync();
            }

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Temporary worker artifacts are best-effort cleanup.
            }
        }
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

    private static async Task<IReadOnlyList<Test262Runner.TestResult>> RunManifestBatchIsolatedAsync(
        Test262Config config,
        string runnerDll,
        IReadOnlyList<string> batchTests,
        int batchIndex,
        string tempRoot,
        Action<string, int>? onProgress)
    {
        string manifestPath = Path.Combine(tempRoot, $"worker_{batchIndex:D2}.manifest.txt");
        string outputPath = Path.Combine(tempRoot, $"worker_{batchIndex:D2}.json");
        string stdoutPath = Path.Combine(tempRoot, $"worker_{batchIndex:D2}.out.log");
        string stderrPath = Path.Combine(tempRoot, $"worker_{batchIndex:D2}.err.log");

        await File.WriteAllLinesAsync(manifestPath, batchTests);

        long timeoutMs = Math.Min(
            (long)config.PerTestTimeoutMs * Math.Max(1, batchTests.Count) + 30_000L,
            int.MaxValue);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{runnerDll}\" run_manifest \"{manifestPath}\" --root \"{config.Test262RootPath}\" --timeout {config.PerTestTimeoutMs} --max-memory-mb {config.MaxMemoryMB} --output \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => proc.WaitForExit((int)timeoutMs));
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                throw new TimeoutException($"Worker batch {batchIndex} timed out after {timeoutMs}ms.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (TryReadManifestResults(outputPath, out var manifestResults) &&
                manifestResults.Count == batchTests.Count)
            {
                onProgress?.Invoke(Path.GetFileName(batchTests[^1]), batchTests.Count);
                return manifestResults;
            }

            Console.Error.WriteLine(
                $"[Test262] Worker batch {batchIndex} returned invalid output. ExitCode={proc.ExitCode}. StdOut={stdout.Trim()} StdErr={stderr.Trim()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Test262] Worker batch {batchIndex} failed: {ex.Message}. Falling back to per-test isolation.");
        }

        var fallbackResults = new List<Test262Runner.TestResult>(batchTests.Count);
        foreach (string testFile in batchTests)
        {
            var result = await RunSingleIsolatedAsync(config, runnerDll, testFile);
            fallbackResults.Add(result);
            onProgress?.Invoke(Path.GetFileName(testFile), 1);
        }

        return fallbackResults;
    }

    private static int GetEffectiveIsolatedWorkerCount(Test262Config config, int testCount)
    {
        if (testCount <= 1)
            return 1;

        int memoryBound = Math.Max(1, config.MaxMemoryMB / Math.Max(1, config.EstimatedIsolatedWorkerMemoryMB));
        return Math.Max(1, Math.Min(testCount, Math.Min(config.WorkerCount, memoryBound)));
    }

    private static List<IReadOnlyList<string>> PartitionTests(IReadOnlyList<string> testFiles, int batchCount)
    {
        int averageTestsPerWorker = (int)Math.Ceiling((double)testFiles.Count / Math.Max(1, batchCount));
        int batchMultiplier = averageTestsPerWorker >= 100 ? 2 : 1;
        int actualBatchCount = Math.Max(1, Math.Min(testFiles.Count, Math.Max(batchCount, batchCount * batchMultiplier)));
        var batches = new List<IReadOnlyList<string>>(actualBatchCount);

        for (int batchIndex = 0; batchIndex < actualBatchCount; batchIndex++)
        {
            int start = batchIndex * testFiles.Count / actualBatchCount;
            int end = (batchIndex + 1) * testFiles.Count / actualBatchCount;
            if (end <= start)
                continue;

            batches.Add(testFiles.Skip(start).Take(end - start).ToList());
        }

        return batches;
    }

    private static TimeSpan GetPersistentWorkerBatchTimeout(Test262Config config, int testCount)
    {
        long timeoutMs = Math.Min(
            (long)config.PerTestTimeoutMs * Math.Max(1, testCount) + 30_000L,
            int.MaxValue);
        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    private static bool TryReadManifestResults(string outputPath, out IReadOnlyList<Test262Runner.TestResult> results)
    {
        results = Array.Empty<Test262Runner.TestResult>();
        if (!File.Exists(outputPath))
            return false;

        try
        {
            var envelope = JsonSerializer.Deserialize<WorkerManifestEnvelope>(File.ReadAllText(outputPath), WorkerJsonOptions);
            if (envelope?.Results == null)
                return false;

            results = envelope.Results.Select(item => new Test262Runner.TestResult
            {
                TestFile = item.TestFile,
                Passed = item.Passed,
                Expected = item.Expected ?? string.Empty,
                Actual = item.Actual ?? string.Empty,
                Error = item.Error ?? string.Empty,
                Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                Metadata = new Test262Runner.TestMetadata
                {
                    Features = item.Features ?? new List<string>()
                }
            }).ToList();
            return true;
        }
        catch
        {
            return false;
        }
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

        var categoryTests = runner.DiscoverTests(category)
            .Take(maxTests)
            .ToList();

        if (categoryTests.Count == 0)
        {
            Console.Error.WriteLine($"[ERROR] No discoverable tests found for category: {category}");
            return 1;
        }

        bool useIsolatedWorkers = ShouldUseIsolatedWorkers(config, categoryTests.Count);

        Console.WriteLine($"[Test262] Running category: {category}");
        if (useIsolatedWorkers && !config.IsolateProcess)
        {
            Console.WriteLine(
                $"[Test262] Auto-enabled isolated workers for category '{category}' ({categoryTests.Count} tests >= threshold {config.AutoParallelThreshold}).");
        }

        var sw = Stopwatch.StartNew();

        IReadOnlyList<Test262Runner.TestResult> results;
        if (useIsolatedWorkers)
        {
            results = await RunChunkIsolatedAsync(config, categoryTests, (name, count) =>
            {
                if (config.Verbose || count % 50 == 0)
                    Console.Write($"\r  [{count}/{categoryTests.Count}] {name}    ");
            });
        }
        else
        {
            results = await runner.RunSpecificTestsAsync(categoryTests, (name, count) =>
            {
                if (config.Verbose || count % 50 == 0)
                    Console.Write($"\r  [{count}/{categoryTests.Count}] {name}    ");
            });
        }

        sw.Stop();
        Console.WriteLine();

        int passed = results.Count(r => r.Passed);
        int failed = results.Count - passed;
        double passRate = results.Count > 0 ? (double)passed / results.Count * 100 : 0;
        Console.WriteLine($"Total: {results.Count} | Pass: {passed} | Fail: {failed} ({passRate:F1}%)");

        var output = ResultsExporter.Export(results, config.Format, totalDuration: sw.Elapsed);
        WriteOutput(output, config);
        ExportFailureBundles(config, results, $"test262-run_category:{category}");

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

        if (!result.Passed)
        {
            ExportFailureBundle(
                testId: GetRelativeTest262Id(config, result.TestFile),
                url: ToFileUrl(result.TestFile),
                summary: $"test262-run_single failure: {result.Error}");
        }

        return result.Passed ? 0 : 1;
    }

    /// <summary>
    /// Run a manifest file of explicit tests.
    /// Intended for isolated microchunk workers spawned by run_chunk.
    /// </summary>
    private static async Task<int> RunManifestAsync(Test262Config config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: run_manifest <manifest-path>");
            return 1;
        }

        string manifestPath = args[1];
        if (!Path.IsPathRooted(manifestPath))
            manifestPath = Path.GetFullPath(manifestPath);

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"[ERROR] Manifest file not found: {manifestPath}");
            return 1;
        }

        var testFiles = (await File.ReadAllLinesAsync(manifestPath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        if (testFiles.Count == 0)
        {
            Console.Error.WriteLine("[ERROR] Manifest did not contain any test files.");
            return 1;
        }

        var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
        runner.MemoryThresholdBytes = config.MaxMemoryMB * 1_000_000L;

        var sw = Stopwatch.StartNew();
        var results = await runner.RunSpecificTestsAsync(testFiles);
        sw.Stop();

        if (!string.IsNullOrEmpty(config.OutputPath))
        {
            var dir = Path.GetDirectoryName(config.OutputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(config.OutputPath, SerializeManifestResults(results));
            Console.WriteLine($"[Test262] Results saved to {config.OutputPath}");
        }

        Console.WriteLine($"{(long)sw.Elapsed.TotalMilliseconds}ms | Pass: {results.Count(r => r.Passed)} | Fail: {results.Count(r => !r.Passed)}");
        return results.Any(r => !r.Passed) ? 1 : 0;
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
  run_manifest <path>          Run a manifest file of explicit test paths
  summary                      Show test suite summary with categories
  help                         Show this help message

OPTIONS:
  --root <path>                Path to test262 root directory
  --format md|json|tap         Output format (default: md)
  --output|-o <path>           Write results to file
  --timeout <ms>               Per-test timeout (default: 10000)
  --chunk-size <N>             Tests per chunk (default: 1000)
  --max-memory-mb <N>         Per-runner managed heap cap in MB (default: 10000)
  --workers <N>                Max isolated worker processes (default: 20)
  --verbose|-v                 Verbose output
  --isolate-process            Run isolated microchunk workers (crash-safe)
  --no-failure-bundles         Disable per-test failure bundle export
  --max-failure-bundles <N>    Max failure bundles per run (default: 20)
  --log-preset <name>          Engine log preset (developer|testrun|perf|ci)

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

    private static string SerializeManifestResults(IReadOnlyList<Test262Runner.TestResult> results)
    {
        var envelope = new WorkerManifestEnvelope
        {
            Results = results.Select(result => new WorkerManifestResult
            {
                TestFile = result.TestFile,
                Passed = result.Passed,
                Expected = result.Expected,
                Actual = result.Actual,
                Error = result.Error,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Features = result.Metadata?.Features ?? new List<string>()
            }).ToList()
        };

        return JsonSerializer.Serialize(envelope, WorkerJsonOptions);
    }

    private sealed class WorkerManifestEnvelope
    {
        public List<WorkerManifestResult> Results { get; set; } = new();
    }

    private sealed class WorkerManifestResult
    {
        public string TestFile { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public string? Error { get; set; }
        public long DurationMs { get; set; }
        public List<string>? Features { get; set; }
    }

    private static void ExportFailureBundles(
        Test262Config config,
        IReadOnlyList<Test262Runner.TestResult> results,
        string summaryPrefix)
    {
        if (!config.ExportFailureBundlesOnFailure || results == null || results.Count == 0)
        {
            return;
        }

        foreach (var failure in results.Where(r => !r.Passed).Take(config.MaxFailureBundlesPerRun))
        {
            ExportFailureBundle(
                GetRelativeTest262Id(config, failure.TestFile),
                ToFileUrl(failure.TestFile),
                $"{summaryPrefix} | {failure.Error}");
        }
    }

    private static void ExportFailureBundle(string testId, string url, string summary)
    {
        try
        {
            var bundlePath = EngineLog.ExportFailureBundle(testId: testId, url: url, summary: summary, maxEntries: 4000);
            Console.WriteLine($"[Test262] Failure bundle exported: {bundlePath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Test262] Failure bundle export failed for {testId}: {ex.Message}");
        }
    }

    private static string GetRelativeTest262Id(Test262Config config, string testFile)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(config.Test262RootPath) && !string.IsNullOrWhiteSpace(testFile))
            {
                return Path.GetRelativePath(config.Test262RootPath, testFile).Replace('\\', '/');
            }
        }
        catch
        {
        }

        return testFile ?? "test262/unknown";
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

