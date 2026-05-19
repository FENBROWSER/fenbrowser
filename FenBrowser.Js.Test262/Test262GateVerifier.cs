using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Js.Test262;

public static class Test262GateVerifier
{
    private static readonly Regex MilestonePattern = new("^M\\d+(?:\\.\\d+)?$", RegexOptions.CultureInvariant);

    public static GateVerificationResult Verify(string currentResultPath, string? previousResultPath)
    {
        using var currentDoc = JsonDocument.Parse(File.ReadAllText(currentResultPath));
        using var previousDoc = !string.IsNullOrWhiteSpace(previousResultPath) && File.Exists(previousResultPath)
            ? JsonDocument.Parse(File.ReadAllText(previousResultPath))
            : null;

        var current = currentDoc.RootElement;
        var previous = previousDoc?.RootElement;
        var violations = new List<string>();

        var currentSummary = ReadSummary(current);
        Summary? previousSummary = previous is null ? null : ReadSummary(previous.Value);

        if (currentSummary.Crashes > 0)
        {
            violations.Add($"No crashes violated: crashes={currentSummary.Crashes}.");
        }

        var unknownCrashes = CountUnknownCrashes(current);
        if (unknownCrashes > 0)
        {
            violations.Add($"No unknown crashes violated: crashes={unknownCrashes}.");
        }

        if (previousSummary is not null)
        {
            var currentEffectivePasses = currentSummary.Passed + currentSummary.UnexpectedPasses;
            var previousEffectivePasses = previousSummary.Value.Passed + previousSummary.Value.UnexpectedPasses;
            if (currentEffectivePasses < previousEffectivePasses)
            {
                violations.Add($"No regression in pass count violated: previous={previousEffectivePasses}, current={currentEffectivePasses}.");
            }

            if (currentSummary.Crashes > previousSummary.Value.Crashes)
            {
                violations.Add($"No new crash violated: previous={previousSummary.Value.Crashes}, current={currentSummary.Crashes}.");
            }
        }

        var currentFailures = BuildFailureSet(current);
        var previousFailures = previous is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : BuildFailureSet(previous.Value);
        var newFailures = currentFailures.Except(previousFailures, StringComparer.OrdinalIgnoreCase).ToList();
        if (newFailures.Count > 0)
        {
            violations.Add($"No new failure in enabled subset violated: count={newFailures.Count}.");
        }

        var uncategorized = CountUncategorizedFailures(current);
        if (uncategorized > 0)
        {
            violations.Add($"All enabled parser tests must be categorized violated: uncategorizedFailures={uncategorized}.");
        }

        var missingExpectationOwnerOrMilestone = CountExpectationMetadataViolations(current);
        if (missingExpectationOwnerOrMilestone > 0)
        {
            violations.Add($"No expected failure without owner/milestone violated: entries={missingExpectationOwnerOrMilestone}.");
        }

        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow,
            source = new
            {
                current = currentResultPath,
                previous = previousResultPath
            },
            summary = new
            {
                current = currentSummary,
                previous = previousSummary
            },
            newFailures = newFailures.Take(200).ToArray(),
            unknownCrashes,
            uncategorizedFailures = uncategorized,
            expectationMetadataViolations = missingExpectationOwnerOrMilestone,
            passed = violations.Count == 0,
            violations
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new GateVerificationResult(violations.Count == 0, violations, json);
    }

    private static Summary ReadSummary(JsonElement root)
    {
        var summary = root.TryGetProperty("summary", out var s) ? s : default;
        return new Summary(
            ReadInt(summary, "total"),
            ReadInt(summary, "passed"),
            ReadInt(summary, "unsupported"),
            ReadInt(summary, "parserErrors"),
            ReadInt(summary, "crashes"),
            ReadInt(summary, "expectedFailures"),
            ReadInt(summary, "unexpectedPasses"));
    }

    private static int ReadInt(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return v.TryGetInt32(out var n) ? n : 0;
    }

    private static HashSet<string> BuildFailureSet(JsonElement root)
    {
        if (root.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tests.EnumerateArray())
            {
                var status = t.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (string.Equals(status, "Passed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "UnexpectedPass", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = t.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    set.Add(path!.Replace('\\', '/'));
                }
            }

            return set;
        }

        var fallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in failures.EnumerateArray())
            {
                var p = item.TryGetProperty("relativePath", out var rel) && rel.ValueKind == JsonValueKind.String
                    ? rel.GetString()
                    : (item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null);
                if (!string.IsNullOrWhiteSpace(p))
                {
                    fallback.Add(p!.Replace('\\', '/'));
                }
            }
        }

        return fallback;
    }

    private static int CountUncategorizedFailures(JsonElement root)
    {
        if (!root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var t in tests.EnumerateArray())
        {
            var status = t.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            if (string.Equals(status, "Passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "UnexpectedPass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category = t.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String ? cat.GetString() : null;
            if (string.IsNullOrWhiteSpace(category))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountUnknownCrashes(JsonElement root)
    {
        if (!root.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array)
        {
            var summary = root.TryGetProperty("summary", out var s) ? s : default;
            return ReadInt(summary, "crashes");
        }

        var count = 0;
        foreach (var failure in failures.EnumerateArray())
        {
            var classification = failure.TryGetProperty("classification", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            if (!string.Equals(classification, "crash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expected = failure.TryGetProperty("expected", out var ex) && ex.ValueKind == JsonValueKind.True;
            if (!expected)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountExpectationMetadataViolations(JsonElement root)
    {
        if (!root.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var failure in failures.EnumerateArray())
        {
            var expected = failure.TryGetProperty("expected", out var ex) && ex.ValueKind == JsonValueKind.True;
            if (!expected)
            {
                continue;
            }

            var ownerMissing = !failure.TryGetProperty("expectedOwner", out var owner) ||
                               owner.ValueKind != JsonValueKind.String ||
                               string.IsNullOrWhiteSpace(owner.GetString()) ||
                               string.Equals(owner.GetString(), "unknown", StringComparison.OrdinalIgnoreCase);
            var milestoneText = failure.TryGetProperty("expiresAtMilestone", out var milestone) && milestone.ValueKind == JsonValueKind.String
                ? milestone.GetString()
                : null;
            var milestoneMissing = string.IsNullOrWhiteSpace(milestoneText) ||
                                   string.Equals(milestoneText, "unknown", StringComparison.OrdinalIgnoreCase) ||
                                   !MilestonePattern.IsMatch(milestoneText);
            if (ownerMissing || milestoneMissing)
            {
                count++;
            }
        }

        return count;
    }

    public readonly record struct GateVerificationResult(bool Passed, IReadOnlyList<string> Violations, string ReportJson);

    private readonly record struct Summary(
        int Total,
        int Passed,
        int Unsupported,
        int ParserErrors,
        int Crashes,
        int ExpectedFailures,
        int UnexpectedPasses);
}
