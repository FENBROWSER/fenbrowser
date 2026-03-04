// =============================================================================
// WPTTestRunner.cs
// Web Platform Tests Runner
// 
// PURPOSE: Execute WPT tests in headless mode and collect results
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Testing
{
    /// <summary>
    /// Runner for Web Platform Tests (WPT).
    /// Executes HTML test files and collects testharness.js results.
    /// </summary>
    public class WPTTestRunner
    {
        private readonly string _wptRootPath;
        private readonly int _timeoutMs;
        private readonly List<TestExecutionResult> _results = new List<TestExecutionResult>();
        
        public class TestExecutionResult
        {
            public string TestFile { get; set; }
            public bool Success { get; set; }
            public bool HarnessCompleted { get; set; }
            public bool TimedOut { get; set; }
            public string CompletionSignal { get; set; }
            public int PassCount { get; set; }
            public int FailCount { get; set; }
            public int TotalCount { get; set; }
            public string Output { get; set; }
            public TimeSpan Duration { get; set; }
            public string Error { get; set; }
        }
        
        public class RunOptions
        {
            public string TestPattern { get; set; } = "*";
            public bool Verbose { get; set; } = false;
            public int MaxTests { get; set; } = int.MaxValue;
            public bool StopOnFirstFailure { get; set; } = false;
        }

        private readonly Func<string, Task> _navigator;
        private sealed class TestExecutionState
        {
            public bool HarnessCompleted { get; set; }
            public bool TimedOut { get; set; }
            public string CompletionSignal { get; set; } = "none";
        }
        
        public WPTTestRunner(string wptRootPath, Func<string, Task> navigator = null, int timeoutMs = 10000)
        {
            _wptRootPath = wptRootPath;
            _navigator = navigator;
            _timeoutMs = timeoutMs;
        }
        
        /// <summary>
        /// Discover all WPT HTML test files in the root path.
        /// </summary>
        public List<string> DiscoverAllTests()
        {
            var files = new List<string>();
            if (!Directory.Exists(_wptRootPath)) return files;

            files.AddRange(Directory.GetFiles(_wptRootPath, "*.html", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(_wptRootPath, "*.htm", SearchOption.AllDirectories));

            return files
                .Where(f => !Path.GetFileName(f).StartsWith("_") && !Path.GetFileName(f).StartsWith("."))
                .OrderBy(f => f)
                .ToList();
        }

        /// <summary>
        /// Run a specific list of test files (used for chunked execution).
        /// </summary>
        public async Task<IReadOnlyList<TestExecutionResult>> RunSpecificTestsAsync(
            List<string> tests,
            Action<string, int>? onProgress = null)
        {
            _results.Clear();
            int count = 0;
            foreach (var testFile in tests)
            {
                count++;
                var result = await RunSingleTestAsync(testFile);
                _results.Add(result);
                onProgress?.Invoke(Path.GetFileName(testFile), count);

                // Force GC every 10 tests to reclaim runtime memory and avoid GC pressure buildup.
                if (count % 10 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
            return _results.AsReadOnly();
        }

        /// <summary>
        /// Run all tests matching a pattern.
        /// </summary>
        public async Task<IReadOnlyList<TestExecutionResult>> RunTestsAsync(RunOptions options = null)
        {
            options = options ?? new RunOptions();
            _results.Clear();
            
            var testFiles = FindTestFiles(options.TestPattern, options.MaxTests);
            
            foreach (var testFile in testFiles)
            {
                var result = await RunSingleTestAsync(testFile, options.Verbose);
                _results.Add(result);
                
                if (options.StopOnFirstFailure && !result.Success)
                    break;
            }
            
            return _results.AsReadOnly();
        }

        /// <summary>
        /// Run tests in a specific category directory.
        /// </summary>
        public async Task<IReadOnlyList<TestExecutionResult>> RunCategoryAsync(string category, Action<string, int> onProgress = null, int maxTests = int.MaxValue)
        {
            _results.Clear();
            
            var categoryPath = Path.Combine(_wptRootPath, category);
            if (!Directory.Exists(categoryPath))
            {
                // Fallback: Check if it's inside "tests" or "wpt" subfolder if the root is raw repo
                var altPath = Path.Combine(_wptRootPath, "tests", category); // Common WPT structure
                if (Directory.Exists(altPath)) categoryPath = altPath;
                else
                {
                     // Try as direct file pattern if not directory? No, this method is for categories.
                     return _results.AsReadOnly();
                }
            }

            // WPT tests are HTML, HTM, or ANY.JS (for worker tests, etc). sticking to HTML/HTM for now.
            var testFiles = new List<string>();
            testFiles.AddRange(Directory.GetFiles(categoryPath, "*.html", SearchOption.AllDirectories));
            testFiles.AddRange(Directory.GetFiles(categoryPath, "*.htm", SearchOption.AllDirectories));
            
            int count = 0;
            foreach (var testFile in testFiles)
            {
                if (count >= maxTests) break;
                
                // Skip helper files (often start with _)
                if (Path.GetFileName(testFile).StartsWith("_")) continue;

                count++;
                var result = await RunSingleTestAsync(testFile);
                _results.Add(result);
                onProgress?.Invoke(Path.GetFileName(testFile), count);
            }
            
            return _results.AsReadOnly();
        }
        
        /// <summary>
        /// Run a single test file.
        /// </summary>
        public async Task<TestExecutionResult> RunSingleTestAsync(string testFile, bool verbose = false)
        {
            var result = new TestExecutionResult { TestFile = testFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(testFile) || !File.Exists(testFile))
            {
                result.Success = false;
                result.Error = $"Test file not found: {testFile}";
                result.CompletionSignal = "invalid-test-file";
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            if (_navigator == null)
            {
                result.Success = false;
                result.Error = "Navigator delegate is required to execute WPT tests.";
                result.CompletionSignal = "no-navigator";
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            // Reftests are visual comparison tests (link rel=match) and do not emit
            // testharness assertions. Treat them as skipped in the harness runner.
            try
            {
                var source = File.ReadAllText(testFile);
                var hasHarness = source.IndexOf("/resources/testharness.js", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasScriptTag = source.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0;
                var isRefTest = source.IndexOf("rel=\"match\"", StringComparison.OrdinalIgnoreCase) >= 0
                                || source.IndexOf("rel='match'", StringComparison.OrdinalIgnoreCase) >= 0
                                || source.IndexOf("rel=match", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasHarness && (isRefTest || !hasScriptTag))
                {
                    result.Success = true;
                    result.HarnessCompleted = true;
                    result.TimedOut = false;
                    result.CompletionSignal = "reftest-skipped";
                    result.PassCount = 0;
                    result.FailCount = 0;
                    result.TotalCount = 0;
                    result.Error = null;
                    sw.Stop();
                    result.Duration = sw.Elapsed;
                    return result;
                }
            }
            catch
            {
                // Continue through normal harness execution path if test source cannot be read.
            }

            try
            {
                // Enable test mode
                WebAPIs.TestHarnessAPI.EnableTestMode(_timeoutMs);
                WebAPIs.TestConsoleCapture.StartCapture();
                
                // Navigate and wait for a structured completion signal from the test harness.
                var execution = await ExecuteTestAsync(testFile, verbose);
                result.HarnessCompleted = execution.HarnessCompleted;
                result.TimedOut = execution.TimedOut;
                result.CompletionSignal = execution.CompletionSignal;
                
                // Collect results
                var (passed, failed, total) = WebAPIs.TestHarnessAPI.GetResultSummary();
                result.PassCount = passed;
                result.FailCount = failed;
                result.TotalCount = total;
                var failedSubtests = WebAPIs.TestHarnessAPI.GetResults()
                    .Where(r => r.Status == WebAPIs.TestHarnessAPI.TestStatus.Fail)
                    .Select(r =>
                    {
                        var name = string.IsNullOrWhiteSpace(r.Name) ? "<unnamed>" : r.Name;
                        var msg = string.IsNullOrWhiteSpace(r.Message) ? "" : r.Message;
                        return string.IsNullOrWhiteSpace(msg) ? name : $"{name}: {msg}";
                    })
                    .Take(5)
                    .ToList();
                var failedSummary = failedSubtests.Count > 0 ? string.Join(" | ", failedSubtests) : null;
                if (total == 0)
                {
                    result.Success = false;
                    result.Error = "No assertions executed by testharness.";
                }
                else if (execution.TimedOut)
                {
                    result.Success = false;
                    result.Error = $"Test timed out waiting for completion signal ({_timeoutMs}ms).";
                }
                else if (!execution.HarnessCompleted)
                {
                    result.Success = false;
                    result.Error = "Harness did not report completion.";
                }
                else
                {
                    result.Success = failed == 0;
                }

                if (!result.Success && string.IsNullOrWhiteSpace(result.Error) && !string.IsNullOrWhiteSpace(failedSummary))
                {
                    result.Error = failedSummary;
                }

                result.Output = WebAPIs.TestConsoleCapture.GetFullOutput();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                WebAPIs.TestConsoleCapture.StopCapture();
                WebAPIs.TestHarnessAPI.DisableTestMode();
            }
            
            sw.Stop();
            result.Duration = sw.Elapsed;
            
            return result;
        }
        
        /// <summary>
        /// Find test files matching pattern.
        /// </summary>
        private List<string> FindTestFiles(string pattern, int maxTests)
        {
            var files = new List<string>();
            
            if (!Directory.Exists(_wptRootPath))
                return files;
            
            var searchPattern = pattern.Contains("*") ? pattern : $"*{pattern}*";
            var foundFiles = Directory.GetFiles(_wptRootPath, searchPattern + ".html", SearchOption.AllDirectories);
            
            foreach (var file in foundFiles)
            {
                if (files.Count >= maxTests) break;
                
                // Skip non-test files
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("_") || fileName.StartsWith("."))
                    continue;
                    
                files.Add(file);
            }
            
            return files;
        }
        
        /// <summary>
        /// Execute a test file using the live engine.
        /// </summary>
        private async Task<TestExecutionState> ExecuteTestAsync(string testFile, bool verbose)
        {
            var state = new TestExecutionState();

            // Convert file path to URI
            var uri = new Uri(testFile).AbsoluteUri;
            
            // 1. Navigate
            if (verbose) Console.WriteLine($"[WPT] Navigating to {uri}");
            await _navigator(uri);

            // 2. Wait for structured completion signals from testharness / testRunner bridge.
            var timeoutAt = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var settleAt = DateTime.UtcNow.AddMilliseconds(200);
            bool fallbackConsoleUsed = false;
            while (DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(25);

                var snapshot = WebAPIs.TestHarnessAPI.GetExecutionSnapshot();
                if (snapshot.TestDone)
                {
                    state.HarnessCompleted = true;
                    state.CompletionSignal = string.IsNullOrWhiteSpace(snapshot.CompletionSignal)
                        ? "testRunner.notifyDone"
                        : snapshot.CompletionSignal;
                    break;
                }

                if (snapshot.HarnessStatusSeen &&
                    string.Equals(snapshot.HarnessStatus, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    state.HarnessCompleted = true;
                    state.CompletionSignal = "testRunner.reportHarnessStatus";
                    break;
                }

                if (snapshot.StructuredResultCount > 0 && DateTime.UtcNow >= settleAt && !snapshot.WaitingForDone)
                {
                    state.HarnessCompleted = true;
                    state.CompletionSignal = "testRunner.reportResult";
                    break;
                }

                // Legacy compatibility path: only if structured signals never appeared.
                if (snapshot.StructuredResultCount == 0 &&
                    !snapshot.HarnessStatusSeen &&
                    !snapshot.TestDone &&
                    !fallbackConsoleUsed)
                {
                    var output = WebAPIs.TestConsoleCapture.GetFullOutput();
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        WebAPIs.TestConsoleCapture.ParseTestHarnessOutput(output);
                        if (output.IndexOf("ran to completion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            output.IndexOf("harness_status", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            state.HarnessCompleted = true;
                            state.CompletionSignal = "console.harness_status.fallback";
                            fallbackConsoleUsed = true;
                            break;
                        }
                    }
                }
            }

            if (!state.HarnessCompleted)
            {
                state.TimedOut = WebAPIs.TestHarnessAPI.GetExecutionSnapshot().WaitingForDone;
                state.CompletionSignal = state.TimedOut ? "timeout" : "none";
            }

            // Small settle delay for pending console flushes/results.
            await Task.Delay(50);
            return state;
        }
        
        /// <summary>
        /// Generate summary report.
        /// </summary>
        public string GenerateSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== WPT Test Summary ===");
            sb.AppendLine();
            
            int totalPass = 0, totalFail = 0, totalTests = 0;
            var failures = new List<TestExecutionResult>();
            
            foreach (var r in _results)
            {
                totalPass += r.PassCount;
                totalFail += r.FailCount;
                totalTests += r.TotalCount;
                
                if (!r.Success)
                    failures.Add(r);
            }
            
            sb.AppendLine($"Tests run: {_results.Count}");
            sb.AppendLine($"Assertions: {totalTests} ({totalPass} passed, {totalFail} failed)");
            sb.AppendLine();
            
            if (failures.Count > 0)
            {
                sb.AppendLine("Failed tests:");
                foreach (var f in failures)
                {
                    sb.AppendLine($"  - {f.TestFile}");
                    if (!string.IsNullOrEmpty(f.Error))
                        sb.AppendLine($"    Error: {f.Error}");
                }
            }
            else if (totalTests == 0)
            {
                sb.AppendLine("No assertions executed. Treated as failure.");
            }
            else
            {
                sb.AppendLine("All tests passed!");
            }
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Generate TAP output for all results.
        /// </summary>
        public string GenerateTAPOutput()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TAP version 13");
            sb.AppendLine($"1..{_results.Count}");
            
            for (int i = 0; i < _results.Count; i++)
            {
                var r = _results[i];
                var status = r.Success ? "ok" : "not ok";
                sb.AppendLine($"{status} {i + 1} - {Path.GetFileName(r.TestFile)}");
                
                if (!r.Success && !string.IsNullOrEmpty(r.Error))
                {
                    sb.AppendLine("  ---");
                    sb.AppendLine($"  message: {r.Error}");
                    sb.AppendLine("  ...");
                }
            }
            
            return sb.ToString();
        }
    }
}
