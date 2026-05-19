using System.Text.Json;

namespace FenBrowser.Js.Test262;

public static class Test262ResultWriter
{
    public static void WriteDryRun(string outputPath, string engine, string test262Commit, IReadOnlyList<string> files)
    {
        var payload = new
        {
            engine,
            mode = "dry-run",
            timestampUtc = DateTime.UtcNow,
            test262Commit,
            discovered = files.Count,
            files = files.Take(200).ToArray()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
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
        int expectedFailures,
        int unexpectedPasses,
        IReadOnlyList<object> failures,
        IReadOnlyList<object> unexpectedPassesList,
        IReadOnlyList<object> tests,
        string? expectationsPath)
    {
        var payload = new
        {
            engine,
            mode = "parser-subset",
            startedAtUtc,
            durationMs,
            test262Commit,
            fenbrowserCommit = "unknown",
            specTarget = "ECMA-262 pinned snapshot",
            expectations = expectationsPath,
            total,
            passed,
            failed = parserErrors,
            crashed = crashes,
            timedOut,
            skipped = 0,
            unsupported,
            expectedFailures,
            unexpectedPasses,
            harnessUnsupported,
            categories = new
            {
                parserMissing = unsupported,
                parserBug = parserErrors,
                earlyErrorBug = 0,
                runtimeMissing = 0,
                runtimeSemanticBug = 0,
                builtinMissing = 0,
                builtinSemanticBug = 0,
                moduleMissing = 0,
                promiseMissing = 0,
                regexpMissing = 0,
                intlMissing = 0,
                proxyMissing = 0,
                typedArrayMissing = 0,
                hostNotApplicable = 0,
                crash = crashes,
                timeout = timedOut
            },
            summary = new
            {
                total,
                passed,
                unsupported,
                parserErrors,
                crashes,
                timedOut,
                harnessUnsupported,
                expectedFailures,
                unexpectedPasses
            },
            failures,
            unexpectedPassesList,
            tests
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, json);
    }
}
