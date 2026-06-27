using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Engine;

[Collection(EngineLogTestCollection.Name)]
public class CustomHtmlEngineDocumentTraceTests
{
    [Fact]
    public async Task RenderAsync_WritesDocumentCreatedTraceForActiveNavigation()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-document-trace-{Guid.NewGuid():N}.jsonl");
        const string navigationId = "nav-document-test";

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

            using (EngineLogCompat.BeginCorrelationScope(navigationId, "test-render", new Dictionary<string, object>
            {
                ["navigationId"] = navigationId,
                ["url"] = "https://example.test/"
            }))
            {
                var engine = new CustomHtmlEngine
                {
                    EnableJavaScript = false
                };

                await engine.RenderAsync(
                    "<!doctype html><html><head><title>trace</title></head><body><main>ok</main></body></html>",
                    new Uri("https://example.test/"),
                    _ => Task.FromResult(string.Empty),
                    _ => Task.FromResult<Stream>(new MemoryStream()),
                    _ => { },
                    viewportWidth: 800,
                    viewportHeight: 600,
                    forceJavascript: false);
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

            JsonDocument documentCreated = null;
            foreach (var line in File.ReadAllLines(tracePath))
            {
                var parsed = JsonDocument.Parse(line);
                if (IsDocumentCreatedForNavigation(parsed, navigationId))
                {
                    documentCreated = parsed;
                    break;
                }

                parsed.Dispose();
            }

            Assert.NotNull(documentCreated);
            using (documentCreated)
            {
                var root = documentCreated.RootElement;
                Assert.Equal("Navigation", root.GetProperty("category").GetString());
                Assert.Equal($"doc-{navigationId}", root.GetProperty("doc_id").GetString());
                Assert.Equal("https://example.test/", root.GetProperty("data").GetProperty("url").GetString());
            }
        }
        finally
        {
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    private static bool IsDocumentCreatedForNavigation(JsonDocument doc, string navigationId)
    {
        var root = doc.RootElement;
        return root.GetProperty("event").GetString() == "DocumentCreated" &&
               root.GetProperty("nav_id").GetString() == navigationId;
    }
}
