using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Engine;

[Collection(EngineLogTestCollection.Name)]
public sealed class BrowserScriptEngineTraceTests
{
    [Fact]
    public async Task SetDomAsync_WritesPerScriptLifecycleTrace()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-script-trace-{Guid.NewGuid():N}.jsonl");
        const string navigationId = "nav-script-test";
        var baseUri = new Uri("https://example.test/app/index.html");

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

            var document = new HtmlParser(
                "<html><body>" +
                "<script>globalThis.__inlineReady = true;var s=document.createElement('script');s.src='/assets/dynamic.js';document.body.appendChild(s);</script>" +
                "<script defer src=\"/assets/app.js\"></script>" +
                "</body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (uri, _) => Task.FromResult(
                uri.AbsolutePath.EndsWith("/dynamic.js", StringComparison.Ordinal)
                    ? "globalThis.__dynamicReady = true;"
                    : "globalThis.__externalReady = true;");

            using (EngineLogCompat.BeginCorrelationScope(navigationId, "script-trace-test", new Dictionary<string, object>
            {
                ["navigationId"] = navigationId,
                ["url"] = baseUri.AbsoluteUri
            }))
            {
                await engine.SetDomAsync(document.DocumentElement, baseUri);
            }

            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });

            var events = LoadScriptTraceEvents(tracePath);

            Assert.Contains(events, e =>
                e.Event == "ScriptDiscovered" &&
                e.ScriptId == "script-1" &&
                e.Data.GetProperty("inline").GetBoolean());
            Assert.Contains(events, e =>
                e.Event == "ScriptReady" &&
                e.ScriptId == "script-1" &&
                e.Data.GetProperty("fetchStatus").GetString() == "ready");
            Assert.Contains(events, e =>
                e.Event == "ScriptFetchStarted" &&
                e.ScriptId == "script-2" &&
                e.ResourceUrl == "https://example.test/assets/app.js");
            Assert.Contains(events, e =>
                e.Event == "ScriptFetchCompleted" &&
                e.ScriptId == "script-2");
            Assert.Contains(events, e =>
                e.Event == "ScriptReady" &&
                e.ScriptId == "script-2" &&
                e.Data.GetProperty("external").GetBoolean());
            Assert.Contains(events, e =>
                e.Event == "DOMContentLoadedBlockedByScript" &&
                e.ScriptId == "script-2" &&
                e.Data.GetProperty("blocksDOMContentLoaded").GetBoolean());
            Assert.Contains(events, e =>
                e.Event == "ScriptExecutionStarted" &&
                e.ScriptId == "script-2" &&
                e.NavId == navigationId);
            Assert.Contains(events, e =>
                e.Event == "ScriptExecutionCompleted" &&
                e.ScriptId == "script-2" &&
                e.Category == "ScriptLoader");
            Assert.Contains(events, e =>
                e.Event == "ScriptDiscovered" &&
                e.ScriptId == "dynamic-script-1" &&
                !e.Data.GetProperty("parserInserted").GetBoolean());
            Assert.Contains(events, e =>
                e.Event == "ScriptFetchStarted" &&
                e.ScriptId == "dynamic-script-1" &&
                e.ResourceUrl == "https://example.test/assets/dynamic.js");
            Assert.Contains(events, e =>
                e.Event == "ScriptExecutionCompleted" &&
                e.ScriptId == "dynamic-script-1");
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

            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static List<ScriptTraceEvent> LoadScriptTraceEvents(string tracePath)
    {
        var events = new List<ScriptTraceEvent>();
        foreach (var line in File.ReadAllLines(tracePath))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("category").GetString() != "ScriptLoader")
            {
                continue;
            }

            events.Add(new ScriptTraceEvent(
                root.GetProperty("category").GetString(),
                root.GetProperty("event").GetString(),
                root.GetProperty("nav_id").GetString(),
                root.GetProperty("script_id").GetString(),
                root.GetProperty("data").TryGetProperty("resource_url", out var resourceUrl)
                    ? resourceUrl.GetString()
                    : string.Empty,
                root.GetProperty("data").Clone()));
        }

        return events;
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }

    private sealed record ScriptTraceEvent(
        string Category,
        string Event,
        string NavId,
        string ScriptId,
        string ResourceUrl,
        JsonElement Data);
}
