using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsHostLifetimeMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public FenJsHostLifetimeMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SameDomObjectWithinSession_ReusesOneHostHandle()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            var engine = CreateEngine();
            await SetDocumentAsync(engine, 1);
            engine.Evaluate("globalThis.firstBody=document.body;");
            var afterFirstLookup = engine.GetHostLifetimeSnapshotForTest();

            var result = engine.Evaluate(
                "var secondBody=document.body;" +
                "[String(firstBody===secondBody),String(document===window.document)].join('|');");
            var after = engine.GetHostLifetimeSnapshotForTest();

            Assert.Equal("true|true", result?.ToString());
            Assert.Equal(afterFirstLookup.HostTableLiveCount, after.HostTableLiveCount);
            Assert.Equal(after.HostTableLiveCount, after.HostHandleIdentityCacheCount);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task RepeatedDocumentReset_ReplacesStrongTableAndBoundsLiveHandles()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            var engine = CreateEngine();
            var baselineCounts = new List<int>();
            var windowListenerCounts = new List<int>();
            var previousSession = -1;

            for (var navigation = 1; navigation <= 6; navigation++)
            {
                await SetDocumentAsync(engine, navigation);
                var baseline = engine.GetHostLifetimeSnapshotForTest();
                baselineCounts.Add(baseline.HostTableLiveCount);
                windowListenerCounts.Add(baseline.WindowEventListenerCount);

                Assert.True(baseline.RuntimeSessionGeneration > previousSession);
                Assert.Equal(navigation + 1, baseline.DocumentEpoch);
                Assert.Equal(baseline.HostTableLiveCount, baseline.HostHandleIdentityCacheCount);
                Assert.Equal(baseline.HostTableLiveCount, baseline.HostTableSlotCount);
                Assert.Equal(0, baseline.DocumentEventListenerCount);
                Assert.Equal(0, baseline.PendingPromiseRejectionCount);
                Assert.Equal(0, baseline.ActiveWebSocketCount);

                previousSession = baseline.RuntimeSessionGeneration;
                engine.Evaluate(
                    "globalThis.retained=[];" +
                    "for(var i=0;i<32;i++){var el=document.createElement('div');retained.push(el);}");
                var expanded = engine.GetHostLifetimeSnapshotForTest();

                _output.WriteLine(
                    "navigation={0} session={1} baselineLive={2} expandedLive={3} windowListeners={4}",
                    navigation,
                    baseline.RuntimeSessionGeneration,
                    baseline.HostTableLiveCount,
                    expanded.HostTableLiveCount,
                    baseline.WindowEventListenerCount);

                Assert.True(expanded.HostTableLiveCount >= baseline.HostTableLiveCount + 32);
                Assert.Equal(expanded.HostTableLiveCount, expanded.HostHandleIdentityCacheCount);
                Assert.Equal(expanded.HostTableLiveCount, expanded.HostTableSlotCount);
            }

            Assert.All(baselineCounts, count => Assert.Equal(baselineCounts[0], count));
            Assert.All(windowListenerCounts, count => Assert.Equal(windowListenerCounts[0], count));
            Assert.Equal(2, windowListenerCounts[0]);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    private static FenJsBrowserScriptEngine CreateEngine() =>
        new(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

    private static async Task SetDocumentAsync(FenJsBrowserScriptEngine engine, int navigation)
    {
        var baseUri = new Uri($"https://fixture.test/lifetime/{navigation}/");
        var document = new HtmlParser(
            $"<html><body><div id='document-{navigation}'></div></body></html>",
            baseUri).Parse();
        await engine.SetDomAsync(document.DocumentElement, baseUri);
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
