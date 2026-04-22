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
using System.Text.Json;
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
        private const string StructuredResultMarker = "__FEN_WPT_RESULT__";
        private const string StructuredCompletionMarker = "__FEN_WPT_COMPLETE__";
        
        private static void TryParseTestHarnessResult(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            
            var lines = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var normalized = NormalizeHarnessLine(trimmed);

                var structuredResultIndex = normalized.IndexOf(StructuredResultMarker, StringComparison.Ordinal);
                if (structuredResultIndex >= 0)
                {
                    ParseStructuredResult(normalized[(structuredResultIndex + StructuredResultMarker.Length)..]);
                    continue;
                }

                var structuredCompletionIndex = normalized.IndexOf(StructuredCompletionMarker, StringComparison.Ordinal);
                if (structuredCompletionIndex >= 0)
                {
                    ParseStructuredCompletion(normalized[(structuredCompletionIndex + StructuredCompletionMarker.Length)..]);
                    continue;
                }
                
                // Check for PASS
                var passMatch = TestPassPattern.Match(normalized);
                if (passMatch.Success)
                {
                    TestHarnessAPI.AddResult(passMatch.Groups[2].Value, TestHarnessAPI.TestStatus.Pass);
                    continue;
                }
                
                // Check for FAIL
                var failMatch = TestFailPattern.Match(normalized);
                if (failMatch.Success)
                {
                    TestHarnessAPI.AddResult(failMatch.Groups[2].Value, TestHarnessAPI.TestStatus.Fail);
                    continue;
                }
                
                // Check for TIMEOUT
                var timeoutMatch = TestTimeoutPattern.Match(normalized);
                if (timeoutMatch.Success)
                {
                    TestHarnessAPI.AddResult(timeoutMatch.Groups[1].Value, TestHarnessAPI.TestStatus.Timeout);
                    continue;
                }
                
                // Check for Result: format
                var resultMatch = TestResultPattern.Match(normalized);
                if (resultMatch.Success)
                {
                    var status = resultMatch.Groups[1].Value.ToUpperInvariant() == "PASS" 
                        ? TestHarnessAPI.TestStatus.Pass 
                        : TestHarnessAPI.TestStatus.Fail;
                    TestHarnessAPI.AddResult(resultMatch.Groups[2].Value, status);
                }
            }
        }

        private static string NormalizeHarnessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            // Console bridge often prefixes messages as "[INFO] ...", "[WARN] ...", etc.
            // Strip exactly one leading bracketed level token for harness parsing.
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                var closing = line.IndexOf(']');
                if (closing > 0 && closing < line.Length - 1)
                {
                    var level = line.Substring(1, closing - 1);
                    if (Regex.IsMatch(level, "^[A-Za-z]+$"))
                    {
                        return line[(closing + 1)..].TrimStart();
                    }
                }
            }

            return line;
        }

        private static void ParseStructuredResult(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                var name = root.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : "unnamed";
                var status = root.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetInt32()
                    : -1;
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;

                TestHarnessAPI.AddResult(
                    name,
                    status == 0 ? TestHarnessAPI.TestStatus.Pass : TestHarnessAPI.TestStatus.Fail,
                    message);
            }
            catch (Exception ex)
            {
                TestHarnessAPI.Log($"[Parser] Failed to parse structured WPT result: {ex.Message}");
            }
        }

        private static void ParseStructuredCompletion(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                TestHarnessAPI.ReportHarnessStatus("complete");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                TestHarnessAPI.ReportHarnessStatus("complete", message);
            }
            catch (Exception ex)
            {
                TestHarnessAPI.Log($"[Parser] Failed to parse structured WPT completion: {ex.Message}");
                TestHarnessAPI.ReportHarnessStatus("complete");
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
