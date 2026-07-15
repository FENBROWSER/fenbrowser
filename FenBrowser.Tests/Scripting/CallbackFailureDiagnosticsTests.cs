using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Scripting;

[Collection(EngineLogTestCollection.Name)]
public sealed class CallbackFailureDiagnosticsTests
{
    [Fact]
    public async Task ThrowingTimer_PreservesTypedFailureProvenance()
    {
        var baseUri = new Uri("https://fixture.test/throwing-timer.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function timerFixtureReceiver(){throw new Error('timer-fixture-boom');},1);" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var failureObserved = SpinWait.SpinUntil(
                () => engine.GetEventLoopSnapshot().CallbackFailures > 0,
                millisecondsTimeout: 1000);

            var snapshot = engine.GetEventLoopSnapshot();
            Assert.True(
                failureObserved,
                $"scheduled={snapshot.TimersScheduled}, executed={snapshot.TimersExecuted}, " +
                $"failures={snapshot.CallbackFailures}, events={string.Join(',', snapshot.Events.Select(e => e.EventName))}");
            Assert.Equal(1, snapshot.CallbackFailures);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
            var failures = json.RootElement.GetProperty("CallbackFailureRecords");
            var failure = Assert.Single(failures.EnumerateArray());

            Assert.Equal("setTimeout", failure.GetProperty("CallbackCategory").GetString());
            Assert.StartsWith("timer-", failure.GetProperty("TaskId").GetString());
            Assert.StartsWith("callback-", failure.GetProperty("CallbackId").GetString());
            Assert.Equal("timerFixtureReceiver", failure.GetProperty("CallbackFunctionName").GetString());
            Assert.Equal("script-1", failure.GetProperty("ScriptId").GetString());
            Assert.Equal("inline#1", failure.GetProperty("ScriptSourceLabel").GetString());
            Assert.True(failure.GetProperty("SourceLine").GetInt32() > 0);
            Assert.Equal(1, failure.GetProperty("TimerId").GetInt64());
            Assert.Equal("Object", failure.GetProperty("ReceiverJsType").GetString());
            Assert.StartsWith("[object:", failure.GetProperty("ReceiverRepresentation").GetString());
            Assert.Equal("Error", failure.GetProperty("ExceptionType").GetString());
            Assert.Contains("timer-fixture-boom", failure.GetProperty("ExceptionMessage").GetString());
            Assert.Contains("timerFixtureReceiver", failure.GetProperty("JsStack").GetString());
            Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("HostStack").GetString()));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task NestedTimer_PreservesCreatingScriptAfterActiveScriptClears()
    {
        var baseUri = new Uri("https://fixture.test/nested-timer.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function firstTimer(){" +
                "setTimeout(function nestedTimer(){throw new Error('nested-timer-boom');},1);" +
                "},1);" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.True(SpinWait.SpinUntil(
                () => engine.GetEventLoopSnapshot().CallbackFailures > 0,
                millisecondsTimeout: 1000));

            var failure = Assert.Single(engine.GetEventLoopSnapshot().CallbackFailureRecords);
            Assert.Equal("nestedTimer", failure.CallbackFunctionName);
            Assert.Equal("script-1", failure.ScriptId);
            Assert.Equal("inline#1", failure.ScriptSourceLabel);
            Assert.True(failure.SourceLine > 0);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task HotTimerHelper_InOperatorAcceptsHostObjectAfterJitTierUp()
    {
        var baseUri = new Uri("https://fixture.test/host-object-in.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "function hasInactiveMarker(value){return '__GWS_INACTIVE' in value;}" +
                "setTimeout(function hostObjectInTimer(){" +
                "var result=false;" +
                "for(var i=0;i<12;i++){result=hasInactiveMarker(document.body);}" +
                "globalThis.__hostObjectInCompleted=!result;" +
                "},1);" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    var current = engine.GetEventLoopSnapshot();
                    return current.TimersExecuted > 0 || current.CallbackFailures > 0;
                },
                millisecondsTimeout: 1000));

            var snapshot = engine.GetEventLoopSnapshot();
            Assert.True(
                snapshot.CallbackFailures == 0,
                snapshot.CallbackFailureRecords.FirstOrDefault()?.ExceptionMessage);
            Assert.Equal("true", engine.Evaluate("String(globalThis.__hostObjectInCompleted)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
    }
}
