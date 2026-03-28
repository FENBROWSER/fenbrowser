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
using System.Threading;
using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Security;

namespace FenBrowser.FenEngine.Testing
{
    /// <summary>
    /// Runner for Test262 (ECMAScript Test Suite).
    /// Executes .js test files and validates expected outcomes.
    /// </summary>
    public class Test262Runner
    {
        private const string AsyncCompleteSignal = "Test262:AsyncTestComplete";
        private const string AsyncFailureSignalPrefix = "Test262:AsyncTestFailure:";
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
            public bool IsRaw { get; set; }
        }

        private sealed class Test262AgentController : IDisposable
        {
            private readonly Test262Runner _owner;
            private readonly ConcurrentQueue<string> _reports = new ConcurrentQueue<string>();
            private readonly SemaphoreSlim _reportSignal = new SemaphoreSlim(0);
            private readonly List<Test262AgentWorker> _workers = new List<Test262AgentWorker>();
            private readonly object _workersLock = new object();
            private bool _disposed;

            public Test262AgentController(Test262Runner owner)
            {
                _owner = owner;
            }

            public void StartWorker(string source, Action<string> consoleSink)
            {
                var worker = new Test262AgentWorker(_owner, this, source, consoleSink);
                lock (_workersLock)
                {
                    _workers.Add(worker);
                }

                worker.Start();
            }

            public void Broadcast(JsArrayBuffer sharedBuffer)
            {
                if (sharedBuffer == null)
                {
                    return;
                }

                Test262AgentWorker[] workers;
                lock (_workersLock)
                {
                    workers = _workers.ToArray();
                }

                foreach (var worker in workers)
                {
                    worker.EnqueueBroadcast(sharedBuffer);
                }
            }

            public void Report(FenValue value)
            {
                _reports.Enqueue(value.ToString());
                _reportSignal.Release();
            }

            public string TryGetReport()
            {
                if (!_reportSignal.Wait(0))
                {
                    return null;
                }

                return _reports.TryDequeue(out var report) ? report : null;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                Test262AgentWorker[] workers;
                lock (_workersLock)
                {
                    workers = _workers.ToArray();
                    _workers.Clear();
                }

                foreach (var worker in workers)
                {
                    worker.Dispose();
                }

                _reportSignal.Dispose();
            }
        }

        private sealed class Test262AgentWorker : IDisposable
        {
            private readonly Test262Runner _owner;
            private readonly Test262AgentController _controller;
            private readonly string _source;
            private readonly Action<string> _consoleSink;
            private readonly BlockingCollection<JsArrayBuffer> _broadcasts = new BlockingCollection<JsArrayBuffer>();
            private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
            private volatile FenFunction _receiveBroadcast;
            private volatile bool _leaving;
            private Task _workerTask;

            public Test262AgentWorker(Test262Runner owner, Test262AgentController controller, string source, Action<string> consoleSink)
            {
                _owner = owner;
                _controller = controller;
                _source = source ?? string.Empty;
                _consoleSink = consoleSink;
            }

            public void Start()
            {
                _workerTask = RunBackgroundAsync(RunAsync, _shutdown.Token);
            }

            public void RegisterReceiveBroadcast(FenFunction callback)
            {
                _receiveBroadcast = callback;
            }

            public void EnqueueBroadcast(JsArrayBuffer sharedBuffer)
            {
                if (_leaving || _broadcasts.IsAddingCompleted)
                {
                    return;
                }

                try
                {
                    _broadcasts.Add(sharedBuffer);
                }
                catch (InvalidOperationException)
                {
                }
            }

            public void Report(FenValue value)
            {
                _controller.Report(value);
            }

            public void Leave()
            {
                _leaving = true;
                _broadcasts.CompleteAdding();
            }

            private Task RunAsync()
            {
                var runtime = new FenRuntime();
                if (runtime.Context != null)
                {
                    runtime.Context.Permissions.Grant(JsPermissions.Eval);
                }

                runtime.OnConsoleMessage = _consoleSink;
                _owner.InstallConsoleBindings(runtime);
                runtime.SetGlobal("$262", FenValue.FromObject(_owner.CreateHost262(runtime, _controller, this)));

                try
                {
                    runtime.ExecuteSimple(_source, "test262-agent.js", allowReturn: true);

                    while (!_leaving && !_shutdown.IsCancellationRequested)
                    {
                        if (_receiveBroadcast == null)
                        {
                            break;
                        }

                        JsArrayBuffer buffer;
                        try
                        {
                            buffer = _broadcasts.Take(_shutdown.Token);
                        }
                        catch (InvalidOperationException)
                        {
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        var callback = _receiveBroadcast;
                        if (callback == null)
                        {
                            continue;
                        }

                        runtime.RunWithRealmActivation(() =>
                        {
                            callback.Invoke(new[] { FenValue.FromObject(buffer) }, runtime.Context, FenValue.Undefined);
                            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                        });
                    }
                }
                catch (Exception ex)
                {
                    _controller.Report(FenValue.FromString("AgentError:" + ex.Message));
                }
                finally
                {
                    Leave();
                }

                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Leave();
                _shutdown.Cancel();
                try
                {
                    _workerTask?.Wait(TimeSpan.FromSeconds(1));
                }
                catch
                {
                }

                _shutdown.Dispose();
                _broadcasts.Dispose();
            }
        }
        
