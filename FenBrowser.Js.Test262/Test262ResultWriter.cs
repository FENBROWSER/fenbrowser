using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.Js.Test262;

public static class Test262ResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = Test262JsonContext.Default,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void WriteDryRun(string outputPath, string engine, string test262Commit, IReadOnlyList<string> files)
    {
        var payload = new Test262DryRunResult
        {
            Engine = engine,
            TimestampUtc = DateTime.UtcNow,
            Test262Commit = test262Commit,
            Discovered = files.Count,
            Files = files.Take(200).ToArray()
        };

        var json = JsonSerializer.Serialize(payload, Test262JsonContext.Default.Test262DryRunResult);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, json);
    }

    public static void WriteParserSubset(
        string outputPath,
        string test262Commit,
        string engine,
        DateTime startedAtUtc,
        long durationMs,
        int total,
        int passed,
        int unsupported,
        int parserErrors,
        int crashes,
        int timedOut,
        int harnessUnsupported,
        int invalidTestConfiguration,
        int expectedFailures,
        int unexpectedPasses,
        IReadOnlyList<Test262FailureEntry> failures,
        IReadOnlyList<Test262UnexpectedPassEntry> unexpectedPassesList,
        IReadOnlyList<TestEntry> tests,
        string? expectationsPath)
    {
        var payload = new Test262RunResult
        {
            Engine = engine,
            Mode = "parser-subset",
            StartedAtUtc = startedAtUtc,
            DurationMs = durationMs,
            Test262Commit = test262Commit,
            Expectations = expectationsPath,
            Total = total,
            Passed = passed,
            Failed = parserErrors,
            Crashed = crashes,
            TimedOut = timedOut,
            Skipped = 0,
            Unsupported = unsupported,
            ExpectedFailures = expectedFailures,
            UnexpectedPasses = unexpectedPasses,
            HarnessUnsupported = harnessUnsupported,
            InvalidTestConfiguration = invalidTestConfiguration,
            Categories = new Test262CategoryBreakdown
            {
                ParserMissing = unsupported,
                ParserBug = parserErrors,
                HostNotApplicable = harnessUnsupported + invalidTestConfiguration,
                Crash = crashes,
                Timeout = timedOut
            },
            Summary = new Test262RunSummary
            {
                Total = total,
                Passed = passed,
                Unsupported = unsupported,
                ParserErrors = parserErrors,
                Crashes = crashes,
                TimedOut = timedOut,
                HarnessUnsupported = harnessUnsupported,
                InvalidTestConfiguration = invalidTestConfiguration,
                ExpectedFailures = expectedFailures,
                UnexpectedPasses = unexpectedPasses
            },
            Failures = failures as List<Test262FailureEntry> ?? failures.ToList(),
            UnexpectedPassesList = unexpectedPassesList as List<Test262UnexpectedPassEntry> ?? unexpectedPassesList.ToList(),
            Tests = tests as List<TestEntry> ?? tests.ToList()
        };

        var json = JsonSerializer.Serialize(payload, Test262JsonContext.Default.Test262RunResult);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, json);
    }

    public static void WriteRuntimeSubset(
        string outputPath,
        string test262Commit,
        string engine,
        DateTime startedAtUtc,
        long durationMs,
        int total,
        int passed,
        int unsupported,
        int parserErrors,
        int runtimeErrors,
        int crashes,
        int timedOut,
        int harnessUnsupported,
        int invalidTestConfiguration,
        int expectedFailures,
        int unexpectedPasses,
        IReadOnlyList<Test262FailureEntry> failures,
        IReadOnlyList<Test262UnexpectedPassEntry> unexpectedPassesList,
        IReadOnlyList<TestEntry> tests,
        string? expectationsPath)
    {
        var payload = new Test262RunResult
        {
            Engine = engine,
            Mode = "runtime-subset",
            StartedAtUtc = startedAtUtc,
            DurationMs = durationMs,
            Test262Commit = test262Commit,
            Expectations = expectationsPath,
            Total = total,
            Passed = passed,
            Failed = parserErrors + runtimeErrors,
            Crashed = crashes,
            TimedOut = timedOut,
            Skipped = 0,
            Unsupported = unsupported,
            ExpectedFailures = expectedFailures,
            UnexpectedPasses = unexpectedPasses,
            HarnessUnsupported = harnessUnsupported,
            InvalidTestConfiguration = invalidTestConfiguration,
            Categories = new Test262CategoryBreakdown
            {
                ParserMissing = unsupported,
                ParserBug = parserErrors,
                RuntimeMissing = runtimeErrors,
                RuntimeSemanticBug = runtimeErrors,
                HostNotApplicable = harnessUnsupported + invalidTestConfiguration,
                Crash = crashes,
                Timeout = timedOut
            },
            Summary = new Test262RunSummary
            {
                Total = total,
                Passed = passed,
                Unsupported = unsupported,
                ParserErrors = parserErrors,
                RuntimeErrors = runtimeErrors,
                Crashes = crashes,
                TimedOut = timedOut,
                HarnessUnsupported = harnessUnsupported,
                InvalidTestConfiguration = invalidTestConfiguration,
                ExpectedFailures = expectedFailures,
                UnexpectedPasses = unexpectedPasses
            },
            Failures = failures as List<Test262FailureEntry> ?? failures.ToList(),
            UnexpectedPassesList = unexpectedPassesList as List<Test262UnexpectedPassEntry> ?? unexpectedPassesList.ToList(),
            Tests = tests as List<TestEntry> ?? tests.ToList()
        };

        var json = JsonSerializer.Serialize(payload, Test262JsonContext.Default.Test262RunResult);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, json);
    }
}
