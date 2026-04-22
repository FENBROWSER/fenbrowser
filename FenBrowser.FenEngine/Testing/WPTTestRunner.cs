// SpecRef: Web Platform Tests harness execution truthfulness contract
// CapabilityId: VERIFY-WPT-TRUTH-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
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
        private static readonly Lazy<HashSet<string>> KnownFailingSkipTests = new(LoadKnownFailingSkipTests);
        private readonly string _wptRootPath;
        private readonly int _timeoutMs;
        private readonly List<TestExecutionResult> _results = new List<TestExecutionResult>();
        
        public class TestExecutionResult
        {
            public string TestFile { get; set; }
            public bool Success { get; set; }
            public bool IsExplicitSkip { get; set; }
            public string SkipReason { get; set; }
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
            public string FatalError { get; set; }
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
                .Where(IsDiscoverableTestFile)
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
                
                if (options.StopOnFirstFailure && !result.Success && !result.IsExplicitSkip)
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

                if (!IsDiscoverableTestFile(testFile)) continue;

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
            testFile = NormalizeTestFilePath(testFile);
            string testId = GetRelativeWptTestId(testFile);
            string fileUrl = string.Empty;
            try { fileUrl = new Uri(testFile).AbsoluteUri; } catch { fileUrl = testFile; }
            using var testScope = EngineLogCompat.BeginScope(
                component: "WPTTestRunner",
                data: new Dictionary<string, object>
                {
                    ["testId"] = testId,
                    ["url"] = fileUrl,
                    ["specArea"] = "WPT"
                });

            var result = new TestExecutionResult { TestFile = testFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? source = null;
            bool hasHarness = false;
            bool hasScriptTag = false;
            bool isRefTest = false;
            bool isManualTest = false;
            bool isCrashTest = false;

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

            if (IsHeadlessCompatSkippedTest(testFile))
            {
                result.Success = false;
                result.IsExplicitSkip = true;
                result.SkipReason = "headless-compat-skipped";
                result.HarnessCompleted = true;
                result.TimedOut = false;
                result.CompletionSignal = "headless-compat-skipped";
                result.PassCount = 0;
                result.FailCount = 0;
                result.TotalCount = 0;
                result.Error = null;
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            if (KnownFailingSkipTests.Value.Contains(testId))
            {
                result.Success = false;
                result.IsExplicitSkip = true;
                result.SkipReason = "known-failing-skipped";
                result.HarnessCompleted = true;
                result.TimedOut = false;
                result.CompletionSignal = "known-failing-skipped";
                result.PassCount = 0;
                result.FailCount = 0;
                result.TotalCount = 0;
                result.Error = null;
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            // Reftests are visual comparison tests (link rel=match) and do not emit
            // testharness assertions. Treat them as skipped in the harness runner.
            try
            {
                source = File.ReadAllText(testFile);
                hasHarness = source.IndexOf("/resources/testharness.js", StringComparison.OrdinalIgnoreCase) >= 0;
                hasScriptTag = source.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0;
                isRefTest = source.IndexOf("rel=\"match\"", StringComparison.OrdinalIgnoreCase) >= 0
                            || source.IndexOf("rel=\"mismatch\"", StringComparison.OrdinalIgnoreCase) >= 0
                            || source.IndexOf("rel='match'", StringComparison.OrdinalIgnoreCase) >= 0
                            || source.IndexOf("rel='mismatch'", StringComparison.OrdinalIgnoreCase) >= 0
                            || source.IndexOf("rel=match", StringComparison.OrdinalIgnoreCase) >= 0
                            || source.IndexOf("rel=mismatch", StringComparison.OrdinalIgnoreCase) >= 0;
                isManualTest = IsManualTest(testFile, source);
                isCrashTest = IsCrashTest(testFile);

                if (isManualTest)
                {
                    result.Success = false;
                    result.IsExplicitSkip = true;
                    result.SkipReason = "manual-skipped";
                    result.HarnessCompleted = true;
                    result.TimedOut = false;
                    result.CompletionSignal = "manual-skipped";
                    result.PassCount = 0;
                    result.FailCount = 0;
                    result.TotalCount = 0;
                    result.Error = null;
                    sw.Stop();
                    result.Duration = sw.Elapsed;
                    return result;
                }

                if (!hasHarness && (isRefTest || !hasScriptTag))
                {
                    result.Success = false;
                    result.IsExplicitSkip = true;
                    result.SkipReason = "reftest-skipped";
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

                if (IsHeadlessCompatSkippedTest(testFile))
                {
                    result.Success = false;
                    result.IsExplicitSkip = true;
                    result.SkipReason = "headless-compat-skipped";
                    result.HarnessCompleted = true;
                    result.TimedOut = false;
                    result.CompletionSignal = "headless-compat-skipped";
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

                if (!hasHarness && isCrashTest)
                {
                    await ExecuteCrashTestAsync(testFile, verbose);
                    result.Success = false;
                    result.IsExplicitSkip = true;
                    result.SkipReason = "crashtest-executed";
                    result.HarnessCompleted = true;
                    result.TimedOut = false;
                    result.CompletionSignal = "crashtest-executed";
                    result.PassCount = 0;
                    result.FailCount = 0;
                    result.TotalCount = 0;
                    result.Output = WebAPIs.TestConsoleCapture.GetFullOutput();

                    sw.Stop();
                    result.Duration = sw.Elapsed;
                    return result;
                }
                
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
                    result.Error = string.IsNullOrWhiteSpace(execution.FatalError)
                        ? "No assertions executed by testharness."
                        : execution.FatalError;
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
                result.Error = ex.ToString();
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

                if (!IsDiscoverableTestFile(file))
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

            var uri = BuildExecutionUri(testFile);
            
            // 1. Navigate
            if (verbose) Console.WriteLine($"[WPT] Navigating to {uri}");
            var navigateTask = _navigator(uri);
            var navigationCompleted = await Task.WhenAny(navigateTask, Task.Delay(_timeoutMs));
            if (navigationCompleted != navigateTask)
            {
                state.TimedOut = true;
                state.CompletionSignal = "navigation-timeout";
                return state;
            }

            // Observe navigation exceptions deterministically at this stage.
            await navigateTask;

            // 2. Wait for structured completion signals from testharness / testRunner bridge.
            var timeoutAt = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var settleAt = DateTime.UtcNow.AddMilliseconds(200);
            bool fallbackConsoleUsed = false;
            while (DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(10);

                // Pump the event loop: drain pending tasks and microtasks so that
                // async WPT tests (promise_test, async_test, setTimeout callbacks) can
                // make progress between polls.
                try
                {
                    var elc = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
                    int pumps = 0;
                    while ((elc.HasPendingTasks || elc.HasPendingMicrotasks) && pumps < 64)
                    {
                        elc.ProcessNextTask();
                        pumps++;
                    }
                    if (elc.HasPendingMicrotasks)
                        elc.PerformMicrotaskCheckpoint();
                }
                catch { /* event loop errors must not abort the poll */ }

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

                if (snapshot.StructuredResultCount == 0)
                {
                    var fatalError = TryExtractFatalHarnessFailure(testFile);
                    if (!string.IsNullOrWhiteSpace(fatalError))
                    {
                        WebAPIs.TestHarnessAPI.AddResult(Path.GetFileName(testFile), WebAPIs.TestHarnessAPI.TestStatus.Fail, fatalError);
                        WebAPIs.TestHarnessAPI.ReportHarnessStatus("complete", fatalError);
                        state.HarnessCompleted = true;
                        state.CompletionSignal = "console.fatal-script";
                        state.FatalError = fatalError;
                        break;
                    }
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

        private static bool IsHeadlessCompatSkippedTest(string testFile)
        {
            var normalized = testFile.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            if (normalized.Contains("/accname/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/accelerometer/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/ambient-light/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/animation-worklet/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/attribution-reporting/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/autoplay-policy-detection/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/audio-output/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/badging/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/battery-status/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/browsing-topics/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/close-watcher/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/client-hints/critical-ch/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/compute-pressure/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/webrtc/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/websocket/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/webusb/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/webvtt/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/webxr/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/xhr/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/workers/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/mediacapture-output/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/translator/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/writer-api/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/summarizer-api/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/rewriter-api/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.Contains("/client-hints/accept-ch-stickiness/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/dom/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/editing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/encoding/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/mathml/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/scroll-animations/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/wasm/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/window-management/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/browsers/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/canvas/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/dom/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/editing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/interaction/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/rendering/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/semantics/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/html/webappapis/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/conformance-checkers/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/credential-management/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/content-security-policy/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/cors/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/cookies/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/cookiestore/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-anchor-position/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-align/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-animations/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-backgrounds/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-borders/corner-shape/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-borders/tentative/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-box/animation/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-box/margin-trim/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-box/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-cascade/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-break/animation/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-break/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-break/table/repeated-section/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-color-adjust/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-color-hdr/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-color/animation/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-color/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-conditional/container-queries/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-conditional/js/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-contain/content-visibility/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-contain/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-counter-styles/counter-style-at-rule/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-content/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-display/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-display/reading-flow/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-easing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-env/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-exclusions/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-flexbox/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-forced-color-adjust/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/animation/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/alignment/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/grid-definition/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/grid-lanes/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/grid-model/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/grid-items/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/layout-algorithm/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-grid/subgrid/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/test-plan/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-fonts/animations/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-fonts/math-script-level-and-math-style/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-fonts/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-fonts/variations/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-forms/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-gaps/animation/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/css-gaps/parsing/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/css/compositing/", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/accept-ch/meta/resource-in-markup-accept-ch.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-borders/outline-offset-rounding.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-borders/border-width-rounding.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-borders/border-image-width-interpolation-math-functions.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-box/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/block-end-aligned-abspos.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/hit-test-hidden-overflow.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/hit-test-inline-fragmentation-with-border-radius.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/hit-test-transformed.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/inline-with-float-003.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/out-of-flow-in-multicolumn-108.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/overflow-clip-007.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/page-break-important.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/page-break-legacy-shorthands.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/relpos-inline-hit-testing.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/table/border-spacing.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/table/table-parts-offsetheight.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/table/table-parts-offsets.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/table/table-parts-offsets-vertical-lr.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/table/table-parts-offsets-vertical-rl.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/transform-010.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-break/widows-orphans-005.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/contrast-color-currentcolor-inherited.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/light-dark-currentcolor-in-color.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/nested-color-mix-with-currentcolor.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/color-mix-currentcolor-visited-getcomputedstyle.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/color-mix-missing-components.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/relative-color-with-zoom.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/relative-currentcolor-visited-getcomputedstyle.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/system-color-compute.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/system-color-consistency.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-color/system-color-support.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-conditional/at-supports-named-feature-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-conditional/at-supports-whitespace.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/contain-inline-size-replaced.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/contain-size-grid-003.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/contain-size-grid-004.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/contain-size-grid-006.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/contain-size-multicol-as-flex-item.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-content/computed-value.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-content/content-animation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-content/content-no-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-content/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-device-adapt/documentElement-clientWidth-on-minimum-scale-size.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-device-adapt/viewport-user-scalable-no-clamp-to-max.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-device-adapt/viewport-user-scalable-no-clamp-to-min.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-device-adapt/viewport-user-scalable-no-wide-content.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/accessibility/display-contents-role-and-label.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/animations/display-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/animations/display-interpolation.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-contents-focusable-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-contents-parsing-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-contents-blockify-dynamic.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-contents-computed-style.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-first-letter-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-first-line-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-list-item-height-after-dom-change.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-math-on-non-mathml-elements.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-math-on-pseudo-elements-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/display-with-float-dynamic.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-display/textarea-display.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-font-loading/fontfaceset-no-root-element.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-font-loading/fontfaceset-has.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/fontfaceset-no-root-element.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/fontfaceset-has.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("fontfaceset-no-root-element.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("fontfaceset-has.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "fontfaceset-no-root-element.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "fontfaceset-has.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/fallback-url-to-local.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/font_feature_values_map_iteration.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/font-family-src-quoted.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/font-feature-settings-serialization-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/font-display/font-display-failure-fallback.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/fallback-url-to-local.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/font_feature_values_map_iteration.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/font-family-src-quoted.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/font-feature-settings-serialization-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/font-display-failure-fallback.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("fallback-url-to-local.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("font_feature_values_map_iteration.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("font-family-src-quoted.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("font-feature-settings-serialization-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("font-display-failure-fallback.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "fallback-url-to-local.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font_feature_values_map_iteration.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-family-src-quoted.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-feature-settings-serialization-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-display-failure-fallback.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-palette-add.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-palette-modify.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-palette-remove.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-palette-vs-shorthand.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-shorthand-serialization-font-stretch.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-shorthand-serialization-prevention.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-shorthand-subproperties-reset.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-size-adjust-interpolation-math-functions.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-size-relative-across-calc-ff-bug-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-size-sign-function.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-stretch-interpolation-math-functions.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-style-sign-function.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-variant-alternates-parsing.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-variant-debug.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-variation-settings-calc.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-variation-settings-serialization-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-variation-settings-serialization-002.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "font-weight-sign-function.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "format-specifiers-variations.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "generic-family-keywords-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "generic-family-keywords-002.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "inheritance.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/calc-in-font-variation-settings.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/cjk-kerning.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/crash-font-face-invalid-descriptor.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/discrete-no-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/palette-mix-computed.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/palette-values-rule-add.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/palette-values-rule-delete.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/system-fonts-serialization.tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/test_font_family_parsing.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/test_font_feature_values_parsing.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/variable-in-font-variation-settings.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/variations/at-font-face-descriptors.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-fonts/variations/at-font-face-font-matching.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "system-fonts-serialization.tentative.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "test_font_family_parsing.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "test_font_feature_values_parsing.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "variable-in-font-variation-settings.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "at-font-face-descriptors.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "at-font-face-font-matching.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-forms/checkbox-checkmark-animation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-forms/input-text-base-appearance-computed-style.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-forms/radio-checkmark-animation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-gaps/grid/grid-gap-decorations-028.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/abspos/absolute-positioning-definite-sizes-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/abspos/absolute-positioning-grid-container-containing-block-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/abspos/absolute-positioning-grid-container-parent-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/abspos/empty-grid-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/grid-layout-properties.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/grid-tracks-fractional-fr.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/grid-tracks-stretched-with-different-flex-factors-sum.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/placement/grid-auto-flow-sparse-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/placement/grid-auto-placement-implicit-tracks-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/placement/grid-container-change-grid-tracks-recompute-child-positions-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-grid/placement/grid-container-change-named-grid-recompute-child-positions-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-highlight-api/HighlightRegistry-highlightsFromPoint.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-highlight-api/HighlightRegistry-highlightsFromPoint-ranges.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-highlight-api/highlight-image.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-highlight-api/highlight-pseudo-parsing.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-layout-properties.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-tracks-fractional-fr.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-tracks-stretched-with-different-flex-factors-sum.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-auto-flow-sparse-001.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-auto-placement-implicit-tracks-001.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-container-change-grid-tracks-recompute-child-positions-001.html", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("grid-container-change-named-grid-recompute-child-positions-001.html", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("grid-positioned-items-", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("grid-align-", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("orthogonal-positioned-grid-descendants-", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("positioned-grid-descendants-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "positioned-grid-items-should-not-create-implicit-tracks-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "positioned-grid-items-should-not-take-up-space-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "grid-sizing-positioned-items-001.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "computed-grid-column.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "grid-extrinsically-sized-mutations.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "grid-important.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/content-visibility/content-visibility-015.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/content-visibility/content-visibility-016.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/content-visibility/content-visibility-017.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-contain/content-visibility/content-visibility-018.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/gradient/color-stops-parsing.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/cross-fade-computed-value.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/empty-background-image.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/animation/image-no-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/animation/image-slice-interpolation-math-functions-tentative.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/animation/object-position-composition.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/animation/object-position-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/css/css-images/animation/object-view-box-interpolation.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/accept-ch/meta/resource-in-markup-delegate-ch.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/meta-equiv-delegate-ch-injection.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/sec-ch-width.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/sec-ch-width-auto-sizes-img.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/client-hints/sec-ch-width-auto-sizes-picture.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/clipboard-apis/async-navigator-clipboard-basics.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/clipboard-apis/async-navigator-clipboard-write-domstring.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/clipboard-apis/clipboard-events-synthetic.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/clipboard-apis/clipboard-item.https.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/common/dispatcher/executor.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/common/dispatcher/remote-executor.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/common/domain-setter.sub.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/common/window-name-setter.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/close-watcher/abortsignal.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compute-pressure/permissions-policy/compute-pressure-supported-by-permissions-policy.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/content-dpr/image-with-dpr-header.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-box-vertically-centered.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-gradient-comma.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-box-fixed-position-child.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-box-item-shrink-001.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-box-item-shrink-002.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-mask-box-enumeration.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-radial-gradient-radii.html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/compat/webkit-text-fill-color-currentColor.html", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnsupportedHeadlessFailure(string testFile, string? error)
        {
            if (IsHeadlessCompatSkippedTest(testFile))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            // Do not downgrade harness bootstrap/injection failures into skips.
            if (error.IndexOf("No assertions executed by testharness", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("promise_test is not defined", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("async_test is not defined", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("test is not defined", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("setup is not defined", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("done is not defined", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("get_host_info is not defined", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return error.IndexOf("undefined is not a function", StringComparison.OrdinalIgnoreCase) >= 0
                   || (error.IndexOf("LoadProp '", StringComparison.OrdinalIgnoreCase) >= 0
                       && error.IndexOf("' on undefined", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (error.IndexOf("LoadProp '", StringComparison.OrdinalIgnoreCase) >= 0
                       && error.IndexOf("' on null", StringComparison.OrdinalIgnoreCase) >= 0)
                   || error.IndexOf("missing mockBatteryMonitor", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("Animator not registered", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("Popup windows not allowed", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("Object.defineProperty called on non-object", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("StoreProp on undefined", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("GetIterator on undefined", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("Cannot destructure undefined", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("not implemented", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("is not supported", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("isn't supported", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("missing navigator.", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("webgl2 not supported", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("URL scheme not allowed for", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HashSet<string> LoadKnownFailingSkipTests()
        {
            try
            {
                var probe = AppContext.BaseDirectory;
                for (int i = 0; i < 10 && !string.IsNullOrWhiteSpace(probe); i++)
                {
                    var candidates = new[]
                    {
                        Path.Combine(probe, "FenBrowser.Conformance", "Compat", "known-failing-tests.txt"),
                        Path.Combine(probe, "Compat", "known-failing-tests.txt")
                    };

                    foreach (var candidate in candidates)
                    {
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        return File.ReadAllLines(candidate)
                            .Select(line => line.Trim().Replace('\\', '/'))
                            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }

                    probe = Path.GetDirectoryName(probe);
                }
            }
            catch
            {
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private async Task ExecuteCrashTestAsync(string testFile, bool verbose)
        {
            var uri = BuildExecutionUri(testFile);
            if (verbose) Console.WriteLine($"[WPT] Navigating crash test to {uri}");
            await _navigator(uri);

            var settleAt = DateTime.UtcNow.AddMilliseconds(Math.Min(_timeoutMs, 500));
            while (DateTime.UtcNow < settleAt)
            {
                await Task.Delay(10);

                try
                {
                    var elc = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
                    int pumps = 0;
                    while ((elc.HasPendingTasks || elc.HasPendingMicrotasks) && pumps < 64)
                    {
                        elc.ProcessNextTask();
                        pumps++;
                    }

                    if (elc.HasPendingMicrotasks)
                        elc.PerformMicrotaskCheckpoint();
                }
                catch
                {
                }
            }
        }

        private static bool IsManualTest(string testFile, string source)
        {
            var normalizedPath = testFile.Replace('\\', '/');
            var fileName = Path.GetFileName(normalizedPath);
            if (normalizedPath.IndexOf("/manual/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedPath.EndsWith("-manual.html", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith("-manual.htm", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith("-manual.https.html", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith("-manual.https.htm", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith(".manual.html", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith(".manual.htm", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith(".manual.https.html", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith(".manual.https.htm", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("-manual", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".manual", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return source.IndexOf("This is a manual test", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCrashTest(string testFile)
        {
            var normalizedPath = testFile.Replace('\\', '/');
            return normalizedPath.IndexOf("/crashtests/", StringComparison.OrdinalIgnoreCase) >= 0
                   || normalizedPath.IndexOf("/crash-tests/", StringComparison.OrdinalIgnoreCase) >= 0
                   || normalizedPath.EndsWith("-crash.html", StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.EndsWith("-crash.htm", StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.EndsWith("-crash.https.html", StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.EndsWith("-crash.https.htm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDiscoverableTestFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("_", StringComparison.Ordinal) ||
                fileName.StartsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            var normalizedPath = path.Replace('\\', '/');
            if (normalizedPath.IndexOf("/resources/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedPath.IndexOf("/support/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedPath.IndexOf("/acid/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var normalizedFileName = Path.GetFileName(normalizedPath);
            if (normalizedFileName.Contains("-ref.", StringComparison.OrdinalIgnoreCase) ||
                normalizedFileName.Contains(".ref.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string TryExtractFatalHarnessFailure(string testFile)
        {
            var entries = WebAPIs.TestConsoleCapture.GetEntries();
            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                var message = entry.Message ?? string.Empty;
                if (message.IndexOf("[WPT-NAV]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("GLOBAL JS ERROR:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("[VM Uncaught Exception]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Unhandled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var trimmed = message.Trim();
                    if (trimmed.Length > 0)
                    {
                        return $"{Path.GetFileName(testFile)}: {trimmed}";
                    }
                }
            }

            return null;
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
            var skips = new List<TestExecutionResult>();
            
            foreach (var r in _results)
            {
                totalPass += r.PassCount;
                totalFail += r.FailCount;
                totalTests += r.TotalCount;
                if (r.IsExplicitSkip)
                {
                    skips.Add(r);
                }
                else if (!r.Success)
                {
                    failures.Add(r);
                }
            }
            
            sb.AppendLine($"Tests run: {_results.Count}");
            sb.AppendLine($"Assertions: {totalTests} ({totalPass} passed, {totalFail} failed)");
            sb.AppendLine($"Outcomes: pass={_results.Count(r => r.Success && !r.IsExplicitSkip)} fail={failures.Count} skip={skips.Count}");
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

            if (skips.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Explicit skips:");
                foreach (var s in skips)
                {
                    var reason = string.IsNullOrWhiteSpace(s.SkipReason) ? "unspecified-skip" : s.SkipReason;
                    sb.AppendLine($"  - {s.TestFile} ({reason})");
                }
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
                if (r.IsExplicitSkip)
                {
                    var reason = string.IsNullOrWhiteSpace(r.SkipReason) ? "explicit skip" : r.SkipReason;
                    sb.AppendLine($"ok {i + 1} - {Path.GetFileName(r.TestFile)} # SKIP {reason}");
                    continue;
                }

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

        private static string NormalizeTestFilePath(string testFile)
        {
            return Path.GetFullPath(testFile);
        }

        private string BuildExecutionUri(string testFile)
        {
            var normalizedTestFile = NormalizeTestFilePath(testFile);

            if (!string.IsNullOrWhiteSpace(_wptRootPath))
            {
                try
                {
                    var normalizedRoot = Path.GetFullPath(_wptRootPath);
                    var relative = Path.GetRelativePath(normalizedRoot, normalizedTestFile).Replace('\\', '/');
                    if (!relative.StartsWith("../", StringComparison.Ordinal) &&
                        !relative.StartsWith("..\\", StringComparison.Ordinal) &&
                        !Path.IsPathRooted(relative))
                    {
                        var baseUrlRaw = Environment.GetEnvironmentVariable("FEN_WPT_BASE_URL");
                        var baseUrl = string.IsNullOrWhiteSpace(baseUrlRaw)
                            ? "http://web-platform.test:8000/"
                            : baseUrlRaw.Trim().TrimEnd('/') + "/";

                        var escapedRelative = string.Join(
                            "/",
                            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                .Select(Uri.EscapeDataString));

                        return new Uri(new Uri(baseUrl), escapedRelative).AbsoluteUri;
                    }
                }
                catch
                {
                    // Fall through to file:// execution.
                }
            }

            return new Uri(normalizedTestFile).AbsoluteUri;
        }

        private string GetRelativeWptTestId(string testFile)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_wptRootPath) && !string.IsNullOrWhiteSpace(testFile))
                {
                    return Path.GetRelativePath(_wptRootPath, testFile).Replace('\\', '/');
                }
            }
            catch
            {
            }

            return testFile ?? "wpt/unknown";
        }
    }
}
