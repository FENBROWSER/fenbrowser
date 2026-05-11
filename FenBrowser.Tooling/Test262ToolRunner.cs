// SpecRef: Test262 harness execution and deterministic reporting semantics
// CapabilityId: VERIFY-TEST262-TRUTH-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tooling
{
    internal static class Test262ToolRunner
    {
        private static readonly Regex FrontMatterRegex = new Regex(@"/\*---(?<meta>.*?)---\*/", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly object EngineExecutionLock = new object();
        private const int DefaultTimeoutMs = 10000;
        private const string Test262HostBootstrap = @"
(function (g) {
  if (g.$262) { return; }
  function toCompletion(fn) {
    try {
      fn();
      return { type: 'normal', value: void 0 };
    } catch (e) {
      return { type: 'throw', value: e };
    }
  }
  g.$262 = {
    global: g,
    gc: function () {},
    destroy: function () {},
    evalScript: function (code) {
      return toCompletion(function () { (0, eval)(String(code)); });
    },
    createRealm: function () {
      return {
        global: g,
        gc: function () {},
        destroy: function () {},
        evalScript: function (code) {
          return toCompletion(function () { (0, eval)(String(code)); });
        },
        createRealm: function () { return this; }
      };
    },
    IsHTMLDDA: function () { return {}; }
  };
})(globalThis);
";

        public static async Task RunAsync(string[] args)
        {
            var options = ParseOptions(args);
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath) ?? Directory.GetCurrentDirectory());
            options.EventLogPath = string.IsNullOrWhiteSpace(options.EventLogPath)
                ? Path.ChangeExtension(options.OutputPath, ".events.jsonl")
                : options.EventLogPath;
            if (File.Exists(options.EventLogPath) && !options.WorkerMode)
            {
                File.Delete(options.EventLogPath);
            }

            var tests = DiscoverTests(options);
            if (options.ShardCount > 0)
            {
                tests = tests.Where((_, index) => index % options.ShardCount == options.ShardIndex).ToList();
            }

            Console.WriteLine($"[test262] discovered={tests.Count} workers={options.Workers} max={(options.MaxTests.HasValue ? options.MaxTests.Value.ToString() : "all")}{(options.ShardCount > 0 ? $" shard={options.ShardIndex}/{options.ShardCount}" : string.Empty)}");

            if (!options.WorkerMode && options.Workers > 1)
            {
                var spawned = await RunShardedAsync(options).ConfigureAwait(false);
                if (spawned)
                {
                    return;
                }
            }

            await RunInProcessAsync(options, tests).ConfigureAwait(false);
        }

        private static async Task RunInProcessAsync(Test262Options options, List<string> tests)
        {
            var results = new ConcurrentBag<Test262CaseResult>();
            var processed = 0;
            var eventLogLock = new object();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Workers)
            };

            var progressInterval = GetProgressInterval(tests.Count);
            var progressScope = options.ShardCount > 0
                ? $" shard={options.ShardIndex}/{options.ShardCount}"
                : string.Empty;

            try
            {
                await Parallel.ForEachAsync(tests, parallelOptions, (testPath, ct) =>
                {
                    var scenarioResults = RunSingleTest(options, testPath);
                    foreach (var result in scenarioResults)
                    {
                        results.Add(result);
                        AppendEvent(options.EventLogPath, eventLogLock, result);
                    }

                    var done = Interlocked.Increment(ref processed);
                    if (done % progressInterval == 0 || done == tests.Count)
                    {
                        var snapshot = results.ToArray();
                        var progressSummary = BuildSummary(options, tests.Count, snapshot);
                        Console.WriteLine($"[test262] progress{progressScope} files={done}/{tests.Count} pass={progressSummary.Passed} fail={progressSummary.Failed} skip={progressSummary.Skipped} timeout={progressSummary.TimedOut} total={progressSummary.TotalScenarios}");
                    }

                    if (scenarioResults.Any(r => r.Outcome == "timeout"))
                    {
                        throw new Test262RunAbortedException("Stopping shard after scenario timeout to avoid reporting unexecuted tests as completed.");
                    }

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Test262RunAbortedException ex)
            {
                Console.WriteLine($"[test262] aborted{progressScope} {ex.Message}");
            }

            var ordered = results.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Scenario, StringComparer.Ordinal).ToList();
            var summary = BuildSummary(options, tests.Count, ordered);
            var payload = new Test262ReportPayload
            {
                Summary = summary,
                Results = ordered
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(options.OutputPath, json, new UTF8Encoding(false));

            Console.WriteLine($"[test262] output={options.OutputPath}");
            Console.WriteLine($"[test262] events={options.EventLogPath}");
            Console.WriteLine($"[test262] pass={summary.Passed} fail={summary.Failed} skip={summary.Skipped} timeout={summary.TimedOut} total={summary.TotalScenarios}");
            Console.WriteLine($"[test262] categories={FormatCategoryCounts(summary.Categories)}");
        }

        private static async Task<bool> RunShardedAsync(Test262Options options)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "FenBrowser.Tooling.dll");
            if (!File.Exists(dllPath))
            {
                return false;
            }

            var workerCount = Math.Max(1, options.Workers);
            var workerDir = Path.Combine(
                Path.GetDirectoryName(options.OutputPath) ?? Directory.GetCurrentDirectory(),
                $"test262_workers_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(workerDir);

            var workerOutputs = new string[workerCount];
            var workerEventLogs = new string[workerCount];
            var workerTasks = new List<Task<int>>(workerCount);
            for (var i = 0; i < workerCount; i++)
            {
                var workerOutput = Path.Combine(workerDir, $"worker_{i:D2}.json");
                var workerEventLog = Path.Combine(workerDir, $"worker_{i:D2}.events.jsonl");
                workerOutputs[i] = workerOutput;
                workerEventLogs[i] = workerEventLog;
                workerTasks.Add(LaunchWorkerAsync(dllPath, options, i, workerCount, workerOutput, workerEventLog));
            }

            var exitCodes = await Task.WhenAll(workerTasks).ConfigureAwait(false);
            if (exitCodes.Any(code => code != 0))
            {
                throw new InvalidOperationException($"One or more test262 workers failed. Exit codes: {string.Join(",", exitCodes)}");
            }

            var mergedResults = new List<Test262CaseResult>();
            var discoveredTests = 0;
            foreach (var output in workerOutputs)
            {
                var payload = LoadReportPayload(output);
                if (payload == null)
                {
                    continue;
                }

                discoveredTests += payload.Summary?.DiscoveredTests ?? 0;
                if (payload.Results != null)
                {
                    mergedResults.AddRange(payload.Results);
                }
            }

            var ordered = mergedResults
                .OrderBy(r => r.File, StringComparer.Ordinal)
                .ThenBy(r => r.Scenario, StringComparer.Ordinal)
                .ToList();
            MergeEventLogs(workerEventLogs, options.EventLogPath);
            var summary = BuildSummary(options, discoveredTests, ordered);
            var mergedPayload = new Test262ReportPayload
            {
                Summary = summary,
                Results = ordered
            };

            var json = JsonSerializer.Serialize(mergedPayload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(options.OutputPath, json, new UTF8Encoding(false));

            Console.WriteLine($"[test262] output={options.OutputPath}");
            Console.WriteLine($"[test262] events={options.EventLogPath}");
            Console.WriteLine($"[test262] pass={summary.Passed} fail={summary.Failed} skip={summary.Skipped} timeout={summary.TimedOut} total={summary.TotalScenarios}");
            Console.WriteLine($"[test262] categories={FormatCategoryCounts(summary.Categories)}");
            return true;
        }

        private static Test262ReportPayload LoadReportPayload(string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                return null;
            }

            var json = File.ReadAllText(outputPath);
            return JsonSerializer.Deserialize<Test262ReportPayload>(json);
        }

        private static async Task<int> LaunchWorkerAsync(string dllPath, Test262Options options, int shardIndex, int shardCount, string workerOutput, string workerEventLog)
        {
            var args = new StringBuilder();
            args.Append('"').Append(dllPath).Append('"');
            args.Append(" test262");
            args.Append(" --root ").Append('"').Append(options.RootPath).Append('"');
            args.Append(" --workers 1");
            args.Append(" --timeout-ms ").Append(options.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (options.MaxTests.HasValue)
            {
                args.Append(" --max ").Append(options.MaxTests.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(options.Filter))
            {
                args.Append(" --filter ").Append('"').Append(options.Filter).Append('"');
            }

            args.Append(" --output ").Append('"').Append(workerOutput).Append('"');
            args.Append(" --event-log ").Append('"').Append(workerEventLog).Append('"');
            args.Append(" --worker-mode");
            args.Append(" --shard-index ").Append(shardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Append(" --shard-count ").Append(shardCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = args.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return 1;
            }

            var stdoutTask = RelayOutputAsync(process.StandardOutput, string.Empty, test262Only: true);
            var stderrTask = RelayOutputAsync(process.StandardError, "[test262][worker-stderr] ", test262Only: false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode;
        }

        private static async Task RelayOutputAsync(StreamReader reader, string prefix, bool test262Only)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (test262Only && !line.StartsWith("[test262]", StringComparison.Ordinal))
                {
                    continue;
                }

                Console.WriteLine(prefix + line);
            }
        }

        private static int GetProgressInterval(int totalTests)
        {
            if (totalTests <= 100)
            {
                return 10;
            }

            if (totalTests <= 1000)
            {
                return 50;
            }

            return 100;
        }

        private static Test262Summary BuildSummary(Test262Options options, int discoveredTests, List<Test262CaseResult> results)
        {
            return BuildSummary(options, discoveredTests, (IReadOnlyCollection<Test262CaseResult>)results);
        }

        private static Test262Summary BuildSummary(Test262Options options, int discoveredTests, IReadOnlyCollection<Test262CaseResult> results)
        {
            var pass = results.Count(r => r.Outcome == "pass");
            var fail = results.Count(r => r.Outcome == "fail");
            var skip = results.Count(r => r.Outcome == "skip");
            var timeout = results.Count(r => r.Outcome == "timeout");
            var categories = results
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "unknown" : r.Category, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return new Test262Summary
            {
                Root = options.RootPath,
                Workers = options.Workers,
                TimeoutMs = options.TimeoutMs,
                Filter = options.Filter ?? string.Empty,
                MaxTests = options.MaxTests ?? 0,
                EventLogPath = options.EventLogPath ?? string.Empty,
                DiscoveredTests = discoveredTests,
                TotalScenarios = results.Count,
                Passed = pass,
                Failed = fail,
                Skipped = skip,
                TimedOut = timeout,
                Categories = categories,
                RunAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private static string FormatCategoryCounts(Dictionary<string, int> categories)
        {
            if (categories == null || categories.Count == 0)
            {
                return "none";
            }

            return string.Join(",", categories.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        }

        private static void AppendEvent(string eventLogPath, object eventLogLock, Test262CaseResult result)
        {
            if (string.IsNullOrWhiteSpace(eventLogPath))
            {
                return;
            }

            var json = JsonSerializer.Serialize(result);
            lock (eventLogLock)
            {
                File.AppendAllText(eventLogPath, json + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        private static void MergeEventLogs(IEnumerable<string> workerEventLogs, string mergedEventLogPath)
        {
            if (string.IsNullOrWhiteSpace(mergedEventLogPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(mergedEventLogPath) ?? Directory.GetCurrentDirectory());
            using var writer = new StreamWriter(mergedEventLogPath, append: false, new UTF8Encoding(false));
            foreach (var workerEventLog in workerEventLogs)
            {
                if (!File.Exists(workerEventLog))
                {
                    continue;
                }

                foreach (var line in File.ReadLines(workerEventLog))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        writer.WriteLine(line);
                    }
                }
            }
        }

        private static List<string> DiscoverTests(Test262Options options)
        {
            var testRoot = Path.Combine(options.RootPath, "test");
            if (!Directory.Exists(testRoot))
            {
                throw new DirectoryNotFoundException($"Test262 test directory not found: {testRoot}");
            }

            IEnumerable<string> files = Directory.EnumerateFiles(testRoot, "*.js", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("_FIXTURE.js", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}harness{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(options.Filter))
            {
                files = files.Where(p => p.Contains(options.Filter, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = files.OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (options.MaxTests.HasValue)
            {
                ordered = ordered.Take(options.MaxTests.Value).ToList();
            }

            return ordered;
        }

        private static List<Test262CaseResult> RunSingleTest(Test262Options options, string absolutePath)
        {
            var relative = Path.GetRelativePath(Path.Combine(options.RootPath, "test"), absolutePath).Replace('\\', '/');

            string content;
            try
            {
                content = File.ReadAllText(absolutePath);
            }
            catch (Exception ex)
            {
                return new List<Test262CaseResult>
                {
                    new Test262CaseResult
                    {
                        File = relative,
                        Scenario = "default",
                        Outcome = "fail",
                        Category = "io_error",
                        Message = $"Unable to read test file: {ex.Message}"
                    }
                };
            }

            var metadata = ParseMetadata(content);
            if (metadata.Flags.Contains("module") || metadata.Flags.Contains("async"))
            {
                return new List<Test262CaseResult>
                {
                    new Test262CaseResult
                    {
                        File = relative,
                        Scenario = "default",
                        Outcome = "skip",
                        Category = metadata.Flags.Contains("module") ? "unsupported_module" : "unsupported_async",
                        Message = "Skipped unsupported test flag (module/async)."
                    }
                };
            }

            var scenarios = GetScenarios(metadata.Flags);
            var harnessSource = BuildHarnessSource(options.RootPath, metadata.Includes);
            var testBody = StripFrontMatter(content);
            var results = new List<Test262CaseResult>();

            foreach (var scenario in scenarios)
            {
                var script = ComposeScenarioScript(harnessSource, testBody, scenario);
                var runResult = ExecuteScript(script, metadata, scenario, options.TimeoutMs);
                runResult.File = relative;
                runResult.Scenario = scenario;
                results.Add(runResult);
                if (runResult.Outcome == "timeout")
                {
                    break;
                }
            }

            return results;
        }

        private static Test262CaseResult ExecuteScript(string script, Test262Metadata metadata, string scenario, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            var task = Task.Run(() =>
            {
                lock (EngineExecutionLock)
                {
                    var host = new JsHostAdapter(_ => { }, (_, __) => { }, _ => { }, log: _ => { });
                    var engine = new JavaScriptEngine(host, JavaScriptRuntimeProfile.Balanced);
                    try
                    {
                        return engine.Evaluate(script);
                    }
                    catch (Exception ex)
                    {
                        return $"Error: {ex.GetBaseException().Message}";
                    }
                }
            });

            if (!task.Wait(timeoutMs))
            {
                stopwatch.Stop();
                return new Test262CaseResult
                {
                    Outcome = "timeout",
                    Category = "timeout",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Message = $"Timed out after {timeoutMs}ms."
                };
            }

            stopwatch.Stop();
            var raw = task.Result;
            var text = raw?.ToString() ?? string.Empty;
            var threw = IsErrorResult(text);

            if (metadata.NegativeType.Length == 0)
            {
                return new Test262CaseResult
                {
                    Outcome = threw ? "fail" : "pass",
                    Category = threw ? ClassifyError(text) : "pass",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Message = threw ? text : "ok"
                };
            }

            if (!threw)
            {
                return new Test262CaseResult
                {
                    Outcome = "fail",
                    Category = "negative_missed",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Message = $"Expected negative {metadata.NegativeType} but script succeeded."
                };
            }

            var matched = text.IndexOf(metadata.NegativeType, StringComparison.OrdinalIgnoreCase) >= 0;
            return new Test262CaseResult
            {
                Outcome = matched ? "pass" : "fail",
                Category = matched ? "negative_expected" : "negative_mismatch",
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = matched ? $"Expected negative matched: {metadata.NegativeType}" : $"Expected {metadata.NegativeType}, got: {text}"
            };
        }

        private static string ClassifyError(string text)
        {
            if (text.Contains("Test262Error", StringComparison.Ordinal))
            {
                return "assertion";
            }

            if (text.Contains("SyntaxError", StringComparison.Ordinal))
            {
                return "syntax_error";
            }

            if (text.Contains("TypeError", StringComparison.Ordinal))
            {
                return "type_error";
            }

            if (text.Contains("ReferenceError", StringComparison.Ordinal))
            {
                return "reference_error";
            }

            if (text.Contains("RangeError", StringComparison.Ordinal))
            {
                return "range_error";
            }

            return "runtime_error";
        }

        private static bool IsErrorResult(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("TypeError", StringComparison.Ordinal) ||
                   text.Contains("ReferenceError", StringComparison.Ordinal) ||
                   text.Contains("SyntaxError", StringComparison.Ordinal) ||
                   text.Contains("RangeError", StringComparison.Ordinal) ||
                   text.Contains("Test262Error", StringComparison.Ordinal);
        }

        private static string ComposeScenarioScript(string harnessSource, string testBody, string scenario)
        {
            var sb = new StringBuilder();
            sb.Append(harnessSource);
            sb.AppendLine();
            if (string.Equals(scenario, "strict", StringComparison.Ordinal))
            {
                sb.AppendLine("\"use strict\";");
            }

            sb.AppendLine(testBody);
            return sb.ToString();
        }

        private static string[] GetScenarios(HashSet<string> flags)
        {
            if (flags.Contains("onlyStrict"))
            {
                return new[] { "strict" };
            }

            if (flags.Contains("noStrict"))
            {
                return new[] { "default" };
            }

            return new[] { "default", "strict" };
        }

        private static string BuildHarnessSource(string rootPath, List<string> includes)
        {
            var harnessRoot = Path.Combine(rootPath, "harness");
            var merged = new StringBuilder();
            merged.AppendLine(Test262HostBootstrap);
            merged.AppendLine();
            AppendHarnessFile(merged, harnessRoot, "assert.js");
            AppendHarnessFile(merged, harnessRoot, "sta.js");

            foreach (var inc in includes.Distinct(StringComparer.Ordinal))
            {
                AppendHarnessFile(merged, harnessRoot, inc);
            }

            return merged.ToString();
        }

        private static void AppendHarnessFile(StringBuilder sb, string harnessRoot, string relativeFile)
        {
            var safeRelative = relativeFile.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(harnessRoot, safeRelative));
            if (!full.StartsWith(Path.GetFullPath(harnessRoot), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(full))
            {
                return;
            }

            sb.AppendLine(File.ReadAllText(full));
            sb.AppendLine();
        }

        private static string StripFrontMatter(string content)
        {
            return FrontMatterRegex.Replace(content, string.Empty);
        }

        private static Test262Metadata ParseMetadata(string content)
        {
            var metadata = new Test262Metadata();
            var match = FrontMatterRegex.Match(content);
            if (!match.Success)
            {
                return metadata;
            }

            var block = match.Groups["meta"].Value;
            var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var inNegative = false;
            var listKey = string.Empty;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("includes:", StringComparison.Ordinal))
                {
                    var inline = ParseInlineArray(line.Substring("includes:".Length)).ToList();
                    if (inline.Count > 0)
                    {
                        metadata.Includes.AddRange(inline);
                        listKey = string.Empty;
                    }
                    else
                    {
                        listKey = "includes";
                    }

                    inNegative = false;
                    continue;
                }

                if (line.StartsWith("flags:", StringComparison.Ordinal))
                {
                    var inline = ParseInlineArray(line.Substring("flags:".Length)).ToList();
                    if (inline.Count > 0)
                    {
                        foreach (var flag in inline)
                        {
                            metadata.Flags.Add(flag);
                        }
                        listKey = string.Empty;
                    }
                    else
                    {
                        listKey = "flags";
                    }

                    inNegative = false;
                    continue;
                }

                if (line.StartsWith("negative:", StringComparison.Ordinal))
                {
                    inNegative = true;
                    listKey = string.Empty;
                    continue;
                }

                if (inNegative && line.StartsWith("phase:", StringComparison.Ordinal))
                {
                    metadata.NegativePhase = line.Substring("phase:".Length).Trim();
                    continue;
                }

                if (inNegative && line.StartsWith("type:", StringComparison.Ordinal))
                {
                    metadata.NegativeType = line.Substring("type:".Length).Trim();
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    var item = line.Substring(1).Trim().Trim('"', '\'');
                    if (item.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(listKey, "includes", StringComparison.Ordinal))
                    {
                        metadata.Includes.Add(item);
                        continue;
                    }

                    if (string.Equals(listKey, "flags", StringComparison.Ordinal))
                    {
                        metadata.Flags.Add(item);
                        continue;
                    }
                }
            }

            return metadata;
        }

        private static IEnumerable<string> ParseInlineArray(string raw)
        {
            var text = raw.Trim();
            if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }

            var inner = text.Substring(1, text.Length - 2);
            return inner.Split(',')
                .Select(s => s.Trim().Trim('"', '\''))
                .Where(s => s.Length > 0);
        }

        private static Test262Options ParseOptions(string[] args)
        {
            var options = new Test262Options
            {
                RootPath = string.Empty,
                Workers = Environment.ProcessorCount,
                TimeoutMs = DefaultTimeoutMs,
                OutputPath = Path.Combine(Directory.GetCurrentDirectory(), "Results", "test262_fenrunner_results.json")
            };

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
                {
                    options.RootPath = arg.Substring("--root=".Length).Trim();
                    continue;
                }

                if (string.Equals(arg, "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.RootPath = args[++i];
                    continue;
                }

                if (arg.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--workers=".Length), out var workers) && workers > 0)
                    {
                        options.Workers = workers;
                    }

                    continue;
                }

                if (string.Equals(arg, "--workers", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var workers) && workers > 0)
                    {
                        options.Workers = workers;
                    }

                    continue;
                }

                if (arg.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--timeout-ms=".Length), out var timeoutMs) && timeoutMs > 0)
                    {
                        options.TimeoutMs = timeoutMs;
                    }

                    continue;
                }

                if (string.Equals(arg, "--timeout-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var timeoutMs) && timeoutMs > 0)
                    {
                        options.TimeoutMs = timeoutMs;
                    }

                    continue;
                }

                if (arg.StartsWith("--max=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--max=".Length), out var max) && max > 0)
                    {
                        options.MaxTests = max;
                    }

                    continue;
                }

                if (string.Equals(arg, "--max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var max) && max > 0)
                    {
                        options.MaxTests = max;
                    }

                    continue;
                }

                if (arg.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
                {
                    options.Filter = arg.Substring("--filter=".Length);
                    continue;
                }

                if (string.Equals(arg, "--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Filter = args[++i];
                    continue;
                }

                if (arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                {
                    options.OutputPath = Path.GetFullPath(arg.Substring("--output=".Length));
                    continue;
                }

                if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutputPath = Path.GetFullPath(args[++i]);
                    continue;
                }

                if (arg.StartsWith("--event-log=", StringComparison.OrdinalIgnoreCase))
                {
                    options.EventLogPath = Path.GetFullPath(arg.Substring("--event-log=".Length));
                    continue;
                }

                if (string.Equals(arg, "--event-log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.EventLogPath = Path.GetFullPath(args[++i]);
                    continue;
                }

                if (string.Equals(arg, "--worker-mode", StringComparison.OrdinalIgnoreCase))
                {
                    options.WorkerMode = true;
                    continue;
                }

                if (arg.StartsWith("--shard-index=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--shard-index=".Length), out var shardIndex) && shardIndex >= 0)
                    {
                        options.ShardIndex = shardIndex;
                    }
                    continue;
                }

                if (string.Equals(arg, "--shard-index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var shardIndex) && shardIndex >= 0)
                    {
                        options.ShardIndex = shardIndex;
                    }
                    continue;
                }

                if (arg.StartsWith("--shard-count=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--shard-count=".Length), out var shardCount) && shardCount > 0)
                    {
                        options.ShardCount = shardCount;
                    }
                    continue;
                }

                if (string.Equals(arg, "--shard-count", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var shardCount) && shardCount > 0)
                    {
                        options.ShardCount = shardCount;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(options.RootPath))
            {
                throw new ArgumentException("test262 requires --root <path-to-official-test262-clone>");
            }

            options.RootPath = Path.GetFullPath(options.RootPath);
            if (!Directory.Exists(options.RootPath))
            {
                throw new DirectoryNotFoundException($"test262 root not found: {options.RootPath}");
            }

            if (options.ShardCount > 0 && options.ShardIndex >= options.ShardCount)
            {
                throw new ArgumentException($"Invalid shard configuration: shard-index {options.ShardIndex} >= shard-count {options.ShardCount}");
            }

            return options;
        }

        private sealed class Test262Options
        {
            public string RootPath { get; set; }
            public int Workers { get; set; }
            public int TimeoutMs { get; set; }
            public int? MaxTests { get; set; }
            public string Filter { get; set; }
            public string OutputPath { get; set; }
            public string EventLogPath { get; set; }
            public bool WorkerMode { get; set; }
            public int ShardIndex { get; set; }
            public int ShardCount { get; set; }
        }

        private sealed class Test262Metadata
        {
            public List<string> Includes { get; } = new List<string>();
            public HashSet<string> Flags { get; } = new HashSet<string>(StringComparer.Ordinal);
            public string NegativePhase { get; set; } = string.Empty;
            public string NegativeType { get; set; } = string.Empty;
        }

        private sealed class Test262ReportPayload
        {
            public Test262Summary Summary { get; set; }
            public List<Test262CaseResult> Results { get; set; }
        }

        private sealed class Test262Summary
        {
            public string Root { get; set; }
            public int Workers { get; set; }
            public int TimeoutMs { get; set; }
            public string Filter { get; set; }
            public int MaxTests { get; set; }
            public string EventLogPath { get; set; }
            public int DiscoveredTests { get; set; }
            public int TotalScenarios { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public int TimedOut { get; set; }
            public Dictionary<string, int> Categories { get; set; }
            public string RunAtUtc { get; set; }
        }

        private sealed class Test262CaseResult
        {
            public string File { get; set; }
            public string Scenario { get; set; }
            public string Outcome { get; set; }
            public string Category { get; set; }
            public long DurationMs { get; set; }
            public string Message { get; set; }
        }

        private sealed class Test262RunAbortedException : Exception
        {
            public Test262RunAbortedException(string message)
                : base(message)
            {
            }
        }
    }
}
