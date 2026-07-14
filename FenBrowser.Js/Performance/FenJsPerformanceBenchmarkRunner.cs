using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Diagnostics;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Performance;

public sealed record FenJsPerformanceScenario(
    string Name,
    string Source,
    double ExpectedNumber,
    int Iterations,
    int WarmupIterations);

public sealed record FenJsPerformanceResult(
    string Name,
    int SourceCharacters,
    int Iterations,
    double AverageParseMs,
    long AverageParseAllocatedBytes,
    double AverageBytecodeGenerationMs,
    long AverageBytecodeAllocatedBytes,
    double AverageExecutionMs,
    long AverageExecutionAllocatedBytes,
    int TopLevelInstructionCount,
    int TotalInstructionCount,
    int InstructionsExecuted,
    int LiveHeapCells,
    int HeapCollections,
    int MinorHeapCollections,
    int ManagedGen0Collections,
    int ManagedGen1Collections,
    int ManagedGen2Collections,
    double ResultNumber);

public sealed record FenJsPerformanceEnvironment(
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

public sealed record FenJsPerformanceReport(
    DateTimeOffset CreatedAtUtc,
    FenJsPerformanceEnvironment Environment,
    IReadOnlyList<FenJsPerformanceResult> Results);

public sealed class FenJsPerformanceBenchmarkRunner
{
    public FenJsPerformanceReport RunDefaultSuite()
        => RunSuite(BuildDefaultSuite());

    public FenJsPerformanceReport RunSuite(IReadOnlyList<FenJsPerformanceScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var results = new List<FenJsPerformanceResult>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            results.Add(RunScenario(scenario));
        }

        return new FenJsPerformanceReport(DateTimeOffset.UtcNow, CaptureEnvironment(), results);
    }

    public FenJsPerformanceResult RunScenario(FenJsPerformanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), "Iterations must be positive.");
        }

        for (var i = 0; i < scenario.WarmupIterations; i++)
        {
            var warmProgram = Parse(scenario);
            var warmFunction = Compile(scenario, warmProgram);
            _ = new BytecodeInterpreter().Execute(warmFunction);
        }

        double parseTicks = 0;
        long parseAllocated = 0;
        ProgramNode? program = null;
        for (var i = 0; i < scenario.Iterations; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            program = Parse(scenario);
            parseTicks += Stopwatch.GetTimestamp() - started;
            parseAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        double compileTicks = 0;
        long compileAllocated = 0;
        BytecodeFunction? function = null;
        for (var i = 0; i < scenario.Iterations; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            function = Compile(scenario, program!);
            compileTicks += Stopwatch.GetTimestamp() - started;
            compileAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);
        for (var i = 0; i < scenario.WarmupIterations; i++)
        {
            AssertExpectedResult(scenario, interpreter.Execute(function!));
        }

        var heapCollectionsBefore = heap.GcCollectionCount;
        var minorHeapCollectionsBefore = heap.MinorCollectionCount;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        double executionTicks = 0;
        long executionAllocated = 0;
        JsValue result = JsValue.Undefined;
        for (var i = 0; i < scenario.Iterations; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            result = interpreter.Execute(function!);
            executionTicks += Stopwatch.GetTimestamp() - started;
            executionAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            AssertExpectedResult(scenario, result);
        }

        var countedFunction = Compile(scenario, Parse(scenario));
        var countingInterpreter = new BytecodeInterpreter
        {
            InstructionBudget = int.MaxValue
        };
        AssertExpectedResult(scenario, countingInterpreter.Execute(countedFunction));

        return new FenJsPerformanceResult(
            scenario.Name,
            scenario.Source.Length,
            scenario.Iterations,
            ToAverageMilliseconds(parseTicks, scenario.Iterations),
            parseAllocated / scenario.Iterations,
            ToAverageMilliseconds(compileTicks, scenario.Iterations),
            compileAllocated / scenario.Iterations,
            ToAverageMilliseconds(executionTicks, scenario.Iterations),
            executionAllocated / scenario.Iterations,
            function!.Instructions.Count,
            CountInstructions(function),
            countingInterpreter.InstructionsExecuted,
            heap.LiveCellCount,
            heap.GcCollectionCount - heapCollectionsBefore,
            heap.MinorCollectionCount - minorHeapCollectionsBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            ReadNumber(result));
    }

    public async Task<string> WriteReportAsync(
        FenJsPerformanceReport report,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        outputPath ??= Path.Combine(
            FindWorkspaceRoot(),
            "Results",
            "performance",
            $"fenjs_perf_benchmark_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    public static IReadOnlyList<FenJsPerformanceScenario> BuildDefaultSuite()
    {
        return
        [
            new(
                "arithmetic-loop",
                "(function(){ let sum=0; for(let i=0;i<50000;i++){ sum=sum+i; } return sum; })();",
                1_249_975_000,
                5,
                2),
            new(
                "property-access",
                "(function(){ let o={x:1,y:2}; let total=0; for(let i=0;i<40000;i++){ total=total+o.x; o.x=o.x+1; total=total+o.y; } return total+o.x; })();",
                800_140_001,
                5,
                2),
            new(
                "prototype-chain",
                "(function(){ let p={value:3}; let o=Object.create(p); let total=0; for(let i=0;i<30000;i++){ total=total+o.value; } return total; })();",
                90_000,
                5,
                2),
            new(
                "function-calls",
                "(function(){ function add(a,b){ return a+b; } let total=0; for(let i=0;i<20000;i++){ total=add(total,1); } return total; })();",
                20_000,
                5,
                2)
        ];
    }

    public static string FormatSummary(FenJsPerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder("FenJS performance benchmarks\n");
        foreach (var result in report.Results)
        {
            builder.AppendLine(
                $"{result.Name}: parse={result.AverageParseMs:0.###}ms/{result.AverageParseAllocatedBytes}B " +
                $"bytecode={result.AverageBytecodeGenerationMs:0.###}ms/{result.AverageBytecodeAllocatedBytes}B " +
                $"execute={result.AverageExecutionMs:0.###}ms/{result.AverageExecutionAllocatedBytes}B " +
                $"instructions={result.InstructionsExecuted} heap={result.LiveHeapCells} " +
                $"heapGc={result.HeapCollections}/{result.MinorHeapCollections}");
        }

        return builder.ToString();
    }

    private static ProgramNode Parse(FenJsPerformanceScenario scenario)
        => JsParser.ParseScript(new SourceText(scenario.Source, $"benchmark:{scenario.Name}"));

    private static BytecodeFunction Compile(FenJsPerformanceScenario scenario, ProgramNode program)
    {
        new AstValidator().Validate(program, new DiagnosticBag());
        return new BytecodeCompiler().CompileProgram(program);
    }

    private static void AssertExpectedResult(FenJsPerformanceScenario scenario, JsValue result)
    {
        if (result.Tag is not (JsValueTag.Number or JsValueTag.Int32) ||
            Math.Abs(ReadNumber(result) - scenario.ExpectedNumber) > double.Epsilon)
        {
            throw new InvalidOperationException(
                $"FenJS benchmark '{scenario.Name}' returned {result.Tag}:{ReadNumber(result)}; expected {scenario.ExpectedNumber}.");
        }
    }

    private static double ReadNumber(JsValue value)
        => value.Tag == JsValueTag.Int32 ? value.AsInt32() : value.AsNumber();

    private static int CountInstructions(BytecodeFunction function)
    {
        var count = function.Instructions.Count;
        foreach (var nested in function.NestedFunctions)
        {
            count += CountInstructions(nested);
        }

        return count;
    }

    private static double ToAverageMilliseconds(double ticks, int iterations)
        => Math.Round(ticks * 1000 / Stopwatch.Frequency / iterations, 3);

    private static FenJsPerformanceEnvironment CaptureEnvironment()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        return new FenJsPerformanceEnvironment(
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
