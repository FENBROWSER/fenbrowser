// =============================================================================
// Program.cs
// FenBrowser Unified Conformance CLI
//
// PURPOSE: Orchestrate all conformance test suites (Test262, WPT, Acid, html5lib)
//          from a single CLI and generate aggregated compliance reports.
//
// USAGE:
//   FenBrowser.Conformance run all [options]
//   FenBrowser.Conformance run test262 [category] [options]
//   FenBrowser.Conformance run wpt [category] [options]
//   FenBrowser.Conformance run acid [1|2|3] [options]
//   FenBrowser.Conformance run html5lib [options]
//   FenBrowser.Conformance report [options]
// =============================================================================

using System.Diagnostics;
using FenBrowser.FenEngine.Testing;
using FenBrowser.Host.ProcessIsolation;
using System.Text.Json;

namespace FenBrowser.Conformance;

public static class Program
{
    private static string _repoRoot = "";
    private static bool _verbose = false;
    private static string _outputPath = string.Empty;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // Auto-discover repo root
        _repoRoot = DiscoverRepoRoot();

        // Parse global flags
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose" or "-v":
                    _verbose = true;
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    _outputPath = args[++i];
                    break;
            }
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "run" when args.Length >= 2 => await RunSuiteAsync(args[1..]),
                "gate" => RunGate(args.Skip(1).ToArray()),
                "matrix" => BuildSpecMatrix(args.Skip(1).ToArray()),
                "fullsweep-inventory" => BuildFullSweepInventory(args.Skip(1).ToArray()),
                "ipc-fuzz" => RunIpcFuzz(args.Skip(1).ToArray()),
                "parser-fuzz" => RunParserFuzz(args.Skip(1).ToArray()),
                "a11y-validate" => RunAccessibilityValidation(),
                "corb-validate" => RunCorbValidation(),
                "report" => GenerateReport(),
                "--help" or "-h" or "help" => PrintUsage(),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            if (_verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // =========================================================================
    // Suite Dispatch
    // =========================================================================

    private static async Task<int> RunSuiteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: run <test262|wpt|acid|html5lib|all> [options]");
            return 1;
        }

        var suite = args[0].ToLowerInvariant();
        var report = new ConformanceReport();

        // Auto-default: save results to Results/<suite>_results.json
        if (string.IsNullOrEmpty(_outputPath))
        {
            string fileName = suite switch
            {
                "all" => "conformance_results.json",
                "test262" => "test262_results.json",
                "wpt" => "wpt_results.json",
                "acid" => "acid_results.json",
                "html5lib" => "html5lib_results.json",
                _ => $"{suite}_results.json"
            };
            _outputPath = Path.Combine(_repoRoot, "Results", fileName);
        }

        switch (suite)
        {
            case "all":
                await RunTest262Async(report, args.Skip(1).ToArray());
                await RunHtml5LibAsync(report, args.Skip(1).ToArray());
                await RunAcidAsync(report, args.Skip(1).ToArray());
                // WPT requires setup; only run if root exists
                var wptRoot = Path.Combine(_repoRoot, "wpt");
                if (Directory.Exists(wptRoot))
                    await RunWptAsync(report, args.Skip(1).ToArray());
                else
                    Console.WriteLine("[Conformance] Skipping WPT (wpt/ directory not found)");
                break;

            case "test262":
                await RunTest262Async(report, args.Skip(1).ToArray());
                break;

            case "wpt":
                await RunWptAsync(report, args.Skip(1).ToArray());
                break;

            case "acid":
                await RunAcidAsync(report, args.Skip(1).ToArray());
                break;

            case "html5lib":
                await RunHtml5LibAsync(report, args.Skip(1).ToArray());
                break;

            default:
                Console.Error.WriteLine($"Unknown suite: {suite}");
                return 1;
        }

        // Save results — clear content first (each run overwrites previous)
        if (!string.IsNullOrEmpty(_outputPath))
        {
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            report.SaveReport(_outputPath);
            Console.WriteLine($"\n[Conformance] Results saved to {_outputPath}");
        }

        // Always print to console
        Console.WriteLine();
        Console.WriteLine(report.GenerateMarkdown());

        return 0;
    }

    // =========================================================================
    // Individual Suite Runners
    // =========================================================================

    private static async Task RunTest262Async(ConformanceReport report, string[] args)
    {
        var test262Root = Path.Combine(_repoRoot, "test262");
        if (!Directory.Exists(test262Root))
        {
            Console.Error.WriteLine("[Conformance] test262/ directory not found. Skipping.");
            return;
        }

        string category = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : "";
        int maxTests = GetMaxTests(args, 500); // Default to 500 for "all" runs

        Console.WriteLine(
            $"[Test262] Running{(string.IsNullOrEmpty(category) ? "" : $" category: {category}")} (max: {maxTests})...");
        var runner = new Test262Runner(test262Root, 10_000);
        var sw = Stopwatch.StartNew();

        IReadOnlyList<Test262Runner.TestResult> results;
        if (!string.IsNullOrEmpty(category))
        {
            results = await runner.RunCategoryAsync(category, ProgressCallback, maxTests);
        }
        else
        {
            // Run a slice from the beginning
            results = await runner.RunSliceAsync("", 0, maxTests, ProgressCallback);
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine(runner.GenerateSummary());

        report.AddResult("Test262", string.IsNullOrEmpty(category) ? "all" : category,
            results.Count, results.Count(r => r.Passed), results.Count(r => !r.Passed),
            sw.Elapsed);
    }

    private static async Task RunHtml5LibAsync(ConformanceReport report, string[] args)
    {
        var bootstrap = HasFlag(args, "--bootstrap-html5lib");
        var differential = HasFlag(args, "--differential");
        var oraclePython = ResolveOption(args, "--oracle-python") ?? "python";

        // Look for html5lib-tests in common locations
        string? html5LibRoot = null;
        var candidates = new[]
        {
            Path.Combine(_repoRoot, "html5lib-tests"),
            Path.Combine(_repoRoot, "test_data", "html5lib-tests"),
            Path.Combine(_repoRoot, "tests", "html5lib-tests"),
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                html5LibRoot = c;
                break;
            }
        }

        if (html5LibRoot == null && bootstrap)
        {
            html5LibRoot = Path.Combine(_repoRoot, "html5lib-tests");
            Console.WriteLine("[html5lib] Bootstrapping html5lib-tests...");
            var cloneExit = RunProcess(
                "git",
                $"clone --depth 1 https://github.com/html5lib/html5lib-tests \"{html5LibRoot}\"",
                _repoRoot,
                out var cloneStdOut,
                out var cloneStdErr);
            if (cloneExit != 0)
            {
                Console.WriteLine("[html5lib] bootstrap failed.");
                if (!string.IsNullOrWhiteSpace(cloneStdOut)) Console.WriteLine(cloneStdOut.Trim());
                if (!string.IsNullOrWhiteSpace(cloneStdErr)) Console.WriteLine(cloneStdErr.Trim());
                html5LibRoot = null;
            }
        }

        if (html5LibRoot == null)
        {
            Console.WriteLine("[html5lib] html5lib-tests directory not found. Skipping.");
            Console.WriteLine("           Clone https://github.com/html5lib/html5lib-tests into your repo root.");
            report.AddResult("html5lib", "tree-construction", 0, 0, 0, TimeSpan.Zero,
                "Not available (html5lib-tests not found)");
            return;
        }

        int maxTests = GetMaxTests(args, 500);
        IHtmlTreeOracle? oracle = null;
        if (differential)
        {
            oracle = new PythonHtml5LibOracle(oraclePython);
            if (!oracle.IsAvailable)
            {
                Console.WriteLine($"[html5lib] Differential oracle unavailable: {oracle.AvailabilityError}");
            }
        }

        Console.WriteLine($"[html5lib] Running tree construction tests (max: {maxTests}){(differential ? " with differential mode" : "")}...");
        var runner = new Html5LibTestRunner(html5LibRoot, differentialMode: differential, oracle: oracle);
        var sw = Stopwatch.StartNew();

        var results = await runner.RunAllAsync(ProgressCallback, maxTests);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine(runner.GenerateSummary());

        report.AddResult("html5lib", "tree-construction",
            results.Count, results.Count(r => r.Passed), results.Count(r => !r.Passed),
            sw.Elapsed);
    }

    private static async Task RunAcidAsync(ConformanceReport report, string[] args)
    {
        string testNum = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : "all";

        Console.WriteLine($"[Acid] Running Acid tests: {testNum}...");
        var runner = new AcidTestRunner(Path.Combine(_repoRoot, "TestResults", "acid"));
        var sw = Stopwatch.StartNew();

        int passed = 0, failed = 0;

        if (testNum is "1" or "all")
        {
            try
            {
                await runner.RunAcid1Async(null!);
                passed++;
                Console.WriteLine("  Acid1: PASS");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  Acid1: FAIL ({ex.Message})");
            }
        }

        if (testNum is "2" or "all")
        {
            try
            {
                await runner.RunAcid2Async(null!);
                passed++;
                Console.WriteLine("  Acid2: PASS");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  Acid2: FAIL ({ex.Message})");
            }
        }

        if (testNum is "3" or "all")
        {
            try
            {
                await runner.RunAcid3Async(null!);
                passed++;
                Console.WriteLine("  Acid3: PASS");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  Acid3: FAIL ({ex.Message})");
            }
        }

        sw.Stop();
        report.AddResult("Acid", testNum, passed + failed, passed, failed, sw.Elapsed);
    }

    private static async Task RunWptAsync(ConformanceReport report, string[] args)
    {
        var wptRoot = Path.Combine(_repoRoot, "wpt");
        if (!Directory.Exists(wptRoot))
        {
            Console.Error.WriteLine("[WPT] wpt/ directory not found. Skipping.");
            report.AddResult("WPT", "all", 0, 0, 0, TimeSpan.Zero,
                "Not available (wpt/ not found)");
            return;
        }

        string category = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : "dom";
        const int safeWptMax = 50;
        int requestedMax = GetMaxTests(args, 50);
        int maxTests = Math.Min(requestedMax, safeWptMax);
        if (requestedMax > safeWptMax)
        {
            Console.WriteLine(
                $"[WPT] Requested --max {requestedMax} exceeds safe limit {safeWptMax}; clamping to {safeWptMax} to avoid known VM recursion crash cases.");
        }

        Console.WriteLine($"[WPT] Running category: {category} (max: {maxTests})...");

        var navigator = new HeadlessNavigator(wptRoot, 30_000);
        var runner = new WPTTestRunner(wptRoot, navigator.GetNavigatorDelegate(), 30_000);
        var sw = Stopwatch.StartNew();

        var results = await runner.RunCategoryAsync(category, ProgressCallback, maxTests);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine(runner.GenerateSummary());

        report.AddResult("WPT", category,
            results.Count, results.Count(r => r.Success), results.Count(r => !r.Success),
            sw.Elapsed);
    }

    // =========================================================================
    // Gate Command
    // =========================================================================

    private static int RunIpcFuzz(string[] args)
    {
        var results = IpcFuzzBaseline.RunDefaultSuite();
        var passed = results.All(r => r.Failures == 0);

        foreach (var result in results)
        {
            Console.WriteLine($"[IPC-FUZZ] {result.Channel}: cases={result.CasesRun} ok={result.Successes} fail={result.Failures}");
            foreach (var sample in result.FailureSamples)
            {
                Console.WriteLine($"  sample: {sample}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_outputPath))
        {
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = results.Select(r => new
            {
                channel = r.Channel,
                casesRun = r.CasesRun,
                successes = r.Successes,
                failures = r.Failures,
                failureSamples = r.FailureSamples
            });

            File.WriteAllText(_outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        return passed ? 0 : 2;
    }

    private static int RunParserFuzz(string[] args)
    {
        var scriptPath = Path.Combine(_repoRoot, "scripts", "ci", "run-parser-fuzz-regressions.ps1");
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"[parser-fuzz] Script not found: {scriptPath}");
            return 1;
        }

        var exitCode = RunProcess(
            "powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            _repoRoot,
            out var stdOut,
            out var stdErr);

        if (!string.IsNullOrWhiteSpace(stdOut)) Console.WriteLine(stdOut.Trim());
        if (!string.IsNullOrWhiteSpace(stdErr)) Console.WriteLine(stdErr.Trim());
        return exitCode;
    }

    private static int RunAccessibilityValidation()
    {
        var resolvedOutput = string.IsNullOrWhiteSpace(_outputPath)
            ? Path.Combine(_repoRoot, "Results", "a11y_platform_snapshot.json")
            : _outputPath;
        return AccessibilityValidation.Run(_repoRoot, resolvedOutput);
    }

    private static int RunCorbValidation()
    {
        var resolvedOutput = string.IsNullOrWhiteSpace(_outputPath)
            ? Path.Combine(_repoRoot, "Results", "corb_validation.json")
            : _outputPath;
        return CorbValidation.Run(_repoRoot, resolvedOutput);
    }

    private static int RunGate(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: gate <policy-path>|default <name|all>");
            return 1;
        }

        List<ConformanceGateResult> results;
        if (string.Equals(args[0], "default", StringComparison.OrdinalIgnoreCase))
        {
            var selector = args.Length >= 2 ? args[1] : "all";
            var policyPaths = ResolveBuiltInPolicies(selector);
            if (policyPaths.Count == 0)
            {
                Console.Error.WriteLine($"Unknown built-in gate selector: {selector}");
                return 1;
            }

            results = policyPaths
                .Select(path => ConformanceGateEvaluator.Evaluate(_repoRoot, ConformanceGatePolicy.Load(path)))
                .ToList();
        }
        else
        {
            var policyPath = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(_repoRoot, args[0]);
            results = new List<ConformanceGateResult>
            {
                ConformanceGateEvaluator.Evaluate(_repoRoot, ConformanceGatePolicy.Load(policyPath))
            };
        }

        var markdown = string.Join(Environment.NewLine + Environment.NewLine, results.Select(r => r.ToMarkdown()));
        Console.WriteLine(markdown);

        if (!string.IsNullOrWhiteSpace(_outputPath))
        {
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (_outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var jsonPayload = "[" + string.Join("," + Environment.NewLine, results.Select(r => r.ToJson())) + "]";
                File.WriteAllText(_outputPath, jsonPayload);
            }
            else
            {
                File.WriteAllText(_outputPath, markdown);
            }
        }

        return results.All(r => r.Passed) ? 0 : 2;
    }

    private static int BuildSpecMatrix(string[] args)
    {
        var wptPath = ResolveOption(args, "--wpt")
            ?? Path.Combine(_repoRoot, "Results", "wpt_results_latest.json");
        var scope = ResolveOption(args, "--scope") ?? "html-css-core";
        var format = (ResolveOption(args, "--format") ?? "json").ToLowerInvariant();

        try
        {
            var matrix = SpecComplianceMatrixBuilder.BuildFromWpt(wptPath, scope);
            var outputPath = string.IsNullOrWhiteSpace(_outputPath)
                ? Path.Combine(_repoRoot, "Results", format == "md" ? "spec_compliance_matrix.md" : "spec_compliance_matrix.json")
                : _outputPath;

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = format == "md" ? matrix.ToMarkdown() : matrix.ToJson();
            File.WriteAllText(outputPath, payload);

            Console.WriteLine($"[Conformance] Spec compliance matrix written: {outputPath}");
            Console.WriteLine($"[Conformance] Scope: {scope} | Total: {matrix.Summary.Total} | Failed: {matrix.Summary.Failed} | Clusters: {matrix.Clusters.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Conformance] Failed to build spec matrix: {ex.Message}");
            if (_verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }
    }

    private static int BuildFullSweepInventory(string[] args)
    {
        var format = (ResolveOption(args, "--format") ?? "json").ToLowerInvariant();
        var wptPath = ResolveOption(args, "--wpt");
        var test262Path = ResolveOption(args, "--test262");

        try
        {
            var inventory = FullSweepInventoryBuilder.Build(_repoRoot, wptPath, test262Path);
            var outputPath = string.IsNullOrWhiteSpace(_outputPath)
                ? Path.Combine(_repoRoot, "Results", format == "md" ? "full_sweep_inventory.md" : "full_sweep_inventory.json")
                : _outputPath;

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = format == "md" ? inventory.ToMarkdown() : inventory.ToJson();
            File.WriteAllText(outputPath, payload);

            Console.WriteLine($"[Conformance] Full sweep inventory written: {outputPath}");
            Console.WriteLine($"[Conformance] Items: {inventory.Summary.TotalItems} (critical={inventory.Summary.Critical}, high={inventory.Summary.High}, medium={inventory.Summary.Medium}, low={inventory.Summary.Low})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Conformance] Failed to build full sweep inventory: {ex.Message}");
            if (_verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }
    }

    private static List<string> ResolveBuiltInPolicies(string selector)
    {
        var gatesRoot = Path.Combine(_repoRoot, "FenBrowser.Conformance", "Gates");
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["test262"] = new[] { Path.Combine(gatesRoot, "test262_gate_policy.json") },
            ["wpt-c80"] = new[] { Path.Combine(gatesRoot, "wpt_dom_gate_80.json") },
            ["wpt-c90"] = new[] { Path.Combine(gatesRoot, "wpt_dom_gate_90.json") },
            ["wpt-d"] = new[] { Path.Combine(gatesRoot, "wpt_css_layout_gate.json") },
            ["wpt-e"] = new[] { Path.Combine(gatesRoot, "wpt_fetch_cors_gate.json") },
            ["all"] = new[]
            {
                Path.Combine(gatesRoot, "test262_gate_policy.json"),
                Path.Combine(gatesRoot, "wpt_dom_gate_80.json"),
                Path.Combine(gatesRoot, "wpt_dom_gate_90.json"),
                Path.Combine(gatesRoot, "wpt_css_layout_gate.json"),
                Path.Combine(gatesRoot, "wpt_fetch_cors_gate.json")
            }
        };

        return map.TryGetValue(selector, out var values)
            ? values.ToList()
            : new List<string>();
    }

    // =========================================================================
    // Report Command
    // =========================================================================

    private static int GenerateReport()
    {
        Console.WriteLine("[Conformance] Generating empty report template...");
        Console.WriteLine("  Run 'FenBrowser.Conformance run all' to generate a report with data.");

        var report = new ConformanceReport();
        report.AddResult("Test262", "(not run)", 0, 0, 0, TimeSpan.Zero);
        report.AddResult("WPT", "(not run)", 0, 0, 0, TimeSpan.Zero);
        report.AddResult("Acid", "(not run)", 0, 0, 0, TimeSpan.Zero);
        report.AddResult("html5lib", "(not run)", 0, 0, 0, TimeSpan.Zero);

        if (!string.IsNullOrEmpty(_outputPath))
        {
            report.SaveReport(_outputPath);
            Console.WriteLine($"  Report template saved to {_outputPath}");
        }
        else
        {
            Console.WriteLine(report.GenerateMarkdown());
        }

        return 0;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string DiscoverRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "FenBrowser.sln")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback to current directory
        return Directory.GetCurrentDirectory();
    }

    private static int GetMaxTests(string[] args, int defaultMax)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--max" && i + 1 < args.Length && int.TryParse(args[i + 1], out int m))
                return m;
        }

        return defaultMax;
    }

    private static string? ResolveOption(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static int RunProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        out string stdOut,
        out string stdErr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            stdOut = string.Empty;
            stdErr = $"Failed to start process: {fileName}";
            return 1;
        }

        stdOut = process.StandardOutput.ReadToEnd();
        stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void ProgressCallback(string name, int count)
    {
        if (_verbose || count % 50 == 0)
            Console.Write($"\r  [{count}] {name}    ");
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
FenBrowser Unified Conformance CLI
===================================

USAGE:
  FenBrowser.Conformance <command> [options]

COMMANDS:
  run all [options]              Run all test suites (Test262, WPT, Acid, html5lib)
  run test262 [category]         Run ECMAScript Test262 tests
  run wpt [category]             Run Web Platform Tests
  run acid [1|2|3]               Run Acid tests
  run html5lib                   Run html5lib parser tests
  gate <policy>|default <name>   Evaluate WPT/Test262 milestone gate policies
  ipc-fuzz                       Run IPC fuzz-baseline suite for renderer/network/target channels
  parser-fuzz                    Run parser/renderer hostile corpus fuzz regression script
  a11y-validate                  Export platform accessibility mapping snapshot artifact
  corb-validate                  Run bounded CORB validation cases and write artifact
  matrix                         Build HTML/CSS spec compliance matrix from WPT results
  fullsweep-inventory            Build Core/Engine/Host hardening inventory (stubs/basic/legacy bridges)
  report                         Generate conformance report template
  help                           Show this help message

OPTIONS:
  --output|-o <path>             Write report to file
  --max <N>                      Max tests per suite
  --bootstrap-html5lib           Auto-clone html5lib-tests when missing (run html5lib)
  --differential                 Compare html5lib run with external oracle (python html5lib)
  --oracle-python <exe>          Python executable for differential oracle (default: python)
  --wpt <path>                   WPT result JSON path for matrix command
  --test262 <path>               Test262 result JSON path for fullsweep-inventory
  --scope <name>                 Scope label for matrix command (default: html-css-core)
  --format <json|md>             Output format for matrix/fullsweep-inventory (default: json)
  --verbose|-v                   Verbose output

EXAMPLES:
  FenBrowser.Conformance run all --max 100 -o conformance_report.md
  FenBrowser.Conformance run test262 language/expressions --max 50
  FenBrowser.Conformance gate default all
  FenBrowser.Conformance gate FenBrowser.Conformance/Gates/test262_gate_policy.json
  FenBrowser.Conformance matrix --wpt Results/wpt_results_latest.json --scope html-css-core --format json
  FenBrowser.Conformance fullsweep-inventory --format md
  FenBrowser.Conformance run acid 2
  FenBrowser.Conformance run html5lib --verbose
");
        return 0;
    }
}
