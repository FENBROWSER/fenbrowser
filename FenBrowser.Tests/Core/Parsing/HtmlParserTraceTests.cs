using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Core.Parsing;

[Collection(EngineLogTestCollection.Name)]
public class HtmlParserTraceTests
{
    [Fact]
    public void ParseDocumentDetailed_WritesHtmlParsingTraceEvents()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-html-parse-trace-{Guid.NewGuid():N}.jsonl");
        const string navigationId = "nav-html-parse-test";

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

            // Resource discovery is traced by the preload observer, which the parser
            // attaches only when it has a prefetcher to feed - as the browser always
            // gives it. example.test never resolves, so nothing is fetched.
            using var prefetcher = new ResourcePrefetcher(new ResourceManager(new System.Net.Http.HttpClient(), false));
            using (EngineLogCompat.BeginCorrelationScope(navigationId, "test-parser", new Dictionary<string, object>
            {
                ["navigationId"] = navigationId,
                ["url"] = "https://example.test/"
            }))
            {
                HtmlParser.ParseDocumentDetailed(
                    "<!doctype html><html><head><title>trace</title><link rel=\"stylesheet\" href=\"/site.css\"><script src=\"/app.js\"></script></head><body><p>ok</p></body></html>",
                    new HtmlParserOptions
                    {
                        BaseUri = new Uri("https://example.test/"),
                        Prefetcher = prefetcher
                    });
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

            var sawStarted = false;
            var sawCompleted = false;
            var sawStylesheet = false;
            var sawScript = false;
            foreach (var line in File.ReadAllLines(tracePath))
            {
                using var parsed = JsonDocument.Parse(line);
                var root = parsed.RootElement;
                if (root.GetProperty("nav_id").GetString() != navigationId)
                {
                    continue;
                }

                var category = root.GetProperty("category").GetString();
                var eventName = root.GetProperty("event").GetString();

                sawStarted |= category == "HTMLParser" && eventName == "HTMLParsingStarted";
                sawCompleted |= category == "HTMLParser" &&
                                eventName == "HTMLParsingCompleted" &&
                                root.GetProperty("data").GetProperty("tokenCount").GetInt32() > 0;
                // Discovery events log a resource's origin and path length, never
                // its path, which can carry tokens.
                sawStylesheet |= category == "ResourceLoader" &&
                                 eventName == "StylesheetDiscovered" &&
                                 IsDiscovery(root, "stylesheet", "/site.css");
                sawScript |= category == "ResourceLoader" &&
                             eventName == "ScriptDiscovered" &&
                             IsDiscovery(root, "script-src", "/app.js");
            }

            Assert.True(sawStarted);
            Assert.True(sawCompleted);
            Assert.True(sawStylesheet);
            Assert.True(sawScript);
        }
        finally
        {
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    private static bool IsDiscovery(JsonElement root, string discoveryType, string path)
    {
        var data = root.GetProperty("data");
        return data.GetProperty("resourceUrl").GetString() == "https://example.test" &&
               data.GetProperty("discoveryType").GetString() == discoveryType &&
               data.GetProperty("resourcePathLength").GetInt32() == path.Length;
    }
}
