// SpecRef: WPT external harness execution and deterministic reporting semantics
// CapabilityId: VERIFY-WPT-EXTERNAL-01
// Determinism: strict
// FallbackPolicy: fail-closed
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FenBrowser.Tooling
{
    internal static class WptToolRunner
    {
        private const int DefaultTimeoutSeconds = 600;
        private const int DefaultProcesses = 10;

        public static async Task RunAsync(string[] args)
        {
            var options = ParseOptions(args);
            Directory.CreateDirectory(options.OutputDir);

            var rawLogPath = Path.Combine(options.OutputDir, "wpt.raw.json");
            var reportPath = Path.Combine(options.OutputDir, "wpt.report.json");
            var machLogPath = Path.Combine(options.OutputDir, "wpt.mach.log");
            var stdoutPath = Path.Combine(options.OutputDir, "wpt.stdout.log");
            var stderrPath = Path.Combine(options.OutputDir, "wpt.stderr.log");
            var summaryPath = Path.Combine(options.OutputDir, "wpt.summary.json");

            var command = BuildCommand(options, rawLogPath, reportPath, machLogPath);
            var startedAt = DateTime.UtcNow;
            Console.WriteLine($"[wpt] output-dir={options.OutputDir}");
            Console.WriteLine($"[wpt] tests={string.Join(" ", options.Tests)}");
            Console.WriteLine($"[wpt] processes={options.Processes} timeout-seconds={options.TimeoutSeconds}");
            if (!string.IsNullOrWhiteSpace(options.VenvPath))
            {
                Console.WriteLine($"[wpt] venv={options.VenvPath} skip-venv-setup={options.SkipVenvSetup}");
            }

            var result = await RunProcessAsync(options.WptRoot, command, stdoutPath, stderrPath, options.TimeoutSeconds).ConfigureAwait(false);
            var endedAt = DateTime.UtcNow;
            var rawCounts = CountRawLog(rawLogPath);

            var summary = new WptSummary
            {
                WptRoot = options.WptRoot,
                BrowserBinary = options.BrowserBinary,
                WebDriverBinary = options.WebDriverBinary,
                Tests = options.Tests,
                Processes = options.Processes,
                TimeoutSeconds = options.TimeoutSeconds,
                VenvPath = options.VenvPath,
                SkipVenvSetup = options.SkipVenvSetup,
                TimedOut = result.TimedOut,
                ExitCode = result.ExitCode,
                FailurePhase = result.TimedOut && rawCounts.TestStart == 0 ? "wpt_startup" : string.Empty,
                StartedAtUtc = startedAt.ToString("o"),
                FinishedAtUtc = endedAt.ToString("o"),
                DurationSeconds = (endedAt - startedAt).TotalSeconds,
                RawLogPath = rawLogPath,
                ReportPath = reportPath,
                MachLogPath = machLogPath,
                StdoutPath = stdoutPath,
                StderrPath = stderrPath,
                TestStart = rawCounts.TestStart,
                TestEnd = rawCounts.TestEnd,
                StatusCounts = rawCounts.StatusCounts,
                Command = command.FileName + " " + string.Join(" ", command.Arguments)
            };

            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(summaryPath, json, new UTF8Encoding(false));

            Console.WriteLine($"[wpt] summary={summaryPath}");
            Console.WriteLine($"[wpt] raw={rawLogPath}");
            Console.WriteLine($"[wpt] report={reportPath}");
            Console.WriteLine($"[wpt] exit={summary.ExitCode} timedOut={summary.TimedOut} phase={summary.FailurePhase} testStart={summary.TestStart} testEnd={summary.TestEnd} statuses={FormatStatusCounts(summary.StatusCounts)}");
        }

        private static WptCommand BuildCommand(WptOptions options, string rawLogPath, string reportPath, string machLogPath)
        {
            var arguments = new List<string>
            {
                "wpt"
            };

            if (!string.IsNullOrWhiteSpace(options.VenvPath))
            {
                arguments.Add("--venv");
                arguments.Add(options.VenvPath);
                if (options.SkipVenvSetup)
                {
                    arguments.Add("--skip-venv-setup");
                }
            }

            arguments.AddRange(new[]
            {
                "run",
                "--yes",
                "--no-manifest-update",
                "--no-pause-after-test",
                "--processes",
                options.Processes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--binary",
                options.BrowserBinary,
                "--webdriver-binary",
                options.WebDriverBinary,
                "--log-raw",
                rawLogPath,
                "--log-wptreport",
                reportPath,
                "--log-mach",
                machLogPath,
                "fenbrowser"
            });

            arguments.AddRange(options.Tests);

            // Invoke the WPT venv's interpreter directly. The bare "python" on PATH
            // may be an unrelated environment lacking wptrunner deps (mozlog etc.);
            // wpt.py imports those before it activates --venv, so the launching
            // interpreter itself must already have them.
            var pythonExe = "python";
            if (!string.IsNullOrWhiteSpace(options.VenvPath))
            {
                var venvPython = Path.Combine(options.VenvPath, "Scripts", "python.exe");
                if (File.Exists(venvPython))
                {
                    pythonExe = venvPython;
                }
            }

            return new WptCommand(pythonExe, arguments);
        }

        private static async Task<WptProcessResult> RunProcessAsync(string workingDirectory, WptCommand command, string stdoutPath, string stderrPath, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in command.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            var currentToolingExe = Path.Combine(AppContext.BaseDirectory, "FenBrowser.Tooling.exe");
            if (File.Exists(currentToolingExe))
            {
                psi.Environment["FEN_WPT_TOOLING_EXE"] = currentToolingExe;
            }

            using var stdout = new StreamWriter(stdoutPath, append: false, new UTF8Encoding(false));
            using var stderr = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false));
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new WptProcessResult { ExitCode = 1 };
            }

            var stdoutTask = RelayOutputAsync(process.StandardOutput, stdout, "[wpt]");
            var stderrTask = RelayOutputAsync(process.StandardError, stderr, "[wpt][stderr]");
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var exitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
            var timedOut = completed == timeoutTask;
            if (timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new WptProcessResult
            {
                ExitCode = timedOut ? -1 : process.ExitCode,
                TimedOut = timedOut
            };
        }

        private static async Task RelayOutputAsync(StreamReader reader, StreamWriter writer, string prefix)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (line.StartsWith("TEST_", StringComparison.Ordinal) ||
                    line.StartsWith("SUITE_", StringComparison.Ordinal) ||
                    line.StartsWith("ERROR", StringComparison.Ordinal) ||
                    line.StartsWith("CRITICAL", StringComparison.Ordinal))
                {
                    Console.WriteLine($"{prefix} {line}");
                }
            }
        }

        private static WptRawCounts CountRawLog(string rawLogPath)
        {
            var counts = new WptRawCounts();
            if (!File.Exists(rawLogPath))
            {
                return counts;
            }

            foreach (var line in File.ReadLines(rawLogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("action", out var actionProperty))
                    {
                        continue;
                    }

                    var action = actionProperty.GetString();
                    if (string.Equals(action, "test_start", StringComparison.Ordinal))
                    {
                        counts.TestStart++;
                    }
                    else if (string.Equals(action, "test_end", StringComparison.Ordinal))
                    {
                        counts.TestEnd++;
                        var status = root.TryGetProperty("status", out var statusProperty)
                            ? statusProperty.GetString() ?? "UNKNOWN"
                            : "UNKNOWN";
                        counts.StatusCounts.TryGetValue(status, out var current);
                        counts.StatusCounts[status] = current + 1;
                    }
                }
                catch (JsonException)
                {
                }
            }

            return counts;
        }

        private static string FormatStatusCounts(Dictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                return "none";
            }

            return string.Join(",", counts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        }

        private static WptOptions ParseOptions(string[] args)
        {
            var repoRoot = Directory.GetCurrentDirectory();
            var options = new WptOptions
            {
                WptRoot = @"C:\Users\udayk\Videos\wpt",
                BrowserBinary = FindFirstExisting(
                    Path.Combine(repoRoot, "FenBrowser.Host", "bin", "Release", "net10.0", "FenBrowser.Host.exe"),
                    Path.Combine(repoRoot, "FenBrowser.Host", "bin", "Debug", "net10.0", "FenBrowser.Host.exe")),
                WebDriverBinary = Path.Combine(repoRoot, "scripts", "wpt-webdriver-launcher.cmd"),
                OutputDir = Path.Combine(repoRoot, "Results", $"wpt_{DateTime.UtcNow:yyyyMMdd_HHmmss}"),
                Processes = DefaultProcesses,
                TimeoutSeconds = DefaultTimeoutSeconds,
                VenvPath = FindFirstExisting(Path.Combine(@"C:\Users\udayk\Videos\wpt", "_venv3")),
                SkipVenvSetup = false,
                Tests = new List<string> { "dom/" }
            };

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (TryReadOption(arg, "--root", args, ref i, out var root))
                {
                    options.WptRoot = Path.GetFullPath(root);
                    continue;
                }

                if (TryReadOption(arg, "--binary", args, ref i, out var binary))
                {
                    options.BrowserBinary = Path.GetFullPath(binary);
                    continue;
                }

                if (TryReadOption(arg, "--webdriver-binary", args, ref i, out var webdriverBinary))
                {
                    options.WebDriverBinary = Path.GetFullPath(webdriverBinary);
                    continue;
                }

                if (TryReadOption(arg, "--output-dir", args, ref i, out var outputDir))
                {
                    options.OutputDir = Path.GetFullPath(outputDir);
                    continue;
                }

                if (TryReadOption(arg, "--processes", args, ref i, out var processesText) &&
                    int.TryParse(processesText, out var processes) &&
                    processes > 0)
                {
                    options.Processes = processes;
                    continue;
                }

                if (TryReadOption(arg, "--timeout-seconds", args, ref i, out var timeoutText) &&
                    int.TryParse(timeoutText, out var timeoutSeconds) &&
                    timeoutSeconds > 0)
                {
                    options.TimeoutSeconds = timeoutSeconds;
                    continue;
                }

                if (TryReadOption(arg, "--venv", args, ref i, out var venvPath))
                {
                    options.VenvPath = Path.GetFullPath(venvPath);
                    continue;
                }

                if (string.Equals(arg, "--skip-venv-setup", StringComparison.OrdinalIgnoreCase))
                {
                    options.SkipVenvSetup = true;
                    continue;
                }

                if (TryReadOption(arg, "--tests", args, ref i, out var tests))
                {
                    options.Tests = tests.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .ToList();
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Tests = args.Skip(i).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    break;
                }
            }

            if (!Directory.Exists(options.WptRoot))
            {
                throw new DirectoryNotFoundException($"WPT root not found: {options.WptRoot}");
            }

            if (string.IsNullOrWhiteSpace(options.BrowserBinary) || !File.Exists(options.BrowserBinary))
            {
                throw new FileNotFoundException($"FenBrowser Host binary not found: {options.BrowserBinary}");
            }

            if (string.IsNullOrWhiteSpace(options.WebDriverBinary) || !File.Exists(options.WebDriverBinary))
            {
                throw new FileNotFoundException($"WPT WebDriver launcher not found: {options.WebDriverBinary}");
            }

            if (options.Tests == null || options.Tests.Count == 0)
            {
                throw new ArgumentException("wpt requires at least one test path via --tests or trailing arguments.");
            }

            return options;
        }

        private static bool TryReadOption(string arg, string name, string[] args, ref int index, out string value)
        {
            value = string.Empty;
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(name.Length + 1);
                return true;
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                value = args[++index];
                return true;
            }

            return false;
        }

        private static string FindFirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists) ?? paths.FirstOrDefault() ?? string.Empty;
        }

        private sealed class WptOptions
        {
            public string WptRoot { get; set; }
            public string BrowserBinary { get; set; }
            public string WebDriverBinary { get; set; }
            public string OutputDir { get; set; }
            public int Processes { get; set; }
            public int TimeoutSeconds { get; set; }
            public string VenvPath { get; set; }
            public bool SkipVenvSetup { get; set; }
            public List<string> Tests { get; set; }
        }

        private sealed class WptCommand
        {
            public WptCommand(string fileName, List<string> arguments)
            {
                FileName = fileName;
                Arguments = arguments;
            }

            public string FileName { get; }
            public List<string> Arguments { get; }
        }

        private sealed class WptProcessResult
        {
            public int ExitCode { get; set; }
            public bool TimedOut { get; set; }
        }

        private sealed class WptRawCounts
        {
            public int TestStart { get; set; }
            public int TestEnd { get; set; }
            public Dictionary<string, int> StatusCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private sealed class WptSummary
        {
            public string WptRoot { get; set; }
            public string BrowserBinary { get; set; }
            public string WebDriverBinary { get; set; }
            public List<string> Tests { get; set; }
            public int Processes { get; set; }
            public int TimeoutSeconds { get; set; }
            public string VenvPath { get; set; }
            public bool SkipVenvSetup { get; set; }
            public bool TimedOut { get; set; }
            public int ExitCode { get; set; }
            public string FailurePhase { get; set; }
            public string StartedAtUtc { get; set; }
            public string FinishedAtUtc { get; set; }
            public double DurationSeconds { get; set; }
            public string RawLogPath { get; set; }
            public string ReportPath { get; set; }
            public string MachLogPath { get; set; }
            public string StdoutPath { get; set; }
            public string StderrPath { get; set; }
            public int TestStart { get; set; }
            public int TestEnd { get; set; }
            public Dictionary<string, int> StatusCounts { get; set; }
            public string Command { get; set; }
        }
    }
}