        public Test262Runner(string test262RootPath, int timeoutMs = 10000)
        {
            _test262RootPath = Path.GetFullPath(test262RootPath);
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
            
            var testFiles = Directory.GetFiles(categoryPath, "*.js", SearchOption.AllDirectories)
                .Where(IsDiscoverableTestFile)
                .ToList();
            int count = 0;
            var resultsBag = new System.Collections.Concurrent.ConcurrentBag<TestResult>();
            
            // Limit concurrency to avoid overwhelming the system
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 1 }; // Sequential to avoid static state races (DefaultPrototype, etc.)

            await Parallel.ForEachAsync(testFiles, parallelOptions, async (testFile, token) =>
            {
                if (Interlocked.Increment(ref count) > maxTests) return;
                
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
                                      .Where(IsDiscoverableTestFile)
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
        /// <summary>
        /// Memory threshold in bytes. If GC reports more than this, skip the test.
        /// Default: 1.5 GB
        /// </summary>
        public long MemoryThresholdBytes { get; set; } = 8_000_000_000L;


        private static Task<T> RunBackground<T>(Func<T> operation, CancellationToken cancellationToken = default)
        {
            return Task.Factory.StartNew(operation, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private static Task RunBackgroundAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            return Task.Factory.StartNew(operation, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        public async Task<TestResult> RunSingleTestAsync(string testFile)
        {
            var result = new TestResult { TestFile = testFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Test262AgentController agentController = null;

            try
            {
                // Memory safety: skip test if managed heap is already too large
                long currentMemory = GC.GetTotalMemory(false);
                if (currentMemory > MemoryThresholdBytes)
                {
                    // Force GC and re-check
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    currentMemory = GC.GetTotalMemory(true);
                    if (currentMemory > MemoryThresholdBytes)
                    {
                        result.Passed = false;
                        result.Error = $"SKIPPED: Memory pressure ({currentMemory / 1_000_000}MB > {MemoryThresholdBytes / 1_000_000}MB threshold)";
                        result.Actual = "Skipped (memory)";
                        sw.Stop();
                        result.Duration = sw.Elapsed;
                        return result;
                    }
                }
                // Normalize to an absolute path exactly once; avoid duplicate root prefixing
                // when discovered paths are already root-relative (for example: test262\\test\\...).
                var resolvedTestFile = Path.IsPathRooted(testFile)
                    ? testFile
                    : Path.GetFullPath(testFile);

                if (!File.Exists(resolvedTestFile))
                {
                    var trimmed = testFile.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var rootName = Path.GetFileName(_test262RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(rootName))
                    {
                        var rootPrefix = rootName + Path.DirectorySeparatorChar;
                        var altRootPrefix = rootName + Path.AltDirectorySeparatorChar;
                        if (trimmed.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith(altRootPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            trimmed = trimmed.Substring(rootName.Length + 1);
                        }
                    }
                    resolvedTestFile = Path.Combine(_test262RootPath, trimmed);
                }

                var content = await File.ReadAllTextAsync(resolvedTestFile);
                var metadata = ParseMetadata(content);
                result.Metadata = metadata;
                bool isModuleGoal = metadata.IsModule;

                // Parse-phase negatives should be validated against the test source itself.
                // Running them only as part of a large harness prelude can hide goal-sensitive early errors.
                if (metadata.Negative && string.Equals(metadata.NegativePhase, "parse", StringComparison.OrdinalIgnoreCase))
                {
                    var parseSource = metadata.IsOnlyStrict ? "\"use strict\";\n" + content : content;
                    var parseLexer = new Lexer(parseSource);
                    var parseParser = new Parser(
                        parseLexer,
                        isModule: isModuleGoal,
                        allowReturnOutsideFunction: true,
                        allowRecovery: false);
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
                agentController = new Test262AgentController(this);
                TaskCompletionSource<string> asyncSignal = null;
                if (metadata.IsAsync)
                {
                    asyncSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (runtime.Context != null) 
                {
                    runtime.Context.Permissions.Grant(JsPermissions.Eval);
                }
                runtime.OnConsoleMessage = (msg) =>
                {
                    if (asyncSignal != null && !string.IsNullOrEmpty(msg))
                    {
                        if (msg.StartsWith(AsyncCompleteSignal, StringComparison.Ordinal))
                        {
                            asyncSignal.TrySetResult(AsyncCompleteSignal);
                        }
                        else if (msg.StartsWith(AsyncFailureSignalPrefix, StringComparison.Ordinal))
                        {
                            asyncSignal.TrySetResult(msg);
                        }
                    }
                    Console.WriteLine($"[JS-CONSOLE] {msg}");
                };

                if (isModuleGoal &&
                    metadata.Negative &&
                    !string.Equals(metadata.NegativePhase, "parse", StringComparison.OrdinalIgnoreCase) &&
                    runtime.Context?.ModuleLoader is FenBrowser.FenEngine.Core.ModuleLoader strictModuleLoader)
                {
                    strictModuleLoader.ThrowOnEvaluationError = true;
                }

                InstallConsoleBindings(runtime);
                runtime.SetGlobal("$262", FenValue.FromObject(CreateHost262(runtime, agentController)));
                
                var preludeBuilder = new StringBuilder();
                if (!metadata.IsRaw)
                {
                    // 2. Load Harness Files
                    // Default harness files required by most tests.
                    var harnessPath = Path.Combine(_test262RootPath, "harness");
                    var assertJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "assert.js"));
                    var staJs = await File.ReadAllTextAsync(Path.Combine(harnessPath, "sta.js"));
                    if (metadata.IsAsync && !metadata.Includes.Any(x => string.Equals(x, "doneprintHandle.js", StringComparison.Ordinal)))
                    {
                        metadata.Includes.Add("doneprintHandle.js");
                    }

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
                }

                // 4. Combine and Execute
                var preludeScript = preludeBuilder.ToString();
                var fullScript = preludeScript + content;
                // var fullScript = content; // SKIP HARNESS FOR DEBUGGING

                // if (testFile.EndsWith("assign-to-global-undefined.js", StringComparison.OrdinalIgnoreCase))
                // {
                //     Console.WriteLine($"[DBG] metadata.IsOnlyStrict={metadata.IsOnlyStrict}");
                //     Console.WriteLine($"[DBG] pre-run HasBinding('undeclared')={runtime.GlobalEnv.HasBinding("undeclared")}");
                // }
                
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

                    // Execute with timeout + memory watchdog
                    using var cts = new System.Threading.CancellationTokenSource();
                    long memBefore = GC.GetTotalMemory(false);

                    var token = cts.Token;
                    Task<FenBrowser.FenEngine.Core.Interfaces.IValue> executionTask;
                    if (isModuleGoal)
                    {
                        executionTask = RunBackground<FenBrowser.FenEngine.Core.Interfaces.IValue>(() =>
                        {
                            if (runtime.Context?.ModuleLoader == null)
                                throw new InvalidOperationException("Module loader is not available");
                            if (!string.IsNullOrWhiteSpace(preludeScript))
                            {
                                var preludeResult = runtime.ExecuteSimple(preludeScript, allowReturn: true, cancellationToken: token);
                                if (preludeResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
                                    return preludeResult;
                            }
                            runtime.Context.ModuleLoader.LoadModuleSrc(content, resolvedTestFile);
                            return FenValue.Undefined;
                        }, token);
                    }
                    else
                    {
                        executionTask = RunBackground(() => runtime.ExecuteSimple(fullScript, allowReturn: true, cancellationToken: token), token);
                    }

                    // Memory watchdog: check every 500ms, cancel if memory grows > 500MB for this test
                    var memoryWatchdog = RunBackgroundAsync(async () =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, cts.Token).ConfigureAwait(false);
                            long memNow = GC.GetTotalMemory(false);
                            if (memNow - memBefore > 500_000_000) // 500MB growth from this single test
                            {
                                cts.Cancel();
                                return;
                            }
                        }
                    }, cts.Token);

                    var timeoutTask = Task.Delay(_timeoutMs, cts.Token);
                    var completedTask = await Task.WhenAny(executionTask, timeoutTask, memoryWatchdog);

                    if (completedTask == executionTask && !cts.IsCancellationRequested)
                    {
                        // Task completed within timeout
                        FenBrowser.FenEngine.Core.Interfaces.IValue resultValue = null;
                        bool cancelledDuringAwait = false;
                        try
                        {
                            resultValue = await executionTask;
                        }
                        catch (OperationCanceledException)
                        {
                            cancelledDuringAwait = true;
                        }
                        if (cancelledDuringAwait)
                        {
                            cts.Cancel();
                            result.Passed = false;
                            result.Actual = "Timeout";
                            result.Error = $"Test cancelled/timed out after {_timeoutMs}ms";
                            try { EventLoopCoordinator.ResetInstance(); } catch { }
                            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                            GC.WaitForPendingFinalizers();
                            return result;
                        }
                        
                        // IMPORTANT: FenRuntime.ExecuteSimple returns FenValue.FromError on failure, NOT an exception.
                        // We must check if resultValue.Type is Error.
                        // We access the raw interface or rely on FenValue struct if possible.
                        // Since runtime returns IValue, we cast to FenBrowser.FenEngine.Core.FenValue if needed or use IValue.Type property
                        
                        bool isError = false;
                        string errorMsg = "";

                        // Check for Error/Throw value types only Ã¢â‚¬â€ no string-based heuristics
                        if (resultValue != null && (resultValue.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error || resultValue.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw))
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
                        catch (Exception ex) { Console.WriteLine($"[Test262Runner] Failed reading __VERIFICATION_RESULT__: {ex.Message}"); }

                        if (!isError && metadata.IsAsync && asyncSignal != null)
                        {
                            var asyncDeadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
                            while (!asyncSignal.Task.IsCompleted && DateTime.UtcNow < asyncDeadline)
                            {
                                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                                await Task.Delay(1);
                            }

                            if (!asyncSignal.Task.IsCompleted)
                            {
                                isError = true;
                                errorMsg = $"Async test did not signal $DONE within {_timeoutMs}ms";
                            }
                            else
                            {
                                var signalValue = await asyncSignal.Task;
                                if (!string.Equals(signalValue, AsyncCompleteSignal, StringComparison.Ordinal))
                                {
                                    isError = true;
                                    errorMsg = signalValue;
                                }
                            }
                        }

                        cts.Cancel(); // stop watchdog

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
                        // Timeout or memory watchdog triggered
                        cts.Cancel(); // stop watchdog if timeout, or stop timeout if memory
                        result.Passed = false;
                        bool memoryKill = completedTask == memoryWatchdog;
                        result.Actual = memoryKill ? "MemoryLimit" : "Timeout";
                        long memAfter = GC.GetTotalMemory(false);
                        result.Error = memoryKill
                            ? $"Test killed: memory grew {(memAfter - memBefore) / 1_000_000}MB (limit 500MB)"
                            : $"Test timed out after {_timeoutMs}ms";
                        // Reset EventLoop to break any spinning microtask/promise chains
                        try { EventLoopCoordinator.ResetInstance(); } catch (Exception ex) { Console.WriteLine($"[Test262Runner] EventLoop reset failed after timeout/memory kill: {ex.Message}"); }
                        // Force aggressive GC to reclaim memory from abandoned closures
                        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                        GC.WaitForPendingFinalizers();
                    }
                }
                catch (Exception ex)
                {
                    if (metadata.Negative)
                    {
                        var matches = ExceptionMatchesNegativeExpectation(ex, metadata);
                        result.Passed = matches;
                        result.Expected = $"Error ({metadata.NegativeType})";
                        result.Actual = $"Error: {ex.Message}";
                        if (!matches)
                        {
                            result.Error = $"Expected {metadata.NegativeType} but got {GetExceptionTypeDisplayName(ex)}: {ex.Message}";
                        }
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

            agentController?.Dispose();

            // CRITICAL: Reset singleton EventLoop between tests to prevent memory accumulation
            try { EventLoopCoordinator.ResetInstance(); } catch (Exception ex) { Console.WriteLine($"[Test262Runner] EventLoop reset failed after test run: {ex.Message}"); }

            // Force GC every 50 tests or if memory is above 500MB
            long mem = GC.GetTotalMemory(false);
            if (mem > 500_000_000)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
            }

            return result;
        }

        private void InstallConsoleBindings(FenRuntime runtime)
        {
            var consoleObj = new FenObject();
            consoleObj.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
            {
                var msg = args.Length > 0 ? args[0].ToString() : string.Empty;
                runtime.OnConsoleMessage?.Invoke(msg);
                return FenValue.Undefined;
            })));

            runtime.SetGlobal("console", FenValue.FromObject(consoleObj));
            runtime.SetGlobal("print", FenValue.FromFunction(new FenFunction("print", (args, thisVal) =>
            {
                var msg = args.Length > 0 ? args[0].ToString() : string.Empty;
                runtime.OnConsoleMessage?.Invoke(msg);
                return FenValue.Undefined;
            })));
        }

        private FenObject CreateHost262(FenRuntime runtime, Test262AgentController agentController = null, Test262AgentWorker agentWorker = null)
        {
            static bool TryReadAgentCounter(FenValue typedArrayValue, int index, out FenValue current)
            {
                current = FenValue.Undefined;
                if (!typedArrayValue.IsObject)
                {
                    return false;
                }

                if (typedArrayValue.AsObject() is JsInt32Array int32Array)
                {
                    if (index < 0 || index >= int32Array.Length)
                    {
                        return false;
                    }

                    current = FenValue.FromNumber(int32Array.GetIndex(index));
                    return true;
                }

                if (typedArrayValue.AsObject() is JsBigInt64Array bigInt64Array)
                {
                    if (index < 0 || index >= bigInt64Array.Length)
                    {
                        return false;
                    }

                    current = bigInt64Array.GetBigIntIndex(index);
                    return true;
                }

                return false;
            }

            static bool AgentCounterEquals(FenValue current, FenValue expected)
            {
                if (current.IsBigInt || expected.IsBigInt)
                {
                    return current.IsBigInt &&
                           expected.IsBigInt &&
                           current.AsBigInt().Value == expected.AsBigInt().Value;
                }

                return current.ToNumber().Equals(expected.ToNumber());
            }

            FenValue CreateResolvedAgentPromise(FenValue value)
            {
                return FenValue.FromObject(JsPromise.Resolve(value, runtime.Context));
            }

            FenValue CreatePendingAgentPromise(Action<FenFunction, FenFunction> beginAsync)
            {
                FenFunction resolve = null;
                FenFunction reject = null;
                var promise = new JsPromise(
                    FenValue.FromFunction(new FenFunction("executor", (promiseArgs, promiseThis) =>
                    {
                        resolve = promiseArgs.Length > 0 && promiseArgs[0].IsFunction ? promiseArgs[0].AsFunction() : null;
                        reject = promiseArgs.Length > 1 && promiseArgs[1].IsFunction ? promiseArgs[1].AsFunction() : null;
                        return FenValue.Undefined;
                    })),
                    runtime.Context);

                beginAsync(resolve, reject);
                return FenValue.FromObject(promise);
            }

            void ResolveAgentPromise(FenFunction resolve, FenValue value)
            {
                if (resolve == null)
                {
                    return;
                }

                runtime.RunWithRealmActivation(() =>
                {
                    resolve.Invoke(new[] { value }, runtime.Context, FenValue.Undefined);
                    EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                });
            }

            void RejectAgentPromise(FenFunction reject, string message)
            {
                if (reject == null)
                {
                    return;
                }

                runtime.RunWithRealmActivation(() =>
                {
                    reject.Invoke(new[] { FenValue.FromString(message) }, runtime.Context, FenValue.Undefined);
                    EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                });
            }

            var host262 = new FenObject();
            host262.Set("IsHTMLDDA", FenValue.FromObject(new Test262HtmlDdaObject()));
            host262.Set("detachArrayBuffer", FenValue.FromFunction(new FenFunction("detachArrayBuffer", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject || args[0].AsObject() is not FenBrowser.FenEngine.Core.Types.JsArrayBuffer jsArrayBuffer)
                {
                    throw new FenTypeError("TypeError: detachArrayBuffer expects an ArrayBuffer");
                }

                var transferFn = jsArrayBuffer.Get("transfer");
                if (!transferFn.IsFunction)
                {
                    throw new InvalidOperationException("TypeError: detachArrayBuffer host hook unavailable");
                }

                transferFn.AsFunction().Invoke(Array.Empty<FenValue>(), runtime.Context, FenValue.FromObject(jsArrayBuffer));
                return FenValue.Undefined;
            })));
            host262.Set("evalScript", FenValue.FromFunction(new FenFunction("evalScript", (args, thisVal) =>
            {
                return EvaluateScriptOrThrow(runtime, args, "test262-host-eval.js");
            })));
            host262.Set("createRealm", FenValue.FromFunction(new FenFunction("createRealm", (args, thisVal) =>
            {
                var childRuntime = new FenRuntime();
                ConfigureChildRealm(childRuntime, runtime.OnConsoleMessage, agentController);

                var realmObject = new FenObject();
                realmObject.Set("global", FenValue.FromObject(new Test262GlobalProxyObject(childRuntime)));
                realmObject.Set("evalScript", FenValue.FromFunction(new FenFunction("evalScript", (innerArgs, innerThis) =>
                {
                    return EvaluateScriptOrThrow(childRuntime, innerArgs, "test262-created-realm-eval.js");
                })));

                return FenValue.FromObject(realmObject);
            })));
            host262.Set("gc", FenValue.FromFunction(new FenFunction("gc", (args, thisVal) =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                return FenValue.Undefined;
            })));

            if (agentController != null)
            {
                var agent = new FenObject();
                var timeouts = new FenObject();
                timeouts.Set("yield", FenValue.FromNumber(100));
                timeouts.Set("small", FenValue.FromNumber(200));
                timeouts.Set("long", FenValue.FromNumber(1000));
                timeouts.Set("huge", FenValue.FromNumber(10000));
                agent.Set("timeouts", FenValue.FromObject(timeouts));

                agent.Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
                {
                    if (args.Length == 0)
                    {
                        throw new FenTypeError("TypeError: $262.agent.start requires source text");
                    }

                    agentController.StartWorker(args[0].ToString(), runtime.OnConsoleMessage);
                    return FenValue.Undefined;
                })));

