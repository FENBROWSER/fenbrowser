using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.Js.Test262;

/// <summary>
/// Writes structured JSONL progress events to a companion file so external
/// tooling (e.g. the live TUI) can render real-time dashboards.
///
/// Thread-safe: all public methods acquire a lock so progress can be written
/// from the runner's finally block without coordinating with the heartbeat timer.
/// </summary>
public sealed class Test262ProgressWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public Test262ProgressWriter(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 4096);
    }

    public void WriteBatchStart(string tag, string mode, string scope, int total, int timeoutMs)
    {
        WriteEvent(new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["type"] = "batch_start",
            ["tag"] = tag,
            ["mode"] = mode,
            ["scope"] = scope,
            ["total"] = total,
            ["timeoutMs"] = timeoutMs
        });
    }

    public void WriteTestResult(int index, string relativePath, string status,
        long durationMs, string? category)
    {
        // Small-enough payload per test — just the essentials.
        // The TUI can always cross-reference the final JSON for full metadata.
        WriteEvent(new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["type"] = "test_result",
            ["idx"] = index,
            ["path"] = relativePath,
            ["status"] = status,
            ["durationMs"] = durationMs,
            ["category"] = category
        });
    }

    public void WriteHeartbeat(int completed, int total, int passed, int failed,
        int crashed, int timedOut, int unsupported, long elapsedMs, long etaMs)
    {
        WriteEvent(new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["type"] = "heartbeat",
            ["completed"] = completed,
            ["total"] = total,
            ["passed"] = passed,
            ["failed"] = failed,
            ["crashed"] = crashed,
            ["timedOut"] = timedOut,
            ["unsupported"] = unsupported,
            ["elapsedMs"] = elapsedMs,
            ["etaMs"] = etaMs
        });
    }

    public void WriteBatchComplete(int total, int passed, int failed, int crashed,
        int timedOut, int unsupported, int unexpectedPasses, int expectedFailures,
        long durationMs)
    {
        WriteEvent(new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["type"] = "batch_complete",
            ["total"] = total,
            ["passed"] = passed,
            ["failed"] = failed,
            ["crashed"] = crashed,
            ["timedOut"] = timedOut,
            ["unsupported"] = unsupported,
            ["unexpectedPasses"] = unexpectedPasses,
            ["expectedFailures"] = expectedFailures,
            ["durationMs"] = durationMs
        });
    }

    public void WriteStallKilled(string tag, int completed)
    {
        WriteEvent(new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["type"] = "stall_killed",
            ["tag"] = tag,
            ["completed"] = completed
        });
    }

    private void WriteEvent(Dictionary<string, object?> fields)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                var json = JsonSerializer.Serialize(fields, Test262JsonContext.Default.DictionaryStringObject);
                _writer.WriteLine(json);
                // Flush after heartbeat/batch_complete so the TUI sees updates promptly.
                // Per-test events (test_result) stay buffered for performance.
                var etype = fields.GetValueOrDefault("type", "") as string;
                if (etype == "heartbeat" || etype == "batch_complete" || etype == "batch_start")
                {
                    _writer.Flush();
                }
            }
            catch
            {
                // Never let progress logging crash the runner.
                // The final result JSON is the authoritative record.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _writer.Dispose(); }
        catch { /* best-effort */ }
    }
}
