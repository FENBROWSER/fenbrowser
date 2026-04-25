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
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Workers)
            };

            await Parallel.ForEachAsync(tests, parallelOptions, (testPath, ct) =>
            {
                var scenarioResults = RunSingleTest(options.RootPath, testPath);
                foreach (var result in scenarioResults)
                {
                    results.Add(result);
                }

                var done = Interlocked.Increment(ref processed);
                if (done % 100 == 0 || done == tests.Count)
                {
                    Console.WriteLine($"[test262] progress {done}/{tests.Count}");
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

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
            Console.WriteLine($"[test262] pass={summary.Passed} fail={summary.Failed} skip={summary.Skipped} total={summary.TotalScenarios}");
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
            var workerTasks = new List<Task<int>>(workerCount);
            for (var i = 0; i < workerCount; i++)
            {
                var workerOutput = Path.Combine(workerDir, $"worker_{i:D2}.json");
                workerOutputs[i] = workerOutput;
                workerTasks.Add(LaunchWorkerAsync(dllPath, options, i, workerCount, workerOutput));
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
            var summary = BuildSummary(options, discoveredTests, ordered);
            var mergedPayload = new Test262ReportPayload
            {
                Summary = summary,
                Results = ordered
            };

            var json = JsonSerializer.Serialize(mergedPayload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(options.OutputPath, json, new UTF8Encoding(false));

            Console.WriteLine($"[test262] output={options.OutputPath}");
            Console.WriteLine($"[test262] pass={summary.Passed} fail={summary.Failed} skip={summary.Skipped} total={summary.TotalScenarios}");
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

        private static async Task<int> LaunchWorkerAsync(string dllPath, Test262Options options, int shardIndex, int shardCount, string workerOutput)
        {
            var args = new StringBuilder();
            args.Append('"').Append(dllPath).Append('"');
            args.Append(" test262");
            args.Append(" --root ").Append('"').Append(options.RootPath).Append('"');
            args.Append(" --workers 1");
            if (options.MaxTests.HasValue)
            {
                args.Append(" --max ").Append(options.MaxTests.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(options.Filter))
            {
                args.Append(" --filter ").Append('"').Append(options.Filter).Append('"');
            }

            args.Append(" --output ").Append('"').Append(workerOutput).Append('"');
            args.Append(" --worker-mode");
            args.Append(" --shard-index ").Append(shardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Append(" --shard-count ").Append(shardCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = args.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return 1;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        private static Test262Summary BuildSummary(Test262Options options, int discoveredTests, List<Test262CaseResult> results)
        {
            var pass = results.Count(r => r.Outcome == "pass");
            var fail = results.Count(r => r.Outcome == "fail");
            var skip = results.Count(r => r.Outcome == "skip");
            return new Test262Summary
            {
                Root = options.RootPath,
                Workers = options.Workers,
                Filter = options.Filter ?? string.Empty,
                MaxTests = options.MaxTests ?? 0,
                DiscoveredTests = discoveredTests,
                TotalScenarios = results.Count,
                Passed = pass,
                Failed = fail,
                Skipped = skip,
                RunAtUtc = DateTime.UtcNow.ToString("o")
            };
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

        private static List<Test262CaseResult> RunSingleTest(string rootPath, string absolutePath)
        {
            var relative = Path.GetRelativePath(Path.Combine(rootPath, "test"), absolutePath).Replace('\\', '/');

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
                        Message = $"Unable to read test file: {ex.Message}"
                    }
                };
            }

            var metadata = ParseMetadata(content);
            if (metadata.Flags.Contains("module") || metadata.Flags.Contains("async") || metadata.Flags.Contains("generated"))
            {
                return new List<Test262CaseResult>
                {
                    new Test262CaseResult
                    {
                        File = relative,
                        Scenario = "default",
                        Outcome = "skip",
                        Message = "Skipped unsupported test flag (module/async/generated)."
                    }
                };
            }

            var scenarios = GetScenarios(metadata.Flags);
            var harnessSource = BuildHarnessSource(rootPath, metadata.Includes);
            var testBody = StripFrontMatter(content);
            var results = new List<Test262CaseResult>();

            foreach (var scenario in scenarios)
            {
                var script = ComposeScenarioScript(harnessSource, testBody, scenario);
                var runResult = ExecuteScript(script, metadata, scenario);
                runResult.File = relative;
                runResult.Scenario = scenario;
                results.Add(runResult);
            }

            return results;
        }

        private static Test262CaseResult ExecuteScript(string script, Test262Metadata metadata, string scenario)
        {
            object raw;
            lock (EngineExecutionLock)
            {
                var host = new JsHostAdapter(_ => { }, (_, __) => { }, _ => { }, log: _ => { });
                var engine = new JavaScriptEngine(host, JavaScriptRuntimeProfile.Balanced);
                try
                {
                    raw = engine.Evaluate(script);
                }
                catch (Exception ex)
                {
                    raw = $"Error: {ex.GetBaseException().Message}";
                }
            }

            var text = raw?.ToString() ?? string.Empty;
            var threw = IsErrorResult(text);

            if (metadata.NegativeType.Length == 0)
            {
                return new Test262CaseResult
                {
                    Outcome = threw ? "fail" : "pass",
                    Message = threw ? text : "ok"
                };
            }

            if (!threw)
            {
                return new Test262CaseResult
                {
                    Outcome = "fail",
                    Message = $"Expected negative {metadata.NegativeType} but script succeeded."
                };
            }

            var matched = text.IndexOf(metadata.NegativeType, StringComparison.OrdinalIgnoreCase) >= 0;
            return new Test262CaseResult
            {
                Outcome = matched ? "pass" : "fail",
                Message = matched ? $"Expected negative matched: {metadata.NegativeType}" : $"Expected {metadata.NegativeType}, got: {text}"
            };
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
            public int? MaxTests { get; set; }
            public string Filter { get; set; }
            public string OutputPath { get; set; }
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
            public string Filter { get; set; }
            public int MaxTests { get; set; }
            public int DiscoveredTests { get; set; }
            public int TotalScenarios { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public string RunAtUtc { get; set; }
        }

        private sealed class Test262CaseResult
        {
            public string File { get; set; }
            public string Scenario { get; set; }
            public string Outcome { get; set; }
            public string Message { get; set; }
        }
    }
}
