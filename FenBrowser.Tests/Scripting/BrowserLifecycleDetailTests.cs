using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection(EngineLogTestCollection.Name)]
public sealed class BrowserLifecycleDetailTests
{
    [Fact]
    public void TimedOutEventLoopSample_IsExplicitlyHistorical()
    {
        var snapshot = new BrowserEventLoopSnapshot
        {
            Status = "running",
            DocumentReadyState = "loading",
            DomContentLoadedFired = false,
            LoadFired = false,
            PendingHostTimers = 0
        };

        var detail = BrowserHost.FormatDocumentLifecycleSettleDetail(
            navigationId: 7,
            snapshot,
            settled: false,
            elapsedMs: 1500,
            settleTimeoutMs: 1500);

        Assert.Contains("eventLoopObservation=transition-time", detail);
        Assert.Contains("eventLoopObservationTimedOut=1", detail);
        Assert.Contains("documentReadyStateAtObservation=loading", detail);
        Assert.Contains("domContentLoadedAtObservation=0", detail);
        Assert.Contains("loadAtObservation=0", detail);
        Assert.DoesNotContain("documentReadyState=loading", detail);
    }

    [Fact]
    public async Task CompletedDocument_HasOneConsistentTerminalLifecycleState()
    {
        var baseUri = new Uri("https://fixture.test/lifecycle-complete.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div>complete</div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var snapshot = engine.GetEventLoopSnapshot();
            Assert.Equal("complete", engine.Evaluate("document.readyState")?.ToString());
            Assert.Equal("completed", snapshot.Status);
            Assert.Equal("complete", snapshot.DocumentReadyState);
            Assert.True(snapshot.DomContentLoadedFired);
            Assert.True(snapshot.LoadFired);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.CompletedUtc));

            var domContentLoaded = Assert.Single(
                snapshot.Events,
                entry => entry.EventName == "DOMContentLoadedFired");
            var load = Assert.Single(
                snapshot.Events,
                entry => entry.EventName == "LoadFired");
            Assert.Equal("interactive", domContentLoaded.DocumentReadyState);
            Assert.Equal("complete", load.DocumentReadyState);
            Assert.True(snapshot.Events.IndexOf(domContentLoaded) < snapshot.Events.IndexOf(load));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task CompletedDocument_FiresReadyStateChangeAroundDOMContentLoadedAndLoad()
    {
        var baseUri = new Uri("https://fixture.test/lifecycle-readystatechange.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "globalThis.__timeline = [];" +
                "document.addEventListener('readystatechange', function () { globalThis.__timeline.push('rs:' + document.readyState); });" +
                "document.addEventListener('DOMContentLoaded', function () { globalThis.__timeline.push('dcl'); });" +
                "window.onload = function () { globalThis.__timeline.push('load'); };" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            // HTML end steps: readystatechange(interactive) precedes
            // DOMContentLoaded; readystatechange(complete) precedes load.
            Assert.Equal("rs:interactive", engine.Evaluate("globalThis.__timeline[0]")?.ToString());
            Assert.Equal("dcl", engine.Evaluate("globalThis.__timeline[1]")?.ToString());
            Assert.Equal("rs:complete", engine.Evaluate("globalThis.__timeline[2]")?.ToString());
            Assert.Equal("load", engine.Evaluate("globalThis.__timeline[3]")?.ToString());
            Assert.Equal("4", engine.Evaluate("String(globalThis.__timeline.length)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
