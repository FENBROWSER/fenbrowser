// =============================================================================
// TestConsoleCapture.cs
// Console Output Capture for Test Harness
// 
// PURPOSE: Intercepts console.log/error/warn and stores for test result parsing
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Captures console output for test result parsing.
    /// Intercepts console methods and stores structured output.
    /// </summary>
    public static class TestConsoleCapture
    {
        private static bool _capturing = false;
        private static List<ConsoleEntry> _entries = new List<ConsoleEntry>();
        private static Action<string> _originalConsoleHandler;
        
        public class ConsoleEntry
        {
            public DateTime Timestamp { get; set; }
            public string Level { get; set; } // log, warn, error, info
            public string Message { get; set; }
        }
        
        /// <summary>
        /// Start capturing console output.
        /// </summary>
        public static void StartCapture()
        {
            _capturing = true;
            _entries.Clear();
        }
        
        /// <summary>
        /// Stop capturing console output.
        /// </summary>
        public static void StopCapture()
        {
            _capturing = false;
        }
        
        /// <summary>
        /// Check if capturing is active.
        /// </summary>
        public static bool IsCapturing => _capturing;
        
        /// <summary>
        /// Get all captured entries.
        /// </summary>
        public static IReadOnlyList<ConsoleEntry> GetEntries() => _entries.AsReadOnly();
        
        /// <summary>
        /// Add a console entry.
        /// </summary>
        public static void AddEntry(string level, string message)
        {
            if (!_capturing) return;
            
            _entries.Add(new ConsoleEntry
            {
                Timestamp = DateTime.Now,
                Level = level ?? "log",
                Message = message ?? ""
            });
            
            // Also try to parse testharness.js output
            TryParseTestHarnessResult(message);
        }
        
        /// <summary>
        /// Get all output as single string.
        /// </summary>
        public static string GetFullOutput()
        {
            var sb = new StringBuilder();
            foreach (var entry in _entries)
            {
                sb.AppendLine($"[{entry.Level}] {entry.Message}");
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Clear captured entries.
        /// </summary>
        public static void Clear()
        {
            _entries.Clear();
        }
        
        // --- testharness.js result parsing ---
        
        // Patterns for testharness.js output
        private static readonly Regex TestPassPattern = new Regex(@"^(PASS|OK)\s+(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex TestFailPattern = new Regex(@"^(FAIL|NOT OK)\s+(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex TestTimeoutPattern = new Regex(@"^TIMEOUT\s+(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex TestResultPattern = new Regex(@"^Result:\s+(PASS|FAIL)\s+(.+)$", RegexOptions.IgnoreCase);
        
        private static void TryParseTestHarnessResult(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            
            var lines = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                // Check for PASS
                var passMatch = TestPassPattern.Match(trimmed);
                if (passMatch.Success)
                {
                    TestHarnessAPI.AddResult(passMatch.Groups[2].Value, TestHarnessAPI.TestStatus.Pass);
                    continue;
                }
                
                // Check for FAIL
                var failMatch = TestFailPattern.Match(trimmed);
                if (failMatch.Success)
                {
                    TestHarnessAPI.AddResult(failMatch.Groups[2].Value, TestHarnessAPI.TestStatus.Fail);
                    continue;
                }
                
                // Check for TIMEOUT
                var timeoutMatch = TestTimeoutPattern.Match(trimmed);
                if (timeoutMatch.Success)
                {
                    TestHarnessAPI.AddResult(timeoutMatch.Groups[1].Value, TestHarnessAPI.TestStatus.Timeout);
                    continue;
                }
                
                // Check for Result: format
                var resultMatch = TestResultPattern.Match(trimmed);
                if (resultMatch.Success)
                {
                    var status = resultMatch.Groups[1].Value.ToUpperInvariant() == "PASS" 
                        ? TestHarnessAPI.TestStatus.Pass 
                        : TestHarnessAPI.TestStatus.Fail;
                    TestHarnessAPI.AddResult(resultMatch.Groups[2].Value, status);
                }
            }
        }
        
        /// <summary>
        /// Parse structured testharness.js completion output.
        /// </summary>
        public static void ParseTestHarnessOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return;
            
            // Common patterns in testharness.js final output
            // "Harness: the test ran to completion."
            // "Pass: N, Fail: M, Timeout: X, NotRun: Y"
            
            var summaryPattern = new Regex(@"Pass:\s*(\d+),?\s*Fail:\s*(\d+)", RegexOptions.IgnoreCase);
            var summaryMatch = summaryPattern.Match(output);
            if (summaryMatch.Success)
            {
                TestHarnessAPI.Log($"[Parser] Found summary: Pass={summaryMatch.Groups[1].Value}, Fail={summaryMatch.Groups[2].Value}");
            }
            
            // Check for completion
            if (output.Contains("ran to completion") || output.Contains("harness_status"))
            {
                TestHarnessAPI.Log("[Parser] Test harness completed");
            }
        }
    }
}
