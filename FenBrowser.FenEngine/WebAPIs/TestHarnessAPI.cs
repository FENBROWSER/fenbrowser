// =============================================================================
// TestHarnessAPI.cs
// WPT/Test262 Test Harness API Implementation
// 
// SPEC REFERENCE: Web Platform Tests testharness.js
//                 https://web-platform-tests.org/writing-tests/testharness-api.html
// 
// PURPOSE: Provides window.testRunner and related APIs for automated testing
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Test harness API for WPT and JavaScript test suites.
    /// Exposes window.testRunner for test automation.
    /// </summary>
    public static class TestHarnessAPI
    {
        // --- Test State ---
        private static bool _testMode = false;
        private static bool _waitUntilDone = false;
        private static bool _testDone = false;
        private static StringBuilder _testLog = new StringBuilder();
        private static List<TestResult> _results = new List<TestResult>();
        private static int _testTimeout = 10000; // Default 10s
        private static string _completionSignal = "none";
        private static bool _harnessStatusSeen = false;
        private static string _harnessStatus = string.Empty;
        private static int _resultEventCount = 0;
        
        public class TestResult
        {
            public string Name { get; set; }
            public TestStatus Status { get; set; }
            public string Message { get; set; }
            public string Stack { get; set; }
            public double Duration { get; set; }
        }
        
        public enum TestStatus
        {
            Pass,
            Fail,
            Timeout,
            NotRun,
            PreconditionFailed
        }

        public sealed class ExecutionSnapshot
        {
            public bool TestMode { get; set; }
            public bool WaitingForDone { get; set; }
            public bool TestDone { get; set; }
            public string CompletionSignal { get; set; }
            public bool HarnessStatusSeen { get; set; }
            public string HarnessStatus { get; set; }
            public int ResultEventCount { get; set; }
            public int StructuredResultCount { get; set; }
            public int TimeoutMs { get; set; }
        }
        
        // --- Public API ---
        
        /// <summary>
        /// Enable test mode for the engine.
        /// </summary>
        public static void EnableTestMode(int timeoutMs = 10000)
        {
            _testMode = true;
            _waitUntilDone = false;
            _testDone = false;
            _testLog.Clear();
            _results.Clear();
            _testTimeout = timeoutMs;
            _completionSignal = "none";
            _harnessStatusSeen = false;
            _harnessStatus = string.Empty;
            _resultEventCount = 0;
        }
        
        /// <summary>
        /// Disable test mode.
        /// </summary>
        public static void DisableTestMode()
        {
            _testMode = false;
        }
        
        /// <summary>
        /// Check if test mode is enabled.
        /// </summary>
        public static bool IsTestMode => _testMode;
        
        /// <summary>
        /// Check if tests should wait for async completion.
        /// </summary>
        public static bool IsWaitingForDone => _waitUntilDone && !_testDone;
        
        /// <summary>
        /// Check if test execution is complete.
        /// </summary>
        public static bool IsTestDone => _testDone;
        
        /// <summary>
        /// Get captured test log.
        /// </summary>
        public static string GetTestLog() => _testLog.ToString();
        
        /// <summary>
        /// Get test results.
        /// </summary>
        public static IReadOnlyList<TestResult> GetResults() => _results.AsReadOnly();
        
        /// <summary>
        /// Get configured timeout in ms.
        /// </summary>
        public static int GetTimeout() => _testTimeout;

        /// <summary>
        /// Get a structured execution snapshot for runner-level completion logic.
        /// </summary>
        public static ExecutionSnapshot GetExecutionSnapshot()
        {
            return new ExecutionSnapshot
            {
                TestMode = _testMode,
                WaitingForDone = _waitUntilDone && !_testDone,
                TestDone = _testDone,
                CompletionSignal = _completionSignal,
                HarnessStatusSeen = _harnessStatusSeen,
                HarnessStatus = _harnessStatus,
                ResultEventCount = _resultEventCount,
                StructuredResultCount = _results.Count,
                TimeoutMs = _testTimeout
            };
        }
        
        /// <summary>
        /// Add a test result.
        /// </summary>
        public static void AddResult(string name, TestStatus status, string message = null, string stack = null)
        {
            _resultEventCount++;
            _results.Add(new TestResult
            {
                Name = name,
                Status = status,
                Message = message,
                Stack = stack
            });
        }
        
        /// <summary>
        /// Log a message to test output.
        /// </summary>
        public static void Log(string message)
        {
            _testLog.AppendLine(message);
        }

        public static void ReportHarnessStatus(string status, string message = null)
        {
            _harnessStatusSeen = true;
            _harnessStatus = status ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message))
            {
                Log($"[testRunner] harness_status={status} message={message}");
            }
            else
            {
                Log($"[testRunner] harness_status={status}");
            }

            if (string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                _testDone = true;
                if (_completionSignal == "none")
                {
                    _completionSignal = "testRunner.reportHarnessStatus";
                }
            }
        }
        
        // --- FenRuntime Registration ---
        
        /// <summary>
        /// Create testRunner object for FenRuntime window.
        /// </summary>
        public static FenObject CreateTestRunnerObject()
        {
            var testRunner = new FenObject();
            
            // notifyDone() - Signal test completion
            testRunner.Set("notifyDone", FenValue.FromFunction(new FenFunction("notifyDone", (args, thisVal) =>
            {
                _testDone = true;
                _completionSignal = "testRunner.notifyDone";
                Log("[testRunner] notifyDone() called");
                return FenValue.Undefined;
            })));
            
            // waitUntilDone() - Keep page alive for async tests
            testRunner.Set("waitUntilDone", FenValue.FromFunction(new FenFunction("waitUntilDone", (args, thisVal) =>
            {
                _waitUntilDone = true;
                Log("[testRunner] waitUntilDone() called");
                return FenValue.Undefined;
            })));
            
            // log(message) - Structured logging
            testRunner.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
            {
                var msg = args.Length > 0 ? args[0].ToString() : "";
                Log(msg);
                return FenValue.Undefined;
            })));

            // reportHarnessStatus(status, message) - Structured harness lifecycle signal
            testRunner.Set("reportHarnessStatus", FenValue.FromFunction(new FenFunction("reportHarnessStatus", (args, thisVal) =>
            {
                var status = args.Length > 0 ? args[0].ToString() : "";
                var message = args.Length > 1 ? args[1].ToString() : null;
                ReportHarnessStatus(status, message);
                return FenValue.Undefined;
            })));
            
            // dumpAsText() - Signal text-based output mode
            testRunner.Set("dumpAsText", FenValue.FromFunction(new FenFunction("dumpAsText", (args, thisVal) =>
            {
                Log("[testRunner] dumpAsText() mode enabled");
                return FenValue.Undefined;
            })));
            
            // setTestTimeout(ms) - Set custom timeout
            testRunner.Set("setTestTimeout", FenValue.FromFunction(new FenFunction("setTestTimeout", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsNumber)
                {
                    _testTimeout = (int)args[0].ToNumber();
                    Log($"[testRunner] Timeout set to {_testTimeout}ms");
                }
                return FenValue.Undefined;
            })));
            
            // reportResult(name, status, message) - Report individual test result
            testRunner.Set("reportResult", FenValue.FromFunction(new FenFunction("reportResult", (args, thisVal) =>
            {
                var name = args.Length > 0 ? args[0].ToString() : "unknown";
                var pass = args.Length > 1 && args[1].ToBoolean();
                var msg = args.Length > 2 ? args[2].ToString() : null;
                
                AddResult(name, pass ? TestStatus.Pass : TestStatus.Fail, msg);
                Log($"[testRunner] Result: {name} = {(pass ? "PASS" : "FAIL")}");
                return FenValue.Undefined;
            })));
            
            // completeTest() - Alias for notifyDone
            testRunner.Set("completeTest", FenValue.FromFunction(new FenFunction("completeTest", (args, thisVal) =>
            {
                _testDone = true;
                _completionSignal = "testRunner.completeTest";
                Log("[testRunner] completeTest() called");
                return FenValue.Undefined;
            })));
            
            return testRunner;
        }
        
        /// <summary>
        /// Register testRunner on FenRuntime's global environment.
        /// </summary>
        public static void Register(FenRuntime runtime)
        {
            if (runtime  == null) return;
            
            var testRunner = CreateTestRunnerObject();
            runtime.GlobalEnv.Set("testRunner", FenValue.FromObject(testRunner));
        }
        
        /// <summary>
        /// Get summary of test results.
        /// </summary>
        public static (int passed, int failed, int total) GetResultSummary()
        {
            int passed = 0, failed = 0;
            foreach (var r in _results)
            {
                if (r.Status == TestStatus.Pass) passed++;
                else failed++;
            }
            return (passed, failed, _results.Count);
        }
        
        /// <summary>
        /// Generate TAP (Test Anything Protocol) output.
        /// </summary>
        public static string GenerateTAPOutput()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TAP version 13");
            sb.AppendLine($"1..{_results.Count}");
            
            for (int i = 0; i < _results.Count; i++)
            {
                var r = _results[i];
                var status = r.Status == TestStatus.Pass ? "ok" : "not ok";
                sb.AppendLine($"{status} {i + 1} - {r.Name}");
                if (r.Status != TestStatus.Pass && !string.IsNullOrEmpty(r.Message))
                {
                    sb.AppendLine($"  ---");
                    sb.AppendLine($"  message: {r.Message}");
                    sb.AppendLine($"  ...");
                }
            }
            
            return sb.ToString();
        }
    }
}
