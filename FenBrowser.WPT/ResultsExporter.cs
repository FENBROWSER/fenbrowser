// =============================================================================
// ResultsExporter.cs
// Multi-format test results exporter for WPT
//
// PURPOSE: Export WPT test results in Markdown, JSON, or TAP format
//          for CI integration and human-readable reporting.
// =============================================================================

using System.Text;
using System.Text.Json;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.WPT;

/// <summary>
/// Exports WPT results in multiple formats (Markdown, JSON, TAP).
/// </summary>
public static class ResultsExporter
{
    /// <summary>
    /// Export results in the configured format.
    /// </summary>
    public static string Export(
        IReadOnlyList<WPTTestRunner.TestExecutionResult> results,
        OutputFormat format,
        string? category = null,
        TimeSpan? totalDuration = null,
        int chunkNumber = 0)
    {
        return format switch
        {
            OutputFormat.Markdown => ExportMarkdown(results, category, totalDuration, chunkNumber),
            OutputFormat.Json => ExportJson(results, category, totalDuration, chunkNumber),
            OutputFormat.Tap => ExportTap(results),
            _ => ExportMarkdown(results, category, totalDuration, chunkNumber)
        };
    }

    /// <summary>
    /// Export results as a Markdown report.
    /// </summary>
    public static string ExportMarkdown(
        IReadOnlyList<WPTTestRunner.TestExecutionResult> results,
        string? category = null,
        TimeSpan? totalDuration = null,
        int chunkNumber = 0)
    {
        var sb = new StringBuilder();

        int totalTests = results.Count;
        int passed = results.Count(r => r.Success);
        int failed = results.Count(r => !r.Success);
        int timedOut = results.Count(r => r.TimedOut);
        int totalAssertions = results.Sum(r => r.TotalCount);
        int passedAssertions = results.Sum(r => r.PassCount);
        int failedAssertions = results.Sum(r => r.FailCount);
        double passRate = totalTests > 0 ? (double)passed / totalTests * 100 : 0;
        long durationMs = totalDuration.HasValue
            ? (long)totalDuration.Value.TotalMilliseconds
            : (long)results.Sum(r => r.Duration.TotalMilliseconds);

        sb.AppendLine("# WPT Conformance Results");
        sb.AppendLine();
        if (chunkNumber > 0)
            sb.AppendLine($"**Chunk**: {chunkNumber}");
        if (!string.IsNullOrEmpty(category))
            sb.AppendLine($"**Category**: {category}");
        sb.AppendLine($"**Date**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Duration**: {durationMs}ms ({durationMs / 1000.0:F1}s)");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Test Files | {totalTests} |");
        sb.AppendLine($"| Passed | {passed} |");
        sb.AppendLine($"| Failed | {failed} |");
        sb.AppendLine($"| Timed Out | {timedOut} |");
        sb.AppendLine($"| Pass Rate | {passRate:F1}% |");
        sb.AppendLine($"| Assertions | {totalAssertions} ({passedAssertions} ✓ / {failedAssertions} ✗) |");
        sb.AppendLine();

        // Failures
        var failures = results.Where(r => !r.Success).Take(50).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("## Failures (top 50)");
            sb.AppendLine();
            sb.AppendLine("| Test | Signal | Pass | Fail | Error |");
            sb.AppendLine("|------|--------|------|------|-------|");
            foreach (var f in failures)
            {
                var name = Path.GetFileName(f.TestFile);
                var error = (f.Error ?? "").Replace("|", "\\|");
                if (error.Length > 80) error = error[..80] + "...";
                sb.AppendLine($"| {name} | {f.CompletionSignal} | {f.PassCount} | {f.FailCount} | {error} |");
            }
            int totalFailed = results.Count(r => !r.Success);
            if (totalFailed > 50)
                sb.AppendLine($"| ... | | | | {totalFailed - 50} more |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Export results as structured JSON.
    /// </summary>
    public static string ExportJson(
        IReadOnlyList<WPTTestRunner.TestExecutionResult> results,
        string? category = null,
        TimeSpan? totalDuration = null,
        int chunkNumber = 0)
    {
        var report = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            chunk = chunkNumber > 0 ? chunkNumber : (int?)null,
            category = category,
            total = results.Count,
            passed = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            timedOut = results.Count(r => r.TimedOut),
            passRate = results.Count > 0 ? (double)results.Count(r => r.Success) / results.Count * 100 : 0,
            totalAssertions = results.Sum(r => r.TotalCount),
            passedAssertions = results.Sum(r => r.PassCount),
            failedAssertions = results.Sum(r => r.FailCount),
            durationMs = totalDuration.HasValue
                ? (long)totalDuration.Value.TotalMilliseconds
                : (long)results.Sum(r => r.Duration.TotalMilliseconds),
            results = results.Select(r => new
            {
                file = Path.GetFileName(r.TestFile),
                success = r.Success,
                harnessCompleted = r.HarnessCompleted,
                timedOut = r.TimedOut,
                completionSignal = r.CompletionSignal,
                passCount = r.PassCount,
                failCount = r.FailCount,
                totalCount = r.TotalCount,
                error = r.Error,
                durationMs = (long)r.Duration.TotalMilliseconds
            })
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Export results in TAP format.
    /// </summary>
    public static string ExportTap(IReadOnlyList<WPTTestRunner.TestExecutionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TAP version 13");
        sb.AppendLine($"1..{results.Count}");

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var status = r.Success ? "ok" : "not ok";
            var name = Path.GetFileName(r.TestFile);
            sb.AppendLine($"{status} {i + 1} - {name} [P:{r.PassCount}/F:{r.FailCount}]");

            if (!r.Success && !string.IsNullOrEmpty(r.Error))
            {
                sb.AppendLine("  ---");
                sb.AppendLine($"  message: {r.Error}");
                sb.AppendLine($"  signal: {r.CompletionSignal}");
                sb.AppendLine("  ...");
            }
        }

        return sb.ToString();
    }
}
