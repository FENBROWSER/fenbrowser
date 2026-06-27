using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
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

            using (EngineLogCompat.BeginCorrelationScope(navigationId, "test-parser", new Dictionary<string, object>
            {
                ["navigationId"] = navigationId,
                ["url"] = "https://example.test/"
            }))
            {
                HtmlParser.ParseDocumentDetailed(
                    "<!doctype html><html><head><title>trace</title></head><body><p>ok</p></body></html>",
                    new HtmlParserOptions
                    {
                        BaseUri = new Uri("https://example.test/")
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
            foreach (var line in File.ReadAllLines(tracePath))
            {
                using var parsed = JsonDocument.Parse(line);
                var root = parsed.RootElement;
                if (root.GetProperty("category").GetString() != "HTMLParser" ||
                    root.GetProperty("nav_id").GetString() != navigationId)
                {
                    continue;
                }

                sawStarted |= root.GetProperty("event").GetString() == "HTMLParsingStarted";
                sawCompleted |= root.GetProperty("event").GetString() == "HTMLParsingCompleted" &&
                                root.GetProperty("data").GetProperty("tokenCount").GetInt32() > 0;
            }

            Assert.True(sawStarted);
            Assert.True(sawCompleted);
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
