// SpecRef: WPT verdict classification and deterministic conformance gate policy
// CapabilityId: VERIFY-WPT-TRUTH-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System.Text;
using System.Text.Json;

namespace FenBrowser.Conformance;

public sealed class ConformanceGatePolicy
{
    public string Name { get; set; } = string.Empty;
    public string Suite { get; set; } = string.Empty;
    public string CurrentResultPath { get; set; } = string.Empty;
    public string? BaselineResultPath { get; set; }
    public string? ExpectedFailuresPath { get; set; }
    public string? ExpectedCategory { get; set; }
    public int? MinimumTotal { get; set; }
    public double? MinimumPassRate { get; set; }
    public int? MaximumUnexpectedFailures { get; set; }
    public int? MaximumTimeouts { get; set; }
    public bool RequireNoNewRegressions { get; set; }
    public bool RequireZeroNoAssertionFailures { get; set; }
    public List<string> RequiredArtifacts { get; set; } = new();

    public static ConformanceGatePolicy Load(string path)
    {
        var json = File.ReadAllText(path);
        var policy = JsonSerializer.Deserialize<ConformanceGatePolicy>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (policy == null)
        {
            throw new InvalidOperationException($"Failed to deserialize conformance gate policy '{path}'.");
        }

        if (string.IsNullOrWhiteSpace(policy.Name))
        {
            policy.Name = Path.GetFileNameWithoutExtension(path);
        }

        return policy;
    }
}

