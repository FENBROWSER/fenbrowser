using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System.Text.Json;

namespace FenBrowser.Tests.Logging;

[Collection(EngineLogTestCollection.Name)]
public class EngineLogSettingsTests
{
    [Fact]
    public void DisabledCompatibilityLogging_DoesNotAllocateForConstantMessages()
    {
        bool wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLogCompat.IsEnabled = false;
            EngineLogCompat.Debug("suppressed paint diagnostic", LogCategory.Paint);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                EngineLogCompat.Debug("suppressed paint diagnostic", LogCategory.Paint);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }

    [Fact]
    public void FilteredCompatibilityLogging_DoesNotAllocateForConstantMessages()
    {
        bool wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLogCompat.IsEnabled = true;
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Info,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            EngineLogCompat.Debug("filtered paint diagnostic", LogCategory.Paint);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                EngineLogCompat.Debug("filtered paint diagnostic", LogCategory.Paint);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }

    [Fact]
    public void DisabledInterpolatedCompatibilityLogging_DoesNotAllocateAtCaller()
    {
        bool wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLogCompat.IsEnabled = false;
            var probe = new FormattingProbe();
            EngineLogCompat.Log(LogCategory.Paint, LogLevel.Debug, $"suppressed paint diagnostic {probe}");
            Assert.Equal(0, probe.ToStringCalls);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                EngineLogCompat.Log(LogCategory.Paint, LogLevel.Debug, $"suppressed paint diagnostic {i}");
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }

    [Fact]
    public void FilteredInterpolatedCompatibilityLogging_DoesNotFormatOrAllocateAtCaller()
    {
        bool wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLogCompat.IsEnabled = true;
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Info,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            var probe = new FormattingProbe();
            EngineLogCompat.Log(LogCategory.Paint, LogLevel.Debug, $"filtered paint diagnostic {probe}");
            Assert.Equal(0, probe.ToStringCalls);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                EngineLogCompat.Log(LogCategory.Paint, LogLevel.Debug, $"filtered paint diagnostic {i}");
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }

    [Fact]
    public void EnabledInterpolatedCompatibilityLogging_FormatsAndEmitsMessage()
    {
        bool wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Debug,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = true,
                EnableTraceSink = false,
                RingBufferCapacity = 1000
            });
            EngineLog.ClearCompatibilityBuffer();
            var probe = new FormattingProbe();

            EngineLogCompat.Log(LogCategory.Paint, LogLevel.Debug, $"visible paint diagnostic {probe}");

            Assert.Equal(1, probe.ToStringCalls);
            var entry = Assert.Single(EngineLog.GetCompatibilityRecentEntries());
            Assert.Equal(LogCategory.Paint, entry.Category);
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Equal("visible paint diagnostic formatted", entry.Message);
            Assert.Equal("EngineLogSettingsTests.cs", entry.SourceFile);
            Assert.Equal(nameof(EnabledInterpolatedCompatibilityLogging_FormatsAndEmitsMessage), entry.MethodName);
            Assert.True(entry.SourceLine > 0);
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }

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

    [Fact]
    public void Flush_DrainsAcceptedEventsBeforeArtifactCopy()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-flush-{Guid.NewGuid():N}.jsonl");
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

            for (var i = 0; i < 64; i++)
            {
                EngineLog.Write(
                    LogSubsystem.Verification,
                    LogSeverity.Info,
                    "flush-fixture",
                    fields: new Dictionary<string, object> { ["sequence"] = i });
            }

            Assert.True(EngineLog.Flush(TimeSpan.FromSeconds(2)));
            Assert.Equal(64, File.ReadAllLines(tracePath).Length);
        }
        finally
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    private sealed class FormattingProbe
    {
        public int ToStringCalls { get; private set; }

        public override string ToString()
        {
            ToStringCalls++;
            return "formatted";
        }
    }
}
