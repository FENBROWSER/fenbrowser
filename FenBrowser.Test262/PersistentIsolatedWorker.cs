using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.Test262;

internal static class PersistentIsolatedWorkerHost
{
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(Test262Config config)
    {
        while (true)
        {
            string? line = await Console.In.ReadLineAsync();
            if (line == null)
            {
                return 0;
            }

            PersistentWorkerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<PersistentWorkerRequest>(line, ProtocolJsonOptions);
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = string.Empty,
                    Success = false,
                    Error = $"Invalid worker request JSON: {ex.Message}"
                });
                continue;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Command))
            {
                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = request?.RequestId ?? string.Empty,
                    Success = false,
                    Error = "Worker request was empty."
                });
                continue;
            }

            if (string.Equals(request.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = request.RequestId,
                    Success = true
                });
                return 0;
            }

            if (!string.Equals(request.Command, "runBatch", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = $"Unknown worker command '{request.Command}'."
                });
                continue;
            }

            try
            {
                var testFiles = request.TestFiles ?? new List<string>();
                var runner = new Test262Runner(config.Test262RootPath, config.PerTestTimeoutMs);
                runner.MemoryThresholdBytes = config.MaxMemoryMB * 1_000_000L;
                var results = await runner.RunSpecificTestsAsync(testFiles);

                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = request.RequestId,
                    Success = true,
                    RecycleSuggested = ShouldSuggestRecycle(config),
                    Results = results.Select(result => new PersistentWorkerResult
                    {
                        TestFile = result.TestFile,
                        Passed = result.Passed,
                        Expected = result.Expected,
                        Actual = result.Actual,
                        Error = result.Error,
                        DurationMs = (long)result.Duration.TotalMilliseconds,
                        Features = result.Metadata?.Features ?? new List<string>()
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(new PersistentWorkerResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = ex.ToString()
                });
            }
        }
    }

    private static bool ShouldSuggestRecycle(Test262Config config)
    {
        try
        {
            long workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
            long recycleThresholdBytes = Math.Max(128L, config.EstimatedIsolatedWorkerMemoryMB) * 1_000_000L;
            return workingSetBytes >= recycleThresholdBytes;
        }
        catch
        {
            return false;
        }
    }

    private static Task WriteResponseAsync(PersistentWorkerResponse response)
    {
        string payload = JsonSerializer.Serialize(response, ProtocolJsonOptions);
        return Console.Out.WriteLineAsync(payload);
    }
}

internal sealed class PersistentIsolatedWorkerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Test262Config _config;
    private readonly string _runnerDll;
    private readonly int _workerIndex;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrPump;
    private readonly object _stderrLock = new object();
    private readonly Queue<string> _stderrTail = new Queue<string>();
    private int _batchesServed;

    public PersistentIsolatedWorkerClient(Test262Config config, string runnerDll, int workerIndex)
    {
        _config = config;
        _runnerDll = runnerDll;
        _workerIndex = workerIndex;
    }

    public async Task StartAsync()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        await DisposeProcessAsync();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_runnerDll}\" run_worker --root \"{_config.Test262RootPath}\" --timeout {_config.PerTestTimeoutMs} --max-memory-mb {_config.MaxMemoryMB}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _stdout = process.StandardOutput;
        _stderrPump = PumpStderrAsync(process.StandardError);
        _batchesServed = 0;
    }

    public async Task<PersistentWorkerBatchResult> RunBatchAsync(IReadOnlyList<string> testFiles, TimeSpan timeout)
    {
        await StartAsync();

        string requestId = Guid.NewGuid().ToString("N");
        var request = new PersistentWorkerRequest
        {
            RequestId = requestId,
            Command = "runBatch",
            TestFiles = testFiles.ToList()
        };

        string payload = JsonSerializer.Serialize(request, ProtocolJsonOptions);
        await _stdin!.WriteLineAsync(payload);

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            var readTask = _stdout!.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(remaining));
            if (completed != readTask)
            {
                throw new TimeoutException($"Persistent worker {_workerIndex} timed out waiting for response.");
            }

            string? line = await readTask;
            if (line == null)
            {
                throw new IOException($"Persistent worker {_workerIndex} exited unexpectedly. {GetStderrTail()}");
            }

            PersistentWorkerResponse? response = TryParseResponse(line);
            if (response == null || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!response.Success)
            {
                throw new InvalidOperationException(
                    $"Persistent worker {_workerIndex} reported failure: {response.Error} {GetStderrTail()}");
            }

            _batchesServed++;
            var results = (response.Results ?? new List<PersistentWorkerResult>()).Select(result => new Test262Runner.TestResult
            {
                TestFile = result.TestFile,
                Passed = result.Passed,
                Expected = result.Expected ?? string.Empty,
                Actual = result.Actual ?? string.Empty,
                Error = result.Error ?? string.Empty,
                Duration = TimeSpan.FromMilliseconds(result.DurationMs),
                Metadata = new Test262Runner.TestMetadata
                {
                    Features = result.Features ?? new List<string>()
                }
            }).ToList();

            return new PersistentWorkerBatchResult
            {
                Results = results,
                RecycleSuggested = response.RecycleSuggested
            };
        }

        throw new TimeoutException($"Persistent worker {_workerIndex} timed out without a matching response.");
    }

    public bool ShouldRecycle()
    {
        if (_process == null || _process.HasExited)
        {
            return true;
        }

        if (_batchesServed >= _config.WorkerRecycleBatchCount)
        {
            return true;
        }

        try
        {
            _process.Refresh();
            long recycleThresholdBytes = Math.Max(128L, _config.EstimatedIsolatedWorkerMemoryMB) * 1_000_000L;
            return _process.WorkingSet64 >= recycleThresholdBytes;
        }
        catch
        {
            return true;
        }
    }

    public async Task RecycleAsync()
    {
        await DisposeProcessAsync();
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeProcessAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeProcessAsync()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited && _stdin != null)
            {
                string payload = JsonSerializer.Serialize(new PersistentWorkerRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Command = "shutdown"
                }, ProtocolJsonOptions);

                await _stdin.WriteLineAsync(payload);
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(1000));
            }
        }
        catch
        {
            // Best effort shutdown.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        try
        {
            if (_stderrPump != null)
            {
                await Task.WhenAny(_stderrPump, Task.Delay(500));
            }
        }
        catch
        {
            // Ignore stderr pump teardown failures.
        }

        _stdin?.Dispose();
        _stdout?.Dispose();
        _process.Dispose();
        _stdin = null;
        _stdout = null;
        _stderrPump = null;
        _process = null;
        _batchesServed = 0;
    }

    private async Task PumpStderrAsync(StreamReader stderr)
    {
        try
        {
            while (true)
            {
                string? line = await stderr.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                lock (_stderrLock)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 40)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch
        {
            // Ignore stderr pump errors during worker teardown.
        }
    }

    private string GetStderrTail()
    {
        lock (_stderrLock)
        {
            if (_stderrTail.Count == 0)
            {
                return string.Empty;
            }

            return "stderr: " + string.Join(" | ", _stderrTail);
        }
    }

    private static PersistentWorkerResponse? TryParseResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<PersistentWorkerResponse>(line, ProtocolJsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class PersistentWorkerBatchResult
{
    public IReadOnlyList<Test262Runner.TestResult> Results { get; set; } = Array.Empty<Test262Runner.TestResult>();
    public bool RecycleSuggested { get; set; }
}

internal sealed class PersistentWorkerRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public List<string>? TestFiles { get; set; }
}

internal sealed class PersistentWorkerResponse
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool RecycleSuggested { get; set; }
    public string? Error { get; set; }
    public List<PersistentWorkerResult>? Results { get; set; }
}

internal sealed class PersistentWorkerResult
{
    public string TestFile { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    public List<string>? Features { get; set; }
}