public sealed class ConformanceGateResult
{
    public string Name { get; set; } = string.Empty;
    public string Suite { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Passed { get; set; }
    public string CurrentResultPath { get; set; } = string.Empty;
    public string? BaselineResultPath { get; set; }
    public int Total { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public int TimedOutCount { get; set; }
    public double PassRate { get; set; }
    public int UnexpectedFailureCount { get; set; }
    public int RegressionCount { get; set; }
    public int ImprovementCount { get; set; }
    public int NoAssertionFailureCount { get; set; }
    public List<string> MissingArtifacts { get; } = new();
    public List<string> Violations { get; } = new();
    public List<string> UnexpectedFailures { get; } = new();
    public List<string> Regressions { get; } = new();
    public List<string> Improvements { get; } = new();
    public List<string> NoAssertionFailures { get; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Name}");
        sb.AppendLine();
        sb.AppendLine($"- `suite`: `{Suite}`");
        sb.AppendLine($"- `category`: `{Category ?? "<none>"}`");
        sb.AppendLine($"- `status`: `{(Passed ? "PASS" : "FAIL")}`");
        sb.AppendLine($"- `artifact`: `{CurrentResultPath}`");
        if (!string.IsNullOrWhiteSpace(BaselineResultPath))
        {
            sb.AppendLine($"- `baseline`: `{BaselineResultPath}`");
        }
        sb.AppendLine($"- `total`: `{Total}`");
        sb.AppendLine($"- `passed`: `{PassedCount}`");
        sb.AppendLine($"- `failed`: `{FailedCount}`");
        sb.AppendLine($"- `timedOut`: `{TimedOutCount}`");
        sb.AppendLine($"- `passRate`: `{PassRate:F1}%`");
        sb.AppendLine($"- `unexpectedFailures`: `{UnexpectedFailureCount}`");
        sb.AppendLine($"- `regressions`: `{RegressionCount}`");
        sb.AppendLine($"- `improvements`: `{ImprovementCount}`");
        sb.AppendLine($"- `noAssertionFailures`: `{NoAssertionFailureCount}`");
        sb.AppendLine();

        AppendList(sb, "Violations", Violations);
        AppendList(sb, "Missing Artifacts", MissingArtifacts);
        AppendList(sb, "Unexpected Failures", UnexpectedFailures);
        AppendList(sb, "Regressions", Regressions);
        AppendList(sb, "Improvements", Improvements);
        AppendList(sb, "No-Assertion Failures", NoAssertionFailures);

        return sb.ToString();
    }

    public string ToJson()
    {
        var payload = new
        {
            name = Name,
            suite = Suite,
            category = Category,
            passed = Passed,
            currentResultPath = CurrentResultPath,
            baselineResultPath = BaselineResultPath,
            total = Total,
            passedCount = PassedCount,
            failedCount = FailedCount,
            timedOutCount = TimedOutCount,
            passRate = PassRate,
            unexpectedFailureCount = UnexpectedFailureCount,
            regressionCount = RegressionCount,
            improvementCount = ImprovementCount,
            noAssertionFailureCount = NoAssertionFailureCount,
            missingArtifacts = MissingArtifacts,
            violations = Violations,
            unexpectedFailures = UnexpectedFailures,
            regressions = Regressions,
            improvements = Improvements,
            noAssertionFailures = NoAssertionFailures
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static void AppendList(StringBuilder sb, string heading, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        foreach (var value in values)
        {
            sb.AppendLine($"- {value}");
        }
        sb.AppendLine();
    }
}

public static class ConformanceGateEvaluator
{
    public static ConformanceGateResult Evaluate(string repoRoot, ConformanceGatePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new ArgumentException("Repository root is required.", nameof(repoRoot));
        }

        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var result = new ConformanceGateResult
        {
            Name = policy.Name,
            Suite = policy.Suite,
            CurrentResultPath = ResolvePath(repoRoot, policy.CurrentResultPath),
            BaselineResultPath = string.IsNullOrWhiteSpace(policy.BaselineResultPath)
                ? null
                : ResolvePath(repoRoot, policy.BaselineResultPath)
        };

        foreach (var artifact in policy.RequiredArtifacts ?? Enumerable.Empty<string>())
        {
            var resolved = ResolvePath(repoRoot, artifact);
            if (!File.Exists(resolved))
            {
                result.MissingArtifacts.Add(artifact);
            }
        }

        if (!File.Exists(result.CurrentResultPath))
        {
            result.MissingArtifacts.Add(policy.CurrentResultPath);
        }

        if (result.MissingArtifacts.Count > 0)
        {
            result.Violations.Add("Required result artifacts are missing.");
            result.Passed = false;
            return result;
        }

        var current = LoadRun(result.CurrentResultPath, policy.Suite);
        result.Category = current.Category;
        result.Total = current.Total;
        result.PassedCount = current.Passed;
        result.FailedCount = current.Failed;
        result.TimedOutCount = current.TimedOut;
        result.PassRate = current.PassRate;

        if (!string.IsNullOrWhiteSpace(policy.ExpectedCategory) &&
            !string.Equals(policy.ExpectedCategory, current.Category, StringComparison.OrdinalIgnoreCase))
        {
            result.Violations.Add($"Expected category '{policy.ExpectedCategory}' but artifact category is '{current.Category ?? "<none>"}'.");
        }

        var expectedFailures = LoadExpectedFailures(repoRoot, policy.ExpectedFailuresPath);
        var currentFailures = current.Outcomes.Where(kvp => !kvp.Value).Select(kvp => kvp.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var failure in currentFailures.Where(name => !expectedFailures.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).Take(50))
        {
            result.UnexpectedFailures.Add(failure);
        }
        result.UnexpectedFailureCount = currentFailures.Count(name => !expectedFailures.Contains(name));

        if (policy.MaximumUnexpectedFailures.HasValue &&
            result.UnexpectedFailureCount > policy.MaximumUnexpectedFailures.Value)
        {
            result.Violations.Add(
                $"Unexpected failure budget exceeded: {result.UnexpectedFailureCount} > {policy.MaximumUnexpectedFailures.Value}.");
        }

        if (policy.MinimumTotal.HasValue && result.Total < policy.MinimumTotal.Value)
        {
            result.Violations.Add($"Minimum total tests not met: {result.Total} < {policy.MinimumTotal.Value}.");
        }

        if (policy.MinimumPassRate.HasValue && result.PassRate + 0.0001 < policy.MinimumPassRate.Value)
        {
            result.Violations.Add($"Minimum pass rate not met: {result.PassRate:F1}% < {policy.MinimumPassRate.Value:F1}%.");
        }

        if (policy.MaximumTimeouts.HasValue && result.TimedOutCount > policy.MaximumTimeouts.Value)
        {
            result.Violations.Add($"Timeout budget exceeded: {result.TimedOutCount} > {policy.MaximumTimeouts.Value}.");
        }

        if (policy.RequireZeroNoAssertionFailures && string.Equals(policy.Suite, "wpt", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var failure in current.Errors
                         .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value) &&
                                       kvp.Value!.IndexOf("No assertions executed by testharness.", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Select(kvp => kvp.Key)
                         .OrderBy(name => name, StringComparer.Ordinal)
                         .Take(50))
            {
                result.NoAssertionFailures.Add(failure);
            }

            result.NoAssertionFailureCount = current.Errors.Count(kvp =>
                !string.IsNullOrWhiteSpace(kvp.Value) &&
                kvp.Value!.IndexOf("No assertions executed by testharness.", StringComparison.OrdinalIgnoreCase) >= 0);

            if (result.NoAssertionFailureCount > 0)
            {
                result.Violations.Add($"No-assertion harness failures detected: {result.NoAssertionFailureCount}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.BaselineResultPath))
        {
            if (!File.Exists(result.BaselineResultPath))
            {
                if (policy.RequireNoNewRegressions)
                {
                    result.Violations.Add($"Baseline artifact is missing: {policy.BaselineResultPath}.");
                }
            }
            else
            {
                var baseline = LoadRun(result.BaselineResultPath, policy.Suite);
                foreach (var kvp in baseline.Outcomes)
                {
                    if (!current.Outcomes.TryGetValue(kvp.Key, out var currentOutcome))
                    {
                        continue;
                    }

                    if (kvp.Value && !currentOutcome)
                    {
                        result.RegressionCount++;
                        if (result.Regressions.Count < 50)
                        {
                            result.Regressions.Add(kvp.Key);
                        }
                    }
                    else if (!kvp.Value && currentOutcome)
                    {
                        result.ImprovementCount++;
                        if (result.Improvements.Count < 50)
                        {
                            result.Improvements.Add(kvp.Key);
                        }
                    }
                }

                if (policy.RequireNoNewRegressions && result.RegressionCount > 0)
                {
                    result.Violations.Add($"Regression check failed: {result.RegressionCount} previously passing tests now fail.");
                }
            }
        }
        else if (policy.RequireNoNewRegressions)
        {
            result.Violations.Add("Baseline artifact path is required for regression gating but was not configured.");
        }

        result.Passed = result.Violations.Count == 0;
        return result;
    }

    private static ConformanceRun LoadRun(string path, string suite)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var run = new ConformanceRun
        {
            Category = root.TryGetProperty("category", out var categoryElement) && categoryElement.ValueKind == JsonValueKind.String
                ? categoryElement.GetString()
                : null,
            Total = GetInt(root, "total"),
            Passed = GetInt(root, "passed"),
            Failed = GetInt(root, "failed"),
            TimedOut = GetInt(root, "timedOut"),
            PassRate = GetDouble(root, "passRate")
        };

        if (!root.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
        {
            return run;
        }

        foreach (var item in resultsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var file = fileElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            var passed = string.Equals(suite, "wpt", StringComparison.OrdinalIgnoreCase)
                ? GetBool(item, "success")
                : GetBool(item, "passed");

            run.Outcomes[file] = passed;
            run.Errors[file] = item.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;
        }

        return run;
    }

    private static HashSet<string> LoadExpectedFailures(string repoRoot, string? relativePath)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return set;
        }

        var resolved = ResolvePath(repoRoot, relativePath);
        if (!File.Exists(resolved))
        {
            return set;
        }

        foreach (var rawLine in File.ReadAllLines(resolved))
        {
            var line = rawLine;
            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
            {
                line = line.Substring(0, commentIndex);
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                set.Add(line);
            }
        }

        return set;
    }

    private static string ResolvePath(string repoRoot, string relativeOrAbsolutePath)
    {
        return Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(repoRoot, relativeOrAbsolutePath);
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
               value.GetBoolean();
    }

    private sealed class ConformanceRun
    {
        public string? Category { get; set; }
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int TimedOut { get; set; }
        public double PassRate { get; set; }
        public Dictionary<string, bool> Outcomes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Errors { get; } = new(StringComparer.Ordinal);
    }
}
