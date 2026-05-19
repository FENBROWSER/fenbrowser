using System.Text.Json;

namespace FenBrowser.Js.Test262;

public static class Test262ResultWriter
{
    public static void WriteDryRun(string outputPath, string test262Commit, IReadOnlyList<string> files)
    {
        var payload = new
        {
            engine = "FenJS",
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
        int total,
        int passed,
        int unsupported,
        int parserErrors,
        int crashes,
        int expectedFailures,
        int unexpectedPasses,
        IReadOnlyList<object> failures,
        IReadOnlyList<object> unexpectedPassesList,
        IReadOnlyList<object> tests,
        string? expectationsPath)
    {
        var payload = new
        {
            engine = "FenJS",
            mode = "parser-subset",
            timestampUtc = DateTime.UtcNow,
            test262Commit,
            expectations = expectationsPath,
            summary = new
            {
                total,
                passed,
                unsupported,
                parserErrors,
                crashes,
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