                agent.Set("broadcast", FenValue.FromFunction(new FenFunction("broadcast", (args, thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsObject || args[0].AsObject() is not JsArrayBuffer sharedBuffer)
                    {
                        throw new FenTypeError("TypeError: $262.agent.broadcast expects a SharedArrayBuffer");
                    }

                    agentController.Broadcast(sharedBuffer);
                    return FenValue.Undefined;
                })));

                agent.Set("getReport", FenValue.FromFunction(new FenFunction("getReport", (args, thisVal) =>
                {
                    var report = agentController.TryGetReport();
                    return report == null ? FenValue.Null : FenValue.FromString(report);
                })));

                agent.Set("sleep", FenValue.FromFunction(new FenFunction("sleep", (args, thisVal) =>
                {
                    int delay = args.Length > 0 ? Math.Max(0, (int)args[0].ToNumber()) : 0;
                    Thread.Sleep(delay);
                    return FenValue.Undefined;
                })));

                agent.Set("tryYield", FenValue.FromFunction(new FenFunction("tryYield", (args, thisVal) =>
                {
                    Thread.Sleep(100);
                    return FenValue.Undefined;
                })));

                agent.Set("trySleep", FenValue.FromFunction(new FenFunction("trySleep", (args, thisVal) =>
                {
                    int delay = args.Length > 0 ? Math.Max(0, (int)args[0].ToNumber()) : 0;
                    Thread.Sleep(delay);
                    return FenValue.Undefined;
                })));

                agent.Set("monotonicNow", FenValue.FromFunction(new FenFunction("monotonicNow", (args, thisVal) =>
                {
                    double milliseconds = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                    return FenValue.FromNumber(milliseconds);
                })));

                agent.Set("waitUntil", FenValue.FromFunction(new FenFunction("waitUntil", (args, thisVal) =>
                {
                    if (args.Length < 3)
                    {
                        throw new FenTypeError("TypeError: $262.agent.waitUntil requires typedArray, index, and expected");
                    }

                    int index = (int)args[1].ToNumber();
                    var expected = args[2];
                    var spin = new SpinWait();
                    while (true)
                    {
                        if (!TryReadAgentCounter(args[0], index, out var current))
                        {
                            throw new FenTypeError("TypeError: $262.agent.waitUntil expects an Int32Array or BigInt64Array");
                        }

                        if (AgentCounterEquals(current, expected))
                        {
                            return current;
                        }

                        if (spin.Count > 100)
                        {
                            Thread.Sleep(1);
                        }
                        else
                        {
                            spin.SpinOnce();
                        }
                    }
                })));

                agent.Set("safeBroadcast", FenValue.FromFunction(new FenFunction("safeBroadcast", (args, thisVal) =>
                {
                    if (args.Length == 0 || !args[0].IsObject ||
                        args[0].AsObject() is not JsTypedArray typedArray ||
                        typedArray.Buffer == null ||
                        !typedArray.Buffer.IsShared ||
                        (typedArray is not JsInt32Array && typedArray is not JsBigInt64Array))
                    {
                        throw new FenTypeError("TypeError: $262.agent.safeBroadcast expects a shared Int32Array or BigInt64Array");
                    }

                    agentController.Broadcast(typedArray.Buffer);
                    return FenValue.Undefined;
                })));

                agent.Set("safeBroadcastAsync", FenValue.FromFunction(new FenFunction("safeBroadcastAsync", (args, thisVal) =>
                {
                    if (args.Length < 3 || !args[0].IsObject ||
                        args[0].AsObject() is not JsTypedArray typedArray ||
                        typedArray.Buffer == null ||
                        !typedArray.Buffer.IsShared ||
                        (typedArray is not JsInt32Array && typedArray is not JsBigInt64Array))
                    {
                        throw new FenTypeError("TypeError: $262.agent.safeBroadcastAsync expects a shared Int32Array or BigInt64Array plus index and expected value");
                    }

                    int index = (int)args[1].ToNumber();
                    var expected = args[2];
                    agentController.Broadcast(typedArray.Buffer);

                    return CreatePendingAgentPromise((resolve, reject) =>
                    {
                        _ = RunBackgroundAsync(async () =>
                        {
                            try
                            {
                                var spin = new SpinWait();
                                while (true)
                                {
                                    if (!TryReadAgentCounter(args[0], index, out var current))
                                    {
                                        throw new FenTypeError("TypeError: $262.agent.safeBroadcastAsync expects an Int32Array or BigInt64Array");
                                    }

                                    if (AgentCounterEquals(current, expected))
                                    {
                                        Thread.Sleep(100);
                                        ResolveAgentPromise(resolve, current);
                                        return;
                                    }

                                    if (spin.Count > 100)
                                    {
                                        await Task.Delay(1).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        spin.SpinOnce();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                RejectAgentPromise(reject, ex.Message);
                            }
                        });
                    });
                })));

                agent.Set("receiveBroadcast", FenValue.FromFunction(new FenFunction("receiveBroadcast", (args, thisVal) =>
                {
                    if (agentWorker == null)
                    {
                        throw new FenTypeError("TypeError: $262.agent.receiveBroadcast is only available inside agent workers");
                    }

                    if (args.Length == 0 || !args[0].IsFunction)
                    {
                        throw new FenTypeError("TypeError: $262.agent.receiveBroadcast requires a callback");
                    }

                    agentWorker.RegisterReceiveBroadcast(args[0].AsFunction());
                    return FenValue.Undefined;
                })));

                agent.Set("report", FenValue.FromFunction(new FenFunction("report", (args, thisVal) =>
                {
                    if (agentWorker == null)
                    {
                        throw new FenTypeError("TypeError: $262.agent.report is only available inside agent workers");
                    }

                    agentWorker.Report(args.Length > 0 ? args[0] : FenValue.Undefined);
                    return FenValue.Undefined;
                })));

                agent.Set("leaving", FenValue.FromFunction(new FenFunction("leaving", (args, thisVal) =>
                {
                    agentWorker?.Leave();
                    return FenValue.Undefined;
                })));

                agent.Set("getReportAsync", FenValue.FromFunction(new FenFunction("getReportAsync", (args, thisVal) =>
                {
                    var report = agentController.TryGetReport();
                    if (report != null)
                    {
                        return CreateResolvedAgentPromise(FenValue.FromString(report));
                    }

                    return CreatePendingAgentPromise((resolve, reject) =>
                    {
                        _ = RunBackgroundAsync(async () =>
                        {
                            try
                            {
                                while (true)
                                {
                                    var next = agentController.TryGetReport();
                                    if (next != null)
                                    {
                                        ResolveAgentPromise(resolve, FenValue.FromString(next));
                                        return;
                                    }

                                    await Task.Delay(1).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                RejectAgentPromise(reject, ex.Message);
                            }
                        });
                    });
                })));

                host262.Set("agent", FenValue.FromObject(agent));
            }

            return host262;
        }

        private void ConfigureChildRealm(FenRuntime runtime, Action<string> consoleSink, Test262AgentController agentController = null)
        {
            if (runtime.Context != null)
            {
                runtime.Context.Permissions.Grant(JsPermissions.Eval);
            }

            runtime.OnConsoleMessage = consoleSink;
            InstallConsoleBindings(runtime);
            runtime.SetGlobal("$262", FenValue.FromObject(CreateHost262(runtime, agentController)));
        }

        private static FenValue GetRealmGlobal(FenRuntime runtime)
        {
            if (runtime.GetGlobal("globalThis") is FenValue globalThis && globalThis.IsObject)
            {
                return globalThis;
            }

            if (runtime.GetGlobal("window") is FenValue window && window.IsObject)
            {
                return window;
            }

            return FenValue.FromObject(new FenObject());
        }

        private static FenValue EvaluateScriptOrThrow(FenRuntime runtime, FenValue[] args, string sourceName)
        {
            string script = args.Length > 0 ? args[0].AsString() : string.Empty;
            var result = runtime.ExecuteSimple(script, sourceName, allowReturn: false);
            if (result is not FenValue fenResult)
            {
                return FenValue.Undefined;
            }

            if (fenResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
            {
                throw CreateEvaluationException(fenResult.AsError());
            }

            if (fenResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw)
            {
                throw CreateEvaluationException(fenResult.GetThrownValue());
            }

            return fenResult;
        }

        private static Exception CreateEvaluationException(string error)
        {
            string message = NormalizeEvaluationError(error);

            if (message.StartsWith("SyntaxError:", StringComparison.Ordinal))
            {
                return new FenSyntaxError(message);
            }

            if (message.StartsWith("TypeError:", StringComparison.Ordinal))
            {
                return new FenTypeError(message);
            }

            if (message.StartsWith("ReferenceError:", StringComparison.Ordinal))
            {
                return new FenReferenceError(message);
            }

            if (message.StartsWith("RangeError:", StringComparison.Ordinal))
            {
                return new FenRangeError(message);
            }

            if (message.Contains("SyntaxError", StringComparison.Ordinal))
            {
                return new FenSyntaxError(message);
            }

            if (message.Contains("TypeError", StringComparison.Ordinal))
            {
                return new FenTypeError(message);
            }

            if (message.Contains("ReferenceError", StringComparison.Ordinal))
            {
                return new FenReferenceError(message);
            }

            return new InvalidOperationException(message);
        }

        private static Exception CreateEvaluationException(FenValue thrownValue)
        {
            string message = DescribeThrownValue(thrownValue);
            return CreateEvaluationException(message);
        }

        private static string DescribeThrownValue(FenValue thrownValue)
        {
            if (thrownValue.IsObject || thrownValue.IsFunction)
            {
                var thrownObject = thrownValue.AsObject();
                var nameValue = thrownObject?.Get("name");
                var messageValue = thrownObject?.Get("message");
                string errorName = nameValue.HasValue && nameValue.Value.IsString
                    ? nameValue.Value.AsString()
                    : null;
                string errorMessage = messageValue.HasValue && messageValue.Value.IsString
                    ? messageValue.Value.AsString()
                    : null;

                if (!string.IsNullOrWhiteSpace(errorName) && !string.IsNullOrWhiteSpace(errorMessage))
                {
                    return $"{errorName}: {errorMessage}";
                }

                if (!string.IsNullOrWhiteSpace(errorName))
                {
                    return errorName;
                }
            }

            return thrownValue.ToString();
        }

        private static string NormalizeEvaluationError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "Error: Script evaluation failed";
            }

            const string debugPrefix = "[[DEBUG_TRACE]] ";
            if (error.StartsWith(debugPrefix, StringComparison.Ordinal))
            {
                var tracePayload = error.Substring(debugPrefix.Length);
                var firstLine = tracePayload.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    if (firstLine.StartsWith("JsUncaughtException:", StringComparison.Ordinal))
                    {
                        return firstLine.Substring("JsUncaughtException:".Length).Trim();
                    }

                    return firstLine.Trim();
                }
            }

            return error.Trim();
        }

        private sealed class Test262HtmlDdaObject : FenObject, FenBrowser.FenEngine.Core.Interfaces.IHtmlDdaObject
        {
            public Test262HtmlDdaObject()
            {
                InternalClass = "HTMLDDA";
            }
        }

        private sealed class Test262GlobalProxyObject : FenObject
        {
            private readonly FenRuntime _runtime;

            public Test262GlobalProxyObject(FenRuntime runtime)
            {
                _runtime = runtime;
                InternalClass = "Window";
            }

            public override FenValue Get(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (_runtime.GetGlobal(key) is FenValue globalValue && !globalValue.IsUndefined)
                {
                    return globalValue;
                }

                return base.Get(key, context);
            }

            public override FenValue GetWithReceiver(string key, FenValue receiver, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (_runtime.GetGlobal(key) is FenValue globalValue && !globalValue.IsUndefined)
                {
                    return globalValue;
                }

                return base.GetWithReceiver(key, receiver, context);
            }

            public override void Set(string key, FenValue value, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                _runtime.SetGlobal(key, value);
                base.Set(key, value, context);
            }

            public override void SetWithReceiver(string key, FenValue value, FenValue receiver, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                _runtime.SetGlobal(key, value);
                base.SetWithReceiver(key, value, receiver, context);
            }

            public override bool Has(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (_runtime.GlobalEnv.HasBinding(key))
                {
                    return true;
                }

                return base.Has(key, context);
            }

            public override bool Delete(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                bool deleted = base.Delete(key, context);
                if (_runtime.GlobalEnv.HasLocalBinding(key))
                {
                    _runtime.GlobalEnv.Set(key, FenValue.Undefined);
                }

                return deleted;
            }

            public override IEnumerable<string> Keys(FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                var keys = new HashSet<string>(base.Keys(context), StringComparer.Ordinal);
                foreach (var kvp in _runtime.GlobalEnv.InspectVariables())
                {
                    keys.Add(kvp.Key);
                }

                return keys;
            }

            public override PropertyDescriptor? GetOwnPropertyDescriptor(string key)
            {
                var baseDescriptor = base.GetOwnPropertyDescriptor(key);
                if (baseDescriptor.HasValue)
                {
                    return baseDescriptor;
                }

                if (_runtime.GetGlobal(key) is FenValue globalValue && !globalValue.IsUndefined)
                {
                    return new PropertyDescriptor
                    {
                        Value = globalValue,
                        Writable = true,
                        Enumerable = true,
                        Configurable = true
                    };
                }

                return null;
            }
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
            foreach (var feature in ParseYamlList(yaml, "features"))
            {
                if (!string.IsNullOrEmpty(feature))
                {
                    meta.Features.Add(feature);
                }
            }
            
            // Parse includes
            foreach (var include in ParseYamlList(yaml, "includes"))
            {
                if (!string.IsNullOrEmpty(include))
                {
                    meta.Includes.Add(include);
                }
            }

            // Parse flags
            foreach (var flag in ParseYamlList(yaml, "flags"))
            {
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
                else if (string.Equals(flag, "raw", StringComparison.Ordinal))
                {
                    meta.IsRaw = true;
                }
            }
            
            // Check for async
            if (!meta.IsAsync)
            {
                meta.IsAsync = content.Contains("$DONE");
            }
            
            return meta;
        }

        private static List<string> ParseYamlList(string yaml, string key)
        {
            var values = new List<string>();

            var inlineMatch = Regex.Match(
                yaml,
                $@"(?m)^\s*{Regex.Escape(key)}\s*:\s*\[(?<items>.*?)\]\s*$",
                RegexOptions.Singleline);
            if (inlineMatch.Success)
            {
                foreach (var token in inlineMatch.Groups["items"].Value.Split(','))
                {
                    var item = NormalizeYamlScalar(token);
                    if (!string.IsNullOrEmpty(item))
                    {
                        values.Add(item);
                    }
                }
            }

            var blockMatch = Regex.Match(
                yaml,
                $@"(?ms)^\s*{Regex.Escape(key)}\s*:\s*\r?\n(?<body>(?:\s*-\s*.*(?:\r?\n|$))*)");
            if (blockMatch.Success)
            {
                var lines = blockMatch.Groups["body"].Value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var listItemMatch = Regex.Match(line, @"^\s*-\s*(?<value>.+?)\s*$");
                    if (!listItemMatch.Success)
                    {
                        continue;
                    }

                    var item = NormalizeYamlScalar(listItemMatch.Groups["value"].Value);
                    if (!string.IsNullOrEmpty(item))
                    {
                        values.Add(item);
                    }
                }
            }

            return values.Distinct(StringComparer.Ordinal).ToList();
        }

        private static string NormalizeYamlScalar(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var noComment = raw;
            var commentIdx = noComment.IndexOf('#');
            if (commentIdx >= 0)
            {
                noComment = noComment.Substring(0, commentIdx);
            }

            return noComment.Trim().Trim('"', '\'');
        }

        private static bool ExceptionMatchesNegativeExpectation(Exception exception, TestMetadata metadata)
        {
            if (exception == null || metadata == null || !metadata.Negative)
            {
                return false;
            }

            if (string.Equals(metadata.NegativePhase, "parse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(metadata.NegativeType))
            {
                return true;
            }

            var expected = metadata.NegativeType.Trim();
            var actualType = GetExceptionTypeDisplayName(exception);
            if (actualType.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var message = exception.Message ?? string.Empty;
            if (message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (exception.InnerException != null)
            {
                return ExceptionMatchesNegativeExpectation(exception.InnerException, metadata);
            }

            return false;
        }

        private static string GetExceptionTypeDisplayName(Exception exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var typeName = exception.GetType().Name;
            if (typeName.StartsWith("Fen", StringComparison.Ordinal) &&
                typeName.EndsWith("Error", StringComparison.Ordinal) &&
                typeName.Length > "Fen".Length + "Error".Length)
            {
                return typeName.Substring("Fen".Length);
            }

            return typeName;
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
                .Where(IsDiscoverableTestFile)
                .OrderBy(f => f)
                .ToList();
        }

        private static bool IsDiscoverableTestFile(string testFile)
        {
            var fileName = Path.GetFileName(testFile);
            if (fileName.StartsWith("_", StringComparison.Ordinal) ||
                fileName.Contains("_FIXTURE", StringComparison.Ordinal))
            {
                return false;
            }

            if (fileName.StartsWith("tmp-debug-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("debug_", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("custom-test", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedPath = testFile.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return !normalizedPath.Contains(
                $"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}local-host{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
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

