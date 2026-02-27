// =============================================================================
// ResultsExporter.cs
// Multi-format test results exporter for Test262
//
// PURPOSE: Export test results in Markdown, JSON, or TAP format
//          for CI integration and human-readable reporting.
// =============================================================================

using System.Text;
using System.Text.Json;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.Test262;

/// <summary>
/// Exports Test262 results in multiple formats (Markdown, JSON, TAP).
/// </summary>
public static class ResultsExporter
{
    /// <summary>
    /// Export results in the configured format.
    /// </summary>
    public static string Export(
        IReadOnlyList<Test262Runner.TestResult> results,
        OutputFormat format,
        int? chunkNumber = null,
        TimeSpan? totalDuration = null)
    {
        return format switch
        {
            OutputFormat.Markdown => ExportMarkdown(results, chunkNumber, totalDuration),
            OutputFormat.Json => ExportJson(results, chunkNumber, totalDuration),
            OutputFormat.Tap => ExportTap(results),
            _ => ExportMarkdown(results, chunkNumber, totalDuration)
        };
    }

    /// <summary>
    /// Export results as a Markdown table row (matches existing test262_results.md format).
    /// </summary>
    public static string ExportMarkdown(
        IReadOnlyList<Test262Runner.TestResult> results,
        int? chunkNumber = null,
        TimeSpan? totalDuration = null)
    {
        var sb = new StringBuilder();
        int passed = results.Count(r => r.Passed);
        int failed = results.Count(r => !r.Passed);
        int total = results.Count;
        double passRate = total > 0 ? (double)passed / total * 100 : 0;
        long totalMs = totalDuration.HasValue
            ? (long)totalDuration.Value.TotalMilliseconds
            : (long)results.Sum(r => r.Duration.TotalMilliseconds);
        double avgMs = total > 0 ? (double)totalMs / total : 0;

        if (chunkNumber.HasValue)
        {
            // Single chunk row format (matches run_test262.ps1 output)
            int start = (chunkNumber.Value - 1) * 1000 + 1;
            int end = chunkNumber.Value * 1000;
            sb.AppendLine($"| {chunkNumber.Value} | {start}-{end} | {totalMs} | {total} | {passed} | {failed} | {passRate:F1}% | {avgMs:F2} |");
        }
        else
        {
            // Full summary format
            sb.AppendLine("# Test262 Conformance Results");
            sb.AppendLine();
            sb.AppendLine($"**Date**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**Duration**: {totalMs}ms ({totalMs / 1000.0:F1}s)");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Value |");
            sb.AppendLine($"|--------|-------|");
            sb.AppendLine($"| Total  | {total} |");
            sb.AppendLine($"| Passed | {passed} |");
            sb.AppendLine($"| Failed | {failed} |");
            sb.AppendLine($"| Pass Rate | {passRate:F1}% |");
            sb.AppendLine($"| Avg/Test | {avgMs:F2}ms |");
            sb.AppendLine();

            // Top failures
            var failures = results.Where(r => !r.Passed).Take(50).ToList();
            if (failures.Count > 0)
            {
                sb.AppendLine("## Failures (top 50)");
                sb.AppendLine();
                sb.AppendLine("| Test | Error |");
                sb.AppendLine("|------|-------|");
                foreach (var f in failures)
                {
                    var name = Path.GetFileName(f.TestFile);
                    var error = (f.Error ?? "Unknown").Replace("|", "\\|");
                    if (error.Length > 120) error = error[..120] + "...";
                    sb.AppendLine($"| {name} | {error} |");
                }
                if (results.Count(r => !r.Passed) > 50)
                {
                    sb.AppendLine($"| ... | {results.Count(r => !r.Passed) - 50} more failures |");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Export results as structured JSON.
    /// </summary>
    public static string ExportJson(
        IReadOnlyList<Test262Runner.TestResult> results,
        int? chunkNumber = null,
        TimeSpan? totalDuration = null)
    {
        var report = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            chunk = chunkNumber,
            total = results.Count,
            passed = results.Count(r => r.Passed),
            failed = results.Count(r => !r.Passed),
            passRate = results.Count > 0 ? (double)results.Count(r => r.Passed) / results.Count * 100 : 0,
            durationMs = totalDuration.HasValue
                ? (long)totalDuration.Value.TotalMilliseconds
                : (long)results.Sum(r => r.Duration.TotalMilliseconds),
            results = results.Select(r => new
            {
                file = Path.GetFileName(r.TestFile),
                passed = r.Passed,
                expected = r.Expected,
                actual = r.Actual,
                error = r.Error,
                durationMs = (long)r.Duration.TotalMilliseconds,
                features = r.Metadata?.Features ?? new List<string>()
            })
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Export results in TAP (Test Anything Protocol) format for CI.
    /// </summary>
    public static string ExportTap(IReadOnlyList<Test262Runner.TestResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TAP version 13");
        sb.AppendLine($"1..{results.Count}");

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var status = r.Passed ? "ok" : "not ok";
            var name = Path.GetFileName(r.TestFile);
            sb.AppendLine($"{status} {i + 1} - {name}");

            if (!r.Passed && !string.IsNullOrEmpty(r.Error))
            {
                sb.AppendLine("  ---");
                sb.AppendLine($"  message: {r.Error}");
                if (r.Metadata?.Features.Count > 0)
                    sb.AppendLine($"  features: [{string.Join(", ", r.Metadata.Features)}]");
                sb.AppendLine("  ...");
            }
        }

        return sb.ToString();
    }
}
