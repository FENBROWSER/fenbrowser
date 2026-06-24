using System.Text.Json;

namespace FenBrowser.Js.Test262;

public static class Test262DashboardWriter
{
    public static void WriteDashboard(string currentResultPath, string? previousResultPath, string outputPath)
    {
        using var currentDoc = JsonDocument.Parse(File.ReadAllText(currentResultPath));
        using var previousDoc = !string.IsNullOrWhiteSpace(previousResultPath) && File.Exists(previousResultPath)
            ? JsonDocument.Parse(File.ReadAllText(previousResultPath))
            : null;

        var current = currentDoc.RootElement;
        var previous = previousDoc?.RootElement;

        var tests = ReadTests(current);
        var total = tests.Count > 0 ? tests.Count : ReadInt(current, "summary", "total");
        var passed = tests.Count > 0 ? tests.Count(t => string.Equals(t.Status, "Passed", StringComparison.OrdinalIgnoreCase)) : ReadInt(current, "summary", "passed");
        var failed = tests.Count > 0 ? tests.Count(t => string.Equals(t.Status, "Failed", StringComparison.OrdinalIgnoreCase)) : ReadInt(current, "summary", "parserErrors");
        var crashed = tests.Count > 0 ? tests.Count(t => string.Equals(t.Status, "Crashed", StringComparison.OrdinalIgnoreCase)) : ReadInt(current, "summary", "crashes");
        var timedOut = tests.Count > 0 ? tests.Count(t => string.Equals(t.Status, "TimedOut", StringComparison.OrdinalIgnoreCase)) : 0;
        var unsupported = tests.Count > 0 ? tests.Count(t => string.Equals(t.Status, "UnsupportedFeature", StringComparison.OrdinalIgnoreCase)) : ReadInt(current, "summary", "unsupported");
        var expectedFailures = ReadInt(current, "summary", "expectedFailures");
        var unexpectedPasses = ReadInt(current, "summary", "unexpectedPasses");
        var enabled = total;

        var passRateEnabled = enabled == 0 ? 0d : (double)passed / enabled;
        var supportedEnabled = Math.Max(0, enabled - unsupported);
        var passRateExcludingUnsupported = supportedEnabled == 0 ? 0d : (double)passed / supportedEnabled;

        var failurePaths = tests.Where(t => IsFailingStatus(t.Status)).Select(t => t.Path).ToList();
        if (failurePaths.Count == 0)
        {
            failurePaths = ReadFailurePaths(current);
        }

        var topFailingDirectories = failurePaths
            .Select(NormalizePath)
            .Select(GetDirectoryKey)
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TopFailingDirectory { Directory = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        var crashList = tests
            .Where(t => string.Equals(t.Status, "Crashed", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Path)
            .ToArray();

        if (crashList.Length == 0)
        {
            crashList = ReadCrashPaths(current).ToArray();
        }

        var previousFailures = previous is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : BuildFailureSet(previous.Value);
        var currentFailures = BuildFailureSet(current);
        var newRegressions = currentFailures.Except(previousFailures, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var fixedTests = previousFailures.Except(currentFailures, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        var payload = new Test262DashboardResult
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Source = new Test262DashboardSource
            {
                Current = currentResultPath,
                Previous = previousResultPath
            },
            Metrics = new Test262DashboardMetrics
            {
                TotalTests = total,
                EnabledTests = enabled,
                Passed = passed,
                Failed = failed,
                Crashed = crashed,
                TimedOut = timedOut,
                Unsupported = unsupported,
                ExpectedFailures = expectedFailures,
                UnexpectedPasses = unexpectedPasses,
                PassRateEnabled = passRateEnabled,
                PassRateExcludingUnsupported = passRateExcludingUnsupported,
                PassRateContext = "Rates include enabled tests and explicitly report unsupported and expected failure counts."
            },
            TopFailingDirectories = topFailingDirectories,
            CrashList = crashList,
            NewRegressions = newRegressions,
            FixedTests = fixedTests
        };

        var json = JsonSerializer.Serialize(payload, Test262JsonContext.Default.Test262DashboardResult);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, json);
    }

    private static List<TestRecord> ReadTests(JsonElement root)
    {
        if (!root.TryGetProperty("tests", out var testsNode) || testsNode.ValueKind != JsonValueKind.Array)
        {
            return new List<TestRecord>();
        }

        var tests = new List<TestRecord>();
        foreach (var item in testsNode.EnumerateArray())
        {
            var path = item.TryGetProperty("path", out var pathNode) && pathNode.ValueKind == JsonValueKind.String ? pathNode.GetString() ?? string.Empty : string.Empty;
            var status = item.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String ? statusNode.GetString() ?? string.Empty : string.Empty;
            if (path.Length == 0)
            {
                continue;
            }

            tests.Add(new TestRecord(path, status));
        }

        return tests;
    }

    private static int ReadInt(JsonElement root, string section, string key)
    {
        if (!root.TryGetProperty(section, out var sectionNode) || sectionNode.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!sectionNode.TryGetProperty(key, out var valueNode) || valueNode.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return valueNode.TryGetInt32(out var value) ? value : 0;
    }

    private static List<string> ReadFailurePaths(JsonElement root)
    {
        if (!root.TryGetProperty("failures", out var failuresNode) || failuresNode.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return failuresNode.EnumerateArray()
            .Select(item => item.TryGetProperty("relativePath", out var rel) && rel.ValueKind == JsonValueKind.String
                ? rel.GetString()
                : (item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    private static List<string> ReadCrashPaths(JsonElement root)
    {
        if (!root.TryGetProperty("failures", out var failuresNode) || failuresNode.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var crashes = new List<string>();
        foreach (var item in failuresNode.EnumerateArray())
        {
            if (!item.TryGetProperty("classification", out var cls) || cls.ValueKind != JsonValueKind.String || !string.Equals(cls.GetString(), "crash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("relativePath", out var rel) && rel.ValueKind == JsonValueKind.String)
            {
                crashes.Add(rel.GetString() ?? string.Empty);
            }
            else if (item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
            {
                crashes.Add(path.GetString() ?? string.Empty);
            }
        }

        return crashes.Where(c => c.Length > 0).ToList();
    }

    private static HashSet<string> BuildFailureSet(JsonElement root)
    {
        var tests = ReadTests(root);
        if (tests.Count > 0)
        {
            return tests
                .Where(t => IsFailingStatus(t.Status))
                .Select(t => NormalizePath(t.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return ReadFailurePaths(root)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string GetDirectoryKey(string path)
    {
        var normalized = NormalizePath(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return normalized;
        }

        if (parts.Length >= 3)
        {
            return string.Join('/', parts.Take(3));
        }

        return string.Join('/', parts.Take(parts.Length - 1));
    }

    private static bool IsFailingStatus(string status)
    {
        return !string.Equals(status, "Passed", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(status, "UnexpectedPass", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestRecord(string Path, string Status);
}
