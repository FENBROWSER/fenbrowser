using System.Text.Json;
using System.Linq;
using FenBrowser.Core.Logging;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class LogDomainTests
{
    [Fact]
    public async Task LogEnable_BroadcastsEntryAdded_OnEngineLogWrite()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Debug,
            EnableConsoleSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        var server = new DevToolsServer();
        server.InitializeLog();

        string? capturedJson = null;
        server.OnJsonOutput(json =>
        {
            if (json.Contains("\"method\":\"Log.entryAdded\"", StringComparison.Ordinal))
            {
                capturedJson = json;
            }
        });

        await server.ProcessRequestAsync(ProtocolJson.Serialize(new ProtocolRequest<object>
        {
            Id = 1,
            Method = "Log.enable",
            Params = new { }
        }));

        EngineLog.Write(LogSubsystem.DevTools, LogSeverity.Info, "devtools-log-domain-test");

        for (var i = 0; i < 20 && capturedJson == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(capturedJson);

        using var doc = JsonDocument.Parse(capturedJson!);
        Assert.Equal("Log.entryAdded", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task LogEnable_FilterBySubsystem_AndGetCounters_Works()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Debug,
            EnableConsoleSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        EngineLog.ClearCompatibilityBuffer();

        var server = new DevToolsServer();
        server.InitializeLog();

        string? capturedJson = null;
        server.OnJsonOutput(json =>
        {
            if (json.Contains("\"method\":\"Log.entryAdded\"", StringComparison.Ordinal))
            {
                capturedJson = json;
            }
        });

        await server.ProcessRequestAsync(ProtocolJson.Serialize(new ProtocolRequest<object>
        {
            Id = 1,
            Method = "Log.enable",
            Params = new
            {
                filter = new
                {
                    subsystems = new[] { "Layout" },
                    tabId = "1"
                }
            }
        }));

        EngineLog.Write(
            LogSubsystem.DevTools,
            LogSeverity.Info,
            "devtools-should-be-filtered");

        EngineLog.Write(
            LogSubsystem.Layout,
            LogSeverity.Info,
            "layout-filter-hit",
            LogMarker.None,
            new EngineLogContext(DocumentId: "doc-filter-1", TabId: "1", FrameId: "frame-a"));

        for (var i = 0; i < 20 && capturedJson == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(capturedJson);
        using (var entryDoc = JsonDocument.Parse(capturedJson!))
        {
            Assert.Equal(
                "Layout",
                entryDoc.RootElement.GetProperty("params").GetProperty("entry").GetProperty("subsystem").GetString());
        }

        var countersJson = await server.ProcessRequestAsync(ProtocolJson.Serialize(new ProtocolRequest<object>
        {
            Id = 2,
            Method = "Log.getCounters",
            Params = new { }
        }));

        using var countersDoc = JsonDocument.Parse(countersJson);
        var docs = countersDoc.RootElement.GetProperty("result").GetProperty("documents");
        Assert.True(docs.GetArrayLength() > 0);

        var match = docs.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("documentKey").GetString() == "doc-filter-1");

        Assert.Equal(1L, match.GetProperty("totalCount").GetInt64());
        Assert.Equal("1", match.GetProperty("lastTabId").GetString());
        Assert.Equal("frame-a", match.GetProperty("lastFrameId").GetString());
    }
}
