// =============================================================================
// Test262Runner.cs
// ECMAScript Test Suite Runner
// 
// PURPOSE: Execute Test262 JavaScript tests and collect results
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Testing
{
    /// <summary>
    /// Runner for Test262 (ECMAScript Test Suite).
    /// Executes .js test files and validates expected outcomes.
    /// </summary>
    public class Test262Runner
    {
        private readonly string _test262RootPath;
        private readonly int _timeoutMs;
        private readonly List<TestResult> _results = new List<TestResult>();
        
        public class TestResult
        {
            public string TestFile { get; set; }
            public bool Passed { get; set; }
            public string Expected { get; set; }
            public string Actual { get; set; }
            public string Error { get; set; }
            public TimeSpan Duration { get; set; }
            public TestMetadata Metadata { get; set; }
        }
        
        public class TestMetadata
        {
            public string Description { get; set; }
            public bool Negative { get; set; }
            public string NegativeType { get; set; }
            public string NegativePhase { get; set; }
            public List<string> Features { get; set; } = new List<string>();
            public List<string> Includes { get; set; } = new List<string>();
            public bool IsAsync { get; set; }
        }
        
        public Test262Runner(string test262RootPath, int timeoutMs = 10000)
        {
            _test262RootPath = test262RootPath;
            _timeoutMs = timeoutMs;
        }
        
        /// <summary>
        /// Run tests in a specific category (e.g., "language/expressions").
        /// </summary>
        public async Task<IReadOnlyList<TestResult>> RunCategoryAsync(string category, Action<string, int> onProgress = null, int maxTests = int.MaxValue)
        {
            _results.Clear();
            
            var categoryPath = Path.Combine(_test262RootPath, "test", category);
            if (!Directory.Exists(categoryPath))
            {
                FenLogger.Warn($"[Test262] Category path not found: {categoryPath}", LogCategory.General);
                return _results.AsReadOnly();
            }
            
            var testFiles = Directory.GetFiles(categoryPath, "*.js", SearchOption.AllDirectories);
            int count = 0;
            
            foreach (var testFile in testFiles)
            {
                if (count++ >= maxTests) break;
                
                // Skip helper/harness files
                var fileName = Path.GetFileName(testFile);
                if (fileName.StartsWith("_") || fileName.Contains("_FIXTURE"))
                    continue;
                
                var result = await RunSingleTestAsync(testFile);
                _results.Add(result);
                onProgress?.Invoke(Path.GetFileName(testFile), count);
            }
            
            return _results.AsReadOnly();
        }
        
        /// <summary>
        /// Run a single Test262 test file.
        /// </summary>
        /// <summary>
        /// Run a single Test262 test file.
        /// </summary>
        public async Task<TestResult> RunSingleTestAsync(string testFile)
        {
            var result = new TestResult { TestFile = testFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                var content = await File.ReadAllTextAsync(testFile);
                var metadata = ParseMetadata(content);
                result.Metadata = metadata;

                // 1. Prepare Runtime
                // We recreate the runtime for each test to ensure isolation
                // In the future for perf we might reuse it but clean the global scope
                var runtime = new FenBrowser.FenEngine.Core.FenRuntime();
                
                // 2. Load Harness Files
                // Default harness files required by most tests
                var harnessPath = Path.Combine(_test262RootPath, "harness");
                var assertJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "assert.js"));
                var staJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "sta.js"));
                
                var preludeBuilder = new StringBuilder();
                preludeBuilder.Append(assertJs);
                preludeBuilder.Append("\n;\n");
                preludeBuilder.Append(staJs);
                preludeBuilder.Append("\n;\n");

                // 3. Load Includes
                foreach (var include in metadata.Includes)
                {
                    var includePath = Path.Combine(harnessPath, include);
                    if (File.Exists(includePath))
                    {
                        var incContent = await File.ReadAllTextAsync(includePath);
                        preludeBuilder.Append(incContent);
                        preludeBuilder.Append("\n;\n");
                    }
                    else
                    {
                        FenLogger.Warn($"[Test262] Missing include: {include}", LogCategory.General);
                    }
                }

                // 4. Combine and Execute
                var fullScript = preludeBuilder.ToString() + content;
                
                // Some tests depend on specific global properties or potentially async
                // For now, allow generic execution
                
                try
                {
                    // Execute with timeout
                    var executionTask = Task.Run(() => runtime.ExecuteSimple(fullScript));
                    
                    if (await Task.WhenAny(executionTask, Task.Delay(_timeoutMs)) == executionTask)
                    {
                        // Task completed within timeout
                        // If executionTask threw an exception, await it to rethrow
                        await executionTask;
                        
                        // If we reached here without exception
                        if (metadata.Negative)
                        {
                             result.Passed = false;
                             result.Expected = $"Error ({metadata.NegativeType})";
                             result.Actual = "Success (No Error)";
                             result.Error = "Test was expected to fail but succeeded";
                        }
                        else
                        {
                            result.Passed = true;
                            result.Expected = "Success";
                            result.Actual = "Success";
                        }
                    }
                    else
                    {
                        // Timeout
                        result.Passed = false;
                        result.Actual = "Timeout";
                        result.Error = $"Test timed out after {_timeoutMs}ms";
                        // Note: We cannot easily abort the stuck thread in .NET Core without risky Thread.Abort
                        // We leave it to finish (or hang forever in background) and move on.
                    }
                }
                catch (Exception ex)
                {
                    if (metadata.Negative)
                    {
                        // TODO: Strictly check error type/message if possible
                        result.Passed = true;
                        result.Expected = $"Error ({metadata.NegativeType})";
                        result.Actual = $"Error: {ex.Message}";
                    }
                    else
                    {
                        result.Passed = false;
                        result.Expected = "Success";
                        result.Actual = $"Error: {ex.GetType().Name}";
                        result.Error = ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                // System/IO error or runner error
                result.Passed = false;
                result.Error = $"Runner Error: {ex.Message}";
            }
            
            sw.Stop();
            result.Duration = sw.Elapsed;
            
            return result;
        }
        
        /// <summary>
        /// Parse Test262 YAML frontmatter metadata.
        /// </summary>
        private TestMetadata ParseMetadata(string content)
        {
            var meta = new TestMetadata();
            
            // Extract YAML block between /*--- and ---*/
            var yamlPattern = new Regex(@"/\*---\s*(.*?)\s*---\*/", RegexOptions.Singleline);
            var match = yamlPattern.Match(content);
            
            if (!match.Success)
                return meta;
            
            var yaml = match.Groups[1].Value;
            
            // Parse description
            var descMatch = Regex.Match(yaml, @"description:\s*(.+)", RegexOptions.Multiline);
            if (descMatch.Success)
                meta.Description = descMatch.Groups[1].Value.Trim();
            
            // Parse negative expectation
            if (yaml.Contains("negative:"))
            {
                meta.Negative = true;
                var typeMatch = Regex.Match(yaml, @"type:\s*(\w+)");
                if (typeMatch.Success)
                    meta.NegativeType = typeMatch.Groups[1].Value;
                var phaseMatch = Regex.Match(yaml, @"phase:\s*(\w+)");
                if (phaseMatch.Success)
                    meta.NegativePhase = phaseMatch.Groups[1].Value;
            }
            
            // Parse features
            var featuresMatch = Regex.Match(yaml, @"features:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (featuresMatch.Success)
            {
                var features = featuresMatch.Groups[1].Value.Split(',');
                foreach (var f in features)
                {
                    var trimmed = f.Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(trimmed))
                        meta.Features.Add(trimmed);
                }
            }
            
            // Parse includes
            var includesMatch = Regex.Match(yaml, @"includes:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (includesMatch.Success)
            {
                var includes = includesMatch.Groups[1].Value.Split(',');
                foreach (var i in includes)
                {
                    var trimmed = i.Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(trimmed))
                        meta.Includes.Add(trimmed);
                }
            }
            
            // Check for async
            meta.IsAsync = yaml.Contains("async") || content.Contains("$DONE");
            
            return meta;
        }
        
        /// <summary>
        /// Generate summary report.
        /// </summary>
        public string GenerateSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Test262 Summary ===");
            sb.AppendLine();
            
            int passed = 0, failed = 0;
            var failures = new List<TestResult>();
            
            foreach (var r in _results)
            {
                if (r.Passed) passed++;
                else
                {
                    failed++;
                    failures.Add(r);
                }
            }
            
            sb.AppendLine($"Total: {_results.Count}");
            sb.AppendLine($"Passed: {passed}");
            sb.AppendLine($"Failed: {failed}");
            sb.AppendLine();
            
            if (failures.Count > 0)
            {
                sb.AppendLine("Failures:");
                foreach (var f in failures.GetRange(0, Math.Min(20, failures.Count)))
                {
                    sb.AppendLine($"  - {Path.GetFileName(f.TestFile)}");
                    if (!string.IsNullOrEmpty(f.Error))
                        sb.AppendLine($"    Error: {f.Error}");
                }
                
                if (failures.Count > 20)
                    sb.AppendLine($"  ... and {failures.Count - 20} more");
            }
            
            return sb.ToString();
        }
    }
}
