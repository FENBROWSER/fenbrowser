using System.Text.Json;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Core;

[Collection(EngineLogTestCollection.Name)]
public class NavigationLifecycleTraceTests
{
    [Fact]
    public void LifecycleTransitions_WriteNavigationTraceEvents()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-nav-trace-{Guid.NewGuid():N}.jsonl");
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

            var lifecycle = new NavigationLifecycleTracker();
            var navigationId = lifecycle.BeginNavigation("https://example.test/", isUserInput: true);
            lifecycle.MarkFetching(navigationId, "https://example.test/");
            lifecycle.MarkResponseReceived(
                navigationId,
                "Success",
                "https://example.test/",
                isRedirect: false,
                redirectCount: 0,
                detail: "status=200");
            lifecycle.MarkCommitting(navigationId, "https://example.test/", "network-document");

            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });

            var events = File.ReadAllLines(tracePath)
                .Select(line => JsonDocument.Parse(line))
                .ToList();

            try
            {
                Assert.Contains(events, doc => IsNavigationEvent(doc, "NavigationRequested", navigationId));
                Assert.Contains(events, doc => IsNavigationEvent(doc, "NavigationResponseReceived", navigationId));
                Assert.Contains(events, doc => IsNavigationEvent(doc, "NavigationCommitted", navigationId));
            }
            finally
            {
                foreach (var doc in events)
                {
                    doc.Dispose();
                }
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

    private static bool IsNavigationEvent(JsonDocument doc, string eventName, long navigationId)
    {
        var root = doc.RootElement;
        return root.GetProperty("category").GetString() == "Navigation" &&
               root.GetProperty("event").GetString() == eventName &&
               root.GetProperty("nav_id").GetString() == navigationId.ToString();
    }
}
