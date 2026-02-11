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
using System.Linq;
using System.Collections.Concurrent;
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
            public bool IsOnlyStrict { get; set; }
            public bool IsNoStrict { get; set; }
            public bool IsModule { get; set; }
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
            var resultsBag = new System.Collections.Concurrent.ConcurrentBag<TestResult>();
            
            // Limit concurrency to avoid overwhelming the system
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 1 }; // Sequential to avoid static state races (DefaultPrototype, etc.)

            await Parallel.ForEachAsync(testFiles, parallelOptions, async (testFile, token) =>
            {
                if (Interlocked.Increment(ref count) > maxTests) return;
                
                // Skip helper/harness files
                var fileName = Path.GetFileName(testFile);
                if (fileName.StartsWith("_") || fileName.Contains("_FIXTURE"))
                    return;
                
                var result = await RunSingleTestAsync(testFile);
                resultsBag.Add(result);
                onProgress?.Invoke(Path.GetFileName(testFile), count);
            });
            
            _results.AddRange(resultsBag.OrderBy(r => r.TestFile)); // Sort for consistency
            return _results.AsReadOnly();
        }

        /// <summary>
        /// Run a specific slice of tests (skip/take) from all available tests.
        /// </summary>
        public async Task<IReadOnlyList<TestResult>> RunSliceAsync(string category, int skip, int take, Action<string, int> onProgress = null)
        {
            _results.Clear();
            
            var categoryPath = Path.Combine(_test262RootPath, "test", category);
            if (!Directory.Exists(categoryPath))
            {
                FenLogger.Warn($"[Test262] Category path not found: {categoryPath}", LogCategory.General);
                return _results.AsReadOnly();
            }
            
            // Get all files and sort deterministically
            var allTestFiles = Directory.GetFiles(categoryPath, "*.js", SearchOption.AllDirectories)
                                      .Where(f => !Path.GetFileName(f).StartsWith("_") && !Path.GetFileName(f).Contains("_FIXTURE"))
                                      .OrderBy(f => f) // Deterministic order is CRITICAL for slicing
                                      .Skip(skip)
                                      .Take(take)
                                      .ToList();
                                      
            Console.WriteLine($"[Test262] Found {allTestFiles.Count} tests in slice (Skip {skip}, Take {take})");

            var resultsBag = new ConcurrentBag<TestResult>();
            int count = 0;
            
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 1 }; // Sequential to avoid static state races (DefaultPrototype, etc.)

            await Parallel.ForEachAsync(allTestFiles, parallelOptions, async (testFile, token) =>
            {
                var result = await RunSingleTestAsync(testFile);
                resultsBag.Add(result);
                int current = Interlocked.Increment(ref count);
                onProgress?.Invoke(Path.GetFileName(testFile), current);
            });
            
            _results.AddRange(resultsBag.OrderBy(r => r.TestFile));
            return _results.AsReadOnly();
        }

        /// <summary>
        /// Run a specific slice of tests (skip/take) from all available tests.
        /// </summary>




        /// Run a single Test262 test file.
        /// </summary>
        public async Task<TestResult> RunSingleTestAsync(string testFile)
        {
            var result = new TestResult { TestFile = testFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                if (Path.IsPathRooted(testFile))
            {
               // It's absolute, use it directly
            }
            else
            {
               testFile = Path.Combine(_test262RootPath, testFile);
            }
                var content = await File.ReadAllTextAsync(testFile);
                var metadata = ParseMetadata(content);
                result.Metadata = metadata;
                bool isModuleGoal = metadata.IsModule;

                // Parse-phase negatives should be validated against the test source itself.
                // Running them only as part of a large harness prelude can hide goal-sensitive early errors.
                if (metadata.Negative && string.Equals(metadata.NegativePhase, "parse", StringComparison.OrdinalIgnoreCase))
                {
                    var parseSource = metadata.IsOnlyStrict ? "\"use strict\";\n" + content : content;
                    var parseLexer = new Lexer(parseSource);
                    var parseParser = new Parser(parseLexer, isModule: isModuleGoal);
                    parseParser.ParseProgram();

                    if (parseParser.Errors.Count > 0)
                    {
                        string parseErr = string.Join("\n", parseParser.Errors);
                        bool expectsSyntaxError = string.Equals(metadata.NegativeType, "SyntaxError", StringComparison.OrdinalIgnoreCase);
                        result.Passed = string.IsNullOrEmpty(metadata.NegativeType) ||
                                        parseErr.Contains(metadata.NegativeType, StringComparison.OrdinalIgnoreCase) ||
                                        expectsSyntaxError;
                        result.Expected = $"Error ({metadata.NegativeType})";
                        result.Actual = parseErr;
                        if (!result.Passed)
                        {
                            result.Error = $"Expected {metadata.NegativeType} but got different parse error: {parseErr}";
                        }
                    }
                    else
                    {
                        result.Passed = false;
                        result.Expected = $"Error ({metadata.NegativeType})";
                        result.Actual = "Success (No Error)";
                        result.Error = "Parse-negative test parsed successfully";
                    }

                    sw.Stop();
                    result.Duration = sw.Elapsed;
                    return result;
                }

                // 1. Prepare Runtime
                // We recreate the runtime for each test to ensure isolation
                // In the future for perf we might reuse it but clean the global scope
                var runtime = new FenBrowser.FenEngine.Core.FenRuntime();
                runtime.OnConsoleMessage = (msg) => Console.WriteLine($"[JS-CONSOLE] {msg}");

                if (isModuleGoal &&
                    metadata.Negative &&
                    !string.Equals(metadata.NegativePhase, "parse", StringComparison.OrdinalIgnoreCase) &&
                    runtime.Context?.ModuleLoader is FenBrowser.FenEngine.Core.ModuleLoader strictModuleLoader)
                {
                    strictModuleLoader.ThrowOnEvaluationError = true;
                }

                // Inject console object since it's missing in default runtime
                var consoleObj = new FenBrowser.FenEngine.Core.FenObject();
                consoleObj.Set("log", FenBrowser.FenEngine.Core.FenValue.FromFunction(new FenBrowser.FenEngine.Core.FenFunction("log", (args, thisVal) =>
                {
                    var msg = args.Length > 0 ? args[0].ToString() : "";
                    runtime.OnConsoleMessage?.Invoke(msg);
                    return FenBrowser.FenEngine.Core.FenValue.Undefined;
                })));
                runtime.GlobalEnv.Set("console", FenBrowser.FenEngine.Core.FenValue.FromObject(consoleObj));
                
                // 2. Load Harness Files
                // Default harness files required by most tests
                var harnessPath = Path.Combine(_test262RootPath, "harness");
                var assertJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "assert.js"));
                var staJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "sta.js"));
                
                var preludeBuilder = new StringBuilder();
                if (metadata.IsOnlyStrict)
                {
                    preludeBuilder.Append("\"use strict\";\n");
                }
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
                var preludeScript = preludeBuilder.ToString();
                var fullScript = preludeScript + content;
                // var fullScript = content; // SKIP HARNESS FOR DEBUGGING

                if (testFile.EndsWith("assign-to-global-undefined.js", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[DBG] metadata.IsOnlyStrict={metadata.IsOnlyStrict}");
                    Console.WriteLine($"[DBG] pre-run HasBinding('undeclared')={runtime.GlobalEnv.HasBinding("undeclared")}");
                }
                
                // Some tests depend on specific global properties or potentially async
                // For now, allow generic execution
                
                try
                {
                    // Enforce strict runtime semantics for Test262 `onlyStrict` tests.
                    // Parser directive handling is not yet fully spec-complete in all paths.
                    if (metadata.IsOnlyStrict && runtime.Context != null)
                    {
                        runtime.Context.StrictMode = true;
                    }

                    // Execute with timeout and error checking
                    Task<FenBrowser.FenEngine.Core.Interfaces.IValue> executionTask;
                    if (isModuleGoal)
                    {
                        executionTask = Task.Run<FenBrowser.FenEngine.Core.Interfaces.IValue>(() =>
                        {
                            if (runtime.Context?.ModuleLoader == null)
                            {
                                throw new InvalidOperationException("Module loader is not available");
                            }

                            // Run harness files in script goal, then parse only the test source as module goal.
                            if (!string.IsNullOrWhiteSpace(preludeScript))
                            {
                                runtime.ExecuteSimple(preludeScript);
                            }

                            runtime.Context.ModuleLoader.LoadModuleSrc(content, testFile);
                            return FenValue.Undefined;
                        });
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG] Executing script. Length: {fullScript.Length}");
                        Console.WriteLine($"[DEBUG] Script Tail: {fullScript.Substring(Math.Max(0, fullScript.Length - 100))}");
                        executionTask = Task.Run(() => runtime.ExecuteSimple(fullScript));
                    }
                    
                    if (await Task.WhenAny(executionTask, Task.Delay(_timeoutMs)) == executionTask)
                    {
                        // Task completed within timeout
                        // Re-throw if the task itself faulted (unlikely due to try/catch inside ExecuteSimple but possible)
                        var resultValue = await executionTask; 
                        
                        // IMPORTANT: FenRuntime.ExecuteSimple returns FenValue.FromError on failure, NOT an exception.
                        // We must check if resultValue.Type is Error.
                        // We access the raw interface or rely on FenValue struct if possible.
                        // Since runtime returns IValue, we cast to FenBrowser.FenEngine.Core.FenValue if needed or use IValue.Type property
                        
                        bool isError = false;
                        string errorMsg = "";

                        // DEBUG: Print the raw result value to see what we getting
                        Console.WriteLine($"[DEBUG] ExecuteSimple Result Type: {resultValue?.Type}");
                        Console.WriteLine($"[DEBUG] ExecuteSimple Result: {resultValue}");

                        // Check for Error type (value type 8 based on typical enum) or string containing "Error"
                        // Safer to check the Type property from IValue
                        if (resultValue != null && (resultValue.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error || resultValue.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw || resultValue.ToString().Contains("=== VERIFICATION RESULTS ===") || resultValue.ToString().StartsWith("Error") || resultValue.ToString().StartsWith("FAIL")))
                        {
                            isError = true;
                            errorMsg = resultValue.ToString();
                        }
                        

                        // Check for global result variable as side-channel default
                        try 
                        {
                            var globalResult = runtime.GlobalEnv.Get("__VERIFICATION_RESULT__");
                            if (!globalResult.IsUndefined)
                            {
                                // Console.WriteLine($"[DEBUG] Found __VERIFICATION_RESULT__: {globalResult}");
                                if (globalResult.ToString().Contains("=== VERIFICATION RESULTS ==="))
                                {
                                    isError = true;
                                    errorMsg = globalResult.ToString();
                                }
                            }
                        }
                        catch {}

                        // If we reached here without exception
                        if (metadata.Negative)
                        {
                             if (isError)
                             {
                                 // Check if the error message matches the expected type
                                 if (!string.IsNullOrEmpty(metadata.NegativeType) && !errorMsg.Contains(metadata.NegativeType))
                                 {
                                     result.Passed = false;
                                     result.Expected = $"Error ({metadata.NegativeType})";
                                     result.Actual = $"Error ({errorMsg})"; // Wrong error type
                                     result.Error = $"Expected {metadata.NegativeType} but got different error: {errorMsg}";
                                 }
                                 else
                                 {
                                     result.Passed = true;
                                     result.Expected = $"Error ({metadata.NegativeType})";
                                     result.Actual = errorMsg;
                                 }
                             }
                             else
                             {
                                 result.Passed = false;
                                 result.Expected = $"Error ({metadata.NegativeType})";
                                 result.Actual = "Success (No Error)";
                                 result.Error = "Test was expected to fail but succeeded";
                             }
                        }
                        else
                        {
                            if (isError)
                            {
                                result.Passed = false;
                                result.Expected = "Success";
                                result.Actual = "Error";
                                result.Error = errorMsg;
                            }
                            else
                            {
                                result.Passed = true;
                                result.Expected = "Success";
                                result.Actual = "Success";
                            }
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
        public static TestMetadata ParseMetadata(string content)
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

            // Parse flags
            var flagsMatch = Regex.Match(yaml, @"flags:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (flagsMatch.Success)
            {
                var flags = flagsMatch.Groups[1].Value.Split(',');
                foreach (var f in flags)
                {
                    var flag = f.Trim().Trim('"', '\'');
                    if (string.Equals(flag, "onlyStrict", StringComparison.Ordinal))
                    {
                        meta.IsOnlyStrict = true;
                    }
                    else if (string.Equals(flag, "noStrict", StringComparison.Ordinal))
                    {
                        meta.IsNoStrict = true;
                    }
                    else if (string.Equals(flag, "async", StringComparison.Ordinal))
                    {
                        meta.IsAsync = true;
                    }
                    else if (string.Equals(flag, "module", StringComparison.Ordinal))
                    {
                        meta.IsModule = true;
                    }
                }
            }
            
            // Check for async
            if (!meta.IsAsync)
            {
                meta.IsAsync = content.Contains("$DONE");
            }
            
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

        /// <summary>
        /// Discover all test files in a category (or whole suite if category is empty/null).
        /// </summary>
        public List<string> DiscoverTests(string category = "")
        {
            var path = string.IsNullOrEmpty(category) ? 
                Path.Combine(_test262RootPath, "test") : 
                Path.Combine(_test262RootPath, "test", category);

            if (!Directory.Exists(path))
            {
                FenLogger.Warn($"[Test262] Path not found: {path}", LogCategory.General);
                return new List<string>();
            }

            return Directory.GetFiles(path, "*.js", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("_") && !Path.GetFileName(f).Contains("_FIXTURE"))
                .OrderBy(f => f)
                .ToList();
        }

        /// <summary>
        /// Run a specific list of test files.
        /// </summary>
        public async Task<IReadOnlyList<TestResult>> RunSpecificTestsAsync(IEnumerable<string> testFiles, Action<string, int> onProgress = null)
        {
            var resultsBag = new ConcurrentBag<TestResult>();
            int count = 0;
            
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 1 }; // Sequential to avoid static state races

            await Parallel.ForEachAsync(testFiles, parallelOptions, async (testFile, token) =>
            {
                var result = await RunSingleTestAsync(testFile);
                resultsBag.Add(result);
                int current = Interlocked.Increment(ref count);
                onProgress?.Invoke(Path.GetFileName(testFile), current);
            });
            
            var list = resultsBag.OrderBy(r => r.TestFile).ToList();
            lock (_results)
            {
                _results.AddRange(list);
            }
            return list.AsReadOnly();
        }
    }
}
