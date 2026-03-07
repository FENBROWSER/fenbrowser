// =============================================================================
// ConformanceReport.cs
// Aggregated conformance report generator
//
// PURPOSE: Generate a comprehensive conformance report combining results
//          from all test suites (Test262, WPT, Acid, html5lib) into a single
//          markdown dashboard with per-suite pass rates and overall score.
// =============================================================================

using System.Text;
using System.Text.Json;

namespace FenBrowser.Conformance;

/// <summary>
/// Aggregates results from multiple conformance test suites into
/// a unified markdown report.
/// </summary>
public sealed class ConformanceReport
{
    /// <summary>
    /// Individual suite result summary.
    /// </summary>
    public class SuiteResult
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public double PassRate => Total > 0 ? (double)Passed / Total * 100 : 0;
        public TimeSpan Duration { get; set; }
        public string? Notes { get; set; }
    }

    private readonly List<SuiteResult> _suiteResults = new();

    /// <summary>
    /// Add a suite result to the report.
    /// </summary>
    public void AddSuiteResult(SuiteResult result)
    {
        _suiteResults.Add(result);
    }

    /// <summary>
    /// Add results from a generic pass/fail count.
    /// </summary>
    public void AddResult(string suiteName, string category, int total, int passed, int failed, TimeSpan duration,
        string? notes = null)
    {
        _suiteResults.Add(new SuiteResult
        {
            Name = suiteName,
            Category = category,
            Total = total,
            Passed = passed,
            Failed = failed,
            Duration = duration,
            Notes = notes
        });
    }

    /// <summary>
    /// Calculate overall conformance score across all suites.
    /// </summary>
    public double OverallPassRate
    {
        get
        {
            int totalTests = _suiteResults.Sum(s => s.Total);
            int totalPassed = _suiteResults.Sum(s => s.Passed);
            return totalTests > 0 ? (double)totalPassed / totalTests * 100 : 0;
        }
    }

    /// <summary>
    /// Generate the full conformance report as Markdown.
    /// </summary>
    public string GenerateMarkdown(string? baselinePath = null)
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;

        sb.AppendLine("# FenBrowser Conformance Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated**: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Engine**: FenBrowser FenEngine");
        sb.AppendLine();

        // Overall Score
        int totalTests = _suiteResults.Sum(s => s.Total);
        int totalPassed = _suiteResults.Sum(s => s.Passed);
        int totalFailed = _suiteResults.Sum(s => s.Failed);
        double overallRate = OverallPassRate;
        var totalDuration = TimeSpan.FromMilliseconds(_suiteResults.Sum(s => s.Duration.TotalMilliseconds));

        sb.AppendLine("## Overall Score");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| **Overall Pass Rate** | **{overallRate:F1}%** |");
        sb.AppendLine($"| Total Tests | {totalTests:N0} |");
        sb.AppendLine($"| Passed | {totalPassed:N0} |");
        sb.AppendLine($"| Failed | {totalFailed:N0} |");
        sb.AppendLine($"| Total Duration | {totalDuration.TotalSeconds:F1}s |");
        sb.AppendLine();

        // Per-Suite Breakdown
        sb.AppendLine("## Suite Breakdown");
        sb.AppendLine();
        sb.AppendLine("| Suite | Category | Total | Passed | Failed | Pass Rate | Duration |");
        sb.AppendLine("|-------|----------|------:|-------:|-------:|----------:|---------:|");

        foreach (var s in _suiteResults)
        {
            sb.AppendLine(
                $"| {s.Name} | {s.Category} | {s.Total:N0} | {s.Passed:N0} | {s.Failed:N0} | {s.PassRate:F1}% | {s.Duration.TotalSeconds:F1}s |");
        }

        sb.AppendLine();

        // Per-Suite Details
        var grouped = _suiteResults.GroupBy(s => s.Name).ToList();
        foreach (var group in grouped)
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();

            int suitePassed = group.Sum(s => s.Passed);
            int suiteTotal = group.Sum(s => s.Total);
            double suiteRate = suiteTotal > 0 ? (double)suitePassed / suiteTotal * 100 : 0;

            sb.AppendLine($"**Overall**: {suitePassed:N0}/{suiteTotal:N0} ({suiteRate:F1}%)");
            sb.AppendLine();

            if (group.Count() > 1)
            {
                sb.AppendLine("| Category | Total | Passed | Pass Rate |");
                sb.AppendLine("|----------|------:|-------:|----------:|");
                foreach (var s in group)
                {
                    sb.AppendLine($"| {s.Category} | {s.Total:N0} | {s.Passed:N0} | {s.PassRate:F1}% |");
                }

                sb.AppendLine();
            }

            foreach (var s in group.Where(s => !string.IsNullOrEmpty(s.Notes)))
            {
                sb.AppendLine($"> {s.Notes}");
            }
        }

        // Baseline Comparison
        if (!string.IsNullOrEmpty(baselinePath) && File.Exists(baselinePath))
        {
            sb.AppendLine("## Baseline Comparison");
            sb.AppendLine();
            sb.AppendLine($"_Compared against: {baselinePath}_");
            sb.AppendLine();
            if (TryLoadBaselineSuites(baselinePath, out var baselineSuites))
            {
                sb.AppendLine("| Suite | Category | Passed Delta | Failed Delta | Pass Rate Delta |");
                sb.AppendLine("|-------|----------|-------------:|-------------:|----------------:|");

                foreach (var current in _suiteResults.OrderBy(r => r.Name).ThenBy(r => r.Category))
                {
                    var key = MakeSuiteKey(current.Name, current.Category);
                    if (!baselineSuites.TryGetValue(key, out var baseline))
                    {
                        sb.AppendLine($"| {current.Name} | {current.Category} | n/a | n/a | n/a |");
                        continue;
                    }

                    var passedDelta = current.Passed - baseline.Passed;
                    var failedDelta = current.Failed - baseline.Failed;
                    var passRateDelta = current.PassRate - baseline.PassRate;
                    sb.AppendLine(
                        $"| {current.Name} | {current.Category} | {FormatSigned(passedDelta)} | {FormatSigned(failedDelta)} | {FormatSigned(passRateDelta, "F1")}% |");
                }
            }
            else
            {
                sb.AppendLine("_(Unable to parse baseline report for structured comparison.)_");
            }
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"_Report generated by FenBrowser.Conformance on {now:yyyy-MM-dd HH:mm:ss}_");

        return sb.ToString();
    }

    /// <summary>
    /// Save the report to a file. Outputs JSON for .json paths, Markdown otherwise.
    /// </summary>
    public void SaveReport(string outputPath, string? baselinePath = null)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = GenerateJson();
            File.WriteAllText(outputPath, json);
        }
        else
        {
            var markdown = GenerateMarkdown(baselinePath);
            File.WriteAllText(outputPath, markdown);
        }
    }

    /// <summary>
    /// Generate a JSON representation of the conformance results.
    /// </summary>
    public string GenerateJson()
    {
        var now = DateTime.Now;
        int totalTests = _suiteResults.Sum(s => s.Total);
        int totalPassed = _suiteResults.Sum(s => s.Passed);
        int totalFailed = _suiteResults.Sum(s => s.Failed);
        var totalDuration = TimeSpan.FromMilliseconds(_suiteResults.Sum(s => s.Duration.TotalMilliseconds));

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"date\": \"{now:yyyy-MM-dd HH:mm:ss}\",");
        sb.AppendLine($"  \"engine\": \"FenBrowser FenEngine\",");
        sb.AppendLine($"  \"overallPassRate\": {OverallPassRate:F1},");
        sb.AppendLine($"  \"totalTests\": {totalTests},");
        sb.AppendLine($"  \"totalPassed\": {totalPassed},");
        sb.AppendLine($"  \"totalFailed\": {totalFailed},");
        sb.AppendLine($"  \"totalDurationMs\": {totalDuration.TotalMilliseconds:F0},");
        sb.AppendLine("  \"suites\": [");

        for (int i = 0; i < _suiteResults.Count; i++)
        {
            var s = _suiteResults[i];
            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{EscapeJson(s.Name)}\",");
            sb.AppendLine($"      \"category\": \"{EscapeJson(s.Category)}\",");
            sb.AppendLine($"      \"total\": {s.Total},");
            sb.AppendLine($"      \"passed\": {s.Passed},");
            sb.AppendLine($"      \"failed\": {s.Failed},");
            sb.AppendLine($"      \"passRate\": {s.PassRate:F1},");
            sb.AppendLine($"      \"durationMs\": {s.Duration.TotalMilliseconds:F0}");
            sb.Append("    }");
            if (i < _suiteResults.Count - 1) sb.Append(",");
            sb.AppendLine();
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string MakeSuiteKey(string name, string category)
    {
        return $"{name}::{category}";
    }

    private static string FormatSigned(int value)
    {
        return value >= 0 ? $"+{value}" : value.ToString();
    }

    private static string FormatSigned(double value, string format)
    {
        return value >= 0 ? $"+{value.ToString(format)}" : value.ToString(format);
    }

    private static bool TryLoadBaselineSuites(string baselinePath, out Dictionary<string, SuiteResult> suites)
    {
        suites = new Dictionary<string, SuiteResult>(StringComparer.Ordinal);
        try
        {
            if (!baselinePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(baselinePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("suites", out var suitesElement) || suitesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in suitesElement.EnumerateArray())
            {
                var suite = new SuiteResult
                {
                    Name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    Category = item.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() ?? string.Empty : string.Empty,
                    Total = item.TryGetProperty("total", out var totalElement) && totalElement.ValueKind == JsonValueKind.Number ? totalElement.GetInt32() : 0,
                    Passed = item.TryGetProperty("passed", out var passedElement) && passedElement.ValueKind == JsonValueKind.Number ? passedElement.GetInt32() : 0,
                    Failed = item.TryGetProperty("failed", out var failedElement) && failedElement.ValueKind == JsonValueKind.Number ? failedElement.GetInt32() : 0,
                    Duration = item.TryGetProperty("durationMs", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromMilliseconds(durationElement.GetDouble())
                        : TimeSpan.Zero
                };

                suites[MakeSuiteKey(suite.Name, suite.Category)] = suite;
            }

            return true;
        }
        catch
        {
            suites.Clear();
            return false;
        }
    }
}
