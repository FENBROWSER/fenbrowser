using FenBrowser.Core.Logging;
using System.Text.Json;

namespace FenBrowser.Tests.Logging;

public class EngineLogSettingsTests
{
    [Fact]
    public void DisabledLogging_BlocksWritesAndClearsCompatibilityBuffer()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Info,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        EngineLog.Write(LogSubsystem.General, LogSeverity.Info, "before-off");
        Assert.NotEmpty(EngineLog.GetCompatibilityRecentEntries());

        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = false,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = false,
            EnableTraceSink = false
        });

        EngineLog.Write(LogSubsystem.General, LogSeverity.Error, "after-off");

        Assert.False(EngineLog.IsEnabled(LogSubsystem.General, LogSeverity.Error));
        Assert.Empty(EngineLog.GetCompatibilityRecentEntries());
    }

    [Fact]
    public void EnabledCategories_FilterStructuredWrites()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            EnabledCategories = LogCategory.Network,
            GlobalMinimumSeverity = LogSeverity.Info,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        EngineLog.ClearCompatibilityBuffer();

        EngineLog.Write(LogSubsystem.Layout, LogSeverity.Info, "layout-filtered");
        EngineLog.Write(LogSubsystem.Net, LogSeverity.Info, "network-visible");

        var entries = EngineLog.GetCompatibilityRecentEntries();
        Assert.Single(entries);
        Assert.Equal(LogCategory.Network, entries[0].Category);
        Assert.Equal("network-visible", entries[0].Message);
    }

    [Fact]
    public void TraceSink_WritesDiagnosticJsonlSchema()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Trace,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = true,
                TraceFilePath = tracePath
            });

            var context = new EngineLogContext(
                BrowserSessionId: "session-1",
                NavigationId: "nav-1",
                DocumentId: "doc-1",
                FrameId: "frame-1",
                Url: "https://example.test/",
                RequestId: "request-1",
                RealmId: "realm-1",
                ScriptId: "script-1",
                TaskId: "task-1");

            EngineLog.Write(
                LogSubsystem.Js,
                LogSeverity.Error,
                "script failed",
                LogMarker.EngineBug,
                context,
                new Dictionary<string, object>
                {
                    ["event"] = "ScriptExecutionFailed",
                    ["exception"] = "ReferenceError: missingApi is not defined"
                });

            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });

            var line = Assert.Single(File.ReadAllLines(tracePath));
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            Assert.Equal("ERROR", root.GetProperty("level").GetString());
            Assert.Equal("JS", root.GetProperty("category").GetString());
            Assert.Equal("ScriptExecutionFailed", root.GetProperty("event").GetString());
            Assert.Equal("session-1", root.GetProperty("session_id").GetString());
            Assert.Equal("nav-1", root.GetProperty("nav_id").GetString());
            Assert.Equal("doc-1", root.GetProperty("doc_id").GetString());
            Assert.Equal("frame-1", root.GetProperty("frame_id").GetString());
            Assert.Equal("realm-1", root.GetProperty("realm_id").GetString());
            Assert.Equal("script-1", root.GetProperty("script_id").GetString());
            Assert.Equal("request-1", root.GetProperty("request_id").GetString());
            Assert.Equal("task-1", root.GetProperty("task_id").GetString());
            Assert.Equal("script failed", root.GetProperty("message").GetString());
            Assert.Equal("ReferenceError: missingApi is not defined", root.GetProperty("data").GetProperty("exception").GetString());
        }
        finally
        {
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }
}
