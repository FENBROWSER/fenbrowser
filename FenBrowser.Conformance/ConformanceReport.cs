// =============================================================================
// ConformanceReport.cs
// Aggregated conformance report generator
//
// PURPOSE: Generate a comprehensive conformance report combining results
//          from all test suites (Test262, WPT, Acid, html5lib) into a single
//          markdown dashboard with per-suite pass rates and overall score.
// =============================================================================

using System.Text;

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
    public void AddResult(string suiteName, string category, int total, int passed, int failed, TimeSpan duration, string? notes = null)
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
            sb.AppendLine($"| {s.Name} | {s.Category} | {s.Total:N0} | {s.Passed:N0} | {s.Failed:N0} | {s.PassRate:F1}% | {s.Duration.TotalSeconds:F1}s |");
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
            // In a full implementation, parse the previous report and compute deltas
            sb.AppendLine("_(Baseline comparison will be implemented in a future update)_");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"_Report generated by FenBrowser.Conformance on {now:yyyy-MM-dd HH:mm:ss}_");

        return sb.ToString();
    }

    /// <summary>
    /// Save the report to a file.
    /// </summary>
    public void SaveReport(string outputPath, string? baselinePath = null)
    {
        var markdown = GenerateMarkdown(baselinePath);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, markdown);
    }
}
