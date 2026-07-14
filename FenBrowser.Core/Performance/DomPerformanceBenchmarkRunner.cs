using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Performance;

public sealed record DomPerformanceResult(
    string Name,
    int Operations,
    int Iterations,
    double AverageExecutionMs,
    long AverageAllocatedBytes,
    int ManagedGen0Collections,
    int ManagedGen1Collections,
    int ManagedGen2Collections,
    long ObservedCallbacks);

public sealed record DomPerformanceEnvironment(
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string Framework,
    string RuntimeVersion,
    string BuildConfiguration,
    string GitCommit,
    string ProcessorName,
    int ProcessorCount,
    long TotalAvailableMemoryBytes,
    bool ServerGc,
    string GcLatencyMode,
    string TieredCompilation,
    string TieredPgo,
    string ReadyToRun);

public sealed record DomPerformanceReport(
    DateTimeOffset CreatedAtUtc,
    DomPerformanceEnvironment Environment,
    IReadOnlyList<DomPerformanceResult> Results);

public sealed class DomPerformanceBenchmarkRunner
{
    private const int Iterations = 5;
    private const int WarmupIterations = 2;
    private const int MutationCycles = 10_000;
    private const int Dispatches = 20_000;

    public DomPerformanceReport RunDefaultSuite()
    {
        return new DomPerformanceReport(
            DateTimeOffset.UtcNow,
            CaptureEnvironment(),
            [
                RunAppendRemove(),
                RunAncestorFeatureAppendRemove(),
                RunEventDispatch(withListeners: false),
                RunEventDispatch(withListeners: true)
            ]);
    }

    public async Task<string> WriteReportAsync(
        DomPerformanceReport report,
        string outputPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        outputPath ??= Path.Combine(
            FindWorkspaceRoot(),
            "Results",
            "performance",
            $"dom_perf_benchmark_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    public static string FormatSummary(DomPerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder("DOM performance benchmarks\n");
        foreach (var result in report.Results)
        {
            builder.AppendLine(
                $"{result.Name}: operations={result.Operations} " +
                $"execute={result.AverageExecutionMs:0.###}ms/{result.AverageAllocatedBytes}B " +
                $"callbacks={result.ObservedCallbacks}");
        }

        return builder.ToString();
    }

    private static DomPerformanceResult RunAppendRemove()
    {
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = ExecuteAppendRemove(withAncestorFeatures: false, measure: false);
        }

        return Measure(
            "append-remove",
            MutationCycles * 2,
            measure => ExecuteAppendRemove(withAncestorFeatures: false, measure));
    }

    private static DomPerformanceResult RunAncestorFeatureAppendRemove()
    {
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = ExecuteAppendRemove(withAncestorFeatures: true, measure: false);
        }

        return Measure(
            "append-remove-ancestor-features",
            MutationCycles * 2,
            measure => ExecuteAppendRemove(withAncestorFeatures: true, measure));
    }

    private static (long Ticks, long Allocated, long Observed) ExecuteAppendRemove(
        bool withAncestorFeatures,
        bool measure)
    {
        var parent = new Element("div");
        if (withAncestorFeatures)
        {
            parent.SetAttribute("id", "benchmark-root");
            parent.SetAttribute("class", "card active interactive selected");
        }

        var child = new Element("span");
        var allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var started = measure ? Stopwatch.GetTimestamp() : 0;

        for (var i = 0; i < MutationCycles; i++)
        {
            parent.AppendChild(child);
            parent.RemoveChild(child);
        }

        return (
            measure ? Stopwatch.GetTimestamp() - started : 0,
            measure ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore : 0,
            child.ParentNode is null ? MutationCycles * 2L : -1);
    }

    private static DomPerformanceResult RunEventDispatch(bool withListeners)
    {
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = ExecuteEventDispatch(withListeners, measure: false);
        }

        var name = withListeners ? "event-dispatch-listeners" : "event-dispatch-no-listeners";
        return Measure(name, Dispatches, measure => ExecuteEventDispatch(withListeners, measure));
    }

    private static (long Ticks, long Allocated, long Observed) ExecuteEventDispatch(bool withListeners, bool measure)
    {
        var root = new Element("main");
        var current = root;
        for (var depth = 0; depth < 7; depth++)
        {
            var child = new Element("div");
            current.AppendChild(child);
            current = child;
        }

        long callbacks = 0;
        if (withListeners)
        {
            root.AddEventListener("fen-perf", _ => callbacks++, capture: true);
            current.ParentNode!.AddEventListener("fen-perf", _ => callbacks++);
            current.AddEventListener("fen-perf", _ => callbacks++);
        }

        var evt = new Event("fen-perf", new EventInit { Bubbles = true, Composed = true });
        var allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var started = measure ? Stopwatch.GetTimestamp() : 0;

        for (var i = 0; i < Dispatches; i++)
        {
            if (!current.DispatchEvent(evt))
            {
                throw new InvalidOperationException("The benchmark event was unexpectedly canceled.");
            }
        }

        return (
            measure ? Stopwatch.GetTimestamp() - started : 0,
            measure ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore : 0,
            callbacks);
    }

    private static DomPerformanceResult Measure(
        string name,
        int operations,
        Func<bool, (long Ticks, long Allocated, long Observed)> execute)
    {
        long ticks = 0;
        long allocated = 0;
        long observed = 0;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        for (var i = 0; i < Iterations; i++)
        {
            var result = execute(true);
            ticks += result.Ticks;
            allocated += result.Allocated;
            observed = result.Observed;
        }

        return new DomPerformanceResult(
            name,
            operations,
            Iterations,
            Math.Round(ticks * 1000d / Stopwatch.Frequency / Iterations, 3),
            allocated / Iterations,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            observed);
    }

    private static DomPerformanceEnvironment CaptureEnvironment()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        return new DomPerformanceEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString(),
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            TryResolveGitCommit(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            gcInfo.TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            ReadRuntimeSetting("DOTNET_TieredCompilation"),
            ReadRuntimeSetting("DOTNET_TieredPGO"),
            ReadRuntimeSetting("DOTNET_ReadyToRun"));
    }

    private static string ReadRuntimeSetting(string name)
        => Environment.GetEnvironmentVariable(name) ?? "runtime-default";

    private static string FindWorkspaceRoot()
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string TryResolveGitCommit()
    {
        var gitPath = Path.Combine(FindWorkspaceRoot(), ".git");
        try
        {
            var head = File.ReadAllText(Path.Combine(gitPath, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return head;
            }

            var refPath = Path.Combine(gitPath, head[5..].Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "unknown";
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
