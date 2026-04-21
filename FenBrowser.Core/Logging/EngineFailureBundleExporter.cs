using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FenBrowser.Core.Logging;

internal static class EngineFailureBundleExporter
{
    public static string CreateBundle(
        IReadOnlyList<LogEntry> entries,
        string testId,
        string url,
        string summary)
    {
        var resultsRoot = Path.Combine(DiagnosticPaths.GetWorkspaceRoot(), "Results");
        Directory.CreateDirectory(resultsRoot);

        var bundleDir = Path.Combine(resultsRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(bundleDir);

        var normalizedEntries = entries ?? Array.Empty<LogEntry>();
        var severityCounts = normalizedEntries
            .GroupBy(e => e.Level.ToString())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var topMessages = normalizedEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Message))
            .GroupBy(e => e.Message)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new Dictionary<string, object>
            {
                ["message"] = g.Key,
                ["count"] = g.Count(),
                ["category"] = g.First().Category.ToString(),
                ["level"] = g.First().Level.ToString()
            })
            .ToList();

        var firstError = normalizedEntries.FirstOrDefault(e => e.Level == LogLevel.Error);
        var firstFatal = normalizedEntries.FirstOrDefault(e => string.Equals(e.Data?.GetValueOrDefault("marker")?.ToString(), LogMarker.Invariant.ToString(), StringComparison.OrdinalIgnoreCase));

        var summaryPayload = new Dictionary<string, object>
        {
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["testId"] = testId,
            ["url"] = url,
            ["summary"] = summary,
            ["entryCount"] = normalizedEntries.Count,
            ["severityCounts"] = severityCounts,
            ["topRepeatedMessages"] = topMessages,
            ["firstError"] = firstError != null ? ToSummaryRecord(firstError) : null,
            ["firstInvariant"] = firstFatal != null ? ToSummaryRecord(firstFatal) : null
        };

        var summaryPath = Path.Combine(bundleDir, "summary.json");
        ResilientFileWriter.WriteAllText(summaryPath, JsonSerializer.Serialize(summaryPayload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        var ndjsonPath = Path.Combine(bundleDir, "logs.ndjson");
        foreach (var entry in normalizedEntries)
        {
            var record = JsonSerializer.Serialize(new
            {
                entry.Timestamp,
                Level = entry.Level.ToString(),
                Category = entry.Category.ToString(),
                entry.Message,
                entry.CorrelationId,
                entry.Component,
                entry.SourceFile,
                entry.SourceLine,
                entry.MethodName,
                entry.DurationMs,
                entry.MemoryBytes,
                entry.Data
            });
            ResilientFileWriter.AppendAllText(ndjsonPath, record + Environment.NewLine);
        }

        TryCopyDiagnosticArtifact("debug_screenshot.png", Path.Combine(bundleDir, "screenshot.png"));
        TryCopyLatestMatching(DiagnosticPaths.GetLogsDirectory(), "fenbrowser_trace_*.jsonl", Path.Combine(bundleDir, "trace.jsonl"));

        return bundleDir;
    }

    private static Dictionary<string, object> ToSummaryRecord(LogEntry entry)
    {
        return new Dictionary<string, object>
        {
            ["timestamp"] = entry.Timestamp,
            ["level"] = entry.Level.ToString(),
            ["category"] = entry.Category.ToString(),
            ["message"] = entry.Message,
            ["sourceFile"] = entry.SourceFile,
            ["sourceLine"] = entry.SourceLine,
            ["method"] = entry.MethodName
        };
    }

    private static void TryCopyDiagnosticArtifact(string sourceFileName, string destinationPath)
    {
        try
        {
            var sourcePath = Path.Combine(DiagnosticPaths.GetLogsDirectory(), sourceFileName);
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryCopyLatestMatching(string directory, string pattern, string destinationPath)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var latest = new DirectoryInfo(directory)
                .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest != null)
            {
                File.Copy(latest.FullName, destinationPath, overwrite: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
